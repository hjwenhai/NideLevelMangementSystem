using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Xunit;
namespace Naide.LevelManager.Tests;
public class WirelessWorkflowTests
{
    private sealed class TestTime:IWorkflowTime
    {
        public double Seconds;public DateTimeOffset UtcNow=>DateTimeOffset.UnixEpoch.AddSeconds(Seconds);public long Timestamp=>(long)(Seconds*1000);
        public TimeSpan Elapsed(long start)=>TimeSpan.FromMilliseconds(Timestamp-start);
        public Task Delay(TimeSpan duration,CancellationToken ct){ct.ThrowIfCancellationRequested();Seconds+=duration.TotalSeconds;return Task.CompletedTask;}
    }
    private sealed class Journal:IWirelessTaskStore
    {
        public List<WirelessTaskRecord> Records=[];
        public Task SaveAsync(WirelessTaskRecord r,CancellationToken ct=default){ct.ThrowIfCancellationRequested();Records.Add(r);return Task.CompletedTask;}
        public Task CreateAsync(WirelessTaskRecord r,CancellationToken ct=default)=>Task.CompletedTask;
        public Task<WirelessTaskRecord?> GetAsync(Guid id,CancellationToken ct=default)=>Task.FromResult<WirelessTaskRecord?>(Records.LastOrDefault());
        public Task<IReadOnlyList<WirelessTaskRecord>> ListAsync(Guid? tankId=null,bool activeOnly=false,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<WirelessTaskRecord>>(Records);
        public Task<IReadOnlyList<WirelessTaskRecord>> LastSuccessfulAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<WirelessTaskRecord>>([]);
        public Task RecoverAsync(DateTimeOffset now,CancellationToken ct=default)=>Task.CompletedTask;
        public Task ReviewAsync(Guid id,AuditActor actor,string reason,DateTimeOffset now,CancellationToken ct=default)=>Task.CompletedTask;
    }
    private sealed class Device(TestTime time,Journal journal):IWirelessMeasurementDevice
    {
        public string? UnsupportedReason {get;set;}
        public bool RemoteFlag=false;
        public int Authorizations,TemperatureReads,Stops;public bool FailRestore,FailTemperatureOnce,PositionFaultAfterReading,Oscillate,FailStop;
        public decimal Surface=5000,Position=5000;public ushort Mode=1;public int Point=-1;private double lastStop;
        public Queue<ushort> Temperatures=new([5000,0,0x8000,3000]);public List<InstrumentCommand> Commands=[];
        public Task AuthorizeOnceAsync(WirelessTaskRecord task,CancellationToken ct){Assert.Equal(WirelessStage.Authorizing,journal.Records.Last().Stage);Authorizations++;return Task.CompletedTask;}
        public Task CommandAsync(WirelessTaskRecord task,InstrumentCommand c,CancellationToken ct)
        {
            Assert.True(journal.Records.Last().WriteIntentRecorded);Commands.Add(c);Mode=CommandEncoding.DirectWord(c);
            if(c==InstrumentCommand.MeasureIII){Point++;Position=task.Plan!.TargetsMm[Point]-10;}
            if(c==InstrumentCommand.Stop){Stops++;lastStop=time.Seconds;if(FailStop)throw new IOException("停止结果不明");}
            if(c==InstrumentCommand.MeasureI){if(FailRestore&&Point>=0)throw new IOException("恢复失败");Position=Surface;}
            return Task.CompletedTask;
        }
        public Task<ReadingSnapshot> ReadPositionAsync(CancellationToken ct)=>Task.FromResult(new ReadingSnapshot{LevelMm=PositionFaultAfterReading&&TemperatureReads>=2?Measurement.Missing("位置失效"):Measurement.Good(Oscillate?((int)time.Seconds%2)*20+Position:Position),ReceivedAtUtc=time.UtcNow,InstrumentFault=0,InstrumentStatus=(ushort)((RemoteFlag?0x8000:0)|Mode),FreshPositionReadConfirmed=true});
        public Task<WirelessTemperatureSample> ReadTemperatureOnceAsync(CancellationToken ct)
        {
            TemperatureReads++;if(Point<0)Assert.True(time.Seconds>=620);else Assert.True(time.Seconds-lastStop>=602);
            if(FailTemperatureOnce&&Point==0&&TemperatureReads==2)throw new IOException("模拟单次温度读取失败");var raw=Temperatures.Dequeue();return Task.FromResult(new WirelessTemperatureSample(raw==0x8000?Measurement.Missing("协议0x8000缺测"):Measurement.Good(unchecked((short)raw)/100m),raw));
        }
    }
    private static WirelessTaskRecord Record(int n=3)=>new(){Target=new(new(){Protocol=AcquisitionProtocol.DirectRtu},new(){TemperatureMode=TemperatureMode.Wireless},0,Guid.NewGuid(),null,null),ActorId="test",ActorName="隔离模拟",CreatedAtUtc=DateTimeOffset.UnixEpoch,RequestedPoints=n};
    [Theory][InlineData(false)][InlineData(true)]public async Task CompleteFlowUsesOneAuthorizationRealDwellsZeroAndMissingRulesAndRestores(bool remote)
    {
        var t=new TestTime();var j=new Journal();var d=new Device(t,j){RemoteFlag=remote};var r=await new WirelessWorkflow(j,t).RunAsync(Record(),d,default);
        Assert.Equal(WirelessStage.Completed,r.Stage);Assert.True(r.HasSuccessfulResult);Assert.Equal(1,d.Authorizations);Assert.Equal(3,d.Stops);Assert.Equal(4,d.TemperatureReads);Assert.Equal(3,r.Plan!.PlannedPoints);Assert.Equal(2,r.ValidPoints);Assert.Equal(15m,r.AverageTemperature.Value);Assert.Equal(50m,r.BaselineTemperature.Value);Assert.Equal(0m,r.Points[0].Temperature.Value);Assert.Equal(1,r.Points[1].Attempts);Assert.Equal((ushort)0x8000,r.Points[1].RawTemperature);Assert.All(r.Points,p=>Assert.True(p.DwellElapsedSeconds>=600));Assert.Equal(InstrumentCommand.MeasureI,d.Commands.Last());Assert.True(t.Seconds>=2426);
    }
    [Fact]public async Task ReadFailureRetriesOnlyTheReadWithoutRepeatingMotion()
    {
        var t=new TestTime();var j=new Journal();var d=new Device(t,j){FailTemperatureOnce=true,Temperatures=new([5000,2500,2600,2700])};var r=await new WirelessWorkflow(j,t).RunAsync(Record(),d,default);
        Assert.True(r.HasSuccessfulResult);Assert.Equal(2,r.Points[0].Attempts);Assert.Equal(5,d.TemperatureReads);Assert.Equal(3,d.Commands.Count(c=>c==InstrumentCommand.MeasureIII));Assert.Equal(1,d.Authorizations);
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]public async Task RestoreOrPositionFailureNeverPublishesAverage(bool restore,bool position)
    {
        var t=new TestTime();var j=new Journal();var d=new Device(t,j){FailRestore=restore,PositionFaultAfterReading=position};var r=await new WirelessWorkflow(j,t).RunAsync(Record(),d,default);
        Assert.Equal(WirelessStage.RequiresReview,r.Stage);Assert.False(r.HasSuccessfulResult);Assert.Null(r.AverageTemperature.Value);Assert.Equal(1,d.Authorizations);if(position)Assert.Equal(1,d.Stops);
    }
    [Fact]public async Task InitialInstabilityTimesOutWithoutDescending()
    {
        var t=new TestTime();var j=new Journal();var d=new Device(t,j){Oscillate=true};var r=await new WirelessWorkflow(j,t).RunAsync(Record(),d,default);Assert.Equal(WirelessStage.RequiresReview,r.Stage);Assert.DoesNotContain(InstrumentCommand.MeasureIII,d.Commands);Assert.Equal(0,d.TemperatureReads);
    }
    [Fact]public async Task UnsupportedGatewayNeverAuthorizesOrMoves()
    {
        var t=new TestTime();var j=new Journal();var d=new Device(t,j){UnsupportedReason="缺少整任务一次认证及停留后新读I6接口"};var r=await new WirelessWorkflow(j,t).RunAsync(Record(),d,default);Assert.Equal(WirelessStage.Failed,r.Stage);Assert.Equal(0,d.Authorizations);Assert.Empty(d.Commands);Assert.False(r.HoldsTank);
    }
    [Fact]public async Task LowLiquidSkipsWithoutDescendingAndStopUncertainNeverRepeats()
    {
        var t=new TestTime();var j=new Journal();var d=new Device(t,j){Surface=499,Position=499};var r=await new WirelessWorkflow(j,t).RunAsync(Record(),d,default);Assert.Equal(WirelessStage.Skipped,r.Stage);Assert.False(r.HasSuccessfulResult);Assert.DoesNotContain(InstrumentCommand.MeasureIII,d.Commands);
        t=new();j=new();d=new(t,j){FailStop=true};r=await new WirelessWorkflow(j,t).RunAsync(Record(),d,default);Assert.Equal(WirelessStage.RequiresReview,r.Stage);Assert.Equal(1,d.Stops);Assert.Single(d.Commands,c=>c==InstrumentCommand.MeasureIII);
    }
}
