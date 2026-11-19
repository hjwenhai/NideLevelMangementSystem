namespace Naide.LevelManager.Domain;
public enum InstrumentCommand { MeasureI=1,MeasureII=2,MeasureIII=3,Stop=4,Hoist=5,Lower=6,Density=7,Repeatability=9,OscillationAvoidance=10,RemoteLevel=11 }
public enum ControlCycle { Normal,IndependentStop }
public enum ControlState { Prepared,Queued,Checking,WriteIntent,Submitted,ReadbackConfirmed,Rejected,Unknown,Reviewed }
public sealed record ControlSettings
{
    public IReadOnlyList<InstrumentCommand> AllowedCommands {get;init;}=[];
    public bool WirelessMeasurementEnabled {get;init;}
    public int ConfirmationTimeoutSeconds {get;init;}=30;
    public int MovementTimeoutSeconds {get;init;}=600;
}
public sealed record ControlTarget(ChannelDefinition Channel,TankDefinition Tank,long ConfigurationRevision,Guid AcquisitionSessionId,ulong? GatewayBootId,uint? GatewayBindingVersion);
public sealed record ControlRecord
{
    public Guid Id {get;init;}=Guid.NewGuid();
    public required ControlTarget Target {get;init;}
    public required InstrumentCommand? Command {get;init;}
    public required ControlCycle Cycle {get;init;}
    public ControlState State {get;init;}=ControlState.Prepared;
    public required string ActorId {get;init;}
    public required string ActorName {get;init;}
    public required DateTimeOffset CreatedAtUtc {get;init;}
    public DateTimeOffset? WriteIntentAtUtc {get;init;}
    public DateTimeOffset? FinishedAtUtc {get;init;}
    public string Result {get;init;}="";
    public string? ReviewReason {get;init;}
    public string? ReviewerId {get;init;}
    public string? ReviewerName {get;init;}
    public DateTimeOffset? ReviewedAtUtc {get;init;}
    public Guid? MeasurementTaskId {get;init;}
    public bool IsReset {get;init;}
    public bool IsAutomaticReset {get;init;}
    public bool IsRecovery {get;init;}
    public DateTimeOffset? FirstDispatchAtUtc {get;init;}
    public Guid? RelatedControlId {get;init;}
    public GatewayControlSnapshot? GatewayResetExpectation {get;init;}
    public GatewayControlSnapshot? GatewayResultSnapshot {get;init;}
    public decimal? TargetLevelMm {get;init;}
    public string? AdjustmentReason {get;init;}
    public LevelAdjustmentEvidence? LevelAdjustment {get;init;}
    public bool RequiresReview=>State is ControlState.WriteIntent or ControlState.Submitted or ControlState.Unknown;
}
public static class ControlReadiness
{
    public static void RequireCurrentData(ReadingSnapshot sample,InstrumentCommand command)
    {
        if(command==InstrumentCommand.Stop)return;
        List<string> missing=[];
        // A fault may invalidate the displayed measurement without invalidating command transport.
        // Parameter adjustment validates fresh raw height again in RemoteLevelDriver before writing.
        if(!sample.InstrumentStatus.HasValue)missing.Add("仪表状态未取得有效数据");
        if(!sample.InstrumentFault.HasValue)missing.Add("仪表故障码未取得有效数据");
        if(missing.Count>0)throw new InvalidOperationException("未受理操作："+string.Join("；",missing)+"。请确认采集已取得新的有效读数后再操作。");
    }
}
public static class CommandEncoding
{
    public static string Name(InstrumentCommand c)=>c switch{InstrumentCommand.MeasureI=>"测量Ⅰ",InstrumentCommand.MeasureII=>"测量Ⅱ（界面）",InstrumentCommand.MeasureIII=>"测量Ⅲ（罐底）",InstrumentCommand.Stop=>"停止",InstrumentCommand.Hoist=>"上升",InstrumentCommand.Lower=>"下降",InstrumentCommand.Density=>"远程密度",InstrumentCommand.Repeatability=>"重复性检查",InstrumentCommand.OscillationAvoidance=>"液体震荡回避",InstrumentCommand.RemoteLevel=>"远程液位修改",_=>throw new ArgumentException("指令未定义")};
    public static ushort DirectWord(InstrumentCommand c)=>c switch{InstrumentCommand.MeasureI=>1,InstrumentCommand.MeasureII=>16,InstrumentCommand.MeasureIII=>32,InstrumentCommand.Stop=>4,InstrumentCommand.Hoist=>2,InstrumentCommand.Lower=>256,InstrumentCommand.Density=>64,InstrumentCommand.Repeatability=>8,InstrumentCommand.OscillationAvoidance=>128,_=>throw new ArgumentException("指令未定义")};
    public static ushort IfxWord(InstrumentCommand c)=>c is >=InstrumentCommand.MeasureI and <=InstrumentCommand.Density?(ushort)c:throw new ArgumentException("IFX未确认支持该动作");
    public static bool IsRemote(ushort status,AcquisitionProtocol p)=>p==AcquisitionProtocol.IfxL25?(status&32)!=0:(status&0x8000)!=0;
    public static bool ModeMatches(ushort status,InstrumentCommand c,AcquisitionProtocol p)
    {
        if(p!=AcquisitionProtocol.IfxL25)return (status&0x01ff)==DirectWord(c);
        var expected=c switch{InstrumentCommand.MeasureI=>1,InstrumentCommand.Hoist=>2,InstrumentCommand.Lower=>3,InstrumentCommand.MeasureII=>4,InstrumentCommand.Stop=>5,InstrumentCommand.MeasureIII=>6,InstrumentCommand.Density=>7,_=>-1};
        return (status&7)==expected;
    }
}
