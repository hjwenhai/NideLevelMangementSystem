using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;

namespace Naide.LevelManager.Tests;

public class MeteringTests
{
    private static readonly AuditActor Admin = new("a", "计量测试", AccountRole.Administrator);
    private static CapacityTable MainTable() => new() { Number = "CT-1", Name = "主表", Points = [new(0, 0), new(10000, 1000)] };
    private static CapacityTable Pressure() => new() { Number = "PT-1", Name = "静压力修正", Kind = CapacityTableKind.StaticPressure, Points = [new(0, 0), new(10000, 10)] };
    [Fact] public void AppliesCustomerDensityRatioThenWaterThenInstrumentDensityForMass()
    {
        var main = MainTable(); var pressure = Pressure();
        var result = MeteringCalculator.Calculate(new() { Enabled = true, MainTableId = main.Id, StaticPressureTableId = pressure.Id, WaterVolumeM3 = 4.25m, StandardDensity20KgM3 = 999 }, Measurement.Good(5000), Measurement.Good(850), main, pressure, null);
        Assert.Equal(500m, result.MainVolumeM3); Assert.Equal(4.25m, result.StaticCorrectionM3);
        Assert.Equal(504.25m, result.CurrentVolumeM3.Value); Assert.Equal(500m, result.NetVolumeM3.Value); Assert.Equal(425m, result.MassT.Value);
    }
    [Fact] public void ConvertsExplicitHeightAndVolumeUnitsWithoutChangingPhysicalResult()
    {
        var main = MainTable() with { HeightUnit = HeightUnit.Metre, VolumeUnit = VolumeUnit.Litre, Points = [new(0, 0), new(10, 1000000)] };
        Assert.Equal(500, CapacityRules.Interpolate(main, 5000));
        Assert.Equal(1000, CapacityRules.Interpolate(main, 10000)); Assert.Equal(0, CapacityRules.Interpolate(main, 0));
    }
    [Fact] public void MissingDensityDoesNotInventStaticCorrectionOrZeroMass()
    {
        var main = MainTable(); var pressure = Pressure(); var setting = new MeteringSettings { Enabled = true, MainTableId = main.Id, StaticPressureTableId = pressure.Id };
        Assert.Null(MeteringCalculator.Calculate(setting, Measurement.Good(5000), Measurement.Missing("未测密"), main, pressure, null).CurrentVolumeM3.Value);
        var withoutPressure = MeteringCalculator.Calculate(setting with { StaticPressureTableId = null }, Measurement.Good(5000), Measurement.Missing("未测密"), main, null, null);
        Assert.Equal(500, withoutPressure.NetVolumeM3.Value); Assert.Null(withoutPressure.MassT.Value);
    }
    [Fact] public void OutOfRangeAndExcessWaterAreInvalidRatherThanClampedOrExtrapolated()
    {
        var table = MainTable(); var settings = new MeteringSettings { Enabled = true, MainTableId = table.Id };
        Assert.Null(MeteringCalculator.Calculate(settings, Measurement.Good(11000), Measurement.Good(850), table, null, null).NetVolumeM3.Value);
        var wet = MeteringCalculator.Calculate(settings with { WaterVolumeM3 = 600 }, Measurement.Good(5000), Measurement.Good(850), table, null, null);
        Assert.Equal(500, wet.CurrentVolumeM3.Value); Assert.Null(wet.NetVolumeM3.Value); Assert.Null(wet.MassT.Value);
    }
    [Fact] public void DecimalTableIsExplicitlyExcludedPendingConfirmation()
    {
        var table = MainTable(); var small = MainTable() with { Id = Guid.NewGuid(), Kind = CapacityTableKind.Decimal };
        var result = MeteringCalculator.Calculate(new() { Enabled = true, MainTableId = table.Id, DecimalTableId = small.Id }, Measurement.Good(5000), Measurement.Good(850), table, null, small);
        Assert.Equal(500, result.NetVolumeM3.Value); Assert.Contains(result.Notes, n => n.Contains("未参与"));
    }
    [Fact] public void RejectsDuplicateHeightsWrongOrderingAndZeroReference()
    {
        Assert.NotEmpty(CapacityRules.Validate(MainTable() with { Points = [new(0, 0), new(0, 1)] }));
        Assert.NotEmpty(CapacityRules.Validate(MainTable() with { Points = [new(1, 2), new(0, 3)] }));
        Assert.NotEmpty(CapacityRules.Validate(Pressure() with { ReferenceDensityKgM3 = 0 }));
    }
    [Fact] public async Task TableVersionsAreImmutableAndConflictingEditsAreRejected()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var a = await f.Store.SaveTableAsync(MainTable(), Admin);
        var b = await f.Store.SaveTableAsync(a with { Points = [new(0, 0), new(10000, 2000)] }, Admin);
        Assert.Equal(1, a.Revision); Assert.Equal(2, b.Revision);
        Assert.Equal(1000, (await f.Store.GetTableAsync(a.Id, 1))!.Points[1].Volume);
        await Assert.ThrowsAsync<RevisionConflictException>(() => f.Store.SaveTableAsync(a, Admin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Store.SaveTableAsync(b, Admin with { Role = AccountRole.Viewer }));
    }
    [Fact] public async Task BindingChecksKindAndUsageBeforeDisablingAndKeepsCalculationEvidence()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var table = await f.Store.SaveTableAsync(MainTable(), Admin);
        var c = Fixtures.Configuration(2); c = c with { Tanks = c.Tanks.Select(t => t with { Metering = new() { Enabled = true, MainTableId = table.Id } }).ToArray() };
        var config = await f.Store.SaveAsync(c, Admin); Assert.Equal(2, (await f.Store.ListTablesAsync())[0].AssociatedTanks.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveTableAsync(table with { Enabled = false }, Admin));
        var calc = new MeteringService(f.Store); var sample = new ReadingSnapshot { TankId = config.Tanks[0].Id, LevelMm = Measurement.Good(5000), InstrumentDensityKgM3 = Measurement.Good(850) };
        var old = await calc.CalculateAsync(config.Tanks[0], sample, default);
        await f.Store.SaveTableAsync(table with { Points = [new(0, 0), new(10000, 2000)] }, Admin);
        var next = await calc.CalculateAsync(config.Tanks[0], sample, default);
        Assert.Equal(500, old.NetVolumeM3.Value); Assert.Equal(1000, next.NetVolumeM3.Value); Assert.NotEqual(old.CalculationRevision, next.CalculationRevision);
        Assert.Equal(2, Convert.ToInt32(await f.Scalar("SELECT count(*) FROM calculation_versions WHERE Id>0")));
        var wrong = config with { Tanks = [config.Tanks[0] with { Metering = new() { MainTableId = table.Id, StaticPressureTableId = table.Id } }] };
        await Assert.ThrowsAsync<ConfigurationException>(() => f.Store.SaveAsync(wrong, Admin));
    }
    [Fact] public async Task XlsxRoundTripPreservesDataAndDoesNotTurnNamesIntoFormulas()
    {
        var table = MainTable() with { Number = "=SUM(A1:A2)", Points = [new(0, 4.169m), new(0.001m, 4.412m)], HeightUnit = HeightUnit.Metre };
        var bytes = CapacityExcel.Export(table); var preview = CapacityExcel.Preview(bytes, 0, 2, 1, 2);
        Assert.Equal(2, preview.ImportedRows); Assert.Equal(table.Points, preview.Points); Assert.Contains(preview.Notes, n => n.Contains("不执行宏"));
        await Task.CompletedTask;
    }
}
