using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;
public sealed record DatabaseRestoreResult(Guid RestoreId,string SafetyDirectory,int Tanks,long ConfigurationRevision,bool RequiresFieldReview);
public static class OfflineDatabaseRestore
{
    public static async Task<DatabaseRestoreResult> RestoreAsync(string backupFile,string dataRoot,bool confirmed,CancellationToken ct=default)
    {
        if(!confirmed)throw new ArgumentException("恢复会替换当前业务数据，请明确确认后执行");if(!Path.IsPathFullyQualified(backupFile)||!Path.IsPathFullyQualified(dataRoot)||!File.Exists(backupFile)||!Directory.Exists(dataRoot))throw new ArgumentException("请使用现有备份文件及业务目录的绝对路径");
        dataRoot=Path.GetFullPath(dataRoot);if((new DirectoryInfo(dataRoot).Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("业务恢复目录不能是重解析链接");
        // The running host holds this same file open. Never stop it implicitly.
        using var serviceLock=new FileStream(Path.Combine(dataRoot,"service.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var id=Guid.NewGuid();var staging=Path.Combine(dataRoot,"restore-stage-"+id.ToString("N"));var safety=Path.Combine(dataRoot,"restore-safety-"+id.ToString("N"));Directory.CreateDirectory(staging);Directory.CreateDirectory(safety);
        var stagedDb=Path.Combine(staging,"level-manager.db");var current=Path.Combine(dataRoot,"level-manager.db");var installed=false;var moved=new List<(string Original,string Saved)>();var keysMoved=false;
        string Child(string path){var full=Path.GetFullPath(path);if(!full.StartsWith(dataRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("恢复操作路径越界");return full;}
        DatabaseBackupManifest? manifest=null;
        try
        {
            await using(var input=new FileStream(backupFile,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true))
            using(var zip=new ZipArchive(input,ZipArchiveMode.Read))
            {
                if(zip.Entries.Count!=2||zip.Entries.Count(e=>e.FullName=="业务数据备份.db")!=1||zip.Entries.Count(e=>e.FullName=="备份说明.json")!=1)throw new InvalidDataException("备份包文件清单无效");var db=zip.GetEntry("业务数据备份.db")!;var description=zip.GetEntry("备份说明.json")!;
                if(description.Length is <1 or >65536||db.Length is <100 or >68719476736)throw new InvalidDataException("备份大小超出允许范围");
                using(var reader=new StreamReader(description.Open()))manifest=JsonSerializer.Deserialize<DatabaseBackupManifest>(await reader.ReadToEndAsync(ct),LicenseVerifier.Json)??throw new InvalidDataException("备份说明为空");
                if(manifest.ProductId!=ProductTrust.ProductId||manifest.AuthorizationIncluded||manifest.Bytes!=db.Length||manifest.SchemaVersion is <1||manifest.SchemaVersion>SqliteConfigurationStore.CurrentSchema||manifest.Sha256 is null||manifest.Sha256.Length!=64)throw new InvalidDataException("备份说明、产品或版本无效");
                if(new DriveInfo(Path.GetPathRoot(dataRoot)!).AvailableFreeSpace<db.Length+1024L*1024*1024)throw new IOException("空间不足以安全展开恢复文件并保留原业务数据");
                await using(var target=new FileStream(stagedDb,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true))
                await using(var source=db.Open())
                {var buffer=new byte[1024*1024];long count=0;int read;while((read=await source.ReadAsync(buffer,ct))>0){count+=read;if(count>manifest.Bytes)throw new InvalidDataException("备份内容超出声明长度");await target.WriteAsync(buffer.AsMemory(0,read),ct);}if(count!=manifest.Bytes)throw new InvalidDataException("备份数据库长度不完整");await target.FlushAsync(ct);target.Flush(true);}
                await using(var file=File.OpenRead(stagedDb)){if(Convert.ToHexString(await SHA256.HashDataAsync(file,ct))!=manifest.Sha256)throw new InvalidDataException("备份SHA256校验失败，未替换当前数据");}
            }
            await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=stagedDb,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
            {
                await db.OpenAsync(ct);await using var check=db.CreateCommand();check.CommandText="PRAGMA user_version";if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))!=manifest!.SchemaVersion)throw new InvalidDataException("备份说明与数据库结构版本不一致");check.CommandText="PRAGMA trusted_schema=OFF;PRAGMA integrity_check";if(Convert.ToString(await check.ExecuteScalarAsync(ct))!="ok")throw new InvalidDataException("恢复文件完整性检查失败");check.CommandText="PRAGMA foreign_key_check";await using var rows=await check.ExecuteReaderAsync(ct);if(await rows.ReadAsync(ct))throw new InvalidDataException("恢复文件外键检查失败");
            }
            var prepared=new SqliteConfigurationStore(stagedDb);await prepared.InitializeAsync(ct);var config=await prepared.GetAsync(ct);ConfigurationRules.RequireValid(config);if(config.Revision!=manifest!.ConfigurationRevision)throw new InvalidDataException("备份说明与配置版本不一致");if(await prepared.NeedsSetupAsync(ct))throw new InvalidDataException("恢复包没有账号，不能作为完整业务恢复点");
            SqliteConfigurationStore.ReleasePools();
            await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=stagedDb,Pooling=false}.ToString())){await db.OpenAsync(ct);await using var checkpoint=db.CreateCommand();checkpoint.CommandText="DELETE FROM instrument_fault_state;PRAGMA wal_checkpoint(TRUNCATE)";await checkpoint.ExecuteNonQueryAsync(ct);}
            ct.ThrowIfCancellationRequested();
            foreach(var suffix in new[]{"","-wal","-shm"})
            {var from=Child(current+suffix);var to=Child(Path.Combine(safety,"level-manager.db"+suffix));if(File.Exists(from)){File.Move(from,to);moved.Add((from,to));}}
            File.Move(stagedDb,current);installed=true;
            var keys=Child(Path.Combine(dataRoot,"keys"));if(Directory.Exists(keys))
            {if((new DirectoryInfo(keys).Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("登录密钥目录为重解析链接，拒绝移动");Directory.Move(keys,Child(Path.Combine(safety,"old-session-keys")));keysMoved=true;}
            // This request is transferred into the separate protected state at startup.
            // It never resets the authorization files next to the business directory.
            await File.WriteAllTextAsync(Child(Path.Combine(dataRoot,"restoration-review.required")),JsonSerializer.Serialize(new{RestoreId=id,RestoredAtUtc=DateTimeOffset.UtcNow,SourceBackup=Path.GetFileName(backupFile)}),CancellationToken.None);
            var result=new DatabaseRestoreResult(id,safety,config.Tanks.Count,config.Revision,true);await File.WriteAllTextAsync(Path.Combine(safety,"restore-result.json"),JsonSerializer.Serialize(result,LicenseVerifier.Json),CancellationToken.None);return result;
        }
        catch
        {
            SqliteConfigurationStore.ReleasePools();
            if(installed&&File.Exists(current))File.Move(current,Child(Path.Combine(staging,"uncommitted-restored.db")));
            foreach(var pair in moved.AsEnumerable().Reverse())if(File.Exists(pair.Saved))File.Move(pair.Saved,pair.Original);
            if(keysMoved)Directory.Move(Child(Path.Combine(safety,"old-session-keys")),Child(Path.Combine(dataRoot,"keys")));
            throw;
        }
        // Staging and safety evidence are retained; no recursive delete is used here.
    }
}
