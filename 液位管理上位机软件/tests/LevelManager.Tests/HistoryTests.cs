using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;

namespace Naide.LevelManager.Tests;

public class HistoryTests
{
    private static readonly AuditActor Admin = new("test", "历史测试", AccountRole.Administrator);
    private static readonly long At = DateTimeOffset.Parse("2025-01-01T00:00:00Z").ToUnixTimeMilliseconds();
    private static HistorySample Sample(SystemConfiguration c, int tank = 0) => new()
    {
        TankId = c.Tanks[tank].Id, ConfigurationRevision = c.Revision,
        AcquisitionSessionId = Guid.NewGuid(), AtUtcMs = At, ReceivedAtUtcMs = At - 100,
        LevelMm = Measurement.Good(1234.567m), InstantTemperatureC = Measurement.Good(0),
        InstrumentDensityKgM3 = Measurement.Good(851.25m), CurrentVolumeM3 = Measurement.Good(123.456789m),
        AtsPoints = [new(1, Measurement.Good(-12.5m)), new(2, new(null, ValueQuality.Fault))]
    };

    [Fact] public async Task HistoryKeepsPrecisionZeroQualityAndConfigurationVersion()
    {
        await using var f = new StoreFixture(); await f.Initialize();
        var config = await f.Store.SaveAsync(Fixtures.Configuration(), Admin);
        var history = new SqliteHistoryStore(f.PathToDb); var sample = Sample(config);
        Assert.Equal(1, await history.AppendAsync([sample]));
        await f.Store.SaveAsync(config with { Tanks = [config.Tanks[0] with { Code = "改名" }] }, Admin);
        var actual = Assert.Single((await history.QueryAsync(new(At, At + 1))).Rows);
        Assert.Equal(sample.ConfigurationRevision, actual.ConfigurationRevision);
        Assert.Equal(1234.567m, actual.LevelMm.Value); Assert.Equal(0m, actual.InstantTemperatureC.Value);
        Assert.Equal(123.456789m, actual.CurrentVolumeM3.Value); Assert.Equal(851.25m, actual.InstrumentDensityKgM3.Value);
        Assert.Equal(ValueQuality.Unconfigured, actual.InterfaceMm.Quality); Assert.Null(actual.InterfaceMm.Value);
        Assert.Equal(-12.5m, actual.AtsPoints[0].TemperatureC.Value); Assert.Equal(ValueQuality.Fault, actual.AtsPoints[1].TemperatureC.Quality);
        Assert.Equal(sample.AcquisitionSessionId, actual.AcquisitionSessionId);
    }

