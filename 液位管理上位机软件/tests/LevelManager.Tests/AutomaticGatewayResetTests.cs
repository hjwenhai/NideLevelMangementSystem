using Naide.LevelManager.Domain;
using Naide.LevelManager.Communication;
using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;

public class AutomaticGatewayResetTests
{
    private static readonly AuditActor Admin=new("a","测试管理员",AccountRole.Administrator);
    private static ControlTarget Target()
    {
        var c=new ChannelDefinition{Protocol=AcquisitionProtocol.GatewayTcp,Host="127.0.0.1",UnitId=1};
        return new(c,new(){ChannelId=c.Id,Address=1},1,Guid.NewGuid(),91,1);
    }
    [Theory][InlineData(3)][InlineData(4)]
    public void OnlyTheNewSubmittedCycleCanBeAutomaticallyAcknowledged(int state)
    {
        var target=Target();var before=new ushort[12];before[5]=27;
        var after=new ushort[12];after[0]=1;after[1]=(ushort)state;after[2]=state==4?(ushort)4:(ushort)0;after[5]=28;
        var observed=GatewayControlReader.Decode(after,target,DateTimeOffset.UtcNow);
        Assert.NotNull(GatewayControlReader.OwnResult(before,observed,target,InstrumentCommand.MeasureI));
        after[5]=29;Assert.Null(GatewayControlReader.OwnResult(before,GatewayControlReader.Decode(after,target,DateTimeOffset.UtcNow),target,InstrumentCommand.MeasureI));
        after[5]=28;after[0]=5;Assert.Null(GatewayControlReader.OwnResult(before,GatewayControlReader.Decode(after,target,DateTimeOffset.UtcNow),target,InstrumentCommand.MeasureI));
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(5)]
    public void PendingAndUnknownResultsNeverAcquireAutomaticResetOwnership(int state)
    {
        var target=Target();var before=new ushort[12];var after=new ushort[12];after[0]=1;after[1]=(ushort)state;after[5]=1;
        Assert.Null(GatewayControlReader.OwnResult(before,GatewayControlReader.Decode(after,target,DateTimeOffset.UtcNow),target,InstrumentCommand.MeasureI));
    }
    [Fact]public void OldLatchAndSaturatedCountersAreNeverAutomaticallyCleared()
    {
        var target=Target();var before=new ushort[12];before[1]=3;var after=new ushort[12];after[0]=1;after[1]=3;after[5]=1;
        Assert.Null(GatewayControlReader.OwnResult(before,GatewayControlReader.Decode(after,target,DateTimeOffset.UtcNow),target,InstrumentCommand.MeasureI));
        before[1]=0;before[4]=65535;before[5]=65534;after[4]=65535;after[5]=65535;
        Assert.Null(GatewayControlReader.OwnResult(before,GatewayControlReader.Decode(after,target,DateTimeOffset.UtcNow),target,InstrumentCommand.MeasureI));
    }
    private static async Task<ControlRecord> Completed(StoreFixture f,SqliteControlStore store)
    {
        var c=await f.Store.SaveAsync(Fixtures.Configuration(),Admin);var target=Target() with{Tank=c.Tanks[0],ConfigurationRevision=c.Revision};
        var r=new ControlRecord{Target=target,Command=InstrumentCommand.MeasureI,Cycle=ControlCycle.Normal,ActorId="a",ActorName="测试管理员",CreatedAtUtc=DateTimeOffset.UtcNow};
        await store.CreateAsync(r);r=r with{State=ControlState.Checking};await store.UpdateAsync(r);
        r=r with{State=ControlState.WriteIntent,WriteIntentAtUtc=DateTimeOffset.UtcNow};await store.UpdateAsync(r);r=r with{State=ControlState.Submitted};await store.UpdateAsync(r);
        r=r with{State=ControlState.ReadbackConfirmed,FinishedAtUtc=DateTimeOffset.UtcNow,GatewayResultSnapshot=new(c.Revision,target.AcquisitionSessionId,"91",1,DateTimeOffset.UtcNow,new(1,3,0,1),new(0,0,0,0))};await store.UpdateAsync(r);return r;
    }
    private static ControlRecord Reset(ControlRecord parent)=>new(){Target=parent.Target,Command=parent.Command,Cycle=parent.Cycle,ActorId=parent.ActorId,ActorName=parent.ActorName,CreatedAtUtc=DateTimeOffset.UtcNow,IsReset=true,IsAutomaticReset=true,RelatedControlId=parent.Id,GatewayResetExpectation=parent.GatewayResultSnapshot};
    [Fact]public async Task AutomaticResetNeedsAnAlreadyDurableMatchingParentAndCannotRewriteIt()
    {
        await using var f=new StoreFixture();await f.Initialize();var store=new SqliteControlStore(f.PathToDb);var parent=await Completed(f,store);var reset=Reset(parent);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CreateAsync(reset with{ActorId="someone-else"}));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CreateAsync(reset with{RelatedControlId=Guid.NewGuid()}));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CreateAsync(reset with{GatewayResetExpectation=reset.GatewayResetExpectation! with{Normal=new(1,5,6,1)}}));
        await store.CreateAsync(reset);await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpdateAsync(reset with{State=ControlState.Checking,IsAutomaticReset=false}));
        await store.RecoverAsync(DateTimeOffset.UtcNow);Assert.Equal(ControlState.ReadbackConfirmed,(await store.GetAsync(parent.Id))!.State);Assert.Equal(ControlState.Rejected,(await store.GetAsync(reset.Id))!.State);
    }
    [Fact]public async Task DefaultIsThirtySecondsAndSavedValuesArePreserved()
    {
        Assert.Equal(30,new SystemConfiguration().ReadingExpirySeconds);
        await using var f=new StoreFixture();await f.Initialize();await f.Store.SaveAsync(Fixtures.Configuration() with{ReadingExpirySeconds=17},Admin);
        Assert.Equal(17,(await f.Store.GetAsync()).ReadingExpirySeconds);
    }
}
