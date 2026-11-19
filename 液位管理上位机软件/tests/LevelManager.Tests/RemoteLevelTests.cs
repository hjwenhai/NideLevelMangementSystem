using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
using Naide.LevelManager.Communication;
using Xunit;
namespace Naide.LevelManager.Tests;

public class RemoteLevelTests
{
    private sealed class Device(bool gateway=false,int slot=1):IRegisterClient
    {
        public readonly List<(int Function,ushort Address,ushort[] Values)> Writes=[];
        public int Current=150000,Offset=-5000;
        public bool LoseOffsetReply,LoseAuthReply,FailClear,WrongReadback,CancelAfterAuth,NoExtension,Busy,RebootAfterStaging,LoseSubmitReply;
        public ushort GatewayResult=3,GatewayError;
        public byte? SubmitRejection;
        public ushort InstrumentMode=0x8001,OptionalDensityState=9,ActualStatusState=9,InstrumentFault;
        public CancellationTokenSource? Cancel;
        public ushort[] Staged=[0,0];public bool Submitted,Prepared;public bool ExtensionRead;
        public ValueTask<ushort[]> ReadInputRegistersAsync(byte u,ushort s,ushort n,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(!gateway)
            {
                if(s==0){var a=new ushort[n];var v=RemoteLevelRules.Words(Current);a[0]=v[0];a[1]=v[1];if(n>26)a[26]=2500;return ValueTask.FromResult(a);}
                return ValueTask.FromResult(new ushort[]{8320,InstrumentMode,InstrumentFault}.Skip(s-50).Take(n).ToArray());
            }
            if(s==27){ExtensionRead=true;return ValueTask.FromResult(new ushort[]{16,3,12,NoExtension?(ushort)0:(ushort)1});}
            var h=new ushort[30];h[2]=3;h[3]=64;h[9]=(ushort)(Prepared&&RebootAfterStaging?92:91);h[27]=16;h[28]=3;h[29]=12;
            if(s==0)return ValueTask.FromResult(h);if(s==6)return ValueTask.FromResult(h[6..10]);
            if(s==1000+24*(slot-1)){var a=new ushort[24];a[0]=3;a[1]=127;a[3]=42;var w=RemoteLevelRules.Words(Current);a[4]=w[0];a[5]=w[1];a[6]=2500;a[7]=8320;a[9]=InstrumentMode;a[10]=InstrumentFault;a[21]=1;return ValueTask.FromResult(a);}
            var d=new ushort[144];d[55]=1;d[57]=42;for(var i=0;i<6;i++)d[64+i*12]=9;d[64+4*12]=OptionalDensityState;d[64+2*12]=ActualStatusState;
            return ValueTask.FromResult(d.Skip(s-(3000+144*(slot-1))).Take(n).ToArray());
        }
        public ValueTask<ushort[]> ReadHoldingRegistersAsync(byte u,ushort s,ushort n,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(s==503)return ValueTask.FromResult(RemoteLevelRules.Words(Offset+(WrongReadback&&Writes.Any(w=>w.Address==503)?1:0)));
            if(s==31000+4*(slot-1))return ValueTask.FromResult(new ushort[]{Staged[0],Staged[1],Prepared?(ushort)1:(ushort)0,0});
            var r=new ushort[12];r[0]=Submitted?(ushort)11:(ushort)0;r[1]=Submitted?GatewayResult:Busy?(ushort)3:(ushort)0;r[2]=Submitted?GatewayError:(ushort)0;return ValueTask.FromResult(r);
        }
        public ValueTask WriteSingleRegisterAsync(byte u,ushort s,ushort v,CancellationToken ct)
        {
            if(ct.IsCancellationRequested)throw new RegisterDispatchPreventedException(new OperationCanceledException(ct));
            Writes.Add((6,s,[v]));
            if(s==129&&v==0xea66){if(CancelAfterAuth)Cancel!.Cancel();if(LoseAuthReply)throw new TimeoutException("认证应答丢失");}
            if(s==129&&v==0&&FailClear)throw new TimeoutException("清零应答丢失");
            if(s==30000+12*(slot-1)){if(SubmitRejection.HasValue)throw new ModbusDeviceException(6,SubmitRejection.Value);Submitted=true;Prepared=false;if(LoseSubmitReply)throw new TimeoutException("提交应答丢失");}
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteMultipleRegistersAsync(byte u,ushort s,ReadOnlyMemory<ushort> v,CancellationToken ct)
        {
            if(ct.IsCancellationRequested)throw new RegisterDispatchPreventedException(new OperationCanceledException(ct));
            Writes.Add((16,s,v.ToArray()));
            if(s==503){Assert.Equal(2,v.Length);Offset=RemoteLevelRules.Signed(v.ToArray());if(LoseOffsetReply)throw new TimeoutException("偏移应答丢失");}
            else{Staged=v.ToArray();Prepared=true;}
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool[]> ReadCoilsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>throw new NotSupportedException();
        public ValueTask<bool[]> ReadDiscreteInputsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>throw new NotSupportedException();
        public ValueTask WriteCoilAsync(byte u,ushort s,bool v,CancellationToken ct)=>throw new NotSupportedException();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static ControlTarget Target(AcquisitionProtocol p=AcquisitionProtocol.DirectRtu,int slot=1)
    {
        var c=new ChannelDefinition{Protocol=p,UnitId=1};var t=new TankDefinition{ChannelId=c.Id,Address=slot,TemperatureMode=TemperatureMode.None,Controls=new(){ConfirmationTimeoutSeconds=3}};
        return new(c,t,1,Guid.NewGuid(),c.IsGateway?91UL:null,c.IsGateway?1U:null);
    }
    private static Task Done(ControlCheckpoint _)=>Task.CompletedTask;
    private static Task Save(LevelAdjustmentEvidence _)=>Task.CompletedTask;
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(6)]
    public async Task GatewayLevelExplicitRejectionNeverCreatesUnknownOrResubmits(int code)
    {
        await using var device=new Device(true){SubmitRejection=(byte)code};
        var result=await new RemoteLevelDriver().ExecuteAsync(device,Target(AcquisitionProtocol.GatewayTcp),15010,Done,Save,default);
        Assert.Equal(ControlState.Rejected,result.State);
        Assert.Contains($"异常码{code:X2}",result.Message);
        Assert.Single(device.Writes,w=>w.Address==30000);
        Assert.False(device.Submitted);
    }
    [Fact]public async Task DirectUsesFreshSignedInputsCorrectAuthenticationAndAtomicOffsetThenClears()
    {
        await using var d=new Device();var saved=new List<LevelAdjustmentEvidence>();
        var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(),15010m,Done,e=>{saved.Add(e);return Task.CompletedTask;},default);
        Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.Equal(-4900,d.Offset);
        Assert.Equal(new[]{(6,(ushort)129),(16,(ushort)503),(6,(ushort)129)},d.Writes.Select(w=>(w.Function,w.Address)));
        Assert.Equal(new ushort[]{0xea66},d.Writes[0].Values);Assert.Equal(new ushort[]{0xffff,0xecdc},d.Writes[1].Values);Assert.Equal(new ushort[]{0},d.Writes[2].Values);
        Assert.Equal(15000m,saved[0].CurrentLevelMm);Assert.Equal(-500m,saved[0].PreviousOffsetMm);Assert.Equal(-490m,saved[^1].ReadbackOffsetMm);Assert.True(saved[^1].AuthorizationClearConfirmed);
        Assert.DoesNotContain(d.Writes,w=>w.Address==699);
    }
    [Theory][InlineData(-10000)][InlineData(100000)][InlineData(1.01)]
    public async Task TargetRangeAndPrecisionAreRejectedBeforeAnyWrites(double value)
    {await using var d=new Device();await Assert.ThrowsAsync<ArgumentException>(()=>new RemoteLevelDriver().ExecuteAsync(d,Target(),(decimal)value,Done,Save,default));Assert.Empty(d.Writes);}
    [Fact]public async Task CalculatedOffsetOutOfRangeDoesNotAuthenticate()
    {await using var d=new Device{Current=-2147483647};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(),15010,Done,Save,default);Assert.Equal(ControlState.Rejected,r.State);Assert.Empty(d.Writes);}
    [Fact]public async Task FailedEvidencePersistencePreventsAllWrites()
    {await using var d=new Device();await Assert.ThrowsAsync<IOException>(()=>new RemoteLevelDriver().ExecuteAsync(d,Target(),15010,Done,_=>throw new IOException("存储不可用"),default));Assert.Empty(d.Writes);}
    [Fact]public async Task LostOffsetAcknowledgementUsesOneReadbackAndNeverRewrites()
    {await using var d=new Device{LoseOffsetReply=true};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(),15010,Done,Save,default);Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.Single(d.Writes,w=>w.Address==503);Assert.Equal((ushort)0,d.Writes.Last().Values[0]);}
    [Fact]public async Task LostAuthenticationAcknowledgementStillClearsWithoutWritingOffset()
    {await using var d=new Device{LoseAuthReply=true};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(),15010,Done,Save,default);Assert.Equal(ControlState.Rejected,r.State);Assert.DoesNotContain(d.Writes,w=>w.Address==503);Assert.Equal(2,d.Writes.Count);Assert.Equal((ushort)0,d.Writes[^1].Values[0]);}
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task UnconfirmedOffsetOrAuthenticationClearRemainsUnknown(bool wrong,bool clear)
    {await using var d=new Device{WrongReadback=wrong,FailClear=clear};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(),15010,Done,Save,default);Assert.Equal(ControlState.Unknown,r.State);Assert.Single(d.Writes,w=>w.Address==503);Assert.Single(d.Writes,w=>w.Address==129&&w.Values[0]==0);}
    [Fact]public async Task CancellationAfterAuthenticationUsesIndependentCleanupToken()
    {using var cancel=new CancellationTokenSource();await using var d=new Device{CancelAfterAuth=true,Cancel=cancel};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(),15010,Done,Save,cancel.Token);Assert.Equal(ControlState.Rejected,r.State);Assert.DoesNotContain(d.Writes,w=>w.Address==503);Assert.Equal((ushort)0,d.Writes[^1].Values[0]);}
    [Fact]public async Task RevokedPermissionBlocksOffsetButStillClearsOnlyOwnedAuthentication()
    {
        var d=new Device();await using var queued=new PriorityRegisterClient(d);var calls=0;
        using var guard=new RegisterWriteGuardScope(_=>++calls>1?Task.FromException(new UnauthorizedAccessException("权限已撤销")):Task.CompletedTask);
        var r=await new RemoteLevelDriver().ExecuteAsync(queued,Target(),15010,Done,Save,default);
        Assert.Equal(ControlState.Rejected,r.State);Assert.Equal(2,calls);Assert.DoesNotContain(d.Writes,w=>w.Address==503);Assert.Equal((ushort)0,d.Writes[^1].Values[0]);
    }
    [Theory][InlineData(AcquisitionProtocol.GatewayTcp)][InlineData(AcquisitionProtocol.GatewayRtu)]
    public async Task GatewayParameterAdjustmentWorksWithoutOptionalDensityOrMotionRemoteBit(AcquisitionProtocol protocol)
    {
        await using var d = new Device(true) { InstrumentMode=1, OptionalDensityState=4 };
        var result=await new RemoteLevelDriver().ExecuteAsync(d,Target(protocol),19372.1m,Done,Save,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State); Assert.Equal(2,d.Writes.Count);
    }
    [Theory][InlineData(3,0)]
    public async Task GatewayParameterAdjustmentStillRejectsMissingActualStatus(int statusState,int fault)
    {
        await using var d = new Device(true) { ActualStatusState=(ushort)statusState, InstrumentFault=(ushort)fault };
        var result=await new RemoteLevelDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),19372.1m,Done,Save,default);
        Assert.Equal(ControlState.Rejected,result.State); Assert.Empty(d.Writes);
    }
    [Fact]public async Task DirectParameterAdjustmentUsesAuthenticationAndReadbackWithoutRemoteBit()
    {
        await using var d = new Device { InstrumentMode=1 };
        var result=await new RemoteLevelDriver().ExecuteAsync(d,Target(),19372.1m,Done,Save,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State); Assert.Equal(3,d.Writes.Count);
        Assert.Equal(new ushort[]{0xea66},d.Writes[0].Values);Assert.Equal((ushort)503,d.Writes[1].Address);Assert.Equal(new ushort[]{0},d.Writes[2].Values);Assert.Equal((ushort)1,d.InstrumentMode);
    }
    [Theory][InlineData(AcquisitionProtocol.DirectRtu)][InlineData(AcquisitionProtocol.GatewayTcp)][InlineData(AcquisitionProtocol.GatewayRtu)]
    public async Task ParameterAdjustmentAllowsReportedFaultAndStillChecksOffsetReadback(AcquisitionProtocol protocol)
    {
        await using var d=new Device(protocol!=AcquisitionProtocol.DirectRtu){InstrumentMode=1,InstrumentFault=0xffff};
        var result=await new RemoteLevelDriver().ExecuteAsync(d,Target(protocol),19372.1m,Done,Save,default);
        Assert.Equal(ControlState.ReadbackConfirmed,result.State);Assert.Equal(protocol==AcquisitionProtocol.DirectRtu?3:2,d.Writes.Count);
    }
    [Fact]public async Task InvalidRawHeightStillPreventsParameterCalculationAndWrites()
    {
        await using var device=new Device{Current=2000000,InstrumentFault=0xffff};
        var result=await new RemoteLevelDriver().ExecuteAsync(device,Target(),15000,Done,Save,default);
        Assert.Equal(ControlState.Rejected,result.State);Assert.Empty(device.Writes);
    }
    [Theory][InlineData(AcquisitionProtocol.GatewayTcp,1)][InlineData(AcquisitionProtocol.GatewayRtu,64)]
    public async Task GatewayRequiresExtensionStagesTwoWordsThenSubmits11OnExistingNormalCycle(AcquisitionProtocol p,int slot)
    {await using var d=new Device(true,slot);var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(p,slot),15010,Done,Save,default);Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.True(d.ExtensionRead);Assert.Equal(2,d.Writes.Count);Assert.Equal((16,(ushort)(31000+4*(slot-1))), (d.Writes[0].Function,d.Writes[0].Address));Assert.Equal(new ushort[]{2,0x4a54},d.Writes[0].Values);Assert.Equal((ushort)(30000+12*(slot-1)),d.Writes[1].Address);Assert.Equal((ushort)11,d.Writes[1].Values[0]);}
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task UnsupportedOrBusyGatewayDoesNotStageOrSubmit(bool unsupported,bool busy)
    {await using var d=new Device(true){NoExtension=unsupported,Busy=busy};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),0,Done,Save,default);Assert.Equal(ControlState.Rejected,r.State);Assert.Empty(d.Writes);}
    [Fact]public async Task GatewayRebootAfterStagingPreventsSubmittingPreparedOldTarget()
    {await using var d=new Device(true){RebootAfterStaging=true};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),0,Done,Save,default);Assert.Equal(ControlState.Rejected,r.State);Assert.Single(d.Writes);Assert.Equal((ushort)31000,d.Writes[0].Address);}
    [Fact]public async Task GatewayLostSubmitReplyPollsWithoutResubmitOrAutomaticReset()
    {await using var d=new Device(true){LoseSubmitReply=true};var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),-1,Done,Save,default);Assert.Equal(ControlState.ReadbackConfirmed,r.State);Assert.Equal(new ushort[]{0xffff,0xfff6},d.Writes[0].Values);Assert.Single(d.Writes,w=>w.Values.Length==1&&w.Values[0]==11);}
    [Fact]public async Task GatewayAuthenticationClearErrorIsUnknownAndPreserved()
    {await using var d=new Device(true){GatewayResult=5,GatewayError=14};LevelAdjustmentEvidence? ev=null;var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(AcquisitionProtocol.GatewayTcp),15010,Done,e=>{ev=e;return Task.CompletedTask;},default);Assert.Equal(ControlState.Unknown,r.State);Assert.False(ev!.AuthorizationClearConfirmed);Assert.Equal((ushort)14,ev.GatewayError);Assert.DoesNotContain(d.Writes,w=>w.Values.Length==1&&w.Values[0]==0);}
    [Fact]public async Task IfxAndGenericMotionEntryCannotAccidentallyIssueCalibration()
    {await using var d=new Device();var r=await new RemoteLevelDriver().ExecuteAsync(d,Target(AcquisitionProtocol.IfxL25),0,Done,Save,default);Assert.Equal(ControlState.Rejected,r.State);r=await new ControlProtocolDriver().ExecuteAsync(d,Target(),InstrumentCommand.RemoteLevel,Done,false,default);Assert.Equal(ControlState.Rejected,r.State);Assert.Empty(d.Writes);}
    [Fact]public void CalibrationIsNotAutomaticallyGrantedToOldOperatorRole()
    {Assert.Contains(AppPermission.RemoteLevelAdjust,PermissionPolicy.Defaults(AccountRole.Administrator));Assert.DoesNotContain(AppPermission.RemoteLevelAdjust,PermissionPolicy.Defaults(AccountRole.Operator));Assert.Throws<ArgumentException>(()=>PermissionPolicy.Validate(PermissionPolicy.DefaultMatrix() with{Roles=PermissionPolicy.DefaultMatrix().Roles.Select(g=>g.Role==AccountRole.Viewer?g with{Permissions=[AppPermission.RemoteLevelAdjust]}:g).ToArray()}));}
}
