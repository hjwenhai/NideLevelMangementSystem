using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class OnlineBackupTests
{
    private static readonly AuditActor Admin=new("a","备份验证",AccountRole.Administrator);
    [Fact]public async Task IncrementalBackupKeepsOneSnapshotWhileIndependentConfigurationWritesContinue()
    {
        await using var f=new StoreFixture();await f.Initialize();var config=await f.Store.SaveAsync(Fixtures.Configuration(2),Admin);
        await f.Scalar("CREATE TABLE backup_probe(Id INTEGER PRIMARY KEY,Payload BLOB);WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<2048)INSERT INTO backup_probe SELECT x,randomblob(4096) FROM n;");
        var writes=0;var destination=Path.Combine(f.Root,"incremental.db");var result=await OnlineDatabaseBackup.CreateAsync(f.PathToDb,destination,async p=>{if(writes==0){writes++;config=await f.Store.SaveAsync(config with{ReadingExpirySeconds=31},Admin);}});
        Assert.Equal(1,writes);Assert.Equal(2,(await f.Store.GetAsync()).Revision);var restored=new SqliteConfigurationStore(destination);await restored.InitializeAsync();Assert.Equal(1,(await restored.GetAsync()).Revision);Assert.Equal(1,result.ConfigurationRevision);Assert.Equal(64,result.Sha256.Length);Assert.True(result.Bytes>8*1024*1024);Assert.Equal(SqliteConfigurationStore.CurrentSchema,result.SchemaVersion);
    }
    [Fact]public async Task ExistingTargetIsNeverOverwrittenAndCancellationDoesNotPublishAPartialDatabase()
    {
        await using var f=new StoreFixture();await f.Initialize();var target=Path.Combine(f.Root,"existing.db");await File.WriteAllTextAsync(target,"leave this file");await Assert.ThrowsAnyAsync<IOException>(()=>OnlineDatabaseBackup.CreateAsync(f.PathToDb,target));Assert.Equal("leave this file",await File.ReadAllTextAsync(target));
        using var cancel=new CancellationTokenSource();cancel.Cancel();var partial=Path.Combine(f.Root,"cancelled.db");await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>OnlineDatabaseBackup.CreateAsync(f.PathToDb,partial,ct:cancel.Token));Assert.False(File.Exists(partial));
    }
    [Fact]public async Task CapacityProfileRequiresExplicitHarnessFlagAndCannotEnterNormalBackup()
    {
        await using var f=new StoreFixture();await f.Initialize();await f.Scalar("UPDATE database_properties SET Value='capacity-test' WHERE Key='profile'");var refused=Path.Combine(f.Root,"refused.db");await Assert.ThrowsAsync<InvalidDataException>(()=>OnlineDatabaseBackup.CreateAsync(f.PathToDb,refused));Assert.False(File.Exists(refused));var allowed=Path.Combine(f.Root,"harness.db");var copy=await OnlineDatabaseBackup.CreateAsync(f.PathToDb,allowed,allowCapacityProfile:true);Assert.True(copy.Bytes>0);
    }
}
