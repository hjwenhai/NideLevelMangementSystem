using System.IO.Compression;
using System.Text.Json;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class OfflineRestoreTests
{
    private static readonly AuditActor Admin=new("a","恢复验证",AccountRole.Administrator);
    private static async Task<(string Root,SqliteConfigurationStore Store,string Zip)> Prepare(StoreFixture fixture,bool badHash=false)
    {
        var root=Path.Combine(fixture.Root,"business");Directory.CreateDirectory(root);var store=new SqliteConfigurationStore(Path.Combine(root,"level-manager.db"));await store.InitializeAsync();await store.CreateInitialAdministratorAsync(new("a","admin","恢复验证",AccountRole.Administrator,"opaque-test-hash",true));await store.SaveAsync(Fixtures.Configuration(),Admin);
        var db=Path.Combine(fixture.Root,"backup.db");var copy=await OnlineDatabaseBackup.CreateAsync(Path.Combine(root,"level-manager.db"),db);var zip=Path.Combine(fixture.Root,"backup.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){archive.CreateEntryFromFile(db,"业务数据备份.db",CompressionLevel.NoCompression);using var writer=new StreamWriter(archive.CreateEntry("备份说明.json").Open());await writer.WriteAsync(JsonSerializer.Serialize(new DatabaseBackupManifest(ProductTrust.ProductId,copy.SchemaVersion,copy.ConfigurationRevision,copy.SnapshotAtUtc,copy.Bytes,badHash?new string('0',64):copy.Sha256),LicenseVerifier.Json));}
        return(root,store,zip);
    }
    [Fact]public async Task RestorePreservesOriginalDataAndAuthorizationAndInvalidatesOldSessionKeys()
    {
        await using var f=new StoreFixture();var setup=await Prepare(f);var config=await setup.Store.GetAsync();await setup.Store.SaveAsync(config with{ReadingExpirySeconds=31},Admin);var auth=setup.Root+".authorization";Directory.CreateDirectory(auth);await File.WriteAllTextAsync(Path.Combine(auth,"clock.dat"),"external protected state unchanged");var keys=Path.Combine(setup.Root,"keys");Directory.CreateDirectory(keys);await File.WriteAllTextAsync(Path.Combine(keys,"session.xml"),"old session key fixture");
        var result=await OfflineDatabaseRestore.RestoreAsync(setup.Zip,setup.Root,true);Assert.True(result.RequiresFieldReview);Assert.Equal(1,result.ConfigurationRevision);Assert.Equal("external protected state unchanged",await File.ReadAllTextAsync(Path.Combine(auth,"clock.dat")));Assert.True(File.Exists(Path.Combine(result.SafetyDirectory,"level-manager.db")));Assert.True(File.Exists(Path.Combine(result.SafetyDirectory,"old-session-keys","session.xml")));Assert.False(Directory.Exists(keys));Assert.True(File.Exists(Path.Combine(setup.Root,"restoration-review.required")));Assert.Equal(1,(await new SqliteConfigurationStore(Path.Combine(setup.Root,"level-manager.db")).GetAsync()).Revision);
    }
    [Fact]public async Task CorruptHashOrARunningServiceNeverReplacesCurrentData()
    {
        await using var f=new StoreFixture();var setup=await Prepare(f,true);await Assert.ThrowsAsync<InvalidDataException>(()=>OfflineDatabaseRestore.RestoreAsync(setup.Zip,setup.Root,true));Assert.Equal(1,(await setup.Store.GetAsync()).Revision);
        using var service=new FileStream(Path.Combine(setup.Root,"service.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);await Assert.ThrowsAnyAsync<IOException>(()=>OfflineDatabaseRestore.RestoreAsync(setup.Zip,setup.Root,true));await Assert.ThrowsAsync<ArgumentException>(()=>OfflineDatabaseRestore.RestoreAsync(setup.Zip,setup.Root,false));Assert.Equal(1,(await setup.Store.GetAsync()).Revision);
    }
    [Theory][InlineData(true)][InlineData(false)]public async Task ManifestCannotMisstateSchemaOrConfigurationEvenWithValidDatabaseHash(bool schema)
    {
        await using var f=new StoreFixture();var setup=await Prepare(f);using(var zip=ZipFile.Open(setup.Zip,ZipArchiveMode.Update)){var entry=zip.GetEntry("备份说明.json")!;DatabaseBackupManifest value;using(var reader=new StreamReader(entry.Open()))value=JsonSerializer.Deserialize<DatabaseBackupManifest>(await reader.ReadToEndAsync(),LicenseVerifier.Json)!;entry.Delete();using var writer=new StreamWriter(zip.CreateEntry("备份说明.json").Open());await writer.WriteAsync(JsonSerializer.Serialize(schema?value with{SchemaVersion=1}:value with{ConfigurationRevision=99},LicenseVerifier.Json));}await Assert.ThrowsAsync<InvalidDataException>(()=>OfflineDatabaseRestore.RestoreAsync(setup.Zip,setup.Root,true));Assert.Equal(1,(await setup.Store.GetAsync()).Revision);Assert.False(File.Exists(Path.Combine(setup.Root,"restoration-review.required")));
    }
}
