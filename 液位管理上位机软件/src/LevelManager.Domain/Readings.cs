namespace Naide.LevelManager.Domain;
public enum ValueQuality { Good, Missing, Offline, Expired, Fault, Unconfigured }
public sealed record Measurement(decimal? Value,ValueQuality Quality,string? Reason=null)
{
 public bool IsValid=>Value.HasValue&&Quality==ValueQuality.Good;
 public static Measurement Good(decimal value)=>new(value,ValueQuality.Good);
 public static Measurement Missing(string reason)=>new(null,ValueQuality.Missing,reason);
}
public sealed record ReadingSnapshot
{
 public Guid TankId {get;init;}
 public long ConfigurationRevision {get;init;}
 public Guid AcquisitionSessionId {get;init;}
 public DateTimeOffset ReceivedAtUtc {get;init;}
 public DateTimeOffset? SourceAtUtc {get;init;}
 public bool SourceTimeConfirmed {get;init;}
 public Measurement LevelMm {get;init;}=Measurement.Missing("尚未采集");
 public Measurement FloatPositionMm {get;init;}=new(null,ValueQuality.Unconfigured);
 public decimal? LastMeasuredLevelMm {get;init;}
 public DateTimeOffset? LastMeasuredLevelAtUtc {get;init;}
 public DateTimeOffset? FrozenSurfaceAtUtc {get;init;}
 public string SurfaceBasis {get;init;}="";
 public Guid? AverageTaskId {get;init;}
 public Measurement InstantTemperatureC {get;init;}=Measurement.Missing("尚未采集");
 public Measurement InstrumentDensityKgM3 {get;init;}=Measurement.Missing("尚未采集");
 public DateTimeOffset? AverageCompletedAtUtc {get;init;}
 public Measurement AverageTemperatureC {get;init;}=new(null,ValueQuality.Unconfigured);
 public IReadOnlyList<TemperaturePoint> AtsPoints {get;init;}=[];
 public Measurement Analog1Ma {get;init;}=new(null,ValueQuality.Unconfigured);
 public Measurement Analog2Ma {get;init;}=new(null,ValueQuality.Unconfigured);
 public uint? LevelSequence {get;init;}
 public uint? GatewayBindingVersion {get;init;}
 public ulong? GatewayBootId {get;init;}
 public bool FreshPositionReadConfirmed {get;init;}
 public Measurement Pressure1KPa {get;init;}=new(null,ValueQuality.Unconfigured);
 public Measurement Pressure2KPa {get;init;}=new(null,ValueQuality.Unconfigured);
 public long CalculationRevision {get;init;}
 public Measurement CurrentVolumeM3 {get;init;}=new(null,ValueQuality.Unconfigured);
 public Measurement NetVolumeM3 {get;init;}=new(null,ValueQuality.Unconfigured);
 public Measurement WaterVolumeM3 {get;init;}=new(null,ValueQuality.Unconfigured);
 public Measurement MassT {get;init;}=new(null,ValueQuality.Unconfigured);
 public IReadOnlyList<string> CalculationNotes {get;init;}=[];
 public IReadOnlyList<InstrumentDiagnostic> InstrumentFaults {get;init;}=[];
 public bool FaultTrackingPersisted {get;init;}=true;
 public ushort? InstrumentStatus {get;init;}
 public ushort? InstrumentFault {get;init;}
 public ushort RawQuality {get;init;}
 public ushort RawValidity {get;init;}
 public uint? DeviceDataAgeMs {get;init;}
 public string Diagnostic {get;init;}="";
 public ReadingSnapshot ForCurrentState(DateTimeOffset now,Guid currentSession,long currentRevision,int expirySeconds,bool running,bool enabled,TemperatureMode temperatureMode)
 {
  ValueQuality? quality=null;string? reason=null;
  if(!enabled){quality=ValueQuality.Unconfigured;reason="储罐或采集通道已停用";}
  else if(!running){quality=ValueQuality.Offline;reason="采集未运行";}
  else if(AcquisitionSessionId!=currentSession||ConfigurationRevision!=currentRevision){quality=ValueQuality.Missing;reason="等待当前采集会话和配置的新读数";}
  else if(ReceivedAtUtc==default||now<ReceivedAtUtc||now-ReceivedAtUtc>TimeSpan.FromSeconds(expirySeconds)){quality=ValueQuality.Expired;reason="数据过期或接收时间异常";}
  var result=quality is null?this:this with{LevelMm=new(null,quality.Value,reason),FloatPositionMm=new(null,quality.Value,reason),InstantTemperatureC=new(null,quality.Value,reason),InstrumentDensityKgM3=new(null,quality.Value,reason),AverageTemperatureC=temperatureMode==TemperatureMode.Wireless&&AverageTaskId.HasValue&&AverageCompletedAtUtc.HasValue?AverageTemperatureC:new(null,quality.Value,reason),AtsPoints=AtsPoints.Select(p=>p with{TemperatureC=new(null,quality.Value,reason)}).ToArray(),Analog1Ma=new(null,quality.Value,reason),Analog2Ma=new(null,quality.Value,reason),FreshPositionReadConfirmed=false,Pressure1KPa=new(null,quality.Value,reason),Pressure2KPa=new(null,quality.Value,reason),WaterVolumeM3=new(null,quality.Value,reason),CurrentVolumeM3=new(null,quality.Value,reason),NetVolumeM3=new(null,quality.Value,reason),MassT=new(null,quality.Value,reason),InstrumentStatus=null,InstrumentFault=null,InstrumentFaults=[],Diagnostic=reason!,RawValidity=0};
  return temperatureMode==TemperatureMode.None?result with{InstantTemperatureC=new(null,ValueQuality.Unconfigured,"未配置温度计"),AverageTemperatureC=new(null,ValueQuality.Unconfigured,"未配置温度计"),AtsPoints=[]}:result;
 }
}
