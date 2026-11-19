using System.Diagnostics;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Communication;

/// <summary>FW9000NN指示调整原件及GT6757X V3.2-D2液位修改扩展；所有地址均为零基。</summary>
public sealed class RemoteLevelDriver(TimeProvider? timeProvider=null)
{
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    public async Task<ControlProtocolResult> ExecuteAsync(IRegisterClient client,ControlTarget target,decimal targetMm,
        Func<ControlCheckpoint,Task> checkpoint,Func<LevelAdjustmentEvidence,Task> persist,CancellationToken ct)
    {
        RemoteLevelRules.ToRaw(targetMm);
        if(target.Channel.Protocol==AcquisitionProtocol.IfxL25)return new(ControlState.Rejected,"IFX30000未提供远程液位修改协议，未发送写入");
        if(target.Channel.Protocol==AcquisitionProtocol.DirectRtu)return await Direct(client,target,targetMm,checkpoint,persist,ct);
        return await Gateway(client,target,targetMm,checkpoint,persist,ct);
    }
    private async Task Fresh(IRegisterClient client,ControlTarget target,CancellationToken ct,bool requireHealthy=true)
    {
        var s=await new DeviceReader(target.Channel.Protocol,clock,controlRead:true).ReadAsync(client,new(target.Channel,target.Tank,target.ConfigurationRevision,target.AcquisitionSessionId),ct);
        if(requireHealthy&&(!s.LevelMm.IsValid||!s.InstrumentFault.HasValue||!s.InstrumentStatus.HasValue))throw new InvalidOperationException("当前原始位置、通讯或仪表状态未确认有效，未提交修改");
        if(target.Channel.IsGateway&&(s.GatewayBootId!=target.GatewayBootId||s.GatewayBindingVersion!=target.GatewayBindingVersion))throw new InvalidOperationException("网关启动标识或本罐绑定版本已变化，未提交修改");
    }
    private async Task<ControlProtocolResult> Direct(IRegisterClient client,ControlTarget target,decimal targetMm,
        Func<ControlCheckpoint,Task> checkpoint,Func<LevelAdjustmentEvidence,Task> persist,CancellationToken ct)
    {
        var unit=checked((byte)target.Tank.Address);var ev=new LevelAdjustmentEvidence(targetMm);
        var authMay=false;var offsetMay=false;var state=ControlState.Rejected;var message="尚未修改仪表";
        try
        {
            await Fresh(client,target,ct);
            var current=RemoteLevelRules.Signed(await client.ReadInputRegistersAsync(unit,0,2,ct));
            var old=RemoteLevelRules.Signed(await client.ReadHoldingRegistersAsync(unit,503,2,ct));
            var next=RemoteLevelRules.Offset(RemoteLevelRules.ToRaw(targetMm),current,old);
            ev=ev with{CurrentLevelMm=current/10m,PreviousOffsetMm=old/10m,NewOffsetMm=next/10m};
            await persist(ev); // Durable calculation evidence before authentication or parameter writes.
            await checkpoint(ControlCheckpoint.AuthorizationWrite);authMay=true;
            try{await client.WriteSingleRegisterAsync(unit,129,0xea66,ct);}
            catch(RegisterDispatchPreventedException){authMay=false;throw;}
            await checkpoint(ControlCheckpoint.MotionWrite);offsetMay=true;
            try{await client.WriteMultipleRegistersAsync(unit,503,RemoteLevelRules.Words(next),ct);}
            catch(RegisterDispatchPreventedException){offsetMay=false;throw;}
            catch(Exception e)when(e is IOException or TimeoutException){message="偏移写入应答未确认，已转只读核对："+e.Message;}
            var actual=RemoteLevelRules.Signed(await client.ReadHoldingRegistersAsync(unit,503,2,ct));
            ev=ev with{ReadbackOffsetMm=actual/10m};await checkpoint(ControlCheckpoint.WriteAcknowledged);
            state=actual==next?ControlState.ReadbackConfirmed:ControlState.Unknown;
            message=actual==next?"液位偏移回读一致，修改已确认；实际液位仍随工况变化，断电后请核对仪表参数是否保留":"液位偏移回读不一致，需人工核查；未自动补写";
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {state=offsetMay?ControlState.Unknown:ControlState.Rejected;message=(offsetMay?"液位修改结果未知，需核查原偏移及实际液位；未重发。":"未写入液位偏移：")+e.Message;}
        finally
        {
            if(authMay)
            {
                // Cleanup owns an independent bounded token, including after Stop or lost acknowledgements.
                using var cleanupGuard=new RegisterWriteGuardScope(_=>Task.CompletedTask);
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try{await client.WriteSingleRegisterAsync(unit,129,0,cleanup.Token);ev=ev with{AuthorizationClearConfirmed=true};}
                catch(Exception e)when(e is not OutOfMemoryException){ev=ev with{AuthorizationClearConfirmed=false};state=ControlState.Unknown;message+="；液位修改认证清零未确认，请核查仪表认证状态。"+e.Message;}
            }
            ev=ev with{AuthorizationAttempted=authMay,OffsetWriteMayHaveBeenSent=offsetMay};
            await persist(ev);
        }
        return new(state,message);
    }
    private async Task<ControlProtocolResult> Gateway(IRegisterClient client,ControlTarget target,decimal targetMm,
        Func<ControlCheckpoint,Task> checkpoint,Func<LevelAdjustmentEvidence,Task> persist,CancellationToken ct)
    {
        var sent=false;var ev=new LevelAdjustmentEvidence(targetMm);
        try
        {
            await Fresh(client,target,ct);var unit=checked((byte)target.Channel.UnitId);
            var caps=await client.ReadInputRegistersAsync(unit,27,4,ct);
            if(caps.Length!=4||caps[0]!=16||caps[1]!=3||caps[2]!=12||caps[3]!=1)throw new InvalidOperationException("网关未确认支持远程液位修改扩展，请核对网关软件版本");
            var control=checked((ushort)(30000+12*(target.Tank.Address-1)));var staging=checked((ushort)(31000+4*(target.Tank.Address-1)));
            var before=await client.ReadHoldingRegistersAsync(unit,control,12,ct);
            GatewayControlReader.RequireAvailable(before,target,InstrumentCommand.RemoteLevel);
            await persist(ev);await checkpoint(ControlCheckpoint.PreparingWrite);
            var words=RemoteLevelRules.Words(RemoteLevelRules.ToRaw(targetMm));
            await client.WriteMultipleRegistersAsync(unit,staging,words,ct);
            var prepared=await client.ReadHoldingRegistersAsync(unit,staging,4,ct);
            if(prepared.Length!=4||prepared[0]!=words[0]||prepared[1]!=words[1]||prepared[2]!=1)throw new InvalidOperationException("网关目标液位暂存未确认，未提交修改");
            await Fresh(client,target,ct); // A reboot or rebind invalidates the prepared target.
            before=await client.ReadHoldingRegistersAsync(unit,control,12,ct);
            GatewayControlReader.RequireAvailable(before,target,InstrumentCommand.RemoteLevel);
            await checkpoint(ControlCheckpoint.MotionWrite);sent=true;
            try{await client.WriteSingleRegisterAsync(unit,control,11,ct);}
            catch(RegisterDispatchPreventedException){sent=false;throw;}
            catch(ModbusDeviceException e)when(e.IsExplicitRejection){sent=false;return new(ControlState.Rejected,$"网关明确拒绝本次液位修改，Modbus功能{e.Function:X2}异常码{e.Code:X2}（{e.RejectionDescription}），请检查设备设置及协议版本");}
            catch(Exception e)when(e is IOException or TimeoutException){/* Only read the result; never repeat command 11. */}
            await checkpoint(ControlCheckpoint.WriteAcknowledged);
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))
            {
                var row=await client.ReadHoldingRegistersAsync(unit,control,12,ct);
                if(row.Length!=12||row[1]>5)throw new InvalidDataException("网关液位修改结果格式无效");
                if(row[1]!=0&&row[0]!=11)throw new InvalidDataException("网关结果已不属于本次液位修改，请核查唯一控制方");
                if(row[1]==3&&row[2]!=0)throw new InvalidDataException("网关确认状态与错误码不一致，需人工核查");
                if(row[1] is 3 or 4 or 5)
                {
                    ev=ev with{GatewayState=row[1],GatewayError=row[2],AuthorizationClearConfirmed=row[1]==3?true:row[2]==14?false:null};await persist(ev);
                    await Fresh(client,target,ct,false);
                    var owned=row[1] is 3 or 4?GatewayControlReader.OwnResult(before,GatewayControlReader.Decode(row,target,clock.GetUtcNow()),target,InstrumentCommand.RemoteLevel):null;
                    return row[1]==3?new(ControlState.ReadbackConfirmed,"网关已确认偏移回读及认证清零；实际液位仍随工况变化，断电后请核对仪表参数是否保留",row[1],row[2],owned):
                        new(row[1]==5?ControlState.Unknown:ControlState.Rejected,"网关液位修改"+(row[1]==5?"结果未知，须人工核查":"失败或取消")+"，错误码 "+row[2]+"；原操作不自动重发",row[1],row[2],owned);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200),clock,ct);
            }
            throw new TimeoutException("网关液位修改结果等待超时");
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {return new(sent?ControlState.Unknown:ControlState.Rejected,(sent?"液位修改可能已提交，需人工核查；未重发。":"未提交网关液位修改：")+e.Message);}
    }
}
