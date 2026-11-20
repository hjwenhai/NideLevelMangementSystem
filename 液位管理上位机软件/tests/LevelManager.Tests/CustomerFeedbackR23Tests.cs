using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Naide.LevelManager.LicenseDesk;
using Xunit;

namespace Naide.LevelManager.Tests;
public sealed class CustomerFeedbackR23Tests
{
    private static readonly AuditActor Admin=new("r23","反馈测试",AccountRole.Administrator);
    private static CapacityTable MainTable()=>new(){Number="MAIN",Name="主表",Points=[new(0,0),new(10000,1000)]};
    [Theory][InlineData(0,1,1)][InlineData(31,1,32)][InlineData(32,2,1)][InlineData(63,2,32)][InlineData(128,5,1)][InlineData(159,5,32)]
    public void IfxPublicAddressMapsExactlyToInternalSlot(int address,int station,int slot)
    {Assert.Equal(slot,IfxAddressing.InternalSlot(address,station));Assert.Equal(address,IfxAddressing.TableAddress(slot,station));}
    [Theory][InlineData(31,2)][InlineData(64,2)][InlineData(-1,1)]
    public void IfxPublicAddressOutsideSelectedStationIsRejected(int address,int station)=>Assert.Throws<ArgumentException>(()=>IfxAddressing.InternalSlot(address,station));
    [Fact]public void IfxExportImportKeepsSlotsAndUsesPublicTableAddress()
    {
        var c=Fixtures.Configuration(2);c=c with{Channels=[c.Channels[0] with{Protocol=AcquisitionProtocol.IfxL25,UnitId=5,BaudRate=9600}],Tanks=c.Tanks.Select(t=>t with{InstrumentTag="LT"+t.Code}).ToArray()};
        var bytes=TankConfigurationExcel.Export(c,[]);using(var d=SpreadsheetDocument.Open(new MemoryStream(bytes),false)){var p=d.WorkbookPart!;var sh=p.Workbook!.Sheets!.Elements<Sheet>().Single(s=>s.Name=="罐体与通讯");var ws=(WorksheetPart)p.GetPartById(sh.Id!);var rows=ws.Worksheet!.Elements<SheetData>().Single().Elements<Row>().ToArray();var column=rows[0].Elements<Cell>().Select((cell,i)=>(cell,i)).Single(x=>x.cell.InnerText=="表地址（IFX按站号范围）").i;Assert.Equal("128",rows[1].Elements<Cell>().ElementAt(column).InnerText);Assert.Equal("129",rows[2].Elements<Cell>().ElementAt(column).InnerText);}
        var preview=TankConfigurationExcel.Preview(bytes,c,[]);Assert.Empty(preview.Issues);Assert.Equal(new[]{1,2},preview.Configuration.Tanks.Select(t=>t.Address));
    }
    [Theory][InlineData(0,0)][InlineData(1000,100)][InlineData(2000,200)]
    public void WaterHeightUsesBaseCapacityThenSubtractsOnlyOnce(int height,int water)
    {
        var main=MainTable();var pressure=new CapacityTable{Number="P",Name="静压",Kind=CapacityTableKind.StaticPressure,Points=[new(0,0),new(10000,10)]};
        var s=new MeteringSettings{Enabled=true,MainTableId=main.Id,WaterHeightMm=height,WaterVolumeM3=999,DensitySource=MeteringDensitySource.Manual,ManualOperatingDensityKgM3=800,StaticPressureTableId=pressure.Id};
        var r=MeteringCalculator.Calculate(s,Measurement.Good(5000),Measurement.Missing("未测密"),main,pressure,null);
        Assert.Equal(water,r.WaterVolumeM3.Value);Assert.Equal(504,r.CurrentVolumeM3.Value);Assert.Equal(504-water,r.NetVolumeM3.Value);Assert.Equal((504-water)*.8m,r.MassT.Value);
    }
    [Fact]public void OldWaterVolumeIsPreservedWhenWaterHeightWasNeverSet()
    {var main=MainTable();var r=MeteringCalculator.Calculate(new(){Enabled=true,WaterVolumeM3=12},Measurement.Good(5000),Measurement.Good(800),main,null,null);Assert.Equal(488,r.NetVolumeM3.Value);Assert.Equal(12,r.WaterVolumeM3.Value);}
    [Theory]
    [InlineData(ValueQuality.Missing, 1000, 100)]
    [InlineData(ValueQuality.Expired, 1000, 100)]
    [InlineData(ValueQuality.Offline, 0, 0)]
    [InlineData(ValueQuality.Fault, 10001, null)]
    public void ManualWaterVolumeUsesOnlyHeightAndCapacityWhenLiquidLevelIsUnavailable(
        ValueQuality levelQuality, int waterHeightMm, int? expectedWaterVolume)
    {
        var settings = new MeteringSettings { Enabled = true, WaterHeightMm = waterHeightMm, StaticPressureTableId = Guid.NewGuid() };
        var result = MeteringCalculator.Calculate(settings, new(null, levelQuality), Measurement.Missing("无密度"), MainTable(), null, null);
        Assert.Equal((decimal?)expectedWaterVolume, result.WaterVolumeM3.Value);
        Assert.Equal(levelQuality, result.NetVolumeM3.Quality);
        Assert.Null(result.CurrentVolumeM3.Value); Assert.Null(result.MassT.Value);
    }
    [Theory][InlineData(-1)][InlineData(5001)]public void WaterAboveSurfaceOrNegativeDoesNotProduceInventory(int h)
    {var r=MeteringCalculator.Calculate(new(){Enabled=true,WaterHeightMm=h},Measurement.Good(5000),Measurement.Good(800),MainTable(),null,null);Assert.False(r.NetVolumeM3.IsValid);Assert.Contains("水位",r.Notes.Single());}
    [Fact]public void WaterHeightRoundTripsConfigurationExcelWithLegacyVolumeRetained()
    {var c=Fixtures.Configuration();c=c with{Tanks=[c.Tanks[0] with{Metering=new(){WaterHeightMm=120.5m,WaterVolumeM3=4.25m}}]};var p=TankConfigurationExcel.Preview(TankConfigurationExcel.Export(c,[]),c,[]);Assert.Empty(p.Issues);Assert.Equal(c.Tanks[0].Metering,p.Configuration.Tanks.Single().Metering);}
    [Fact]public void ExportWithoutOptionalAuthorizationOmitsOptionalColumnsAndRoundTripsBaseSettings()
    {
        var c=Fixtures.Configuration();var bytes=TankConfigurationExcel.Export(c,[],false,false,[]);using(var d=SpreadsheetDocument.Open(new MemoryStream(bytes),false)){var text=d.WorkbookPart!.WorksheetParts.SelectMany(w=>w.Worksheet!.Descendants<Text>()).Select(t=>t.Text).ToArray();Assert.DoesNotContain("允许下降",text);Assert.DoesNotContain(text,t=>t.Contains("允许远程液位"));}
        var p=TankConfigurationExcel.Preview(bytes,c,[],includeAverage:false);Assert.Empty(p.Issues);
    }
    [Fact]public async Task DeletedLegacyTankRemainsRecognizableForRestoringOriginalIdentity()
    {await using var f=new StoreFixture();await f.Initialize();var c=Fixtures.Configuration();var saved=await f.Store.SaveAsync(c,Admin);var id=saved.Tanks[0].Id;await f.Store.SaveAsync(saved with{Tanks=[]},Admin);Assert.True(await f.Store.WasTankDefinedAsync(id));Assert.False(await f.Store.WasTankDefinedAsync(Guid.NewGuid()));}
    [Fact]public async Task DeleteRemovesCurrentTableButPreservesVersionAndAuditAcrossRestart()
    {await using var f=new StoreFixture();await f.Initialize();var table=await f.Store.SaveTableAsync(MainTable(),Admin);Assert.NotNull(await f.Store.GetTableAsync(table.Id));await f.Store.DeleteTableAsync(table.Id,table.Revision,Admin);Assert.Empty(await f.Store.ListTablesAsync());Assert.Null(await f.Store.GetTableAsync(table.Id));Assert.NotNull(await f.Store.GetTableAsync(table.Id,1));var other=new SqliteConfigurationStore(f.PathToDb);Assert.Null(await other.GetTableAsync(table.Id));Assert.NotNull(await other.GetTableAsync(table.Id,1));Assert.Contains(await f.Store.QueryEventsAsync(new()),e=>e.Content.Contains("删除罐容表"));await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.SaveTableAsync(table,Admin));}
    [Fact]public async Task DeleteRejectsLinkedTablesAndStaleRevisionWithoutLosingHistory()
    {await using var f=new StoreFixture();await f.Initialize();var t=await f.Store.SaveTableAsync(MainTable(),Admin);var c=Fixtures.Configuration();await f.Store.SaveAsync(c with{Tanks=[c.Tanks[0] with{Metering=new(){MainTableId=t.Id}}]},Admin);await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.DeleteTableAsync(t.Id,t.Revision,Admin));await Assert.ThrowsAsync<RevisionConflictException>(()=>f.Store.DeleteTableAsync(t.Id,0,Admin));Assert.NotNull(await f.Store.GetTableAsync(t.Id));}
    [Fact]public async Task DeletedTableCannotBeReboundByAStaleConfigurationWindow()
    {await using var f=new StoreFixture();await f.Initialize();var t=await f.Store.SaveTableAsync(MainTable(),Admin);await f.Store.DeleteTableAsync(t.Id,1,Admin);var c=Fixtures.Configuration();await Assert.ThrowsAsync<ConfigurationException>(()=>f.Store.SaveAsync(c with{Tanks=[c.Tanks[0] with{Metering=new(){MainTableId=t.Id}}]},Admin));}
    [Fact]public void DisplayOnlyFrozenSurfaceDoesNotBecomeCurrentValidLevel()
    {var runtime=new WirelessRuntime();var c=Fixtures.Configuration();var tank=c.Tanks.Single();var channel=c.Channels.Single();var session=Guid.NewGuid();var time=DateTimeOffset.UtcNow;ReadingSnapshot Sample(ushort state,decimal position)=>new(){TankId=tank.Id,AcquisitionSessionId=session,ReceivedAtUtc=time,LevelMm=Measurement.Good(position),InstrumentStatus=state};runtime.Apply(Sample(1,1000),tank,channel);var hoist=runtime.Apply(Sample(2,1100),tank,channel);Assert.False(hoist.LevelMm.IsValid);Assert.Equal(1000,hoist.LastMeasuredLevelMm);Assert.Equal(1100,hoist.FloatPositionMm.Value);var calc=MeteringCalculator.Calculate(new(){Enabled=true},hoist.LevelMm,Measurement.Good(800),MainTable(),null,null);Assert.False(calc.NetVolumeM3.IsValid);var next=runtime.Apply(Sample(1,1200),tank,channel);Assert.Equal(1200,next.LastMeasuredLevelMm);Assert.True(next.LevelMm.IsValid);}
    [Fact]public void FrozenSurfaceDoesNotCrossAcquisitionSessionsOrPhysicalBindings()
    {var runtime=new WirelessRuntime();var c=Fixtures.Configuration();var tank=c.Tanks.Single();var channel=c.Channels.Single();var sample=new ReadingSnapshot{TankId=tank.Id,AcquisitionSessionId=Guid.NewGuid(),ReceivedAtUtc=DateTimeOffset.UtcNow,InstrumentStatus=1,LevelMm=Measurement.Good(1000)};runtime.Apply(sample,tank,channel);Assert.Null(runtime.Apply(sample with{AcquisitionSessionId=Guid.NewGuid(),InstrumentStatus=2},tank,channel).LastMeasuredLevelMm);Assert.Null(runtime.Apply(sample with{InstrumentStatus=2},tank with{Address=2},channel).LastMeasuredLevelMm);}
    [Theory][InlineData(InstrumentCommand.MeasureI,true)][InlineData(InstrumentCommand.Stop,true)][InlineData(InstrumentCommand.Hoist,true)][InlineData(InstrumentCommand.Repeatability,true)][InlineData(InstrumentCommand.OscillationAvoidance,true)][InlineData(InstrumentCommand.Lower,false)][InlineData(InstrumentCommand.RemoteLevel,false)][InlineData(InstrumentCommand.MeasureII,false)]
    public void OptionalOperationsAreHiddenUntilExplicitlySigned(InstrumentCommand c,bool available)=>Assert.Equal(available,ProductTrust.CommandAvailable(c,[]));
    [Fact]public void VendorToolSignsIndividualOptionalOperationsAndOldAverageLicenseStaysValid()
    {var license=Issuance.Build(new(new string('A',64),"r23"),"客户",1,true,null,true,new(),DateTimeOffset.UtcNow,0,[InstrumentCommand.MeasureII,InstrumentCommand.Lower]);Assert.Contains("operation-interface",license.EnabledModules);Assert.Contains("operation-lower",license.EnabledModules);Assert.DoesNotContain("operation-bottom",license.EnabledModules);Assert.True(ProductTrust.CommandAvailable(InstrumentCommand.MeasureII,license.EnabledModules));Assert.False(ProductTrust.CommandAvailable(InstrumentCommand.Density,license.EnabledModules));Assert.All(license.EnabledModules,m=>Assert.True(ProductTrust.KnownModule(m)));}
}
