using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;
namespace Naide.LevelManager.Infrastructure;
public sealed record DatabaseBackupProgress(int CopiedPages,int TotalPages);
public sealed record DatabaseBackupResult(long Bytes,string Sha256,int SchemaVersion,long ConfigurationRevision,DateTimeOffset SnapshotAtUtc);
public static class OnlineDatabaseBackup
{
    // A WAL read transaction pins one consistent source snapshot. Each native step
    // releases its connection mutex; the independent writer remains usable.
    public static async Task<DatabaseBackupResult> CreateAsync(string sourcePath,string destination,
        Func<DatabaseBackupProgress,Task>? progress=null,CancellationToken ct=default,bool allowCapacityProfile=false)
    {
        if(!Path.IsPathFullyQualified(sourcePath)||!Path.IsPathFullyQualified(destination)||!File.Exists(sourcePath)||string.Equals(Path.GetFullPath(sourcePath),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("备份需使用有效源库和新的绝对目标文件路径");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);using(var reserved=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}
        var owned=true;
        try
        {
            int schema;long revision;var at=DateTimeOffset.UtcNow;
            await using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=sourcePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=5}.ToString()))
            await using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=destination,Pooling=false,DefaultTimeout=5}.ToString()))
            {
                await source.OpenAsync(ct);await target.OpenAsync(ct);using var snapshot=source.BeginTransaction(deferred:true);
                await using var cmd=source.CreateCommand();cmd.Transaction=snapshot;cmd.CommandText="SELECT Value FROM database_properties WHERE Key='profile'";var profile=Convert.ToString(await cmd.ExecuteScalarAsync(ct));if(profile!="operational"&&!(allowCapacityProfile&&profile=="capacity-test"))throw new InvalidDataException("仅备份本系统业务库，不接受容量样机库");
                cmd.CommandText="PRAGMA journal_mode";if(Convert.ToString(await cmd.ExecuteScalarAsync(ct))!="wal")throw new InvalidOperationException("在线分步备份要求业务库使用WAL");
                cmd.CommandText="PRAGMA user_version";schema=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));if(schema<1||schema>SqliteConfigurationStore.CurrentSchema)throw new InvalidDataException("业务库版本不适用");
                cmd.CommandText="SELECT Revision FROM configuration WHERE Id=1";revision=Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                var backup=raw.sqlite3_backup_init(target.Handle!,"main",source.Handle!,"main");if(backup is null||backup.IsInvalid)throw new IOException("无法启动SQLite分步备份");var watch=Stopwatch.StartNew();var report=Stopwatch.StartNew();var finished=false;
                try
                {
                    while(true)
                    {
                        ct.ThrowIfCancellationRequested();if(watch.Elapsed>TimeSpan.FromHours(1))throw new TimeoutException("在线备份超过一小时，未发布备份文件");
                        var result=raw.sqlite3_backup_step(backup,256);var remaining=raw.sqlite3_backup_remaining(backup);var total=raw.sqlite3_backup_pagecount(backup);
                        if(progress is not null&&(report.Elapsed>TimeSpan.FromSeconds(1)||result==raw.SQLITE_DONE||watch.Elapsed<TimeSpan.FromMilliseconds(100))){await progress(new(total-remaining,total));report.Restart();}
                        if(result==raw.SQLITE_DONE){finished=true;break;}
                        if(result is not(raw.SQLITE_OK or raw.SQLITE_BUSY or raw.SQLITE_LOCKED))throw new IOException("SQLite分步备份失败，状态码 "+result);
                        await Task.Delay(result==raw.SQLITE_OK?1:50,ct);
                    }
                }
                finally{var result=raw.sqlite3_backup_finish(backup);if(finished&&result!=raw.SQLITE_OK)throw new IOException("SQLite备份提交失败，状态码 "+result);}
                await snapshot.CommitAsync(ct);
                await using var verify=target.CreateCommand();verify.CommandText="PRAGMA integrity_check";if(Convert.ToString(await verify.ExecuteScalarAsync(ct))!="ok")throw new InvalidDataException("备份完整性检查失败");verify.CommandText="PRAGMA foreign_key_check";await using(var reader=await verify.ExecuteReaderAsync(ct)){if(await reader.ReadAsync(ct))throw new InvalidDataException("备份外键一致性检查失败");}
                verify.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";await verify.ExecuteNonQueryAsync(ct);
            }
            await using var file=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);var hash=Convert.ToHexString(await SHA256.HashDataAsync(file,ct));owned=false;return new(file.Length,hash,schema,revision,at);
        }
        finally
        {
            if(owned)foreach(var suffix in new[]{"","-wal","-shm","-journal"}){var path=destination+suffix;if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0)File.Delete(path);}
        }
    }
}
