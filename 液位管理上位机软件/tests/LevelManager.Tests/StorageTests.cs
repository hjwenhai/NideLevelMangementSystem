using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;
[assembly:CollectionBehavior(DisableTestParallelization=true)]
namespace Naide.LevelManager.Tests;
public sealed class StoreFixture:IAsyncDisposable
{
 public string Root{get;}=Path.Combine(Environment.GetEnvironmentVariable("LEVEL_TEST_ROOT")??Path.Combine(AppContext.BaseDirectory,"test-data"),Guid.NewGuid().ToString("N"));
 public string PathToDb=>Path.Combine(Root,"test.db");
 public SqliteConfigurationStore Store{get;}
 public StoreFixture(){Store=new(PathToDb);}
 public async Task Initialize()=>await Store.InitializeAsync();
 public ValueTask DisposeAsync(){SqliteConfigurationStore.ReleasePools();var path=Path.GetFullPath(Root);var parent=Path.GetFullPath(Path.GetDirectoryName(Root)!)+Path.DirectorySeparatorChar;if(!path.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("测试目录越界");if(Directory.Exists(path))Directory.Delete(path,true);return ValueTask.CompletedTask;}
 public async Task<object?> Scalar(string sql){await using var c=new SqliteConnection("Data Source="+PathToDb);await c.OpenAsync();await using var cmd=c.CreateCommand();cmd.CommandText=sql;return await cmd.ExecuteScalarAsync();}
}
public class StorageTests
{
 private static readonly AuditActor Admin=new("a","测试管理员",AccountRole.Administrator);
 [Fact]public async Task NewDatabaseHasRepairedNativeEngineAndDurableSettings()
 {await using var f=new StoreFixture();await f.Initialize();var h=await f.Store.GetHealthAsync();Assert.True(SqliteConfigurationStore.HasWalResetFix(h.NativeVersion));Assert.Equal("wal",h.JournalMode);Assert.Equal(2,h.Synchronous);Assert.Equal(SqliteConfigurationStore.CurrentSchema,h.SchemaVersion);Assert.Empty((await f.Store.GetAsync()).Tanks);}
 [Theory][InlineData("3.51.2",false)][InlineData("3.51.3",true)][InlineData("3.50.6",false)][InlineData("3.50.7",true)][InlineData("3.44.6",true)][InlineData("3.52.0",true)]
 public void WalResetFixIsCheckedAgainstNativeVersion(string v,bool expected)=>Assert.Equal(expected,SqliteConfigurationStore.HasWalResetFix(v));
 [Fact]public async Task Saves128AndRejects129WithoutChangingSavedConfiguration()
 {await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(128),Admin);Assert.Equal(1,c.Revision);await Assert.ThrowsAsync<ConfigurationException>(()=>f.Store.SaveAsync(Fixtures.Configuration(129) with{Revision=1},Admin));Assert.Equal(128,(await f.Store.GetAsync()).Tanks.Count);Assert.Equal(2,Convert.ToInt32(await f.Scalar("SELECT count(*) FROM configuration_versions")));}
 [Fact]public async Task RevisionsRejectLostUpdateAndKeepOldSnapshot()
 {await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var updated=await f.Store.SaveAsync(c with{Tanks=[c.Tanks[0] with{Code="新罐号"}]},Admin);await Assert.ThrowsAsync<RevisionConflictException>(()=>f.Store.SaveAsync(c,Admin));Assert.Equal("新罐号",(await f.Store.GetAsync()).Tanks[0].Code);Assert.Contains("T-001",Convert.ToString(await f.Scalar("SELECT Payload FROM configuration_versions WHERE Revision=1")));Assert.Equal(2,updated.Revision);}
 [Fact]public async Task AuditFailureRollsBackConfigurationAndVersionTogether()
 {await using var f=new StoreFixture();await f.Initialize();await f.Scalar("CREATE TRIGGER reject_audit BEFORE INSERT ON operation_events BEGIN SELECT RAISE(ABORT,'injected test failure'); END;");await Assert.ThrowsAsync<DbUpdateException>(()=>f.Store.SaveAsync(Fixtures.Configuration(),Admin));Assert.Equal(0,(await f.Store.GetAsync()).Revision);Assert.Equal(1,Convert.ToInt32(await f.Scalar("SELECT count(*) FROM configuration_versions")));}
 [Fact]public async Task ViewerCannotSaveEvenWhenCallingStoreDirectly()
 {await using var f=new StoreFixture();await f.Initialize();await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Store.SaveAsync(Fixtures.Configuration(),Admin with{Role=AccountRole.Viewer}));Assert.Equal(0,(await f.Store.GetAsync()).Revision);}
 [Fact]public async Task OperationQueryCombinesTankActorKeywordAndTime()
 {await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(2),Admin);var events=await f.Store.QueryEventsAsync(new("操作日志",c.Tanks[0].Id.ToString(),"测试","新增",DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()));Assert.Single(events);Assert.Equal(c.Tanks[0].Code,events[0].TankCode);Assert.NotEmpty(events[0].Details);Assert.Empty(await f.Store.QueryEventsAsync(new(Keyword:"' OR 1=1 --")));await Assert.ThrowsAsync<ArgumentException>(()=>f.Store.QueryEventsAsync(new(StartUtcMs:100,EndUtcMs:99)));}
 [Fact]public async Task BackupCanBeOpenedAsActualRestoredDatabase()
 {await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(128),Admin);var path=Path.Combine(f.Root,"backup.db");await f.Store.BackupAsync(path);var restored=new SqliteConfigurationStore(path);await restored.InitializeAsync();Assert.Equal(128,(await restored.GetAsync()).Tanks.Count);Assert.Equal(c.Revision,(await restored.GetAsync()).Revision);Assert.NotEmpty(await restored.QueryEventsAsync(new()));}
 [Fact]public async Task FirstAdministratorCanOnlyBeCreatedOnce()
 {await using var f=new StoreFixture();await f.Initialize();Assert.True(await f.Store.NeedsSetupAsync());var a=new UserAccount("a","admin","管理员",AccountRole.Administrator,"test-hash",true);await f.Store.CreateInitialAdministratorAsync(a);Assert.False(await f.Store.NeedsSetupAsync());Assert.Equal("ADMIN",(await f.Store.FindAsync("admin"))!.Login);await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.CreateInitialAdministratorAsync(a with{Id="b"}));}
 [Fact]public async Task RejectsNewerDatabaseWithoutChangingVersion()
 {await using var f=new StoreFixture();await f.Scalar("PRAGMA user_version=99");await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Initialize());Assert.Equal(99,Convert.ToInt32(await f.Scalar("PRAGMA user_version")));}
}
