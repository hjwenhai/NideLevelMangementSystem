using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Naide.LevelManager.Application;
using Xunit;
namespace Naide.LevelManager.Tests;
public class ControlStoreTests
{
    private static readonly AuditActor Admin=new("a","管理员",AccountRole.Administrator);
    private static ControlRecord Record(SystemConfiguration c,ControlCycle cycle=ControlCycle.Normal)=>new(){Target=new(c.Channels[0],c.Tanks[0],c.Revision,Guid.NewGuid(),null,null),Command=cycle==ControlCycle.Normal?InstrumentCommand.MeasureIII:InstrumentCommand.Stop,Cycle=cycle,ActorId="a",ActorName="管理员",CreatedAtUtc=DateTimeOffset.UtcNow};
    [Fact]public async Task BlockedRequestIdentifiesTheExactOriginalRecord()
    {
        await using var f=new StoreFixture();await f.Initialize();
        var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);
        var store=new SqliteControlStore(f.PathToDb);var original=Record(c);
        await store.CreateAsync(original);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CreateAsync(Record(c)));
        Assert.Contains(original.Id.ToString(),error.Message);
        Assert.Contains(original.Target.Tank.Code,error.Message);
        Assert.Contains("正常动作通道",error.Message);
        Assert.Contains("测量Ⅲ",error.Message);
        Assert.Single(await store.ListAsync(c.Tanks[0].Id));
    }
    [Fact]public async Task SameCycleIsExclusiveButStopAndNormalRecordsStayIndependent()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteControlStore(f.PathToDb);await store.CreateAsync(Record(c));await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CreateAsync(Record(c)));await store.CreateAsync(Record(c,ControlCycle.IndependentStop));Assert.Equal(2,(await store.ListAsync()).Count);
    }
    [Fact]public async Task RecoveryDistinguishesUnsentFromPossiblySentAndNeverReplays()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteControlStore(f.PathToDb);var normal=Record(c);await store.CreateAsync(normal);var stop=Record(c,ControlCycle.IndependentStop);await store.CreateAsync(stop);
        stop=stop with{State=ControlState.Checking};await store.UpdateAsync(stop);stop=stop with{State=ControlState.WriteIntent,WriteIntentAtUtc=DateTimeOffset.UtcNow};await store.UpdateAsync(stop);await store.RecoverAsync(DateTimeOffset.UtcNow);
        Assert.Equal(ControlState.Rejected,(await store.GetAsync(normal.Id))!.State);Assert.Equal(ControlState.Unknown,(await store.GetAsync(stop.Id))!.State);Assert.True((await store.GetAsync(stop.Id))!.RequiresReview);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpdateAsync(stop with{State=ControlState.Checking}));await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CreateAsync(Record(c,ControlCycle.IndependentStop)));
        await store.ReviewAsync(stop.Id,Admin,"现场已核对浮子停止、位置与仪表模式",DateTimeOffset.UtcNow);Assert.Equal(ControlState.Reviewed,(await store.GetAsync(stop.Id))!.State);Assert.NotNull((await store.GetAsync(stop.Id))!.ReviewerName);
    }
    [Fact]public async Task WriteIntentAndAuditCommitTogetherBeforeADeviceMayBeCalled()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteControlStore(f.PathToDb);var record=Record(c);await store.CreateAsync(record);record=record with{State=ControlState.Checking};await store.UpdateAsync(record);
        await f.Scalar("CREATE TRIGGER deny_control_audit BEFORE INSERT ON operation_events BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;");
        await Assert.ThrowsAnyAsync<Exception>(()=>store.UpdateAsync(record with{State=ControlState.WriteIntent,WriteIntentAtUtc=DateTimeOffset.UtcNow}));Assert.Equal(ControlState.Checking,(await store.GetAsync(record.Id))!.State);Assert.Null((await store.GetAsync(record.Id))!.WriteIntentAtUtc);
    }
    [Fact]public async Task OperatorIdentityCannotBeRewrittenAndUnknownReviewRequiresPermissionAndReason()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteControlStore(f.PathToDb);var record=Record(c);await store.CreateAsync(record);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpdateAsync(record with{ActorName="另一个人",State=ControlState.Checking}));await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>store.ReviewAsync(record.Id,Admin with{Role=AccountRole.Viewer},"核对",DateTimeOffset.UtcNow));await Assert.ThrowsAsync<ArgumentException>(()=>store.ReviewAsync(record.Id,Admin,"",DateTimeOffset.UtcNow));
    }
    [Fact]public async Task OnlyGatewayResultResetCanHaveNoOriginalCommand()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteControlStore(f.PathToDb);var empty=Record(c) with{Command=null};await Assert.ThrowsAsync<ArgumentException>(()=>store.CreateAsync(empty));await Assert.ThrowsAsync<ArgumentException>(()=>store.CreateAsync(empty with{IsReset=true}));var gateway=c.Channels[0] with{Protocol=AcquisitionProtocol.GatewayTcp,Host="127.0.0.1"};var reset=empty with{IsReset=true,Target=empty.Target with{Channel=gateway}};await store.CreateAsync(reset);Assert.Null((await store.GetAsync(reset.Id))!.Command);Assert.True((await store.GetAsync(reset.Id))!.IsReset);
    }
    [Fact]public async Task CalibrationParametersAndEvidenceSurviveRecoveryWithoutRewritingIntent()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var store=new SqliteControlStore(f.PathToDb);
        var r=Record(c) with{Command=InstrumentCommand.RemoteLevel,TargetLevelMm=15010m,AdjustmentReason="现场液位复核"};await store.CreateAsync(r);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpdateAsync(r with{State=ControlState.Checking,TargetLevelMm=0m}));
        r=r with{State=ControlState.Checking,LevelAdjustment=new(15010m){CurrentLevelMm=15000m,PreviousOffsetMm=-500m,NewOffsetMm=-490m}};await store.UpdateAsync(r);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpdateAsync(r with{LevelAdjustment=r.LevelAdjustment with{NewOffsetMm=0m}}));
        r=r with{State=ControlState.WriteIntent,WriteIntentAtUtc=DateTimeOffset.UtcNow};await store.UpdateAsync(r);await store.RecoverAsync(DateTimeOffset.UtcNow);
        var actual=(await store.GetAsync(r.Id))!;Assert.Equal(ControlState.Unknown,actual.State);Assert.Equal(15010m,actual.TargetLevelMm);Assert.Equal(-490m,actual.LevelAdjustment!.NewOffsetMm);Assert.Equal("现场液位复核",actual.AdjustmentReason);
    }
    [Fact]public async Task Schema15UpgradePreservesConfigurationAndAddsDowngradeGuard()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);
        await f.Scalar("PRAGMA user_version=15;");await f.Initialize();
        Assert.Equal((long)SqliteConfigurationStore.CurrentSchema,Convert.ToInt64(await f.Scalar("PRAGMA user_version;")));Assert.Equal(c.Revision,(await f.Store.GetAsync()).Revision);
    }
}

