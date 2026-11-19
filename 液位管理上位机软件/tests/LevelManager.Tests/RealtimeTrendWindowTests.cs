using Naide.LevelManager.Domain;
using Naide.LevelManager.Host;
using Xunit;

namespace Naide.LevelManager.Tests;

public sealed class RealtimeTrendWindowTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(600)]
    [InlineData(2000)]
    public void FastestPollingRetainsTheFullHourAndGapMarkers(int maximumPoints)
    {
        var clock = new Clock();
        var trends = new RealtimeTrends(clock);
        var tank = Guid.NewGuid();
        var first = clock.Now.AddHours(-1).AddMilliseconds(1);
        trends.Publish(new() { TankId = tank, ReceivedAtUtc = first.AddMilliseconds(-1), LevelMm = Measurement.Good(100) });
        for (int i = 0; i < 7200; i++)
        {
            trends.Publish(new() { TankId = tank, ReceivedAtUtc = first.AddMilliseconds(i * 500), LevelMm = Measurement.Good(1000 + i) });
            if (i == 2400)
                trends.Publish(new() { TankId = tank, ReceivedAtUtc = first.AddMilliseconds(i * 500 + 1), Diagnostic = "REALTIME_GAP" });
        }

        var result = trends.Query(new(0, 1, [new(tank, TrendQuantity.Level)], maximumPoints), 30);
        var series = Assert.Single(result.Series);
        Assert.Equal(3_600_000, result.EndUtcMs - result.StartUtcMs);
        Assert.Equal(7200, series.SourceRows);
        Assert.True(series.Reduced);
        Assert.Equal(1000m, series.Points[0].Value);
        Assert.Equal(first.ToUnixTimeMilliseconds(), series.Points[0].AtUtcMs);
        Assert.Contains(series.Points, point => point.GapMarker && point.Value == null);
        Assert.Equal(8199m, series.Points[^1].Value);
    }

    [Fact]
    public void APartlyFilledWindowStaysOneHourAndRollsForwardWithoutNewSamples()
    {
        var clock = new Clock();
        var trends = new RealtimeTrends(clock);
        var tank = Guid.NewGuid();
        var query = new TrendQuery(0, 1, [new(tank, TrendQuantity.Level)], 600);
        trends.Publish(new() { TankId = tank, ReceivedAtUtc = clock.Now, LevelMm = Measurement.Good(1234) });

        var result = trends.Query(query, 30);
        Assert.Equal(3_600_000, result.EndUtcMs - result.StartUtcMs);
        var sample = Assert.Single(Assert.Single(result.Series).Points);
        Assert.Equal(clock.Now.ToUnixTimeMilliseconds(), sample.AtUtcMs);
        Assert.Equal(1234m, sample.Value);

        clock.Now = clock.Now.AddHours(1);
        Assert.Empty(Assert.Single(trends.Query(query, 30).Series).Points);
    }
}
