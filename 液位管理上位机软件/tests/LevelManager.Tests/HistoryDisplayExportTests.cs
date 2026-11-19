using System.IO.Compression;
using System.Xml.Linq;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class HistoryDisplayExportTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RejectsEmptyDuplicateAndUnknownSelection(int kind)
    {
        HistoryDataItem[] selected=kind switch{0=>[],1=>[HistoryDataItem.Level,HistoryDataItem.Level],_=>[(HistoryDataItem)999]};
        Assert.Throws<ArgumentException>(()=>HistoryExportPolicy.Validate(selected));
        HistoryExportPolicy.Validate(null);
    }
    [Fact]
    public async Task ExplicitSelectionExportsOnlyChosenMeasurementsWithFrozenZeroAndInvalidState()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),new("a","管理员",AccountRole.Administrator));var history=new SqliteHistoryStore(f.PathToDb);
        await history.AppendAsync([new(){TankId=c.Tanks[0].Id,AtUtcMs=1000,ConfigurationRevision=c.Revision,AcquisitionSessionId=Guid.NewGuid(),LevelMm=Measurement.Good(0),InstantTemperatureC=new(null,ValueQuality.Offline),InstrumentDensityKgM3=Measurement.Good(832)}]);
        var folder=Path.Combine(f.Root,"selected");var result=await new HistoryExcelExporter(history).ExportAsync(folder,new(0,2000,SelectedQuantities:[HistoryDataItem.Level,HistoryDataItem.InstantTemperature]),c);
        using var zip=ZipFile.OpenRead(Path.Combine(folder,result.FileName));using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();var xml=XDocument.Load(stream);XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var rows=xml.Descendants(n+"row").ToArray();
        var headers=rows[0].Elements(n+"c").Select(cell=>string.Concat(cell.Descendants(n+"t").Select(t=>t.Value))).ToArray();
        Assert.Contains("液位 mm",headers);Assert.Contains("温度 ℃",headers);Assert.DoesNotContain("仪表密度 kg/m³",headers);Assert.DoesNotContain("模拟量输入1 mA",headers);
        using var styleStream=zip.GetEntry("xl/styles.xml")!.Open();var styles=XDocument.Load(styleStream);Assert.Contains(styles.Descendants(n+"left"),b=>(string?)b.Attribute("style")=="thin"&&(string?)b.Element(n+"color")?.Attribute("rgb")=="FFD9E1E6");
        var cells=rows[1].Elements(n+"c").ToArray();Assert.Equal("0",cells[2].Element(n+"v")?.Value);Assert.Equal("正常",string.Concat(cells[3].Descendants(n+"t").Select(t=>t.Value)));Assert.Equal("通讯中断",string.Concat(cells[5].Descendants(n+"t").Select(t=>t.Value)));Assert.Equal(1,result.Rows);
    }
    [Fact]
    public async Task SelectingUnavailableInterfaceRetainsUnconfiguredInsteadOfInventingZero()
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(),new("a","管理员",AccountRole.Administrator));var history=new SqliteHistoryStore(f.PathToDb);
        await history.AppendAsync([new(){TankId=c.Tanks[0].Id,AtUtcMs=1000,ConfigurationRevision=c.Revision,AcquisitionSessionId=Guid.NewGuid(),LevelMm=Measurement.Good(500)}]);
        var folder=Path.Combine(f.Root,"interface");var result=await new HistoryExcelExporter(history).ExportAsync(folder,new(0,2000,SelectedQuantities:[HistoryDataItem.Interface]),c);
        using var zip=ZipFile.OpenRead(Path.Combine(folder,result.FileName));using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();var xml=XDocument.Load(stream);XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var cells=xml.Descendants(n+"row").ElementAt(1).Elements(n+"c").ToArray();
        Assert.Null(cells[2].Element(n+"v"));Assert.Equal("未配置",string.Concat(cells[3].Descendants(n+"t").Select(t=>t.Value)));
    }
}
