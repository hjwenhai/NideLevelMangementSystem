using System.Net;
using System.Text.RegularExpressions;
namespace Naide.LevelManager.Domain;

public enum AcquisitionProtocol { DirectRtu, IfxL25, GatewayRtu, GatewayTcp }
public enum TemperatureMode { None, Ats, Wireless }
public enum TankShape { Vertical, Sphere, FloatingRoof }
public enum SerialParity { None, Even, Odd }
public sealed record ChannelDefinition
{
 public Guid Id { get; init; } = Guid.NewGuid();
 public string Name { get; init; } = "";
 public AcquisitionProtocol Protocol { get; init; }
 public string PortName { get; init; } = "";
 public int BaudRate { get; init; } = 4800;
 public int DataBits { get; init; } = 8;
 public SerialParity Parity { get; init; } = SerialParity.Even;
 public int StopBits { get; init; } = 1;
 public string Host { get; init; } = "";
 public int TcpPort { get; init; } = 502;
 public int UnitId { get; init; } = 1;
 public int PollIntervalMs { get; init; } = 3000;
 public int ReadTimeoutMs { get; init; } = 1000;
 public int ReadRetries { get; init; } = 1;
 public bool Enabled { get; init; } = true;
 public bool IsGateway => Protocol is AcquisitionProtocol.GatewayRtu or AcquisitionProtocol.GatewayTcp;
 public string PhysicalKey => Protocol == AcquisitionProtocol.GatewayTcp
  ? "tcp:" + (IPAddress.TryParse(Host.Trim(), out var ip) ? ip.ToString() : Host.Trim()) + ":" + TcpPort
  : "serial:" + NormalizePort(PortName);
 public static string NormalizePort(string value)
 {
  var text = value.Trim().ToUpperInvariant();
  return text.StartsWith("COM", StringComparison.Ordinal) && int.TryParse(text.AsSpan(3), out var number)
   ? "COM" + number : text;
 }
}
public static class IfxAddressing
{
 public static int TableAddress(int slot,int station)=>checked((station-1)*32+slot-1);
 public static int InternalSlot(int address,int station){var slot=checked(address-(station-1)*32+1);if(slot is <1 or >32)throw new ArgumentException("IFX30000表地址应在"+((station-1)*32)+"～"+(station*32-1)+"之间");return slot;}
}
public sealed record TankDefinition
{
 public Guid Id { get; init; } = Guid.NewGuid();
 public string Code { get; init; } = "";
 public string Name { get; init; } = "";
 public string ProductNumber { get; init; } = "";
 public string Batch { get; init; } = "";
 public decimal? DiameterMm { get; init; }
 public decimal? NominalCapacityM3 { get; init; }
 public string Notes { get; init; } = "";
 public string InstrumentTag { get; init; } = "";
 public string Medium { get; init; } = "";
 public Guid? MediumId { get; init; }
 public DateTimeOffset? PinnedAtUtc { get; init; }
 public IReadOnlyList<decimal?> AtsHeightsMm { get; init; } = [];
 public ControlSettings Controls {get;init;}=new();
 public TrendPreferences Trends { get; init; } = new();
 public AlarmSettings Alarms { get; init; } = new();
 public PressureCalibration Pressure1 { get; init; } = new();
 public PressureCalibration Pressure2 { get; init; } = new();
 public string Zone { get; init; } = "";
 public Guid ChannelId { get; init; }
 public int Address { get; init; } = 1;
 public TankShape Shape { get; init; } = TankShape.Vertical;
 public decimal HeightMm { get; init; } = 12000m;
 public TemperatureMode TemperatureMode { get; init; }
 public int AtsPointCount { get; init; }
 public bool Enabled { get; init; } = true;
 public MeteringSettings Metering { get; init; } = new();
 public bool Hidden { get; init; }
}
public sealed record SystemConfiguration
{
 public long Revision { get; init; }
 public DisplaySettings Display { get; init; } = new();
 public IReadOnlyList<SnapshotSchedule> Schedules {get;init;}=[];
 public IReadOnlyList<TankGroup> Groups { get; init; } = [];
 public IReadOnlyList<MediumDefinition> Media { get; init; } = [];
 public IReadOnlyList<ChannelDefinition> Channels { get; init; } = [];
 public IReadOnlyList<TankDefinition> Tanks { get; init; } = [];
 public bool StartReadOnlyAcquisitionAtStartup {get;init;}
 public int HistoryIntervalSeconds { get; init; } = 60;
 public int RetentionMonths { get; init; } = 12;
 public int ReadingExpirySeconds { get; init; } = 30;
}
public sealed record ValidationIssue(string Field, string Message);
public sealed class ConfigurationException(IReadOnlyList<ValidationIssue> issues) : Exception("配置校验未通过")
{
 public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}
