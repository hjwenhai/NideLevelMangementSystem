using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;

internal static class LeapWindowBenchmark
{
 public static async Task<int> Run(string[] args)
 {
  string Option(string key){var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException("缺少参数 "+key);}
  var original=Option("--source");var output=Option("--output");
  if(!Path.IsPathFullyQualified(original)||!File.Exists(original)||!Path.IsPathFullyQualified(output)||Directory.Exists(output))throw new ArgumentException("请使用容量样本和新的绝对输出目录");
  var bytes=new FileInfo(original).Length;if(new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace<bytes*4)throw new IOException("366天验证空间不足");
  Directory.CreateDirectory(output);var watch=Stopwatch.StartNew();var source=Path.Combine(output,"isolated-366-days.db");Console.WriteLine("cloning isolated capacity sample for 366-day window");File.Copy(original,source);
  var store=new SqliteConfigurationStore(source);await store.InitializeAsync(allowCapacityProfile:true);var config=await store.GetAsync();if(config.Tanks.Count!=128)throw new InvalidDataException("需要128罐样本");
  var from=DateTimeOffset.Parse("2025-01-01T00:00:00Z").ToUnixTimeMilliseconds();const long day=86400000L;var finish=from+366*day;
  await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=source,Pooling=false,DefaultTimeout=300}.ToString()))
  {
   await db.OpenAsync();await using var cmd=db.CreateCommand();cmd.CommandText="SELECT Value FROM database_properties WHERE Key='profile'";if(Convert.ToString(await cmd.ExecuteScalarAsync())!="capacity-test")throw new InvalidOperationException("仅允许合成容量样本，禁止业务库");
   cmd.CommandText="SELECT COUNT(*) FROM history_samples WHERE AtUtcMs >= $from AND AtUtcMs < $to";cmd.Parameters.AddWithValue("$from",from+365*day);cmd.Parameters.AddWithValue("$to",finish);if(Convert.ToInt64(await cmd.ExecuteScalarAsync())!=0)throw new InvalidDataException("第366天已有数据，拒绝覆盖");cmd.Parameters.Clear();
   List<string> columns=[];cmd.CommandText="PRAGMA table_info(history_samples)";await using(var reader=await cmd.ExecuteReaderAsync()){while(await reader.ReadAsync())columns.Add(reader.GetString(1));}
   string Quote(string name)=>"\""+name.Replace("\"","\"\"")+"\"";
   var shifted=columns.Select(c=>c is "AtUtcMs" or "ReceivedAtUtcMs" or "SourceAtUtcMs" or "AverageCompletedAtUtcMs"?Quote(c)+" + $shift":Quote(c));
   cmd.CommandText="PRAGMA foreign_keys=ON;PRAGMA synchronous=FULL";await cmd.ExecuteNonQueryAsync();
   cmd.CommandText="INSERT INTO history_samples("+string.Join(',',columns.Select(Quote))+") SELECT "+string.Join(',',shifted)+" FROM history_samples WHERE AtUtcMs >= $from AND AtUtcMs < $to";cmd.Parameters.AddWithValue("$from",from);cmd.Parameters.AddWithValue("$to",from+day);cmd.Parameters.AddWithValue("$shift",365*day);var added=await cmd.ExecuteNonQueryAsync();if(added!=184320)throw new InvalidDataException("第366天必须完整增加184320条，实际"+added);cmd.Parameters.Clear();
   cmd.CommandText="SELECT COUNT(*) FROM history_samples WHERE AtUtcMs >= $from AND AtUtcMs < $to";cmd.Parameters.AddWithValue("$from",from);cmd.Parameters.AddWithValue("$to",finish);if(Convert.ToInt64(await cmd.ExecuteScalarAsync())!=67461120)throw new InvalidDataException("366天完整行数不符");cmd.Parameters.Clear();cmd.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";await cmd.ExecuteNonQueryAsync();
  }
  Console.WriteLine("366 full days verified: 67,461,120 rows; creating verified backup and actual restored copy");var backup=Path.Combine(output,"capacity-backup.db");var backed=await OnlineDatabaseBackup.CreateAsync(source,backup,allowCapacityProfile:true);var restored=Path.Combine(output,"capacity-restored.db");File.Copy(backup,restored);
  await using(var file=File.OpenRead(restored)){if(Convert.ToHexString(await SHA256.HashDataAsync(file))!=backed.Sha256)throw new InvalidDataException("实际恢复副本SHA256不一致");}
  await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=restored,Pooling=false,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=300}.ToString())){await db.OpenAsync();await using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA integrity_check";if(Convert.ToString(await cmd.ExecuteScalarAsync())!="ok")throw new InvalidDataException("恢复副本完整性失败");cmd.CommandText="PRAGMA foreign_key_check";await using var reader=await cmd.ExecuteReaderAsync();if(await reader.ReadAsync())throw new InvalidDataException("恢复副本外键失败");}
  Console.WriteLine("exporting actual 366-day single-tank Excel with all 16 ATS points");var excel=await new HistoryExcelExporter(new SqliteHistoryStore(restored)).ExportAsync(Path.Combine(output,"excel"),new(from,finish,config.Tanks[0].Id,true),config);if(excel.Rows!=527040)throw new InvalidDataException("366天单罐导出行数不符");
  await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,schemaVersion=SqliteConfigurationStore.CurrentSchema,days=366,tanks=128,expectedHistoryRows=67461120,addedDayRows=184320,backup=backed,restoredSha256Matches=true,restoredIntegrity="ok",restoredForeignKeys="ok",annualExcel=excel,elapsedSeconds=watch.Elapsed.TotalSeconds,peakProcessBytes=Process.GetCurrentProcess().PeakWorkingSet64,realDeviceAccess=false},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS: 366-day final schema, actual backup/restore and annual Excel");return 0;
 }
}
