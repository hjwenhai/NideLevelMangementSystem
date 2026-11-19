using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class InstrumentDiagnosticTests
{
 [Fact]public void FirmwareAndIfxFaultWordsUseTheirOwnDocumentedMappings()
 {
  var now=DateTimeOffset.UtcNow;Assert.Equal("密度测量异常",Assert.Single(InstrumentFaultCatalog.Observe(0x0800,AcquisitionProtocol.DirectRtu,[],now)).Description);Assert.Equal("温度计未连接",Assert.Single(InstrumentFaultCatalog.Observe(0x0800,AcquisitionProtocol.IfxL25,[],now)).Description);Assert.Equal("模拟量输入超量程",Assert.Single(InstrumentFaultCatalog.Observe(0x0100,AcquisitionProtocol.GatewayTcp,[],now)).Description);Assert.Contains("不适用",Assert.Single(InstrumentFaultCatalog.Observe(0x0100,AcquisitionProtocol.IfxL25,[],now)).Description);
 }
 [Fact]public async Task FirstDetectionSurvivesStoreReloadAndClearedFaultReturnsWithNewTime()
 {
  await using var f=new StoreFixture();await f.Initialize();var store=new SqliteInstrumentFaultStore(f.PathToDb);var tank=Guid.NewGuid();var now=DateTimeOffset.UtcNow;var first=InstrumentFaultCatalog.Observe(0x2000,AcquisitionProtocol.GatewayTcp,[],now);await store.SaveAsync(new(tank,"binding-one",first));var read=(await new SqliteInstrumentFaultStore(f.PathToDb).GetAsync(tank))!;var added=InstrumentFaultCatalog.Observe(0x2001,AcquisitionProtocol.GatewayTcp,read.Faults,now.AddMinutes(1));Assert.Equal(13,added[0].Bit);Assert.Equal(now,added[0].FirstDetectedAtUtc);var cleared=InstrumentFaultCatalog.Observe(1,AcquisitionProtocol.GatewayTcp,added,now.AddMinutes(2));var returned=InstrumentFaultCatalog.Observe(0x2001,AcquisitionProtocol.GatewayTcp,cleared,now.AddMinutes(3));Assert.Equal(0,returned[0].Bit);Assert.Equal(now.AddMinutes(3),returned[1].FirstDetectedAtUtc);Assert.Empty(InstrumentFaultCatalog.Observe(0,AcquisitionProtocol.GatewayTcp,returned,now.AddMinutes(4)));
 }
}
