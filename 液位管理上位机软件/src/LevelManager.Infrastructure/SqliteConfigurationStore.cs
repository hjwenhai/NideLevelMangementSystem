using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;

public sealed partial class SqliteConfigurationStore : IConfigurationStore,IUserStore,ISecurityAudit
{
 public const int CurrentSchema=18;
 private readonly string connectionString;
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter(allowIntegerValues:false)}};
 public SqliteConfigurationStore(string path)
 {
  if(!Path.IsPathFullyQualified(path))throw new ArgumentException("数据库必须使用明确的绝对路径",nameof(path));
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  connectionString=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=true,DefaultTimeout=5}.ToString();
 }
 private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
 {
  var connection=new SqliteConnection(connectionString);
  try{await connection.OpenAsync(ct);await using var cmd=connection.CreateCommand();cmd.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";await cmd.ExecuteNonQueryAsync(ct);return connection;}catch{await connection.DisposeAsync();throw;}
 }
 private static LevelDbContext Context(SqliteConnection c)=>new(new DbContextOptionsBuilder<LevelDbContext>().UseSqlite(c).Options);
 public async Task<bool> WasTankDefinedAsync(Guid tankId,CancellationToken ct=default)
 {
  await using var c=await OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT EXISTS(SELECT 1 FROM configuration_versions v,json_each(v.Payload,'$.tanks') t WHERE json_extract(t.value,'$.id')=$id)";cmd.Parameters.AddWithValue("$id",tankId.ToString());return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))==1;
 }
 public async Task InitializeAsync(CancellationToken ct=default, bool allowCapacityProfile=false)
 {
  await using var c=await OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT sqlite_version()";var native=(string)(await cmd.ExecuteScalarAsync(ct))!;
  if(!HasWalResetFix(native))throw new InvalidOperationException("SQLite原生版本未包含要求的WAL修复："+native);
  cmd.CommandText="PRAGMA user_version";var version=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
  if(version>CurrentSchema)throw new InvalidOperationException("数据库版本高于当前程序，拒绝降级打开");
  cmd.CommandText="PRAGMA journal_mode=WAL";var mode=Convert.ToString(await cmd.ExecuteScalarAsync(ct));if(mode!="wal")throw new InvalidOperationException("数据库未能启用WAL");
  using var transaction=c.BeginTransaction();cmd.Transaction=transaction;
  cmd.CommandText="PRAGMA user_version";version=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));if(version>CurrentSchema)throw new InvalidOperationException("数据库版本高于当前程序");
  if(version==0)
  {
   cmd.CommandText="""
    CREATE TABLE configuration(Id INTEGER PRIMARY KEY CHECK(Id=1),Revision INTEGER NOT NULL,Payload TEXT NOT NULL);
    CREATE TABLE configuration_versions(Revision INTEGER PRIMARY KEY,Payload TEXT NOT NULL,AtUtcMs INTEGER NOT NULL);
    CREATE TABLE operation_events(Id INTEGER PRIMARY KEY AUTOINCREMENT,AtUtcMs INTEGER NOT NULL,Category TEXT NOT NULL,ActorId TEXT NOT NULL,ActorName TEXT NOT NULL,TankId TEXT NULL,TankCode TEXT NULL,Content TEXT NOT NULL,Result TEXT NOT NULL,Details TEXT NOT NULL);
    CREATE INDEX IX_operation_events_TankId_AtUtcMs ON operation_events(TankId,AtUtcMs);
    CREATE INDEX IX_operation_events_AtUtcMs ON operation_events(AtUtcMs);
    CREATE TABLE users(Id TEXT PRIMARY KEY,Login TEXT NOT NULL,DisplayName TEXT NOT NULL,Role INTEGER NOT NULL,PasswordHash TEXT NOT NULL,Enabled INTEGER NOT NULL);
    CREATE UNIQUE INDEX IX_users_Login ON users(Login);
    INSERT INTO configuration VALUES(1,0,$initial);
    INSERT INTO configuration_versions VALUES(0,$initial,$at);
    PRAGMA user_version=1;
    """;
   cmd.Parameters.AddWithValue("$initial",JsonSerializer.Serialize(new SystemConfiguration(),Json));cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());await cmd.ExecuteNonQueryAsync(ct);
  }
  if(version<2){cmd.Parameters.Clear();cmd.CommandText=HistorySchema.Sql;await cmd.ExecuteNonQueryAsync(ct);}
  if(version<3){cmd.Parameters.Clear();cmd.CommandText=BusinessSchema.Sql;await cmd.ExecuteNonQueryAsync(ct);}
  if(version<4){cmd.Parameters.Clear();cmd.CommandText="ALTER TABLE history_samples ADD COLUMN Pressure1Pa INTEGER NULL; ALTER TABLE history_samples ADD COLUMN Pressure2Pa INTEGER NULL; ALTER TABLE history_samples ADD COLUMN PressureQualityBits INTEGER NOT NULL DEFAULT 45; PRAGMA user_version=4;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<5){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE alarm_runtime(TankId TEXT NOT NULL,RuleKey TEXT NOT NULL,CurrentId TEXT NULL,Payload TEXT NOT NULL,PRIMARY KEY(TankId,RuleKey)) WITHOUT ROWID; CREATE TABLE alarm_occurrences(Id TEXT PRIMARY KEY,TankId TEXT NOT NULL,RuleKey TEXT NOT NULL,ActivatedAtUtcMs INTEGER NOT NULL,State TEXT NOT NULL,Payload TEXT NOT NULL) WITHOUT ROWID; CREATE INDEX IX_alarm_occurrences_Tank_Time ON alarm_occurrences(TankId,ActivatedAtUtcMs); CREATE INDEX IX_alarm_occurrences_State_Time ON alarm_occurrences(State,ActivatedAtUtcMs); PRAGMA user_version=5;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<6){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE data_jobs(Id TEXT PRIMARY KEY,State TEXT NOT NULL,AtUtcMs INTEGER NOT NULL,Payload TEXT NOT NULL) WITHOUT ROWID;CREATE INDEX IX_data_jobs_State_Time ON data_jobs(State,AtUtcMs);CREATE TABLE data_snapshots(Id TEXT PRIMARY KEY,AtUtcMs INTEGER NOT NULL,Name TEXT NOT NULL,ConfigurationRevision INTEGER NOT NULL,ActorName TEXT NOT NULL,RowCount INTEGER NOT NULL,ValidLevels INTEGER NOT NULL,Payload TEXT NOT NULL,FOREIGN KEY(ConfigurationRevision) REFERENCES configuration_versions(Revision)) WITHOUT ROWID;CREATE INDEX IX_data_snapshots_Time ON data_snapshots(AtUtcMs);PRAGMA user_version=6;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<7){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE scheduled_data_runs(ScheduleId TEXT NOT NULL,LocalDate TEXT NOT NULL,JobId TEXT NOT NULL,PRIMARY KEY(ScheduleId,LocalDate),FOREIGN KEY(JobId) REFERENCES data_jobs(Id)) WITHOUT ROWID;PRAGMA user_version=7;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<8){cmd.Parameters.Clear();cmd.CommandText="ALTER TABLE history_samples ADD COLUMN Analog1MicroAmp INTEGER NULL;ALTER TABLE history_samples ADD COLUMN Analog2MicroAmp INTEGER NULL;ALTER TABLE history_samples ADD COLUMN AnalogQualityBits INTEGER NOT NULL DEFAULT 45;PRAGMA user_version=8;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<9){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE control_records(Id TEXT PRIMARY KEY,TankId TEXT NOT NULL,Cycle TEXT NOT NULL,State TEXT NOT NULL,AtUtcMs INTEGER NOT NULL,ConfigurationRevision INTEGER NOT NULL,Payload TEXT NOT NULL,FOREIGN KEY(ConfigurationRevision) REFERENCES configuration_versions(Revision)) WITHOUT ROWID;CREATE INDEX IX_control_records_Tank_Time ON control_records(TankId,AtUtcMs);CREATE INDEX IX_control_records_State ON control_records(State);PRAGMA user_version=9;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<10){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE wireless_tasks(Id TEXT PRIMARY KEY,TankId TEXT NOT NULL,Stage TEXT NOT NULL,HoldsTank INTEGER NOT NULL,AtUtcMs INTEGER NOT NULL,ConfigurationRevision INTEGER NOT NULL,Version INTEGER NOT NULL,Payload TEXT NOT NULL,FOREIGN KEY(ConfigurationRevision) REFERENCES configuration_versions(Revision)) WITHOUT ROWID;CREATE INDEX IX_wireless_tasks_Tank_Time ON wireless_tasks(TankId,AtUtcMs);CREATE UNIQUE INDEX IX_wireless_tasks_Active_Tank ON wireless_tasks(TankId) WHERE HoldsTank=1;PRAGMA user_version=10;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<11){cmd.Parameters.Clear();cmd.CommandText="ALTER TABLE users ADD COLUMN SecurityVersion INTEGER NOT NULL DEFAULT 0;PRAGMA user_version=11;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<12){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE motion_markers(Id TEXT PRIMARY KEY,AtUtcMs INTEGER NOT NULL,OwnerId TEXT NOT NULL,Kind TEXT NOT NULL,Payload TEXT NOT NULL) WITHOUT ROWID;PRAGMA user_version=12;";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<13){cmd.Parameters.Clear();cmd.CommandText="PRAGMA user_version=13";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<14){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE instrument_fault_state(TankId TEXT PRIMARY KEY,Payload TEXT NOT NULL) WITHOUT ROWID;PRAGMA user_version=14";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<15){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE role_permissions(Id INTEGER PRIMARY KEY CHECK(Id=1),Revision INTEGER NOT NULL,Payload TEXT NOT NULL);PRAGMA user_version=15";await cmd.ExecuteNonQueryAsync(ct);}
  // New parameterized command records cannot be parsed by older releases.
  if(version<16){cmd.Parameters.Clear();cmd.CommandText="PRAGMA user_version=16";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<17){cmd.Parameters.Clear();cmd.CommandText="PRAGMA user_version=17";await cmd.ExecuteNonQueryAsync(ct);}
  if(version<18){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE IF NOT EXISTS deleted_capacity_tables(TableId TEXT PRIMARY KEY,AtUtcMs INTEGER NOT NULL,ActorId TEXT NOT NULL,FOREIGN KEY(TableId) REFERENCES capacity_tables(Id)) WITHOUT ROWID;PRAGMA user_version=18";await cmd.ExecuteNonQueryAsync(ct);}
  cmd.Parameters.Clear();cmd.CommandText="SELECT Value FROM database_properties WHERE Key='profile'";
  var profile=Convert.ToString(await cmd.ExecuteScalarAsync(ct));
  if(profile!="operational"&&!(allowCapacityProfile&&profile=="capacity-test"))throw new InvalidOperationException("容量测试库禁止作为业务库启动");
  await transaction.CommitAsync(ct);
 }
 public static bool HasWalResetFix(string text)
 {
  if(!Version.TryParse(text,out var v))return false;
  return v>=new Version(3,51,3)||v.Major==3&&v.Minor==50&&v.Build>=7||v.Major==3&&v.Minor==44&&v.Build>=6;
 }
 public async Task<SystemConfiguration> GetAsync(CancellationToken ct=default)
 {
  await using var c=await OpenAsync(ct);await using var db=Context(c);var row=await db.Configuration.AsNoTracking().SingleAsync(ct);return JsonSerializer.Deserialize<SystemConfiguration>(row.Payload,Json)??throw new InvalidDataException("配置无法解析");
 }
 public async Task<SystemConfiguration> SaveAsync(SystemConfiguration config,AuditActor actor,CancellationToken ct=default)
 {
  actor.Require(AppPermission.ConfigurationEdit);ConfigurationRules.RequireValid(config);
  await using var c=await OpenAsync(ct);await using var db=Context(c);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var row=await db.Configuration.SingleAsync(ct);if(row.Revision!=config.Revision)throw new RevisionConflictException();
  await ValidateCapacityBindings(c,(SqliteTransaction)tx.GetDbTransaction(),config,ct);
  var before=JsonSerializer.Deserialize<SystemConfiguration>(row.Payload,Json)!;
  var next=config with {Revision=checked(config.Revision+1)};var payload=JsonSerializer.Serialize(next,Json);var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  row.Revision=next.Revision;row.Payload=payload;db.ConfigurationVersions.Add(new(){Revision=next.Revision,Payload=payload,AtUtcMs=now});
  var old=before.Tanks.ToDictionary(t=>t.Id);var updated=next.Tanks.ToDictionary(t=>t.Id);
  foreach(var t in next.Tanks)
  {
   old.TryGetValue(t.Id,out var previous);if(previous is not null&&JsonSerializer.Serialize(previous,Json)==JsonSerializer.Serialize(t,Json))continue;
   db.Events.Add(new(){AtUtcMs=now,Category="操作日志",ActorId=actor.AccountId,ActorName=actor.DisplayName,TankId=t.Id.ToString(),TankCode=t.Code,Content=(previous is null?"新增储罐 ":"修改储罐 ")+t.Code+" 配置",Result="已保存",Details=JsonSerializer.Serialize(new{Before=previous,After=t,Revision=next.Revision},Json)});
  }
  foreach(var t in before.Tanks.Where(t=>!updated.ContainsKey(t.Id)))db.Events.Add(new(){AtUtcMs=now,Category="操作日志",ActorId=actor.AccountId,ActorName=actor.DisplayName,TankId=t.Id.ToString(),TankCode=t.Code,Content="删除储罐 "+t.Code+" 配置",Result="已删除，历史及事件保留",Details=JsonSerializer.Serialize(t,Json)});
  db.Events.Add(new(){AtUtcMs=now,Category="操作日志",ActorId=actor.AccountId,ActorName=actor.DisplayName,Content="保存系统配置版本 "+next.Revision,Result="已保存",Details=JsonSerializer.Serialize(new{BeforeRevision=before.Revision,AfterRevision=next.Revision,ChannelsChanged=!before.Channels.SequenceEqual(next.Channels),HistoryIntervalSeconds=next.HistoryIntervalSeconds,RetentionMonths=next.RetentionMonths},Json)});
  try{await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);}catch(DbUpdateConcurrencyException){throw new RevisionConflictException();}
  return next;
 }
 public async Task<IReadOnlyList<OperationEvent>> QueryEventsAsync(EventQuery filter,CancellationToken ct=default)
 {
  if(filter.Limit is <1 or >1000||filter.StartUtcMs>filter.EndUtcMs)throw new ArgumentException("查询数量或时间范围无效");
  await using var c=await OpenAsync(ct);await using var db=Context(c);var query=db.Events.AsNoTracking();
  if(!string.IsNullOrWhiteSpace(filter.Category)&&filter.Category!="all")query=query.Where(e=>e.Category==filter.Category);
  if(!string.IsNullOrWhiteSpace(filter.TankId)&&filter.TankId!="all")query=query.Where(e=>e.TankId==filter.TankId);
  if(filter.StartUtcMs is {} start)query=query.Where(e=>e.AtUtcMs>=start);if(filter.EndUtcMs is {} end)query=query.Where(e=>e.AtUtcMs<=end);
  if(!string.IsNullOrWhiteSpace(filter.Actor))query=query.Where(e=>e.ActorName.Contains(filter.Actor));
  if(!string.IsNullOrWhiteSpace(filter.Keyword))query=query.Where(e=>e.Content.Contains(filter.Keyword)||e.Result.Contains(filter.Keyword)||(e.TankCode!=null&&e.TankCode.Contains(filter.Keyword)));
  return await query.OrderByDescending(e=>e.AtUtcMs).ThenByDescending(e=>e.Id).Take(filter.Limit).Select(e=>new OperationEvent(e.Id,e.AtUtcMs,e.Category,e.ActorId,e.ActorName,e.TankId,e.TankCode,e.Content,e.Result,e.Details)).ToListAsync(ct);
 }
 public async Task<StorageHealth> GetHealthAsync(CancellationToken ct=default)
 {
  await using var c=await OpenAsync(ct);async Task<object?> Scalar(string sql){await using var cmd=c.CreateCommand();cmd.CommandText=sql;return await cmd.ExecuteScalarAsync(ct);}
  return new((string)(await Scalar("SELECT sqlite_version()"))!,Convert.ToString(await Scalar("PRAGMA journal_mode"))!,Convert.ToInt32(await Scalar("PRAGMA synchronous")),Convert.ToInt32(await Scalar("PRAGMA user_version")));
 }
 public async Task<bool> NeedsSetupAsync(CancellationToken ct=default){await using var c=await OpenAsync(ct);await using var db=Context(c);return !await db.Users.AnyAsync(ct);}
 public async Task<UserAccount?> FindAsync(string login,CancellationToken ct=default)
 {
  await using var c=await OpenAsync(ct);await using var db=Context(c);var key=login.Trim().ToUpperInvariant();var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.Login==key,ct);return user is null?null:Account(user);
 }
 public async Task CreateInitialAdministratorAsync(UserAccount account,CancellationToken ct=default)
 {
  if(account.Role!=AccountRole.Administrator||!account.Enabled||string.IsNullOrWhiteSpace(account.PasswordHash)||string.IsNullOrWhiteSpace(account.Id)||string.IsNullOrWhiteSpace(account.Login))throw new ArgumentException("初始管理员数据无效");
  await using var c=await OpenAsync(ct);await using var db=Context(c);await using var tx=await db.Database.BeginTransactionAsync(ct);
  if(await db.Users.AnyAsync(ct))throw new InvalidOperationException("管理员已设置");
  db.Users.Add(new(){Id=account.Id,Login=account.Login.Trim().ToUpperInvariant(),DisplayName=account.DisplayName,PasswordHash=account.PasswordHash,Role=(int)AccountRole.Administrator,Enabled=true});
  db.Events.Add(new(){AtUtcMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Category="操作日志",ActorId=account.Id,ActorName=account.DisplayName,Content="完成首次管理员设置",Result="成功"});
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
 }
 public async Task BackupAsync(string destination,CancellationToken ct=default)
 {
  if(!Path.IsPathFullyQualified(destination)||File.Exists(destination))throw new ArgumentException("备份应使用未存在的绝对文件路径");
  await using var source=await OpenAsync(ct);await using var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=destination,Pooling=false}.ToString());await target.OpenAsync(ct);ct.ThrowIfCancellationRequested();source.BackupDatabase(target);
  await using var cmd=target.CreateCommand();cmd.CommandText="PRAGMA integrity_check";if(Convert.ToString(await cmd.ExecuteScalarAsync(ct))!="ok")throw new InvalidDataException("备份完整性检查未通过");
 }
 public async Task RecordAsync(string actorId,string actorName,string content,string result,CancellationToken ct=default)
 {
  await using var c=await OpenAsync(ct);await using var db=Context(c);db.Events.Add(new(){AtUtcMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Category="系统事件",ActorId=actorId,ActorName=actorName,Content=content,Result=result});await db.SaveChangesAsync(ct);
 }
 public static void ReleasePools()=>SqliteConnection.ClearAllPools();
}
