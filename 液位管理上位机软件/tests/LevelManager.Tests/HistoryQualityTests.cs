using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class HistoryQualityTests
{
    [Theory]
    [InlineData(TrendQuantity.Level)] [InlineData(TrendQuantity.InstantTemperature)]
    [InlineData(TrendQuantity.AverageTemperature)] [InlineData(TrendQuantity.Density)]
    [InlineData(TrendQuantity.CurrentVolume)] [InlineData(TrendQuantity.NetVolume)]
    [InlineData(TrendQuantity.Mass)] [InlineData(TrendQuantity.Pressure1)] [InlineData(TrendQuantity.Pressure2)]
    public async Task FilterUsesSelectedQuantityAndSameConditionForPagingAndExport(TrendQuantity selected)
    {
        await using var f=new StoreFixture();await f.Initialize();var c=await f.Store.SaveAsync(Fixtures.Configuration(2),new("q","查询测试",AccountRole.Administrator));var history=new SqliteHistoryStore(f.PathToDb);var at=DateTimeOffset.Parse("2026-01-01T00:00:00Z").ToUnixTimeMilliseconds();var session=Guid.NewGuid();
        var samples=Enum.GetValues<ValueQuality>().SelectMany((q,i)=>c.Tanks.Select(t=>
        {
            var value=q==ValueQuality.Good?Measurement.Good(0):new Measurement(null,q);
            var sample=new HistorySample{TankId=t.Id,ConfigurationRevision=c.Revision,AtUtcMs=at+i*1000,AcquisitionSessionId=session,LevelMm=Measurement.Good(999)};
            return selected switch{TrendQuantity.Level=>sample with{LevelMm=value},TrendQuantity.InstantTemperature=>sample with{InstantTemperatureC=value},TrendQuantity.AverageTemperature=>sample with{AverageTemperatureC=value},TrendQuantity.Density=>sample with{InstrumentDensityKgM3=value},TrendQuantity.CurrentVolume=>sample with{CurrentVolumeM3=value},TrendQuantity.NetVolume=>sample with{NetVolumeM3=value},TrendQuantity.Mass=>sample with{MassT=value},TrendQuantity.Pressure1=>sample with{Pressure1KPa=value},_=>sample with{Pressure2KPa=value}};
        })).ToArray();await history.AppendAsync(samples);
        foreach(var filter in Enum.GetValues<HistoryQualityFilter>())
        {
            var expected=filter==HistoryQualityFilter.All?12:filter==HistoryQualityFilter.Invalid?10:2;
            var query=new HistoryQuery(at,at+10000,Limit:1,QualityQuantity:selected,Quality:filter);var found=new List<HistorySample>();HistoryPage page;
            do{page=await history.QueryAsync(query);found.AddRange(page.Rows);query=query with{After=page.Next};}while(page.Next is not null);
            Assert.Equal(expected,found.Count);var exported=new List<HistorySample>();await foreach(var sample in history.ReadForExportAsync(query with{After=null}))exported.Add(sample);Assert.Equal(found.Select(s=>(s.AtUtcMs,s.TankId)),exported.Select(s=>(s.AtUtcMs,s.TankId)));
            if(selected!=TrendQuantity.Level)Assert.All(found,s=>Assert.Equal(999,s.LevelMm.Value));
        }
        await Assert.ThrowsAsync<ArgumentException>(()=>history.QueryAsync(new(at,at+10,Quality:(HistoryQualityFilter)999)));
    }
    [Fact]public void RefreshPeriodIsSeparateFromArchiveAndRejectsOutOfRange()
    {
        var c=Fixtures.Configuration();Assert.Empty(ConfigurationRules.Validate(c with{Display=c.Display with{RefreshIntervalSeconds=30}}));Assert.Equal(60,c.HistoryIntervalSeconds);
        Assert.NotEmpty(ConfigurationRules.Validate(c with{Display=c.Display with{RefreshIntervalSeconds=0}}));Assert.NotEmpty(ConfigurationRules.Validate(c with{Display=c.Display with{RefreshIntervalSeconds=31}}));
    }
}
