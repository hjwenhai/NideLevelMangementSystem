using Microsoft.Data.Sqlite;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;

public class StorageFailureTests
{
 private static readonly AuditActor Admin=new("fault","存储故障隔离测试",AccountRole.Administrator);
 private static string Connection(string file)=>new SqliteConnectionStringBuilder{DataSource=file,Mode=SqliteOpenMode.ReadWrite,Pooling=true,DefaultTimeout=5}.ToString();
 private static HistorySample Sample(SystemConfiguration c,long at)=>new(){TankId=c.Tanks[0].Id,ConfigurationRevision=c.Revision,AtUtcMs=at,AcquisitionSessionId=Guid.Parse("11111111-2222-3333-4444-555555555555"),LevelMm=Measurement.Good(at),AtsPoints=Enumerable.Range(1,16).Select(i=>new TemperaturePoint(i,Measurement.Good(i))).ToArray()};
 [Fact]public async Task NativeSqliteFullRollsBackWholeHistoryBatchAndCanRecoverAfterLimitRaised()
 {
  await using var f=new StoreFixture();await f.Initialize();var config=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var history=new SqliteHistoryStore(f.PathToDb);await history.AppendAsync([Sample(config,1)]);
  async Task SetLimit(long? value){await using var db=new SqliteConnection(Connection(f.PathToDb));await db.OpenAsync();await using var cmd=db.CreateCommand();cmd.CommandText=value.HasValue?"PRAGMA max_page_count="+value.Value:"PRAGMA page_count";var pages=Convert.ToInt64(await cmd.ExecuteScalarAsync());if(!value.HasValue){cmd.CommandText="PRAGMA max_page_count="+pages;Assert.Equal(pages,Convert.ToInt64(await cmd.ExecuteScalarAsync()));}}
  await SetLimit(null);try{var error=await Assert.ThrowsAsync<SqliteException>(()=>history.AppendAsync(Enumerable.Range(2,4096).Select(i=>Sample(config,i)).ToArray()));Assert.Equal(13,error.SqliteErrorCode);Assert.Equal(1,(await history.StatisticsAsync()).Rows);}finally{await SetLimit(1073741823);}
  Assert.Equal(1,await history.AppendAsync([Sample(config,5000)]));Assert.Equal(2,(await history.StatisticsAsync()).Rows);Assert.Equal("ok",Convert.ToString(await f.Scalar("PRAGMA integrity_check")));
 }
 [Fact]public async Task ReadOnlyDatabaseRejectsArchiveWithoutPartialRowsAndWorksAfterPermissionRestored()
 {
  await using var f=new StoreFixture();await f.Initialize();var config=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var history=new SqliteHistoryStore(f.PathToDb);await history.AppendAsync([Sample(config,1)]);SqliteConfigurationStore.ReleasePools();var attributes=File.GetAttributes(f.PathToDb);
  try{File.SetAttributes(f.PathToDb,attributes|FileAttributes.ReadOnly);var error=await Assert.ThrowsAsync<SqliteException>(()=>history.AppendAsync([Sample(config,2)]));Assert.Contains(error.SqliteErrorCode,new[]{8,14});}
  finally{SqliteConfigurationStore.ReleasePools();File.SetAttributes(f.PathToDb,attributes);}
  Assert.Equal(1,(await history.StatisticsAsync()).Rows);Assert.Equal(1,await history.AppendAsync([Sample(config,3)]));Assert.Equal(2,(await history.StatisticsAsync()).Rows);Assert.Equal("ok",Convert.ToString(await f.Scalar("PRAGMA integrity_check")));
 }
}
