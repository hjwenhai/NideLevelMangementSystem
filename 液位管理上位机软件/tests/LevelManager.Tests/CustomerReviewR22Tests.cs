using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class CustomerReviewR22Tests
{
    [Fact] public void ChineseDropDownLabelsRoundTripWithoutChangingCalculationChoices()
    {
        var config=Fixtures.Configuration();config=config with{Tanks=[config.Tanks[0] with{Shape=TankShape.FloatingRoof,TemperatureMode=TemperatureMode.Ats,Metering=new(){DensitySource=MeteringDensitySource.Manual,ManualOperatingDensityKgM3=800,BaseVolumeMethod=BaseVolumeMethod.MainPlusDecimal,FractionalMillimetreRule=FractionalMillimetreRule.Linear}}]};
        var bytes=TankConfigurationExcel.Export(config,[]);using var doc=SpreadsheetDocument.Open(new MemoryStream(bytes),false);
        var texts=doc.WorkbookPart!.WorksheetParts.SelectMany(w=>w.Worksheet!.Descendants<Text>()).Select(t=>t.Text).ToArray();
        Assert.Contains("浮顶罐",texts);Assert.Contains("多点温度计",texts);Assert.Contains("人工工况密度",texts);Assert.Contains("主表下节点＋小数表增量",texts);Assert.Contains("相邻毫米线性插值",texts);
        var lists=doc.WorkbookPart!.WorksheetParts.SelectMany(w=>w.Worksheet!.Descendants<DataValidation>()).Select(v=>v.Formula1?.Text??"").ToArray();
        Assert.DoesNotContain(lists,s=>s.Contains("Instrument,Manual")||s.Contains("Vertical,Sphere")||s.Contains("MainPlusDecimal"));
        // Disable incomplete combination in the fixture; the import still must preserve all enums.
        var result=TankConfigurationExcel.Preview(bytes,config,[]);var tank=result.Configuration.Tanks[0];
        Assert.Equal(config.Tanks[0].Shape,tank.Shape);Assert.Equal(TemperatureMode.Ats,tank.TemperatureMode);Assert.Equal(config.Tanks[0].Metering,tank.Metering);
    }
    [Fact] public void OldEnglishEnumsRemainReadableAfterChineseExportUpgrade()
    {
        var config=Fixtures.Configuration();using var stream=new MemoryStream();stream.Write(TankConfigurationExcel.Export(config,[]));stream.Position=0;
        using(var doc=SpreadsheetDocument.Open(stream,true))
        {
            foreach(var cell in doc.WorkbookPart!.WorksheetParts.SelectMany(w=>w.Worksheet!.Descendants<Cell>()))
            {
                var legacy=cell.InnerText switch{"立式罐"=>"Vertical","不带温度计"=>"None","仪表实测密度"=>"Instrument","主表插值（不叠加小数表）"=>"Interpolate","相邻毫米线性插值"=>"Linear",_=>null};
                if(legacy is null)continue;cell.RemoveAllChildren();cell.DataType=CellValues.InlineString;cell.Append(new InlineString(new Text(legacy)));
            }
            foreach(var w in doc.WorkbookPart!.WorksheetParts)w.Worksheet!.Save();
        }
        var result=TankConfigurationExcel.Preview(stream.ToArray(),config,[]);Assert.Empty(result.Issues);Assert.Equal(config.Tanks[0].Metering,result.Configuration.Tanks[0].Metering);Assert.Equal(config.Tanks[0].Shape,result.Configuration.Tanks[0].Shape);
    }
}
