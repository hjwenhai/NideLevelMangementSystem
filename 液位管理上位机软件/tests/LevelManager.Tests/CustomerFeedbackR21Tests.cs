using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;
namespace Naide.LevelManager.Tests;
public class CustomerFeedbackR21Tests
{
    static readonly decimal[] Cm=[2269,4537,6806,9074,11343,13611,15880,18148,20417];
    static readonly decimal[] Mm=[227,454,681,907,1134,1361,1588,1815,2042];
    static CapacityTable MainTable()=>new(){Number="CT",Name="原表",VolumeUnit=VolumeUnit.Litre,Points=[new(440,99165),new(500,112776),new(600,135461),new(1060,239813)]};
    static CapacityTable Small()=>new(){Number="DT",Name="分区增量",Kind=CapacityTableKind.Decimal,VolumeUnit=VolumeUnit.Litre,DecimalRows=Enumerable.Range(1,9).Select(n=>new DecimalCapacityRow(440,1060,n,Cm[n-1],Mm[n-1])).ToArray()};
    static MeteringSettings Settings()=>new(){Enabled=true,BaseVolumeMethod=BaseVolumeMethod.MainPlusDecimal,MainTableId=MainTable().Id,DecimalTableId=Small().Id};
    [Theory][InlineData(440,99165)][InlineData(447,100753)][InlineData(467,105290)][InlineData(500,112776)][InlineData(527,118901)][InlineData(1060,239813)]
    public void ExactNodesAndNonRoundAnchorUseOriginalCumulativeValues(int h,int expectedLitres)
    {Assert.Equal(expectedLitres/1000m,CapacityRules.WithDecimal(MainTable(),Small(),h,FractionalMillimetreRule.Linear));}
    [Fact]public void TenthMillimetreInterpolatesAdjacentResultsWithoutRounding()
    {Assert.Equal(100.8665m,CapacityRules.WithDecimal(MainTable(),Small(),447.5m,FractionalMillimetreRule.Linear));}
    [Fact]public void ManualDensitySelectedForPressureAndMassWhileInstrumentAndStandardStayIndependent()
    {
        var main=new CapacityTable{Number="M",Name="主表",Points=[new(0,0),new(1000,100)]};
        var pressure=main with{Kind=CapacityTableKind.StaticPressure,Points=[new(0,0),new(1000,1)]};
        var settings=new MeteringSettings{Enabled=true,StaticPressureTableId=pressure.Id,DensitySource=MeteringDensitySource.Manual,ManualOperatingDensityKgM3=800,StandardDensity20KgM3=999};
        var result=MeteringCalculator.Calculate(settings,Measurement.Good(500),Measurement.Good(900),main,pressure,null);
        Assert.Equal(50.4m,result.NetVolumeM3.Value);Assert.Equal(40.32m,result.MassT.Value);
        Assert.Null(MeteringCalculator.Calculate(settings with{ManualOperatingDensityKgM3=null},Measurement.Good(500),Measurement.Good(900),main,pressure,null).MassT.Value);
    }
    [Fact]public void CombinedAndInterpolatedAreMutuallyExclusive()
    {
        var result=MeteringCalculator.Calculate(Settings(),Measurement.Good(527),Measurement.Good(800),MainTable(),null,Small());
        Assert.Equal(118.901m,result.NetVolumeM3.Value);
        var plain=MeteringCalculator.Calculate(Settings() with{BaseVolumeMethod=BaseVolumeMethod.Interpolate},Measurement.Good(527),Measurement.Good(800),MainTable(),null,Small());
        Assert.Equal(118.90095m,plain.NetVolumeM3.Value);
    }
    [Fact]public void InvalidBandsMissingSequenceGapsOverlapAndWrongMainBoundaryAreRejected()
    {
        Assert.NotEmpty(CapacityRules.Validate(Small() with{DecimalRows=Small().DecimalRows.Skip(1).ToArray()}));
        Assert.NotEmpty(CapacityRules.Validate(Small() with{DecimalRows=Small().DecimalRows.Concat(Small().DecimalRows).ToArray()}));
        Assert.Throws<InvalidOperationException>(()=>CapacityRules.WithDecimal(MainTable(),Small() with{DecimalRows=Small().DecimalRows.Select(r=>r with{StartHeightMm=441}).ToArray()},447,FractionalMillimetreRule.Linear));
        Assert.Throws<ArgumentOutOfRangeException>(()=>CapacityRules.WithDecimal(MainTable(),Small(),1061,FractionalMillimetreRule.Linear));
    }
    [Fact]public void DecimalWorkbookFiveColumnRoundTripRetainsEveryOriginalValue()
    {var original=Small();var bytes=CapacityExcel.Export(original);var preview=CapacityExcel.PreviewDecimal(bytes,0,2);Assert.Equal(original.DecimalRows,preview.DecimalRows);Assert.Empty(preview.Points);}
    [Theory][InlineData(CapacityTableKind.Main,HeightUnit.Metre,VolumeUnit.Litre)][InlineData(CapacityTableKind.StaticPressure,HeightUnit.Centimetre,VolumeUnit.CubicMetre)]
    public void NativeWorkbookRetainsExplicitUnitsAndReferenceDensity(CapacityTableKind kind,HeightUnit height,VolumeUnit volume)
    {
        var original=new CapacityTable{Number="ROUNDTRIP",Name="单位核对",Kind=kind,HeightUnit=height,VolumeUnit=volume,ReferenceDensityKgM3=998.2m,Points=[new(0,0),new(1.2345m,.987654m)]};
        var preview=CapacityExcel.Preview(CapacityExcel.Export(original),0,2,1,2);
        Assert.Equal(original.Points,preview.Points);Assert.Equal(kind,preview.Kind);Assert.Equal(height,preview.HeightUnit);Assert.Equal(volume,preview.VolumeUnit);Assert.Equal(kind==CapacityTableKind.StaticPressure?998.2m:null,preview.ReferenceDensityKgM3);
    }
    [Fact]public void NativeNonDecimalWorkbookCannotBeImportedAsFiveColumnTable()
    {Assert.Throws<ArgumentException>(()=>CapacityExcel.PreviewDecimal(CapacityExcel.Export(MainTable()),0,2));}
    [Theory][InlineData("高度单位","inch")][InlineData("容积单位","gallon")][InlineData("静压力参考密度 kg/m³","0")]
    public void InvalidExplicitMetadataIsRejectedInsteadOfSilentlyDefaulting(string key,string value)
    {
        using var stream=new MemoryStream();stream.Write(CapacityExcel.Export(MainTable() with{Kind=CapacityTableKind.StaticPressure}));stream.Position=0;
        using(var doc=SpreadsheetDocument.Open(stream,true))
        {
            var part=doc.WorkbookPart!;var sheet=part.Workbook!.Sheets!.Elements<Sheet>().Single(s=>s.Name=="资料");var ws=(WorksheetPart)part.GetPartById(sheet.Id!);
            var cell=ws.Worksheet!.Descendants<Row>().Single(r=>r.Elements<Cell>().First().InnerText==key).Elements<Cell>().Skip(1).First();cell.RemoveAllChildren();cell.DataType=CellValues.InlineString;cell.Append(new InlineString(new Text(value)));ws.Worksheet.Save();
        }
        Assert.Throws<ArgumentException>(()=>CapacityExcel.Preview(stream.ToArray(),0,2,1,2));
    }
    [Fact]public void ConfigurationWorkbookContainsNamedHumanReadableDropDownAndPreservesOptionalBlanks()
    {
        var c=Fixtures.Configuration();var bytes=TankConfigurationExcel.Export(c,[]);
        using var doc=SpreadsheetDocument.Open(new MemoryStream(bytes),false);var part=doc.WorkbookPart!;
        Assert.NotEmpty(part.Workbook!.DefinedNames!.Elements<DefinedName>());
        var main=(WorksheetPart)part.GetPartById(part.Workbook!.Sheets!.Elements<Sheet>().Single(s=>s.Name=="罐体与通讯").Id!);
        Assert.Contains(main.Worksheet!.Descendants<DataValidation>(),d=>d.Formula1?.Text.StartsWith("TankChoice")==true);
        var preview=TankConfigurationExcel.Preview(bytes,c,[]);Assert.Empty(preview.Issues);Assert.Equal(c.Channels[0].Id,preview.Configuration.Tanks[0].ChannelId);Assert.Null(preview.Configuration.Tanks[0].Metering.ManualOperatingDensityKgM3);
    }
    [Fact]public void HeaderCodesPreserveRawBitCodeAndUnknownBitsAreNotInvented()
    {
        var faults=InstrumentFaultCatalog.Observe(0x2009,AcquisitionProtocol.DirectRtu,[],DateTimeOffset.UtcNow);
        Assert.Contains(faults,f=>f.DisplayCode=="E"&&f.Code=="FW.0x2000");Assert.Contains(faults,f=>f.DisplayCode=="1");Assert.Contains(faults,f=>f.Bit==3&&f.DisplayCode==null);
    }
    [Fact]public void LegacyWorkbookWithoutNewMeteringColumnsRetainsExistingSourceAndManualValue()
    {
        var config=Fixtures.Configuration();config=config with{Tanks=[config.Tanks[0] with{Metering=new(){ManualOperatingDensityKgM3=870,DensitySource=MeteringDensitySource.Manual}}]};
        var original=TankConfigurationExcel.Export(config,[]);using var stream=new MemoryStream();stream.Write(original);stream.Position=0;
        using(var doc=SpreadsheetDocument.Open(stream,true))
        {
            var part=doc.WorkbookPart!;var sheet=part.Workbook!.Sheets!.Elements<Sheet>().Single(s=>s.Name=="报警与计量");var ws=(WorksheetPart)part.GetPartById(sheet.Id!);
            foreach(var row in ws.Worksheet!.Descendants<Row>())foreach(var cell in row.Elements<Cell>().TakeLast(5).ToArray())cell.Remove();ws.Worksheet.Save();
        }
        var preview=TankConfigurationExcel.Preview(stream.ToArray(),config,[]);Assert.Empty(preview.Issues);Assert.Equal(870,preview.Configuration.Tanks[0].Metering.ManualOperatingDensityKgM3);Assert.Equal(MeteringDensitySource.Manual,preview.Configuration.Tanks[0].Metering.DensitySource);
    }
    [Fact]public void NewTemplateDoesNotInventZeroInBlankOptionalInputCells()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"LevelManager.slnx")))directory=directory.Parent;Assert.NotNull(directory);
        using var doc=SpreadsheetDocument.Open(Path.Combine(directory!.FullName,"assets/capacity-templates/capacity-decimal.xlsx"),false);
        var part=doc.WorkbookPart!;var sheet=part.Workbook!.Sheets!.Elements<Sheet>().Single(s=>s.Name=="填写模板");var ws=(WorksheetPart)part.GetPartById(sheet.Id!);
        Assert.Equal("起始高度（mm）",ws.Worksheet!.Descendants<Row>().First().Elements<Cell>().First().InnerText);
        Assert.DoesNotContain(ws.Worksheet.Descendants<Row>().Skip(1).Take(18).SelectMany(r=>r.Elements<Cell>()),c=>c.CellReference?.Value=="A2"&&c.CellValue?.Text=="0");
    }
}

