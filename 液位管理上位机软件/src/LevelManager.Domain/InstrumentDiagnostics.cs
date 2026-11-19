namespace Naide.LevelManager.Domain;
public sealed record InstrumentDiagnostic(string Code,int Bit,string Description,DateTimeOffset FirstDetectedAtUtc)
{
 // Customer-confirmed mapping is keyed by protocol and bit, independent of translated labels.
 public string? DisplayCode => Code switch {
  "FW.0x0001" or "IFX.0x0010"=>"1", "FW.0x0002" or "IFX.0x8000"=>"2",
  "FW.0x0004" or "IFX.0x4000"=>"3", "FW.0x0010" or "IFX.0x0400"=>"5",
  "FW.0x0020" or "IFX.0x0800"=>"6", "FW.0x0040" or "IFX.0x0002"=>"7",
  "FW.0x0080" or "IFX.0x0020"=>"8", "FW.0x0100" or "IFX.0x0004"=>"9",
  "FW.0x0200" or "IFX.0x0008"=>"A", "FW.0x0400" or "IFX.0x2000"=>"B",
  "FW.0x0800"=>"C", "FW.0x2000"=>"E", "FW.0x8000" or "IFX.0x1000"=>"G",_=>null};
}
public sealed record InstrumentFaultState(Guid TankId,string BindingKey,IReadOnlyList<InstrumentDiagnostic> Faults);
public static class InstrumentFaultCatalog
{
 private static readonly string[] Direct=["电机错误","张力过松","张力过紧","线鼓错误","重复性错误","温度计断线","温度计超量程","浮子控制错误","模拟量输入超量程","参数错误","平衡器错误","密度测量异常","保留位已置位，请核对协议","编码器错误","保留位已置位，请核对协议","内部错误"];
 private static readonly string[] Ifx=["不适用位已置位，请核对协议","温度计超量程","模拟量输入超量程","参数错误","电机错误","浮子控制错误","电源未连接","电源异常","不适用位已置位，请核对协议","不适用位已置位，请核对协议","重复性异常","温度计未连接","内部错误","平衡器信号异常","张力过紧","张力过松"];
 public static IReadOnlyList<InstrumentDiagnostic> Observe(ushort raw,AcquisitionProtocol protocol,IReadOnlyList<InstrumentDiagnostic> previous,DateTimeOffset now)
 {
  var ifx=protocol==AcquisitionProtocol.IfxL25;var labels=ifx?Ifx:Direct;List<InstrumentDiagnostic> faults=[];for(var bit=0;bit<16;bit++)if((raw&(1<<bit))!=0){var code=(ifx?"IFX":"FW")+".0x"+(1<<bit).ToString("X4");faults.Add(new(code,bit,labels[bit],previous.FirstOrDefault(p=>p.Code==code)?.FirstDetectedAtUtc??now));}return faults.OrderBy(f=>f.FirstDetectedAtUtc).ThenByDescending(f=>f.Bit).ToArray();
 }
}
