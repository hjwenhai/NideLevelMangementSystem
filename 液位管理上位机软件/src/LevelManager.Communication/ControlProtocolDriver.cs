using System.Diagnostics;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Communication;

public enum ControlCheckpoint { AuthorizationWrite,PreparingWrite,MotionWrite,WriteAcknowledged }
public sealed record ControlProtocolResult(ControlState State,string Message,ushort? DeviceState=null,ushort? DeviceError=null,GatewayControlSnapshot? GatewayResult=null);
public sealed class ControlProtocolDriver(TimeProvider? timeProvider=null)
{
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    public async Task<ControlProtocolResult> ResetGatewayAsync(IRegisterClient client,ControlTarget target,ControlCycle cycle,
        Func<ControlCheckpoint,Task> checkpoint,CancellationToken ct,GatewayControlSnapshot? expected=null)
    {
        var sent=false;
        try
        {
            if(!target.Channel.IsGateway)throw new ArgumentException("仅网关使用此复位通道");await ReadFresh(client,target,ct);
            var unit=checked((byte)target.Channel.UnitId);var start=checked((ushort)(30000+12*(target.Tank.Address-1)));var index=cycle==ControlCycle.IndependentStop?8:1;
            var before=await client.ReadHoldingRegistersAsync(unit,start,12,ct);
            var actual=GatewayControlReader.Decode(before,target,clock.GetUtcNow());
            if(expected is not null)GatewayControlReader.RequireSame(actual,expected,cycle);
            if(before[index]==0)return new(ControlState.ReadbackConfirmed,"原通道已为空闲，未重复写入；未停止或重发动作",0,0);
            if(before[index] is 1 or 2)return new(ControlState.Rejected,"网关原通道仍在受理或执行，禁止复位",before[index],before[index+1]);
            if(before[index]>5)throw new InvalidDataException("网关状态未定义");
            await checkpoint(ControlCheckpoint.PreparingWrite);
            // A user may have opened this confirmation before another control cycle completed.
            await ReadFresh(client,target,ct);
            var immediatelyBefore=GatewayControlReader.Decode(await client.ReadHoldingRegistersAsync(unit,start,12,ct),target,clock.GetUtcNow());
            GatewayControlReader.RequireSame(immediatelyBefore,expected??actual,cycle);sent=true;
            await client.WriteSingleRegisterAsync(unit,cycle==ControlCycle.IndependentStop?(ushort)(start+7):start,0,ct);await checkpoint(ControlCheckpoint.WriteAcknowledged);
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))
            {
                var state=await client.ReadHoldingRegistersAsync(unit,start,12,ct);if(state[index]==0)return new(ControlState.ReadbackConfirmed,"仅选定的网关结果通道已复位；未停止浮子、未重发动作",0,0);await Delay(ct);
            }
            throw new TimeoutException("复位后的通道状态未确认");
        }
        catch(RegisterDispatchPreventedException e){return new(ControlState.Rejected,e.Message);}
        catch(Exception e)when(e is not OutOfMemoryException)
        {return new(sent?ControlState.Unknown:ControlState.Rejected,(sent?"复位结果未知，需核查；未重复复位。":"未发送复位：")+e.Message);}
    }
    public async Task<ControlProtocolResult> ExecuteAsync(IRegisterClient client,ControlTarget target,InstrumentCommand command,
        Func<ControlCheckpoint,Task> checkpoint,bool taskAlreadyAuthorized,CancellationToken ct,bool recoveryOnly=false)
    {
        if(command==InstrumentCommand.RemoteLevel)return new(ControlState.Rejected,"远程液位修改须使用带目标液位的参数接口，未发送写入");
        var motionMayHaveBeenSent=false;
        async Task MarkMotion(){await checkpoint(ControlCheckpoint.MotionWrite);motionMayHaveBeenSent=true;}
        try
        {
            return target.Channel.Protocol switch
            {
                AcquisitionProtocol.DirectRtu=>await Direct(client,target,command,checkpoint,MarkMotion,taskAlreadyAuthorized,recoveryOnly,ct),
                AcquisitionProtocol.IfxL25=>await Ifx(client,target,command,checkpoint,MarkMotion,recoveryOnly,ct),
                _=>await Gateway(client,target,command,checkpoint,MarkMotion,recoveryOnly,ct)
            };
        }
        catch(RegisterDispatchPreventedException e){return new(ControlState.Rejected,e.Message);}
        catch(Exception e) when(e is not OutOfMemoryException)
        {
            return new(motionMayHaveBeenSent?ControlState.Unknown:ControlState.Rejected,
                motionMayHaveBeenSent?"可能已发送动作，结果未知，需人工核查；未自动重发。"+e.Message:"未发送动作："+e.Message);
        }
    }
    private static ControlProtocolResult Rejected(ModbusDeviceException error,string source)
        =>new(ControlState.Rejected,$"{source}明确拒绝本次请求，Modbus功能{error.Function:X2}异常码{error.Code:X2}（{error.RejectionDescription}）。请检查设备设置及协议版本",DeviceError:error.Code);
    private static string ModeText(ushort? status,AcquisitionProtocol protocol)
    {
        if(!status.HasValue)return "未取得有效模式";
        var raw=status.Value;
        var command=Enum.GetValues<InstrumentCommand>().FirstOrDefault(c=>c!=InstrumentCommand.RemoteLevel&&
            (protocol!=AcquisitionProtocol.IfxL25||c<=InstrumentCommand.Density)&&CommandEncoding.ModeMatches(raw,c,protocol));
        return command==0?$"未识别（原始状态0x{raw:X4}）":$"{CommandEncoding.Name(command)}（原始状态0x{raw:X4}）";
    }
    private static string ConfirmationTimeout(ControlTarget target,InstrumentCommand command,ushort? status,TimeSpan elapsed,string stage)
        =>$"{stage}超时，设定{target.Tank.Controls.ConfirmationTimeoutSeconds}秒，已等待{elapsed.TotalSeconds:F1}秒；期望模式{CommandEncoding.Name(command)}，最后读取{ModeText(status,target.Channel.Protocol)}";
    private async Task Delay(CancellationToken ct)=>await Task.Delay(TimeSpan.FromMilliseconds(200),clock,ct);
    private static void RequireCommandState(ReadingSnapshot sample,ControlTarget target,InstrumentCommand command,bool recoveryOnly=false)
    {
        if(command==InstrumentCommand.Stop)return;
        if(recoveryOnly&&sample.InstrumentStatus.HasValue&&CommandEncoding.ModeMatches(sample.InstrumentStatus.Value,InstrumentCommand.MeasureI,target.Channel.Protocol))throw new InvalidOperationException("仪表已处于测量Ⅰ，无需重复恢复，未发送动作");
        if(!sample.InstrumentStatus.HasValue||!sample.InstrumentFault.HasValue)throw new InvalidOperationException("当前通讯、模式或故障状态未确认有效");
        // The instrument remote bit is informational for every action and recovery path.
        // A nonzero instrument fault is diagnostic; authorization and current-mode readback remain required.
    }
    private async Task<ReadingSnapshot> ReadFresh(IRegisterClient client,ControlTarget target,CancellationToken ct)
    {
        var sample=await new DeviceReader(target.Channel.Protocol,clock,controlRead:true).ReadAsync(client,new(target.Channel,target.Tank,target.ConfigurationRevision,target.AcquisitionSessionId),ct);
        if(target.Channel.IsGateway&&(sample.GatewayBootId!=target.GatewayBootId||sample.GatewayBindingVersion!=target.GatewayBindingVersion))throw new InvalidOperationException("网关启动标识或仪表绑定版本已变化");
        return sample;
    }
    private async Task<ControlProtocolResult> Direct(IRegisterClient client,ControlTarget target,InstrumentCommand command,
        Func<ControlCheckpoint,Task> checkpoint,Func<Task> motion,bool authorized,bool recoveryOnly,CancellationToken ct)
    {
        var sample=await ReadFresh(client,target,ct);RequireCommandState(sample,target,command,recoveryOnly);var unit=checked((byte)target.Tank.Address);
        if(!authorized){await checkpoint(ControlCheckpoint.AuthorizationWrite);await client.WriteMultipleRegistersAsync(unit,129,new ushort[]{0x10d9},ct);}
        await motion();
        try{await client.WriteMultipleRegistersAsync(unit,699,new ushort[]{CommandEncoding.DirectWord(command)},ct);}
        catch(ModbusDeviceException error)when(error.IsExplicitRejection){return Rejected(error,"直连仪表");}
        await checkpoint(ControlCheckpoint.WriteAcknowledged);
        var watch=Stopwatch.StartNew();ushort? lastMode=null;
        while(watch.Elapsed<TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))
        {
            var mode=await client.ReadHoldingRegistersAsync(unit,698,1,ct);var status=await client.ReadInputRegistersAsync(unit,51,2,ct);
            if(mode.Length!=1||status.Length!=2)throw new InvalidDataException("仪表回读长度不符");
            lastMode=status[0];
            if(CommandEncoding.ModeMatches(mode[0],command,AcquisitionProtocol.DirectRtu)&&CommandEncoding.ModeMatches(status[0],command,AcquisitionProtocol.DirectRtu))
                return new(ControlState.ReadbackConfirmed,"仪表模式回读已确认；此结果不表示浮子已到目标位置或机械运动结束",status[0],status[1]);
            await Delay(ct);
        }
        throw new TimeoutException(ConfirmationTimeout(target,command,lastMode,watch.Elapsed,"仪表模式回读"));
    }
    private async Task<ControlProtocolResult> Ifx(IRegisterClient client,ControlTarget target,InstrumentCommand command,
        Func<ControlCheckpoint,Task> checkpoint,Func<Task> motion,bool recoveryOnly,CancellationToken ct)
    {
        var sample=await ReadFresh(client,target,ct);RequireCommandState(sample,target,command,recoveryOnly);
        var unit=checked((byte)target.Channel.UnitId);var flag=checked((ushort)(100+target.Tank.Address-1));var register=checked((ushort)(500+3*(target.Tank.Address-1)));var value=CommandEncoding.IfxWord(command);
        var execute=await client.ReadCoilsAsync(unit,flag,1,ct);var completed=await client.ReadDiscreteInputsAsync(unit,flag,1,ct);
        if(execute[0]||completed[0])
        {
            await checkpoint(ControlCheckpoint.PreparingWrite);await client.WriteCoilAsync(unit,flag,false,ct);
            var reset=Stopwatch.StartNew();
            while((await client.ReadCoilsAsync(unit,flag,1,ct))[0]||(await client.ReadDiscreteInputsAsync(unit,flag,1,ct))[0])
            {if(reset.Elapsed>=TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))throw new TimeoutException("IFX旧执行/完成标志未清零，未触发新动作");await Delay(ct);}
        }
        await checkpoint(ControlCheckpoint.PreparingWrite);
        try{await client.WriteSingleRegisterAsync(unit,register,value,ct);}
        catch(ModbusDeviceException error)when(error.IsExplicitRejection){return Rejected(error,"IFX接口单元");}
        await motion();
        try{await client.WriteCoilAsync(unit,flag,true,ct);}
        catch(ModbusDeviceException error)when(error.IsExplicitRejection){return Rejected(error,"IFX接口单元");}
        await checkpoint(ControlCheckpoint.WriteAcknowledged);
        var watch=Stopwatch.StartNew();
        while(!(await client.ReadDiscreteInputsAsync(unit,flag,1,ct))[0])
        {if(watch.Elapsed>=TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))throw new TimeoutException(ConfirmationTimeout(target,command,null,watch.Elapsed,"IFX本次完成标志等待"));await Delay(ct);}
        // Clear only after a new completion edge; an old ON was explicitly excluded above.
        await client.WriteCoilAsync(unit,flag,false,ct);
        while((await client.ReadCoilsAsync(unit,flag,1,ct))[0]||(await client.ReadDiscreteInputsAsync(unit,flag,1,ct))[0])
        {if(watch.Elapsed>=TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))throw new TimeoutException(ConfirmationTimeout(target,command,null,watch.Elapsed,"IFX完成后标志复位等待"));await Delay(ct);}
        ushort? lastMode=null;var lastPosition="尚未取得位置";
        while(watch.Elapsed<TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))
        {
            sample=await ReadFresh(client,target,ct);lastMode=sample.InstrumentStatus;
            lastPosition=sample.LevelMm.IsValid?"浮子位置有效":sample.LevelMm.Reason??"浮子位置无效";
            if(sample.InstrumentFault.HasValue&&lastMode.HasValue&&CommandEncoding.ModeMatches(lastMode.Value,command,AcquisitionProtocol.IfxL25))
                return new(ControlState.ReadbackConfirmed,"IFX本次握手与实际模式回读已确认；机械停止和到位仍需单独判定",lastMode,sample.InstrumentFault);
            await Delay(ct);
        }
        throw new TimeoutException(ConfirmationTimeout(target,command,lastMode,watch.Elapsed,"IFX握手后模式回读")+"；"+lastPosition);
    }
    private async Task<ControlProtocolResult> Gateway(IRegisterClient client,ControlTarget target,InstrumentCommand command,
        Func<ControlCheckpoint,Task> checkpoint,Func<Task> motion,bool recoveryOnly,CancellationToken ct)
    {
        var sample=await ReadFresh(client,target,ct);RequireCommandState(sample,target,command,recoveryOnly);
        var unit=checked((byte)target.Channel.UnitId);var start=checked((ushort)(30000+12*(target.Tank.Address-1)));var stop=command==InstrumentCommand.Stop;
        var before=await client.ReadHoldingRegistersAsync(unit,start,12,ct);var index=stop?8:1;
        GatewayControlReader.RequireAvailable(before,target,command);
        await checkpoint(ControlCheckpoint.PreparingWrite);
        before=await client.ReadHoldingRegistersAsync(unit,start,12,ct);
        GatewayControlReader.RequireAvailable(before,target,command);
        await motion();
        try{await client.WriteSingleRegisterAsync(unit,stop?(ushort)(start+7):start,stop?(ushort)1:(ushort)command,ct);}
        catch(ModbusDeviceException e)when(e.IsExplicitRejection){return Rejected(e,"网关");}
        await checkpoint(ControlCheckpoint.WriteAcknowledged);
        var watch=Stopwatch.StartNew();ushort? lastMode=null;ushort lastResult=0;
        while(watch.Elapsed<TimeSpan.FromSeconds(target.Tank.Controls.ConfirmationTimeoutSeconds))
        {
            var result=await client.ReadHoldingRegistersAsync(unit,start,12,ct);
            var observed=GatewayControlReader.Decode(result,target,clock.GetUtcNow());var state=result[index];var error=result[index+1];lastResult=state;
            if(state==5)return new(ControlState.Unknown,"网关报告结果不确定；需按原通道人工核查，未重发",state,error);
            if(state==4){await ReadFresh(client,target,ct);return new(ControlState.Rejected,"网关报告失败或取消；失败原因已保留，不代表机械停止",state,error,GatewayControlReader.OwnResult(before,observed,target,command));}
            if(state==3)
            {
                sample=await ReadFresh(client,target,ct);lastMode=sample.InstrumentStatus;
                if(!sample.InstrumentFault.HasValue||!sample.InstrumentStatus.HasValue||!CommandEncoding.ModeMatches(sample.InstrumentStatus.Value,command,target.Channel.Protocol)){await Delay(ct);continue;}
                if(error!=0)throw new InvalidDataException("网关确认状态与错误码不一致，需核查");
                return new(ControlState.ReadbackConfirmed,"网关回读与本罐模式已确认；机械到位仍需单独判定",state,error,GatewayControlReader.OwnResult(before,observed,target,command));
            }
            if(state>5)throw new InvalidDataException("网关控制状态未定义");await Delay(ct);
        }
        throw new TimeoutException(ConfirmationTimeout(target,command,lastMode,watch.Elapsed,"网关本次控制结果等待")+$"；最后网关结果状态{lastResult}");
    }
}