    [Fact] public async Task InvalidFieldCannotPersistStaleNumericValue()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var c = await f.Store.SaveAsync(Fixtures.Configuration(), Admin);
        var h = new SqliteHistoryStore(f.PathToDb);
        await h.AppendAsync([Sample(c) with { LevelMm = new(999, ValueQuality.Expired) }]);
        var row = Assert.Single((await h.QueryAsync(new(At, At + 1))).Rows);
        Assert.Null(row.LevelMm.Value); Assert.Equal(ValueQuality.Expired, row.LevelMm.Quality);
    }

    [Fact] public async Task RetryDoesNotOverwritePreviouslyArchivedReading()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var c = await f.Store.SaveAsync(Fixtures.Configuration(), Admin);
        var h = new SqliteHistoryStore(f.PathToDb); var sample = Sample(c);
        Assert.Equal(1, await h.AppendAsync([sample])); Assert.Equal(0, await h.AppendAsync([sample with { LevelMm = Measurement.Good(8) }]));
        Assert.Equal(sample.LevelMm.Value, Assert.Single((await h.QueryAsync(new(At, At + 1))).Rows).LevelMm.Value);
    }

    [Fact] public async Task KeysetPagingDoesNotLoseTanksAtSameTimestamp()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var c = await f.Store.SaveAsync(Fixtures.Configuration(3), Admin);
        var h = new SqliteHistoryStore(f.PathToDb); await h.AppendAsync(Enumerable.Range(0, 3).Select(i => Sample(c, i)).ToArray());
        var a = await h.QueryAsync(new(At, At + 1, Limit: 2)); Assert.Equal(2, a.Rows.Count); Assert.NotNull(a.Next);
        var b = await h.QueryAsync(new(At, At + 1, Limit: 2, After: a.Next)); Assert.Single(b.Rows); Assert.Null(b.Next);
        Assert.Equal(3, a.Rows.Concat(b.Rows).Select(x => x.TankId).Distinct().Count());
        Assert.Single((await h.QueryAsync(new(At, At + 1, c.Tanks[1].Id))).Rows);
        Assert.Empty((await h.QueryAsync(new(At + 1, At + 2))).Rows);
    }

    [Fact] public async Task MissingVersionOrWrongTankRollsBackWholeBatch()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var c = await f.Store.SaveAsync(Fixtures.Configuration(), Admin);
        var h = new SqliteHistoryStore(f.PathToDb); var sample = Sample(c);
        await Assert.ThrowsAsync<ArgumentException>(() => h.AppendAsync([sample, sample with { TankId = Guid.NewGuid(), AtUtcMs = At + 1 }]));
        Assert.Equal(0, (await h.StatisticsAsync()).Rows);
        await Assert.ThrowsAsync<SqliteException>(() => h.AppendAsync([sample, sample with { AtUtcMs = At + 1, CalculationRevision = 99 }]));
        Assert.Equal(0, (await h.StatisticsAsync()).Rows);
    }

    [Fact] public async Task RetentionIsBoundedAndKeepsPinnedEvidenceAndVersions()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var c = await f.Store.SaveAsync(Fixtures.Configuration(), Admin);
        var h = new SqliteHistoryStore(f.PathToDb); var sample = Sample(c);
        await h.AppendAsync(Enumerable.Range(0, 5).Select(i => sample with { AtUtcMs = At + i }).ToArray());
        await f.Scalar($"INSERT INTO history_record_pins SELECT 'task-1',TankKey,AtUtcMs FROM history_samples WHERE AtUtcMs={At}");
        Assert.Equal(2, await h.DeleteExpiredBatchAsync(At + 4, 2)); Assert.Equal(3, (await h.StatisticsAsync()).Rows);
        Assert.Equal(1, await h.DeleteExpiredBatchAsync(At + 4, 2));
        var rows = (await h.QueryAsync(new(At, At + 10))).Rows; Assert.Equal(2, rows.Count);
        Assert.Equal(At, rows[0].AtUtcMs); Assert.Equal(At + 4, rows[1].AtUtcMs);
        Assert.Equal(2, Convert.ToInt32(await f.Scalar("SELECT count(*) FROM configuration_versions")));
    }

    [Fact] public async Task ActualBackupRestoresHistoryAndAtsPayload()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var c = await f.Store.SaveAsync(Fixtures.Configuration(), Admin);
        await new SqliteHistoryStore(f.PathToDb).AppendAsync([Sample(c)]);
        var path = Path.Combine(f.Root, "history-restored.db"); await f.Store.BackupAsync(path);
        var restored = new SqliteConfigurationStore(path); await restored.InitializeAsync();
        var result = Assert.Single((await new SqliteHistoryStore(path).QueryAsync(new(At, At + 1))).Rows);
        Assert.Equal(2, result.AtsPoints.Count); Assert.Equal(0m, result.InstantTemperatureC.Value);
    }

    [Fact] public async Task CapacityProfileCannotAccidentallyStartAsProductionDatabase()
    {
        await using var f = new StoreFixture(); await f.Initialize();
        await f.Scalar("UPDATE database_properties SET Value='capacity-test' WHERE Key='profile'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.InitializeAsync());
        await f.Store.InitializeAsync(allowCapacityProfile: true);
    }

    [Fact] public void MalformedAtsAndGoodWithoutValueAreRejected()
    {
        Assert.Throws<ArgumentException>(() => HistoryEncoding.Encode(new(null, ValueQuality.Good), 0));
        Assert.Throws<InvalidDataException>(() => HistoryEncoding.Decode(1, (long)ValueQuality.Offline, 0));
        Assert.Throws<InvalidDataException>(() => HistoryEncoding.DecodePoints([1, 2, 0]));
        Assert.Throws<ArgumentException>(() => HistoryEncoding.EncodePoints([new(1, Measurement.Good(0)), new(1, Measurement.Good(2))]));
    }
}
