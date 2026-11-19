using Naide.LevelManager.Domain;
using Naide.LevelManager.Communication;
using Xunit;
namespace Naide.LevelManager.Tests;
public class ControlDriverTests
{
    public static IEnumerable<object[]> ReportedFaultCommands()
    {
        foreach(var protocol in Enum.GetValues<AcquisitionProtocol>())
        foreach(var command in Enum.GetValues<InstrumentCommand>().Where(c=>c!=InstrumentCommand.RemoteLevel&&(protocol!=AcquisitionProtocol.IfxL25||c<=InstrumentCommand.Density)))
        foreach(var fault in new ushort[]{0x0802,0x1000,0xffff})yield return [protocol,command,fault];
    }
    [Theory][MemberData(nameof(ReportedFaultCommands))]
    public async Task ReportedFaultDoesNotBlockDispatchOrModeConfirmation(AcquisitionProtocol protocol,InstrumentCommand command,ushort fault)
    {
        await using var device=new Device(protocol){Fault=fault};
        var result=await new ControlProtocolDriver().ExecuteAsync(device,Target(protocol),command,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);
        var address=protocol==AcquisitionProtocol.DirectRtu?699:protocol==AcquisitionProtocol.IfxL25?500:command==InstrumentCommand.Stop?30007:30000;
        Assert.Single(device.Writes,w=>w.Address==address);
        Assert.Equal(fault,device.Fault);
    }
    [Fact]public async Task IfxFieldCommunicationErrorCannotBeMistakenForAnAllowedInstrumentFault()
    {
        await using var device=new Device(AcquisitionProtocol.IfxL25){Fault=0x0802,IfxCommunicationError=2};
        var result=await new ControlProtocolDriver().ExecuteAsync(device,Target(AcquisitionProtocol.IfxL25),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Rejected,result.State);Assert.Empty(device.Writes);
    }
    private sealed class Device(AcquisitionProtocol protocol):IRegisterClient
    {
        public readonly List<(ushort Address,ushort Value)> Writes=[];
        public bool LoseMotionReply,NoCoils,Execute,Done;
        public bool RemoteFlag=true;
        public int DelayedModeReads;private int remainingModeReads;private ushort? pendingMode;
        public byte? MotionException;
        public bool LoseIfxExecuteReply;
        private void SetMode(ushort mode)
        {
            if(KeepOriginalMode)return;
            if(DelayedModeReads>0){pendingMode=mode;remainingModeReads=DelayedModeReads;}
            else Mode=mode;
        }
        private void AdvanceMode()
        {
            if(!pendingMode.HasValue)return;
            if(remainingModeReads--<=0){Mode=pendingMode.Value;pendingMode=null;}
        }
        public bool? AfterMotionRemoteFlag;
        public bool RejectAuthorization,KeepOriginalMode,FailFreshStatus;
        public ushort Fault,IfxCommunicationError,StatusGroup=9,OptionalDensityGroup=9;
        public ushort Mode=1,NormalState,StopState,Error;
        public uint NormalCount=0,StopCount;public int ControlReads;public bool BusyReply,BlockOnSecondRead;
        public ValueTask<ushort[]> ReadInputRegistersAsync(byte unit,ushort start,ushort count,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(start==0||start==51||start==1000)AdvanceMode();
            if(protocol==AcquisitionProtocol.DirectRtu)
            {if(FailFreshStatus&&start>=50)throw new TimeoutException("模拟状态读取失败");if(start==0){var r=new ushort[31];r[1]=50000;r[26]=2500;return ValueTask.FromResult(r);}return ValueTask.FromResult(new ushort[]{8320,(ushort)((RemoteFlag?0x8000:0)|Mode),Fault}.Skip(start-50).Take(count).ToArray());}
            if(protocol==AcquisitionProtocol.IfxL25)return ValueTask.FromResult(start switch{0=>new ushort[]{0,50000,2500,(ushort)((RemoteFlag?32:0)|Mode)},8=>[832],12=>[Fault,IfxCommunicationError],_=>throw new InvalidDataException()});
            var header=new ushort[30];header[2]=3;header[3]=64;header[9]=91;
            if(start==0)return ValueTask.FromResult(header);if(start==6)return ValueTask.FromResult(header[6..10]);
            if(start==1000){var s=new ushort[24];s[0]=3;s[1]=127;s[3]=42;s[5]=50000;s[6]=2500;s[7]=8320;s[9]=(ushort)((RemoteFlag?0x8000:0)|Mode);s[10]=Fault;s[21]=1;return ValueTask.FromResult(s);}
            var d=new ushort[144];d[55]=1;d[57]=42;for(var i=0;i<6;i++)d[64+i*12]=9;d[64+2*12]=StatusGroup;d[64+4*12]=OptionalDensityGroup;
            return ValueTask.FromResult(d.Skip(start-3000).Take(count).ToArray());
        }
        public ValueTask<ushort[]> ReadHoldingRegistersAsync(byte u,ushort s,ushort n,CancellationToken ct)
        {if(s==698)return ValueTask.FromResult(new[]{Mode});ControlReads++;if(BlockOnSecondRead&&ControlReads==2)StopState=3;var r=new ushort[12];r[1]=NormalState;r[2]=NormalState==0?(ushort)0:Error;r[4]=(ushort)(NormalCount>>16);r[5]=(ushort)NormalCount;r[8]=StopState;r[9]=StopState==0?(ushort)0:Error;r[10]=(ushort)(StopCount>>16);r[11]=(ushort)StopCount;return ValueTask.FromResult(r);}
        public ValueTask<bool[]> ReadCoilsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>NoCoils?ValueTask.FromException<bool[]>(new ModbusDeviceException(1,1)):ValueTask.FromResult(new[]{Execute});
        public ValueTask<bool[]> ReadDiscreteInputsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>ValueTask.FromResult(new[]{Done});
        public ValueTask WriteSingleRegisterAsync(byte u,ushort s,ushort v,CancellationToken ct)
        {
            Writes.Add((s,v));
            if(MotionException.HasValue&&s is 500 or 699 or 30000) return ValueTask.FromException(new ModbusDeviceException(protocol==AcquisitionProtocol.DirectRtu?(byte)16:(byte)6,MotionException.Value));
            if(BusyReply&&s is 30000 or 30007)return ValueTask.FromException(new ModbusDeviceException(6,6));
            if(s==129&&RejectAuthorization)return ValueTask.FromException(new ModbusDeviceException(16,3));
            if(protocol==AcquisitionProtocol.IfxL25&&s==500)SetMode(v switch{1=>1,2=>4,3=>6,4=>5,5=>2,6=>3,7=>7,_=>(ushort)0});
            if(s==699){SetMode(v);if(AfterMotionRemoteFlag.HasValue)RemoteFlag=AfterMotionRemoteFlag.Value;}
            if(s==30000){NormalState=v==0?(ushort)0:Error==6?(ushort)5:(ushort)3;if(v!=0)SetMode(CommandEncoding.DirectWord((InstrumentCommand)v));}
            if(s==30007){StopState=v==0?(ushort)0:(ushort)3;if(v!=0)SetMode(4);}
            return s==699&&LoseMotionReply?ValueTask.FromException(new TimeoutException("模拟回执丢失")):ValueTask.CompletedTask;
        }
        public ValueTask WriteMultipleRegistersAsync(byte u,ushort s,ReadOnlyMemory<ushort> v,CancellationToken ct)=>WriteSingleRegisterAsync(u,s,v.Span[0],ct);
        public ValueTask WriteCoilAsync(byte u,ushort s,bool v,CancellationToken ct){Writes.Add((s,v?(ushort)1:(ushort)0));Execute=v;Done=v;return v&&LoseIfxExecuteReply?ValueTask.FromException(new TimeoutException("模拟IFX触发应答丢失")):ValueTask.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(AcquisitionProtocol.DirectRtu)]
    [InlineData(AcquisitionProtocol.IfxL25)]
    [InlineData(AcquisitionProtocol.GatewayTcp)]
    [InlineData(AcquisitionProtocol.GatewayRtu)]
    public async Task AllProtocolsWaitForDelayedModeWithoutResending(AcquisitionProtocol protocol)
    {
        await using var device=new Device(protocol){DelayedModeReads=2};
        var result=await new ControlProtocolDriver().ExecuteAsync(device,Target(protocol),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);
        var motionAddress=protocol==AcquisitionProtocol.DirectRtu?699:protocol==AcquisitionProtocol.IfxL25?500:30000;
        Assert.Single(device.Writes,w=>w.Address==motionAddress);
    }
    [Theory]
    [InlineData(AcquisitionProtocol.DirectRtu)]
    [InlineData(AcquisitionProtocol.IfxL25)]
    [InlineData(AcquisitionProtocol.GatewayTcp)]
    [InlineData(AcquisitionProtocol.GatewayRtu)]
    public async Task ModeTimeoutReportsExpectedLastModeAndConfiguredWait(AcquisitionProtocol protocol)
    {
        await using var device=new Device(protocol){KeepOriginalMode=true};
        var result=await new ControlProtocolDriver().ExecuteAsync(device,Target(protocol),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Unknown,result.State);
        Assert.Contains("期望模式上升",result.Message);
        Assert.Contains("最后读取测量Ⅰ",result.Message);
        Assert.Contains("设定3秒",result.Message);
        var motionAddress=protocol==AcquisitionProtocol.DirectRtu?699:protocol==AcquisitionProtocol.IfxL25?500:30000;
        Assert.Single(device.Writes,w=>w.Address==motionAddress);
    }
    [Theory]
    [InlineData(AcquisitionProtocol.DirectRtu,1)]
    [InlineData(AcquisitionProtocol.DirectRtu,2)]
    [InlineData(AcquisitionProtocol.DirectRtu,3)]
    [InlineData(AcquisitionProtocol.DirectRtu,6)]
    [InlineData(AcquisitionProtocol.IfxL25,1)]
    [InlineData(AcquisitionProtocol.IfxL25,2)]
    [InlineData(AcquisitionProtocol.IfxL25,3)]
    [InlineData(AcquisitionProtocol.IfxL25,6)]
    [InlineData(AcquisitionProtocol.GatewayTcp,1)]
    [InlineData(AcquisitionProtocol.GatewayTcp,2)]
    [InlineData(AcquisitionProtocol.GatewayTcp,3)]
    [InlineData(AcquisitionProtocol.GatewayTcp,6)]
    [InlineData(AcquisitionProtocol.GatewayRtu,1)]
    [InlineData(AcquisitionProtocol.GatewayRtu,2)]
    [InlineData(AcquisitionProtocol.GatewayRtu,3)]
    [InlineData(AcquisitionProtocol.GatewayRtu,6)]
    public async Task ExplicitMotionRejectionIsTerminalFailure(AcquisitionProtocol protocol,int code)
    {
        await using var device=new Device(protocol){MotionException=(byte)code};
        var result=await new ControlProtocolDriver().ExecuteAsync(device,Target(protocol),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Rejected,result.State);
        Assert.Contains($"异常码{code:X2}",result.Message);
        Assert.DoesNotContain("结果未知",result.Message);
    }
    [Theory][InlineData(AcquisitionProtocol.DirectRtu)][InlineData(AcquisitionProtocol.GatewayTcp)]
    public async Task DeviceFailureReplyStillRequiresReview(AcquisitionProtocol protocol)
    {
        await using var device=new Device(protocol){MotionException=4};
        var result=await new ControlProtocolDriver().ExecuteAsync(device,Target(protocol),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Unknown,result.State);
    }
    [Fact]public async Task LostIfxTriggerResponseIsNotReplayed()
    {
        await using var device=new Device(AcquisitionProtocol.IfxL25){LoseIfxExecuteReply=true};
        var result=await new ControlProtocolDriver().ExecuteAsync(device,Target(AcquisitionProtocol.IfxL25),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Unknown,result.State);
        Assert.Single(device.Writes,w=>w.Address==100&&w.Value==1);
    }
    private static ControlTarget Target(AcquisitionProtocol p)
    {
        var channel=new ChannelDefinition{Protocol=p,UnitId=1};var tank=new TankDefinition{ChannelId=channel.Id,Address=1,TemperatureMode=TemperatureMode.None,Controls=new(){ConfirmationTimeoutSeconds=3}};return new(channel,tank,1,Guid.NewGuid(),p is AcquisitionProtocol.GatewayTcp or AcquisitionProtocol.GatewayRtu?91UL:null,p is AcquisitionProtocol.GatewayTcp or AcquisitionProtocol.GatewayRtu?1U:null);
    }
    [Theory]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.MeasureI)]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.Hoist)]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.Lower)]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.MeasureII)]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.MeasureIII)]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.Density)]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.Repeatability)]
    [InlineData(AcquisitionProtocol.GatewayTcp,InstrumentCommand.OscillationAvoidance)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.MeasureI)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.Hoist)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.Lower)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.MeasureII)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.MeasureIII)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.Density)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.Repeatability)]
    [InlineData(AcquisitionProtocol.GatewayRtu,InstrumentCommand.OscillationAvoidance)]
    public async Task GatewayOrdinaryCommandsUseCurrentContractWithoutRemoteBitOrOptionalDensity(AcquisitionProtocol protocol,InstrumentCommand command)
    {
        await using var d=new Device(protocol){RemoteFlag=false,OptionalDensityGroup=4};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(protocol),command,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);
        Assert.Equal(new(ushort,ushort)[]{(30000,(ushort)command)},d.Writes);
    }
    [Theory][InlineData(3,0)]
    public async Task GatewayOrdinaryCommandsStillRefuseMissingStatusGroup(int group,int fault)
    {
        await using var d=new Device(AcquisitionProtocol.GatewayTcp){RemoteFlag=false,StatusGroup=(ushort)group,Fault=(ushort)fault};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Rejected,result.State);Assert.Empty(d.Writes);
    }
    [Theory]
    [InlineData(InstrumentCommand.MeasureI)]
    [InlineData(InstrumentCommand.MeasureII)]
    [InlineData(InstrumentCommand.MeasureIII)]
    [InlineData(InstrumentCommand.Stop)]
    [InlineData(InstrumentCommand.Hoist)]
    [InlineData(InstrumentCommand.Lower)]
    [InlineData(InstrumentCommand.Density)]
    public async Task IfxOrdinaryCommandsConfirmWithoutRemoteBit(InstrumentCommand command)
    {
        await using var d=new Device(AcquisitionProtocol.IfxL25){RemoteFlag=false};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.IfxL25),command,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);Assert.False(d.RemoteFlag);
        Assert.Equal(new(ushort,ushort)[]{(500,CommandEncoding.IfxWord(command)),(100,1),(100,0)},d.Writes);
    }
    [Theory]
    [InlineData(InstrumentCommand.MeasureI,false)]
    [InlineData(InstrumentCommand.MeasureI,true)]
    [InlineData(InstrumentCommand.MeasureII,false)]
    [InlineData(InstrumentCommand.MeasureII,true)]
    [InlineData(InstrumentCommand.MeasureIII,false)]
    [InlineData(InstrumentCommand.MeasureIII,true)]
    [InlineData(InstrumentCommand.Stop,false)]
    [InlineData(InstrumentCommand.Stop,true)]
    [InlineData(InstrumentCommand.Hoist,false)]
    [InlineData(InstrumentCommand.Hoist,true)]
    [InlineData(InstrumentCommand.Lower,false)]
    [InlineData(InstrumentCommand.Lower,true)]
    [InlineData(InstrumentCommand.Density,false)]
    [InlineData(InstrumentCommand.Density,true)]
    [InlineData(InstrumentCommand.Repeatability,false)]
    [InlineData(InstrumentCommand.Repeatability,true)]
    [InlineData(InstrumentCommand.OscillationAvoidance,false)]
    [InlineData(InstrumentCommand.OscillationAvoidance,true)]
    public async Task DirectOrdinaryCommandsAuthenticateAndConfirmWithoutRemoteBit(InstrumentCommand command,bool initialRemote)
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu){RemoteFlag=initialRemote,AfterMotionRemoteFlag=false};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),command,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);
        Assert.Equal(new(ushort,ushort)[]{(129,0x10d9),(699,CommandEncoding.DirectWord(command))},d.Writes);
        Assert.False(d.RemoteFlag);Assert.Equal(CommandEncoding.DirectWord(command),result.DeviceState);
    }
    [Theory][InlineData(false,true,false)][InlineData(false,false,true)]
    public async Task DirectLocalCommandsStillRejectMissingStatusOrFailedAuthentication(bool fault,bool missing,bool authFailure)
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu){RemoteFlag=false,Fault=(ushort)(fault?32:0),FailFreshStatus=missing,RejectAuthorization=authFailure};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),InstrumentCommand.Lower,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Rejected,result.State);Assert.DoesNotContain(d.Writes,w=>w.Address==699);
        if(!authFailure)Assert.Empty(d.Writes);else Assert.Equal(new(ushort,ushort)[]{(129,0x10d9)},d.Writes);
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task DirectLocalCommandsKeepUnknownWithoutRetryForLostReplyOrModeMismatch(bool lost,bool mismatch)
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu){RemoteFlag=false,LoseMotionReply=lost,KeepOriginalMode=mismatch};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),InstrumentCommand.Lower,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Unknown,result.State);Assert.Single(d.Writes,w=>w.Address==699);
    }
    [Fact]public async Task AutomaticallyAuthorizedTaskMovesConfirmWithoutRemoteBit()
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu){RemoteFlag=false};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),InstrumentCommand.Lower,_=>Task.CompletedTask,true,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);Assert.Equal(new(ushort,ushort)[]{(699,256)},d.Writes);
    }
    [Theory][InlineData(2)][InlineData(4)][InlineData(256)]
    public async Task MeasureIRecoveryConfirmsFromOtherModesWithoutRemoteBit(int previousMode)
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu){RemoteFlag=false,Mode=(ushort)previousMode};
        var result=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),InstrumentCommand.MeasureI,_=>Task.CompletedTask,false,default,recoveryOnly:true);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);Assert.Equal(new(ushort,ushort)[]{(129,0x10d9),(699,1)},d.Writes);
    }
    [Fact]public async Task DirectUsesOneTaskAuthorizationAndSeparateWriteWords()
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu);var driver=new ControlProtocolDriver();var target=Target(AcquisitionProtocol.DirectRtu);List<ControlCheckpoint> checkpoints=[];
        var r=await driver.ExecuteAsync(d,target,InstrumentCommand.MeasureIII,p=>{checkpoints.Add(p);return Task.CompletedTask;},false,default);Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.Equal(new(ushort,ushort)[]{(129,0x10d9),(699,32)},d.Writes);Assert.Equal(ControlCheckpoint.AuthorizationWrite,checkpoints[0]);
        await driver.ExecuteAsync(d,target,InstrumentCommand.MeasureI,_=>Task.CompletedTask,true,default);Assert.Equal(1,d.Writes.Count(w=>w.Address==129));Assert.Equal((ushort)1,d.Writes.Last().Value);
    }
    [Fact]public async Task NecessaryRecoveryRechecksActualModeBeforeSending()
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu){Mode=1};var r=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),InstrumentCommand.MeasureI,_=>Task.CompletedTask,false,default,true);Assert.Equal(ControlState.Rejected,r.State);Assert.Empty(d.Writes);
    }
    [Fact]public async Task LostWriteReplyIsUnknownAndMotionIsNotRetried()
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu){LoseMotionReply=true};var r=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),InstrumentCommand.MeasureIII,_=>Task.CompletedTask,false,default);Assert.Equal(ControlState.Unknown,r.State);Assert.Single(d.Writes,w=>w.Address==699);
    }
    [Fact]public async Task FailedDurableCheckpointPreventsTheMotionWrite()
    {
        await using var d=new Device(AcquisitionProtocol.DirectRtu);var r=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.DirectRtu),InstrumentCommand.MeasureIII,p=>p==ControlCheckpoint.MotionWrite?Task.FromException(new IOException("审计不可用")):Task.CompletedTask,false,default);Assert.Equal(ControlState.Rejected,r.State);Assert.DoesNotContain(d.Writes,w=>w.Address==699);
    }
    [Fact]public async Task IfxUsesC01AndRequiresANewCompletionEdgeAndCoilReadSupport()
    {
        await using var d=new Device(AcquisitionProtocol.IfxL25){Execute=true,Done=true};var r=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.IfxL25),InstrumentCommand.MeasureIII,_=>Task.CompletedTask,false,default);Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.Equal(new(ushort,ushort)[]{(100,0),(500,3),(100,1),(100,0)},d.Writes);
        await using var unsupported=new Device(AcquisitionProtocol.IfxL25){NoCoils=true};r=await new ControlProtocolDriver().ExecuteAsync(unsupported,Target(AcquisitionProtocol.IfxL25),InstrumentCommand.MeasureI,_=>Task.CompletedTask,false,default);Assert.Equal(ControlState.Rejected,r.State);Assert.Empty(unsupported.Writes);
    }
    [Fact]public async Task GatewayResetOnlyClearsChosenResultWithoutStoppingOrReplaying()
    {
        await using var d=new Device(AcquisitionProtocol.GatewayTcp){NormalState=3,StopState=3,Mode=32};var driver=new ControlProtocolDriver();
        var r=await driver.ResetGatewayAsync(d,Target(AcquisitionProtocol.GatewayTcp),ControlCycle.Normal,_=>Task.CompletedTask,default);
        Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.Equal((ushort)0,d.NormalState);Assert.Equal((ushort)3,d.StopState);Assert.Equal((ushort)32,d.Mode);Assert.Equal(new(ushort,ushort)[]{(30000,0)},d.Writes);
        d.NormalState=2;var count=d.Writes.Count;r=await driver.ResetGatewayAsync(d,Target(AcquisitionProtocol.GatewayTcp),ControlCycle.Normal,_=>Task.CompletedTask,default);Assert.Equal(ControlState.Rejected,r.State);Assert.Equal(count,d.Writes.Count);
    }
    [Fact]public async Task GatewayStopIsIndependentAndUnknownResultIsNeverAutomaticallyReset()
    {
        await using var d=new Device(AcquisitionProtocol.GatewayTcp){NormalState=2};var driver=new ControlProtocolDriver();var r=await driver.ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),InstrumentCommand.Stop,_=>Task.CompletedTask,false,default);Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.Equal((ushort)2,d.NormalState);Assert.Equal(new(ushort,ushort)[]{(30007,1)},d.Writes);
        await using var unknown=new Device(AcquisitionProtocol.GatewayTcp){Error=6};r=await driver.ExecuteAsync(unknown,Target(AcquisitionProtocol.GatewayTcp),InstrumentCommand.MeasureIII,_=>Task.CompletedTask,false,default);Assert.Equal(ControlState.Unknown,r.State);Assert.Single(unknown.Writes);Assert.DoesNotContain(unknown.Writes,w=>w.Value==0);
    }

    [Theory][InlineData(AcquisitionProtocol.GatewayTcp)][InlineData(AcquisitionProtocol.GatewayRtu)]
    public async Task GatewayNormalActionRefusesUnresetStopBeforeAnyWrite(AcquisitionProtocol protocol)
    {
        await using var d=new Device(protocol){StopState=3,Mode=4};
        var r=await new ControlProtocolDriver().ExecuteAsync(d,Target(protocol),InstrumentCommand.MeasureI,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Rejected,r.State);Assert.Contains("独立停止",r.Message);Assert.Contains("复位",r.Message);Assert.Empty(d.Writes);
    }
    [Fact]public async Task GatewayRechecksBothChannelsAfterDurablePreparation()
    {
        await using var d=new Device(AcquisitionProtocol.GatewayTcp){BlockOnSecondRead=true};
        var r=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Rejected,r.State);Assert.Empty(d.Writes);
    }
    [Fact]public async Task GatewayExplicitBusyReplyIsARejectionNotAnUncertainMotion()
    {
        await using var d=new Device(AcquisitionProtocol.GatewayTcp){BusyReply=true};
        var r=await new ControlProtocolDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),InstrumentCommand.Hoist,_=>Task.CompletedTask,false,default);
        Assert.Equal(ControlState.Rejected,r.State);Assert.Contains("06",r.Message);Assert.Single(d.Writes);
    }
    [Fact]public async Task ResetRefusesChangedCycleBeforeAnyWrite()
    {
        var target=Target(AcquisitionProtocol.GatewayTcp);var words=new ushort[12];words[8]=3;words[11]=7;
        var expected=GatewayControlReader.Decode(words,target,DateTimeOffset.UtcNow);
        await using var d=new Device(AcquisitionProtocol.GatewayTcp){StopState=3,StopCount=8};
        var r=await new ControlProtocolDriver().ResetGatewayAsync(d,target,ControlCycle.IndependentStop,_=>Task.CompletedTask,default,expected);
        Assert.Equal(ControlState.Rejected,r.State);Assert.Contains("周期",r.Message);Assert.Empty(d.Writes);
    }
    [Fact]public async Task ResetRechecksCycleAfterConfirmationCheckpoint()
    {
        var target=Target(AcquisitionProtocol.GatewayTcp);await using var d=new Device(AcquisitionProtocol.GatewayTcp){StopState=3,StopCount=7};
        var words=new ushort[12];words[8]=3;words[11]=7;var expected=GatewayControlReader.Decode(words,target,DateTimeOffset.UtcNow);
        var r=await new ControlProtocolDriver().ResetGatewayAsync(d,target,ControlCycle.IndependentStop,_=>{d.StopCount++;return Task.CompletedTask;},default,expected);
        Assert.Equal(ControlState.Rejected,r.State);Assert.Empty(d.Writes);
    }
    [Fact]public void GatewayStatusKeepsFullWidthBootIdentityAndUnsignedCounters()
    {
        var target=Target(AcquisitionProtocol.GatewayTcp) with{GatewayBootId=18446744073709551614UL};var words=new ushort[12];words[4]=65535;words[5]=65534;
        var s=GatewayControlReader.Decode(words,target,DateTimeOffset.UtcNow);
        Assert.Equal("18446744073709551614",s.GatewayBootId);Assert.Equal(4294967294U,s.Normal.Count);
        Assert.Throws<InvalidDataException>(()=>GatewayControlReader.Decode([0,1],target,DateTimeOffset.UtcNow));words[8]=6;
        Assert.Throws<InvalidDataException>(()=>GatewayControlReader.Decode(words,target,DateTimeOffset.UtcNow));
    }
}
