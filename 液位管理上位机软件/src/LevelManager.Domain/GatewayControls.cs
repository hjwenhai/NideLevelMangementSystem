namespace Naide.LevelManager.Domain;

public sealed record GatewayControlChannel(ushort Value,ushort State,ushort Error,uint Count)
{
    public bool CanReset=>State is 3 or 4 or 5;
}
public sealed record GatewayControlSnapshot(long ConfigurationRevision,Guid AcquisitionSessionId,
    string GatewayBootId,uint GatewayBindingVersion,DateTimeOffset ObservedAtUtc,
    GatewayControlChannel Normal,GatewayControlChannel IndependentStop)
{
    public GatewayControlChannel Channel(ControlCycle cycle)=>cycle switch
    {ControlCycle.Normal=>Normal,ControlCycle.IndependentStop=>IndependentStop,_=>throw new ArgumentException("请选择正常动作或独立停止通道")};
}