public static partial class ConfigurationRules
{
 public const int MaximumTanks = 128;
 public const int BaselineHistoryIntervalSeconds = 60;
 public const int BaselineRetentionMonths = 12;
 public const long BaselineAnnualRows = 67_276_800;
 [GeneratedRegex("^COM[0-9]{1,5}$", RegexOptions.CultureInvariant)]
 private static partial Regex PortPattern();
 public static IReadOnlyList<ValidationIssue> Validate(SystemConfiguration config)
 {
  List<ValidationIssue> errors = [];
  void Add(string f,string m) => errors.Add(new(f,m));
  if(config.Channels is null || config.Tanks is null) { Add("configuration","通道和储罐列表不能为空值"); return errors; }
  if(config.Tanks.Count>MaximumTanks) Add("tanks","全系统最多配置128个罐，停用罐也计入配置数量");
  if(config.Channels.Count>MaximumTanks) Add("channels","采集通道数量超出本期上限");
  if(config.Revision<0) Add("revision","配置版本无效");
  if(config.HistoryIntervalSeconds is <1 or >86400) Add("historyIntervalSeconds","历史间隔应为1～86400秒，性能基线为60秒");
  if(config.RetentionMonths is <1 or >12) Add("retentionMonths","历史保留期限应为1～12个月");
  if(config.ReadingExpirySeconds is <3 or >3600) Add("readingExpirySeconds","数据过期时间应为3～3600秒");
  HashSet<Guid> channelIds = []; HashSet<string> physical = new(StringComparer.OrdinalIgnoreCase);
  foreach(var c in config.Channels)
  {
   if(c is null) {Add("channels","通道不能为空值"); continue;}
   var field="channels/"+c.Id;
   if(c.Id==Guid.Empty || !channelIds.Add(c.Id)) Add(field,"通道编号为空或重复");
   if(string.IsNullOrWhiteSpace(c.Name) || c.Name.Length>100) Add(field+"/name","通道名称应为1～100字符");
   if(!Enum.IsDefined(c.Protocol)||!Enum.IsDefined(c.Parity)) Add(field,"协议或串口校验类型无效");
   if(c.ReadTimeoutMs is <100 or >30000 || c.PollIntervalMs is <500 or >60000 || c.ReadRetries is <0 or >2) Add(field,"轮询、超时或读取重试参数无效");
   if(c.UnitId is <1 or >247) Add(field+"/unitId","站号/Unit ID应为1～247");
   if(c.Protocol==AcquisitionProtocol.GatewayTcp)
   {
    if(c.Host is null || !IPAddress.TryParse(c.Host.Trim(),out _) || c.TcpPort is <1 or >65535) Add(field,"请输入有效的网关IP和TCP端口");
   }
   else
   {
    if(c.PortName is null || !PortPattern().IsMatch(c.PortName.Trim().ToUpperInvariant()) || !int.TryParse(c.PortName.Trim().AsSpan(3),out var number) || number is <1 or >65535) Add(field+"/portName","请选择采集主机的实际串口");
    if(c.DataBits!=8 || c.StopBits is <1 or >2) Add(field,"数据位应为8，停止位为1或2");
    if(c.BaudRate is not (1200 or 2400 or 4800 or 9600 or 19200 or 38400 or 57600 or 115200)) Add(field,"波特率无效");
    if(c.Protocol==AcquisitionProtocol.DirectRtu && c.BaudRate>9600) Add(field,"FW9000直连仅支持1200/2400/4800/9600");

   }
   if(c.Host is not null && c.PortName is not null && !physical.Add(c.PhysicalKey)) Add(field,"同一串口或网关TCP端点只能建立一个采集通道");
  }
  HashSet<Guid> tankIds=[];HashSet<string> codes=new(StringComparer.OrdinalIgnoreCase);HashSet<(Guid,int)> bindings=[];
  Dictionary<Guid,int> counts=[];
  foreach(var t in config.Tanks)
  {
   if(t is null){Add("tanks","储罐不能为空值");continue;}
   var field="tanks/"+t.Id;
   if(t.Id==Guid.Empty||!tankIds.Add(t.Id))Add(field,"储罐编号为空或重复");
   if(string.IsNullOrWhiteSpace(t.Code)||t.Code.Length>80||!codes.Add(t.Code.Trim()))Add(field+"/code","罐号为空、过长或重复");
   if(t.InstrumentTag is null||t.InstrumentTag.Length>100||t.Medium is null||t.Medium.Length>100||t.Zone is null||t.Zone.Length>100)Add(field,"位号、介质或罐区内容无效");

   if(t.Name is null||t.Name.Length>100)Add(field+"/name","储罐名称不能超过100字");
   if(t.ProductNumber is null||t.ProductNumber.Length>100)Add(field+"/productNumber","产品编号不能超过100字");
   if(t.Batch is null||t.Batch.Length>100)Add(field+"/batch","设备批次不能超过100字");
   if(t.Notes is null||t.Notes.Length>1000)Add(field+"/notes","备注不能超过1000字");
   if(t.DiameterMm is <=0 or >1000000)Add(field+"/diameterMm","罐径可留空；填写时应大于0且不超过1000000mm");
   if(t.NominalCapacityM3 is <=0 or >1000000000)Add(field+"/nominalCapacityM3","公称容量可留空；填写时应大于0且不超过1000000000m³");
   if(t.Metering is null){Add(field+"/metering","计量配置不能为空");continue;}
   if(t.Metering.WaterHeightMm is <0 || t.Metering.WaterHeightMm>t.HeightMm)Add(field+"/metering/waterHeightMm","水位应在0与罐高之间");
   if(t.Metering.WaterVolumeM3 is <0 or >1000000000 || t.Metering.StandardDensity20KgM3 is <=0 or >1000000)Add(field+"/metering","水体积或20℃标准密度无效");
   if(!Enum.IsDefined(t.Metering.DensitySource)||!Enum.IsDefined(t.Metering.BaseVolumeMethod)||!Enum.IsDefined(t.Metering.FractionalMillimetreRule)||t.Metering.ManualOperatingDensityKgM3 is <=0 or >1000000)Add(field+"/metering","工况密度或计量计算选项无效");
   if(t.Metering.Enabled&&t.Metering.BaseVolumeMethod==BaseVolumeMethod.MainPlusDecimal&&!t.Metering.DecimalTableId.HasValue)Add(field+"/metering","组合计算需要关联小数表");
   if(t.Metering.Enabled&&!t.Metering.MainTableId.HasValue)Add(field+"/metering","启用体积计算需要关联主罐容表");
   if(t.Metering.MainTableId==Guid.Empty||t.Metering.StaticPressureTableId==Guid.Empty||t.Metering.DecimalTableId==Guid.Empty)Add(field+"/metering","罐容表标识无效");
   if(t.HeightMm is <=0 or >100000)Add(field+"/heightMm","罐高应大于0且不超过100000mm");
   if(!Enum.IsDefined(t.Shape)||!Enum.IsDefined(t.TemperatureMode))Add(field,"罐型或温度类型无效");
   if(t.TemperatureMode==TemperatureMode.Ats && t.AtsPointCount is <1 or >16)Add(field,"ATS测点数应为1～16");
   var channel=config.Channels.FirstOrDefault(c=>c is not null&&c.Id==t.ChannelId);
   if(channel is null){Add(field+"/channelId","请绑定有效采集通道");continue;}
   var max=channel.IsGateway?64:channel.Protocol==AcquisitionProtocol.IfxL25?32:243;
   if(t.Address<1||t.Address>max)Add(field+"/address",$"设备地址或内部编号应为1～{max}");
   if(!bindings.Add((t.ChannelId,t.Address)))Add(field,"同一通道中的设备地址或内部罐号重复");
   counts[t.ChannelId]=counts.GetValueOrDefault(t.ChannelId)+1;
   if(counts[t.ChannelId]>(channel.IsGateway?64:32))Add(field,"单个通道的罐数超出对应设备容量");
  }
  if(!config.Tanks.Any(t=>t is null)&&!config.Channels.Any(c=>c is null))errors.AddRange(PresentationRules.Validate(config));
  return errors;
 }
 public static void RequireValid(SystemConfiguration config)
 {var errors=Validate(config);if(errors.Count!=0)throw new ConfigurationException(errors);}
 public static long EstimateRows(int tanks,int seconds,int days)
 {
  if(tanks is <0 or >MaximumTanks || seconds<1||days<1)throw new ArgumentOutOfRangeException(nameof(tanks));
  return checked((long)tanks*((86400L*days+seconds-1)/seconds));
 }
}

