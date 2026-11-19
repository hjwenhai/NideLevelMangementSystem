using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Communication;

public static class GatewayControlReader
{
    public static GatewayControlSnapshot? OwnResult(ushort[] before,GatewayControlSnapshot after,ControlTarget target,InstrumentCommand command)
    {
        var cycle=command==InstrumentCommand.Stop?ControlCycle.IndependentStop:ControlCycle.Normal;
        var previous=Decode(before,target,after.ObservedAtUtc).Channel(cycle);var current=after.Channel(cycle);
        // Only this submitted cycle may be acknowledged automatically. Never adopt someone else's latch.
        if(previous.State!=0||previous.Value!=0||previous.Error!=0||previous.Count>=uint.MaxValue-1||current.Count!=previous.Count+1||
            current.Value!=(command==InstrumentCommand.Stop?1:(ushort)command)||current.State is not(3 or 4))return null;
        if(current.State==3&&current.Error!=0)return null;
        return after;
    }
    public static GatewayControlSnapshot Decode(ushort[] words,ControlTarget target,DateTimeOffset observed)
    {
        if(words.Length!=12||words[1]>5||words[8]>5)throw new InvalidDataException("网关控制状态格式无效，不能判定空闲，请重新读取");
        if(target.GatewayBootId is null||target.GatewayBindingVersion is null)throw new InvalidOperationException("网关身份或本罐绑定尚未确认");
        GatewayControlChannel Row(int offset)=>new(words[offset],words[offset+1],words[offset+2],((uint)words[offset+4]<<16)|words[offset+5]);
        return new(target.ConfigurationRevision,target.AcquisitionSessionId,target.GatewayBootId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),target.GatewayBindingVersion.Value,observed,
            Row(0),new(words[7],words[8],words[9],((uint)words[10]<<16)|words[11]));
    }
    public static async Task<GatewayControlSnapshot> ReadAsync(IRegisterClient client,ControlTarget target,TimeProvider clock,CancellationToken ct)
    {
        if(!target.Channel.IsGateway)throw new ArgumentException("本罐不是网关接入，无网关结果通道");
        var unit=checked((byte)target.Channel.UnitId);
        var start=checked((ushort)(30000+12*(target.Tank.Address-1)));
        // Check boot and binding around the control frame; never expose an old device's state as current.
        var sample=await new DeviceReader(target.Channel.Protocol,clock).ReadAsync(client,new(target.Channel,target.Tank,target.ConfigurationRevision,target.AcquisitionSessionId),ct);
        if(sample.GatewayBootId!=target.GatewayBootId||sample.GatewayBindingVersion!=target.GatewayBindingVersion)throw new InvalidOperationException("网关已重启或本罐绑定已改变，请刷新后重新确认");
        var words=await client.ReadHoldingRegistersAsync(unit,start,12,ct);
        var boot=await client.ReadInputRegistersAsync(unit,6,4,ct);
        var binding=await client.ReadInputRegistersAsync(unit,checked((ushort)(1020+24*(target.Tank.Address-1))),2,ct);
        if(boot.Length!=4||binding.Length!=2)throw new InvalidDataException("网关身份读取长度无效");
        var bootId=((ulong)boot[0]<<48)|((ulong)boot[1]<<32)|((ulong)boot[2]<<16)|boot[3];
        if(bootId!=target.GatewayBootId||(((uint)binding[0]<<16)|binding[1])!=target.GatewayBindingVersion)throw new InvalidOperationException("网关身份在读取期间改变，请重新读取");
        return Decode(words,target,clock.GetUtcNow());
    }
    public static string Hint(GatewayControlChannel row,string name)=>row.State switch
    {
        0=>name+"通道读数不一致，请重新读取",
        1=>name+"通道已受理、排队中，请等待；不能复位",
        2=>name+"通道执行中，请等待；不能复位",
        3=>name+"结果待复位（回读已确认）；请先核对并复位该结果通道",
        4=>name+"结果待复位（明确失败或取消）；请查看错误并核查后复位",
        5=>name+"结果未知；须先核查现场，再复位该结果通道",
        _=>name+"状态未知，不能提交新动作"
    };
    public static void RequireAvailable(ushort[] words,ControlTarget target,InstrumentCommand command)
    {
        var s=Decode(words,target,DateTimeOffset.UtcNow);
        bool Idle(GatewayControlChannel r)=>r.State==0&&r.Value==0&&r.Error==0;
        if(command!=InstrumentCommand.Stop&&!Idle(s.Normal))throw new InvalidOperationException(Hint(s.Normal,"正常动作"));
        if(!Idle(s.IndependentStop))throw new InvalidOperationException(Hint(s.IndependentStop,"独立停止"));
    }
    public static void RequireSame(GatewayControlSnapshot actual,GatewayControlSnapshot expected,ControlCycle cycle)
    {
        if(actual.ConfigurationRevision!=expected.ConfigurationRevision||actual.AcquisitionSessionId!=expected.AcquisitionSessionId||
            actual.GatewayBootId!=expected.GatewayBootId||actual.GatewayBindingVersion!=expected.GatewayBindingVersion||
            actual.Channel(cycle)!=expected.Channel(cycle))throw new InvalidOperationException("该通道状态、周期或设备绑定已改变，未复位；请刷新并重新核查");
        if(actual.Channel(cycle).Count==uint.MaxValue)throw new InvalidOperationException("网关周期计数已饱和，无法核实原周期，未复位；请联系维护人员");
    }
}
