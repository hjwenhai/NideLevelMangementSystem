using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class WirelessStoreTests
{
    private static readonly AuditActor Admin=new("a","管理员",AccountRole.Administrator);
    private static WirelessTaskRecord Record(SystemConfiguration c)=>new(){Target=new(c.Channels[0],c.Tanks[0],c.Revision,Guid.NewGuid(),null,null),ActorId="a",ActorName="管理员",CreatedAtUtc=DateTimeOffset.UtcNow};
    [Fact]public async Task TaskLeaseAndRecoveryPreserveUncertainWritesAndNeverResume()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteWirelessTaskStore(f.PathToDb);var r=Record(c);await store.CreateAsync(r);await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CreateAsync(Record(c)));
        r=r with{Version=1,Stage=WirelessStage.Authorizing,WriteIntentRecorded=true};await store.SaveAsync(r);await store.RecoverAsync(DateTimeOffset.UtcNow);r=(await store.GetAsync(r.Id))!;Assert.Equal(WirelessStage.RequiresReview,r.Stage);Assert.True(r.HoldsTank);await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveAsync(r with{Version=r.Version+1,Stage=WirelessStage.Descending}));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.ReviewAsync(r.Id,Admin,"",DateTimeOffset.UtcNow));await store.ReviewAsync(r.Id,Admin,"已现场核对原浮子、位置、停止与模式",DateTimeOffset.UtcNow);Assert.False((await store.GetAsync(r.Id))!.HoldsTank);await store.CreateAsync(Record(c));
    }
    [Fact]public async Task UnsentTaskEndsAndAuditFailureCannotCommitAnIntent()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteWirelessTaskStore(f.PathToDb);var r=Record(c);await store.CreateAsync(r);await f.Scalar("CREATE TRIGGER deny_task_audit BEFORE INSERT ON operation_events BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;");
        await Assert.ThrowsAnyAsync<Exception>(()=>store.SaveAsync(r with{Version=1,Stage=WirelessStage.Authorizing,WriteIntentRecorded=true}));Assert.False((await store.GetAsync(r.Id))!.WriteIntentRecorded);await f.Scalar("DROP TRIGGER deny_task_audit");await store.RecoverAsync(DateTimeOffset.UtcNow);Assert.Equal(WirelessStage.Failed,(await store.GetAsync(r.Id))!.Stage);
    }
    [Fact]public async Task LastSuccessfulAverageSurvivesANewerFailedTaskAndFrozenPlanCannotChange()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteWirelessTaskStore(f.PathToDb);var r=Record(c);await store.CreateAsync(r);var plan=WirelessMeasurementRules.Plan(1000,1);r=r with{Version=1,Stage=WirelessStage.Planning,Plan=plan};await store.SaveAsync(r);await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveAsync(r with{Version=2,Plan=WirelessMeasurementRules.Plan(2000,3)}));
        r=r with{Version=2,Stage=WirelessStage.Completed,AverageTemperature=Measurement.Good(0),MeasureIRestored=true,Points=[new(1,500,490,DateTimeOffset.UtcNow.AddMinutes(-10),DateTimeOffset.UtcNow,Measurement.Good(0),1,0,600)],FinishedAtUtc=DateTimeOffset.UtcNow};await store.SaveAsync(r);var newer=Record(c);await store.CreateAsync(newer);await store.RecoverAsync(DateTimeOffset.UtcNow);Assert.Equal(r.Id,(await store.LastSuccessfulAsync()).Single().Id);await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveAsync(r with{Version=3,AverageTemperature=Measurement.Good(20)}));
    }
    [Fact]public async Task UpgradeFromNineKeepsConfigurationAndCreatesTaskTables()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);await f.Scalar("DROP TABLE wireless_tasks;DROP TABLE motion_markers;DROP TABLE instrument_fault_state;DROP TABLE role_permissions;ALTER TABLE users DROP COLUMN SecurityVersion;PRAGMA user_version=9");await f.Initialize();Assert.Equal(c.Revision,(await f.Store.GetAsync()).Revision);Assert.Equal(SqliteConfigurationStore.CurrentSchema,Convert.ToInt32(await f.Scalar("PRAGMA user_version")));Assert.Empty(await new SqliteWirelessTaskStore(f.PathToDb).ListAsync());
    }
    [Fact]public void LiquidSurfaceAndActualPositionStaySeparateAndOldMeanIsAnHistoricalResult()
    {
        var ch=new ChannelDefinition{Protocol=AcquisitionProtocol.DirectRtu};var tank=new TankDefinition{ChannelId=ch.Id,TemperatureMode=TemperatureMode.Wireless};var session=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        var task=new WirelessTaskRecord{Target=new(ch,tank,1,session,null,null),ActorId="a",ActorName="a",CreatedAtUtc=now,Stage=WirelessStage.PointDwell,Plan=WirelessMeasurementRules.Plan(5000,3),PlanFrozenAtUtc=now};var runtime=new WirelessRuntime();runtime.Publish(task);
        var r=runtime.Apply(new(){TankId=tank.Id,AcquisitionSessionId=session,ConfigurationRevision=1,ReceivedAtUtc=now,LevelMm=Measurement.Good(1000),InstrumentStatus=0x8004},tank,ch);Assert.Equal(5000m,r.LevelMm.Value);Assert.Equal(1000m,r.FloatPositionMm.Value);Assert.NotEmpty(r.SurfaceBasis);
        runtime.Publish(task with{Stage=WirelessStage.Completed,AverageTemperature=Measurement.Good(0),FinishedAtUtc=now,MeasureIRestored=true});r=runtime.Apply(r,tank,ch,false).ForCurrentState(now.AddMinutes(1),session,1,30,false,true,TemperatureMode.Wireless);Assert.Equal(0m,r.AverageTemperatureC.Value);Assert.Equal(ValueQuality.Offline,r.LevelMm.Quality);Assert.Equal(task.Id,r.AverageTaskId);
        var stopped=runtime.Apply(new(){LevelMm=Measurement.Good(1000),InstrumentStatus=0x8004},tank,ch);Assert.False(stopped.LevelMm.IsValid);Assert.Equal(1000m,stopped.FloatPositionMm.Value);
        Assert.False(CommandEncoding.ModeMatches(0x8003,InstrumentCommand.MeasureI,AcquisitionProtocol.DirectRtu));
    }
}
