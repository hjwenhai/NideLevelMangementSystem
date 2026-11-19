using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;

namespace Naide.LevelManager.Tests;

public class PresentationTests
{
    [Theory][InlineData(3.2, null)][InlineData(0, null)][InlineData(3.5, 0d)][InlineData(4, 0d)][InlineData(12, 50d)][InlineData(20, 100d)][InlineData(23, 100d)]
    public void PressureUsesConfirmedBrokenWireAndClampingRules(double ma, double? expected)
    {
        var r = PressureCalculator.Calculate(new() { Enabled = true, At4Ma = 0, At20Ma = 100 }, Measurement.Good((decimal)ma));
        Assert.Equal(expected.HasValue ? (decimal?)expected.Value : null, r.Value);
        Assert.Equal(expected.HasValue ? ValueQuality.Good : ValueQuality.Fault, r.Quality);
    }
    [Fact] public void PressureUnitAndDisabledInputRemainDistinctFromZero()
    {
        Assert.Equal(500m, PressureCalculator.Calculate(new() { Enabled = true, Unit = PressureUnit.MPa, At20Ma = 1 }, Measurement.Good(12)).Value);
        Assert.Null(PressureCalculator.Calculate(new(), Measurement.Good(12)).Value);
    }
    [Fact] public void GroupCyclesAndUnknownMembersAreRejected()
    {
        var c = Fixtures.Configuration(); var a = new TankGroup { Name = "罐区" }; var b = new TankGroup { Name = "罐组", ParentId = a.Id };
        Assert.Empty(ConfigurationRules.Validate(c with { Groups = [a, b] }));
        Assert.NotEmpty(ConfigurationRules.Validate(c with { Groups = [a with { ParentId = b.Id }, b] }));
        Assert.NotEmpty(ConfigurationRules.Validate(c with { Groups = [a with { TankIds = [Guid.NewGuid()] }] }));
    }
    [Fact] public void MediaAndDisplayValidationRejectUnsafeColorAndWrongPrecision()
    {
        var c = Fixtures.Configuration();
        Assert.NotEmpty(ConfigurationRules.Validate(c with { Media = [new() { Code = "A", Name = "柴油", Color = "url(example)" }] }));
        Assert.NotEmpty(ConfigurationRules.Validate(c with { Display = new() { MassDecimals = 8 } }));
    }
    [Fact] public async Task PressureHistoryRoundTripKeepsCanonicalValuesAndQuality()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var config = await f.Store.SaveAsync(Fixtures.Configuration(), new("a", "测试", AccountRole.Administrator));
        var sample = new HistorySample { TankId = config.Tanks[0].Id, ConfigurationRevision = config.Revision, AcquisitionSessionId = Guid.NewGuid(), AtUtcMs = 1000, Pressure1KPa = Measurement.Good(123.456m), Pressure2KPa = new(null, ValueQuality.Fault) };
        var history = new SqliteHistoryStore(f.PathToDb); await history.AppendAsync([sample]);
        var row = Assert.Single((await history.QueryAsync(new(0, 2000))).Rows);
        Assert.Equal(123.456m, row.Pressure1KPa.Value); Assert.Null(row.Pressure2KPa.Value); Assert.Equal(ValueQuality.Fault, row.Pressure2KPa.Quality);
    }
}
