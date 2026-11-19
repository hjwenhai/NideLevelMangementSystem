using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Naide.LevelManager.Application;
using Xunit;
namespace Naide.LevelManager.Tests;
public class TrendTests
{
    [Fact] public void ReductionKeepsActualExtremaEndpointsAndBreaksAcrossOmittedInvalidPoints()
    {
        var reducer = new TrendReducer(0, 1000, 100);
        for (var i = 0; i < 1000; i++) reducer.Add(i, i % 17 == 5 ? new(null, ValueQuality.Fault) : Measurement.Good(i == 77 ? -1000 : i == 88 ? 2000 : i));
        var output = reducer.Finish(); Assert.Equal(0, output[0].AtUtcMs); Assert.Equal(999, output[^1].AtUtcMs); Assert.Contains(output, p => p.Value == -1000); Assert.Contains(output, p => p.Value == 2000);
        Assert.True(output.Count <= 100); Assert.Equal(1000, reducer.SourceRows);
        var valid = output.Select((p, i) => (p, i)).Where(p => p.p.Value.HasValue).ToArray();
        for (var i = 1; i < valid.Length; i++)
        {
            var a = valid[i - 1]; var b = valid[i];
            if (Enumerable.Range((int)a.p.AtUtcMs + 1, (int)(b.p.AtUtcMs - a.p.AtUtcMs - 1)).Any(x => x % 17 == 5)) Assert.Contains(output.Skip(a.i + 1).Take(b.i - a.i - 1), p => p.Value is null);
        }
    }
    [Fact] public void RealZeroIsKeptAndAllInvalidDataNeverBecomesZero()
    {
        var reducer = new TrendReducer(0, 1000, 100); reducer.Add(1, Measurement.Good(0)); reducer.Add(2, new(null, ValueQuality.Offline));
        var points = reducer.Finish(); Assert.Contains(points, p => p.Value == 0 && p.Quality == ValueQuality.Good);
        reducer = new(0, 1000, 100); for (var i = 0; i < 1000; i++) reducer.Add(i, new(null, ValueQuality.Expired));
        Assert.All(reducer.Finish(), p => Assert.Null(p.Value));
    }
    [Fact] public void QueryRejectsSixSeriesDuplicateSignalsAndUnboundedRanges()
    {
        var id = Guid.NewGuid(); var s = new TrendSelection(id, TrendQuantity.Level);
        Assert.Throws<ArgumentException>(() => TrendValues.Validate(new(0, 1000, [s, s])));
        Assert.Throws<ArgumentException>(() => TrendValues.Validate(new(0, 1000, Enumerable.Range(0, 6).Select(i => new TrendSelection(Guid.NewGuid(), TrendQuantity.Level)).ToArray())));
        Assert.Throws<ArgumentException>(() => TrendValues.Validate(new(0, 400L * 86400000, [s])));
    }
    [Fact] public async Task StoredTrendUsesArchivedValuesQualitiesAndCanonicalPressureUnits()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var config = await f.Store.SaveAsync(Fixtures.Configuration(), new("a", "管理员", AccountRole.Administrator));
        var history = new SqliteHistoryStore(f.PathToDb); var session = Guid.NewGuid();
        await history.AppendAsync(Enumerable.Range(0, 300).Select(i => new HistorySample { TankId = config.Tanks[0].Id, AtUtcMs = i * 1000, AcquisitionSessionId = session, ConfigurationRevision = config.Revision, LevelMm = i == 155 ? new(null, ValueQuality.Offline) : Measurement.Good(i), Pressure1KPa = Measurement.Good(123.456m) }).ToArray());
        var result = await history.QueryTrendAsync(new(0, 300000, [new(config.Tanks[0].Id, TrendQuantity.Level), new(config.Tanks[0].Id, TrendQuantity.Pressure1)], 100));
        Assert.All(result, s => Assert.Equal(300, s.SourceRows)); Assert.Contains(result[0].Points, p => p.Value is null); Assert.All(result[1].Points, p => Assert.Equal(123.456m, p.Value));
    }
}
