using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class TankConfigurationExcelTests
{
    private static byte[] Change(byte[] file,string sheetName,int row,int col,string text,bool formula=false)
    {
        using var stream=new MemoryStream();stream.Write(file);stream.Position=0;using(var doc=SpreadsheetDocument.Open(stream,true)){var part=doc.WorkbookPart!;var sheet=part.Workbook!.Sheets!.Elements<Sheet>().Single(s=>s.Name==sheetName);var ws=(WorksheetPart)part.GetPartById(sheet.Id!);var cell=ws.Worksheet!.Descendants<Row>().ElementAt(row-1).Elements<Cell>().ElementAt(col-1);cell.RemoveAllChildren();cell.DataType=DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString;if(formula)cell.CellFormula=new CellFormula(text);else cell.InlineString=new InlineString(new Text(text));ws.Worksheet.Save();}return stream.ToArray();
    }
    [Fact]public void WorkbookRoundTripsArchiveAlarmsMeteringControlsNullsAndGroups()
    {
        var config=Fixtures.Configuration();var t=config.Tanks[0] with{Name="柴油储罐",ProductNumber="P-001",Batch="首批",DiameterMm=18000.5m,NominalCapacityM3=3000.25m,Notes="测试备注",TemperatureMode=TemperatureMode.Ats,AtsPointCount=3,AtsHeightsMm=[0,3000,null],Controls=new(){AllowedCommands=[InstrumentCommand.MeasureI,InstrumentCommand.Stop]},Alarms=new(){Temperature=new(){Low=0,High=60,Hysteresis=1,DelaySeconds=5}}};config=config with{Tanks=[t],Groups=[new(){Name="一罐区",TankIds=[t.Id]}]};
        var file=TankConfigurationExcel.Export(config,[]);var preview=TankConfigurationExcel.Preview(file,config,[]);Assert.Empty(preview.Issues);Assert.Equal(0,preview.Added);Assert.Equal(1,preview.Updated);
        var next=preview.Configuration.Tanks.Single();Assert.Equal(t.Name,next.Name);Assert.Equal(t.Notes,next.Notes);Assert.Equal(t.DiameterMm,next.DiameterMm);Assert.Equal(0,next.AtsHeightsMm[0]);Assert.Equal(3000,next.AtsHeightsMm[1]);Assert.Null(next.Metering.StandardDensity20KgM3);Assert.Equal(0,next.Alarms.Temperature.Low);Assert.Equal(t.Controls.AllowedCommands,next.Controls.AllowedCommands);Assert.Equal(config.Groups[0].TankIds,preview.Configuration.Groups[0].TankIds);
    }
    [Fact]public void ImportedRowsUpsertOnlyAndUnlistedTanksRemain()
    {
        var config=Fixtures.Configuration();var other=config.Tanks[0] with{Id=Guid.NewGuid(),Code="T-NEW",Address=2};var file=TankConfigurationExcel.Export(config with{Tanks=[other]},[]);file=Change(file,"罐体与通讯",2,1,"");var preview=TankConfigurationExcel.Preview(file,config,[]);
        Assert.Empty(preview.Issues);Assert.Equal(1,preview.Added);Assert.Equal(2,preview.Configuration.Tanks.Count);Assert.Contains(preview.Configuration.Tanks,t=>t.Id==config.Tanks[0].Id);
    }
    [Fact]public void MissingSheetsUnknownChannelAndInvalidReferenceAreReportedBeforeSave()
    {
        var config=Fixtures.Configuration();var file=TankConfigurationExcel.Export(config,[]);file=Change(file,"罐体与通讯",2,14,Guid.NewGuid().ToString());var preview=TankConfigurationExcel.Preview(file,config,[]);Assert.NotEmpty(preview.Issues);
        Assert.NotEqual(config.Tanks[0].ChannelId,preview.Configuration.Tanks[0].ChannelId);
    }
    [Fact]public void FormulaIsNotExecutedOrAcceptedAsCachedConfiguration()
    {
        var config=Fixtures.Configuration();var file=Change(TankConfigurationExcel.Export(config,[]),"罐体与通讯",2,3,"1+1",true);Assert.Throws<ArgumentException>(()=>TankConfigurationExcel.Preview(file,config,[]));
    }
    [Fact]public void EmptyTemplateHasGuideAndNoInventedTankRows()
    {
        var config=Fixtures.Configuration();var file=TankConfigurationExcel.Export(config,[],true);var preview=TankConfigurationExcel.Preview(file,config,[]);Assert.Contains(preview.Issues,e=>e.Contains("1～128"));
        using var doc=SpreadsheetDocument.Open(new MemoryStream(file),false);Assert.Equal(7,doc.WorkbookPart!.Workbook!.Sheets!.Count());
    }
    [Fact]public void ImportCannotSilentlyDefaultMissingBooleanOrOverwriteIdentity()
    {
        var config=Fixtures.Configuration();var file=TankConfigurationExcel.Export(config,[]);var invalid=TankConfigurationExcel.Preview(Change(file,"罐体与通讯",2,16,""),config,[]);Assert.NotEmpty(invalid.Issues);
        invalid=TankConfigurationExcel.Preview(Change(file,"罐体与通讯",2,1,Guid.NewGuid().ToString()),config,[]);Assert.NotEmpty(invalid.Issues);
    }
    [Fact]public void CoreDeliveryTemplateDoesNotExposeUnopenedAverageOptionOrChangeIr26Mode()
    {
        var config=Fixtures.Configuration();config=config with{Tanks=[config.Tanks[0] with{TemperatureMode=TemperatureMode.Wireless}]};
        var file=TankConfigurationExcel.Export(config,[],includeAverage:false);
        using(var doc=SpreadsheetDocument.Open(new MemoryStream(file),false))
        {
            var texts=string.Join(" ",doc.WorkbookPart!.WorksheetParts.SelectMany(p=>p.Worksheet!.Descendants<Text>()).Select(t=>t.Text));
            Assert.DoesNotContain("无线",texts);Assert.DoesNotContain("平均测温",texts);Assert.DoesNotContain("Wireless",texts);Assert.Contains("仪表温度",texts);
        }
        var preview=TankConfigurationExcel.Preview(file,config,[],includeAverage:false);Assert.Empty(preview.Issues);Assert.Equal(TemperatureMode.Wireless,preview.Configuration.Tanks[0].TemperatureMode);
    }
    [Fact]public void ExportedWorkbookPassesOpenXmlValidation()
    {
        var file=TankConfigurationExcel.Export(Fixtures.Configuration(),[]);
        using var doc=SpreadsheetDocument.Open(new MemoryStream(file),false);
        var issues=new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).Select(i=>i.Part?.Uri+" "+i.Path?.XPath+" "+i.Description).ToArray();Assert.True(issues.Length==0,string.Join("\n",issues));
    }
    [Fact]public void ExportedTanksRestoreAfterDeletionWithOriginalIdsAndMultipleGroups()
    {
        var config=Fixtures.Configuration();var first=config.Tanks[0] with{Name="恢复原罐",ProductNumber="原编号",Batch="原批次",TemperatureMode=TemperatureMode.Ats,AtsPointCount=2,AtsHeightsMm=[0,1200],Enabled=false,Hidden=true,Metering=new(){ManualOperatingDensityKgM3=800,DensitySource=MeteringDensitySource.Manual,WaterVolumeM3=0,StandardDensity20KgM3=850}};
        var second=first with{Id=Guid.NewGuid(),Code="T-102",Address=2,Enabled=true,Hidden=false};
        config=config with{Tanks=[first,second],Groups=[new(){Name="一罐区",TankIds=[first.Id,second.Id]},new(){Name="柴油组",TankIds=[first.Id]}]};
        var deleted=config with{Tanks=[],Groups=config.Groups.Select(g=>g with{TankIds=[]}).ToArray()};
        var preview=TankConfigurationExcel.Preview(TankConfigurationExcel.Export(config,[]),deleted,[]);
        Assert.Empty(preview.Issues);Assert.Equal(2,preview.Added);Assert.Equal(0,preview.Updated);
        var restored=preview.Configuration.Tanks.Single(t=>t.Id==first.Id);
        Assert.Equal(first.Code,restored.Code);Assert.Equal(first.ChannelId,restored.ChannelId);Assert.Equal(first.Address,restored.Address);Assert.Equal(first.ProductNumber,restored.ProductNumber);Assert.Equal(first.Enabled,restored.Enabled);Assert.Equal(first.Hidden,restored.Hidden);Assert.Equal(first.Metering,restored.Metering);Assert.Equal(first.AtsHeightsMm,restored.AtsHeightsMm);
        foreach(var group in config.Groups)Assert.Equal(group.TankIds,preview.Configuration.Groups.Single(g=>g.Id==group.Id).TankIds);
    }
    [Fact]public void PartialDeletionRestoresMissingTankAndKeepsUnlistedTank()
    {
        var config=Fixtures.Configuration();var first=config.Tanks[0];var second=first with{Id=Guid.NewGuid(),Code="T-102",Address=2};var unlisted=first with{Id=Guid.NewGuid(),Code="T-OTHER",Address=3};
        var source=config with{Tanks=[first,second]};var current=config with{Tanks=[first,unlisted]};
        var preview=TankConfigurationExcel.Preview(TankConfigurationExcel.Export(source,[]),current,[]);
        Assert.Empty(preview.Issues);Assert.Equal(1,preview.Added);Assert.Equal(1,preview.Updated);Assert.Equal(3,preview.Configuration.Tanks.Count);Assert.Contains(preview.Configuration.Tanks,t=>t.Id==second.Id);Assert.Contains(preview.Configuration.Tanks,t=>t.Id==unlisted.Id);
    }
    [Fact]public void InvalidIdentityDoesNotProduceMisleadingGroupErrors()
    {
        var config=Fixtures.Configuration();config=config with{Groups=[new(){Name="原罐组",TankIds=[config.Tanks[0].Id]}]};var file=TankConfigurationExcel.Export(config,[]);
        foreach(var text in new[]{"不是编号",Guid.Empty.ToString()})
        {
            var preview=TankConfigurationExcel.Preview(Change(file,"罐体与通讯",2,1,text),config,[]);
            Assert.NotEmpty(preview.Issues);Assert.DoesNotContain(preview.Issues,e=>e.Contains("未在罐体与通讯页列出"));Assert.Equal(0,preview.Added);Assert.Equal(0,preview.Updated);
        }
    }
    [Fact]public void RestoredIdentityCannotBeReusedByTwoRows()
    {
        var config=Fixtures.Configuration();var first=config.Tanks[0];var second=first with{Id=Guid.NewGuid(),Code="T-102",Address=2};config=config with{Tanks=[first,second]};
        var file=Change(TankConfigurationExcel.Export(config,[]),"罐体与通讯",3,1,first.Id.ToString());
        var preview=TankConfigurationExcel.Preview(file,config with{Tanks=[]},[]);
        Assert.Contains(preview.Issues,e=>e.Contains("内部编号被重复使用"));Assert.Single(preview.Configuration.Tanks);
    }
}

