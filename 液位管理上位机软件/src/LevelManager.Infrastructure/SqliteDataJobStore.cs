using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Infrastructure;
public sealed class SqliteDataJobStore(string databasePath) : IDataJobStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,ForeignKeys=true,DefaultTimeout=10}.ToString());
        try{await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous=FULL";await cmd.ExecuteNonQueryAsync(ct);return c;}
        catch{await c.DisposeAsync();throw;}
    }
    private static async Task Save(SqliteConnection c,SqliteTransaction tx,DataJob job,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO data_jobs(Id,State,AtUtcMs,Payload) VALUES($id,$state,$at,$payload) ON CONFLICT(Id) DO UPDATE SET State=excluded.State,Payload=excluded.Payload";
        cmd.Parameters.AddWithValue("$id",job.Id.ToString());cmd.Parameters.AddWithValue("$state",job.State.ToString());cmd.Parameters.AddWithValue("$at",job.CreatedAtUtc.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(job,Json));await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task Audit(SqliteConnection c,SqliteTransaction tx,DataJob job,string text,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,Content,Result,Details) VALUES($at,$category,$actor,$name,$text,$result,$details)";
        cmd.Parameters.AddWithValue("$at",(job.FinishedAtUtc??job.CreatedAtUtc).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$category",job.State is DataJobState.Failed or DataJobState.Interrupted?"任务错误":"操作日志");cmd.Parameters.AddWithValue("$actor",job.ActorId);cmd.Parameters.AddWithValue("$name",job.ActorName);cmd.Parameters.AddWithValue("$text",text);cmd.Parameters.AddWithValue("$result",job.Error??(job.State==DataJobState.Completed?"完成":"已受理"));cmd.Parameters.AddWithValue("$details",JsonSerializer.Serialize(new{EventCode="dataJob."+job.Kind+"."+job.State,job.Id,job.Rows,job.FileName,job.SnapshotId},Json));await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> CreateScheduledAsync(Guid scheduleId,string date,DataJob job,CancellationToken ct=default)
    {
        if(job.Id==Guid.Empty||scheduleId==Guid.Empty||job.RequestJson.Length>30000||job.Kind==DataJobKind.AverageTemperatureDispatch&&job.State!=DataJobState.Running||job.Kind!=DataJobKind.AverageTemperatureDispatch&&(job.Kind!=DataJobKind.Snapshot||job.State!=DataJobState.Queued))throw new ArgumentException("定时任务受理类型或状态无效");
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT count(*) FROM scheduled_data_runs WHERE ScheduleId=$schedule AND LocalDate=$date";cmd.Parameters.AddWithValue("$schedule",scheduleId.ToString());cmd.Parameters.AddWithValue("$date",date);
        if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0)return false;
        cmd.CommandText="SELECT count(*) FROM data_jobs WHERE State IN ('Queued','Running')";if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>=3)return false;
        await Save(c,tx,job,ct);cmd.CommandText="INSERT INTO scheduled_data_runs(ScheduleId,LocalDate,JobId) VALUES($schedule,$date,$job)";cmd.Parameters.AddWithValue("$job",job.Id.ToString());await cmd.ExecuteNonQueryAsync(ct);await Audit(c,tx,job,job.Kind==DataJobKind.AverageTemperatureDispatch?"定时申请平均测温（不等于测温完成）":"定时抓取数据快照",ct);await tx.CommitAsync(ct);return true;
    }
    public async Task RecoverInterruptedAsync(DateTimeOffset now,CancellationToken ct=default)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Payload FROM data_jobs WHERE State IN ('Queued','Running')";
        List<DataJob> jobs=[];await using(var reader=await cmd.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))jobs.Add(JsonSerializer.Deserialize<DataJob>(reader.GetString(0),Json)!);
        foreach(var job in jobs){var ended=job with{State=DataJobState.Interrupted,FinishedAtUtc=now,Error="服务退出前未完成，请重新发起；未自动重放"};await Save(c,tx,ended,ct);await Audit(c,tx,ended,"数据任务因服务退出未完成",ct);}await tx.CommitAsync(ct);
    }
    public async Task CreateAsync(DataJob job,CancellationToken ct=default)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT count(*) FROM data_jobs WHERE State IN ('Queued','Running')";
        if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>=3)throw new InvalidOperationException("已有3个数据任务，请等待或取消后再发起");
        if(job.State!=DataJobState.Queued||job.Id==Guid.Empty||job.RequestJson.Length>30000)throw new ArgumentException("数据任务无效");
        cmd.CommandText="SELECT count(*) FROM data_jobs WHERE Id=$id";cmd.Parameters.AddWithValue("$id",job.Id.ToString());if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))!=0)throw new ArgumentException("任务编号已存在");
        await Save(c,tx,job,ct);await Audit(c,tx,job,job.Kind==DataJobKind.HistoryExport?"申请历史数据导出":"申请数据快照",ct);await tx.CommitAsync(ct);
    }
    public async Task<DataJob?> ClaimAsync(DateTimeOffset now,CancellationToken ct=default)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Payload FROM data_jobs WHERE State='Queued' ORDER BY AtUtcMs,Id LIMIT 1";
        var payload=await cmd.ExecuteScalarAsync(ct) as string;if(payload is null)return null;var job=JsonSerializer.Deserialize<DataJob>(payload,Json)! with{State=DataJobState.Running,StartedAtUtc=now};await Save(c,tx,job,ct);await tx.CommitAsync(ct);return job;
    }
    public async Task UpdateAsync(DataJob job,CancellationToken ct=default)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await Save(c,tx,job,ct);if(job.State is not DataJobState.Running)await Audit(c,tx,job,job.Kind==DataJobKind.HistoryExport?"历史数据导出":job.Kind==DataJobKind.AverageTemperatureDispatch?"定时平均测温申请":"数据快照",ct);await tx.CommitAsync(ct);
    }
    public async Task<DataJob?> GetAsync(Guid id,CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM data_jobs WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());return await cmd.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<DataJob>(json,Json):null;
    }
    public async Task<IReadOnlyList<DataJob>> ListAsync(CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM data_jobs ORDER BY AtUtcMs DESC,Id LIMIT 100";List<DataJob> jobs=[];await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))jobs.Add(JsonSerializer.Deserialize<DataJob>(reader.GetString(0),Json)!);return jobs;
    }
    public async Task CompleteSnapshotAsync(DataJob job,DataSnapshot snapshot,CancellationToken ct=default)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO data_snapshots(Id,AtUtcMs,Name,ConfigurationRevision,ActorName,RowCount,ValidLevels,Payload) VALUES($id,$at,$name,$revision,$actor,$count,$valid,$payload)";
        cmd.Parameters.AddWithValue("$id",snapshot.Id.ToString());cmd.Parameters.AddWithValue("$at",snapshot.AtUtc.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$name",snapshot.Name);cmd.Parameters.AddWithValue("$revision",snapshot.ConfigurationRevision);cmd.Parameters.AddWithValue("$actor",snapshot.ActorName);cmd.Parameters.AddWithValue("$count",snapshot.Readings.Count);cmd.Parameters.AddWithValue("$valid",snapshot.Readings.Count(r=>r.LevelMm.IsValid));cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(snapshot,Json));await cmd.ExecuteNonQueryAsync(ct);
        await Save(c,tx,job,ct);await Audit(c,tx,job,"保存数据快照",ct);await tx.CommitAsync(ct);
    }
    public async Task<DataSnapshot?> SnapshotAsync(Guid id,CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM data_snapshots WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());return await cmd.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<DataSnapshot>(json,Json):null;
    }
    public async Task<IReadOnlyList<DataSnapshotSummary>> SnapshotsAsync(CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Id,Name,AtUtcMs,ConfigurationRevision,ActorName,RowCount,ValidLevels FROM data_snapshots ORDER BY AtUtcMs DESC LIMIT 100";List<DataSnapshotSummary> rows=[];await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(new(Guid.Parse(reader.GetString(0)),reader.GetString(1),DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),reader.GetInt64(3),reader.GetString(4),reader.GetInt32(5),reader.GetInt32(6)));return rows;
    }
}
