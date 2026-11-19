using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
using Naide.LevelManager.Infrastructure;
using Xunit;

namespace Naide.LevelManager.Tests;
public class AlarmTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-28T00:00:00Z");
    private static readonly AlarmRule High = new(Guid.NewGuid(), "T-101", "level.high", "液位高", AlarmQuantity.Level, AlarmDirection.High, 100, 5, 10, AlarmSeverity.General);
    private static readonly AuditActor Operator = new("operator", "操作员", AccountRole.Operator);
    [Fact] public void DelayRequiresContinuousValidBreachAndResetsAfterAnInvalidValue()
    {
        var pending = AlarmEngine.Advance(null, High, Measurement.Good(100), Start); Assert.Empty(pending.Changed);
        pending = AlarmEngine.Advance(pending.Runtime, High, Measurement.Missing("超时"), Start.AddSeconds(9)); Assert.Null(pending.Runtime.CandidateSinceUtc);
        pending = AlarmEngine.Advance(pending.Runtime, High, Measurement.Good(101), Start.AddSeconds(10)); Assert.Empty(pending.Changed);
        Assert.Empty(AlarmEngine.Advance(pending.Runtime, High, Measurement.Good(101), Start.AddSeconds(19)).Changed);
        var active = AlarmEngine.Advance(pending.Runtime, High, Measurement.Good(101), Start.AddSeconds(20));
        Assert.Equal(Start.AddSeconds(10), Assert.Single(active.Changed).DetectedAtUtc);
    }
    [Fact] public void HysteresisAndInvalidDataCannotClearAnActiveAlarm()
    {
        var r = High with { DelaySeconds = 0 }; var state = AlarmEngine.Advance(null, r, Measurement.Good(100), Start).Runtime;
        foreach (var q in Enum.GetValues<ValueQuality>().Where(q => q != ValueQuality.Good))
        { var result = AlarmEngine.Advance(state, r, new(null, q), Start.AddSeconds(1)); Assert.True(result.Runtime.Current!.Active); Assert.Null(result.Runtime.Current.RestoredAtUtc); }
        Assert.True(AlarmEngine.Advance(state, r, Measurement.Good(96), Start.AddSeconds(2)).Runtime.Current!.Active);
        var restored = AlarmEngine.Advance(state, r, Measurement.Good(95), Start.AddSeconds(3)); Assert.Null(restored.Runtime.Current); Assert.Equal("RecoveredUnacknowledged", Assert.Single(restored.Changed).State);
    }
    [Theory][InlineData(AlarmDirection.High, 100, 99)][InlineData(AlarmDirection.Low, 100, 101)]
    public void ZeroHysteresisDoesNotToggleAtTheExactBoundary(AlarmDirection direction, int trigger, int clear)
    {
        var r = High with { Direction = direction, Hysteresis = 0, DelaySeconds = 0 }; var state = AlarmEngine.Advance(null, r, Measurement.Good(trigger), Start).Runtime;
        Assert.Empty(AlarmEngine.Advance(state, r, Measurement.Good(trigger), Start.AddSeconds(1)).Changed);
        Assert.NotNull(Assert.Single(AlarmEngine.Advance(state, r, Measurement.Good(clear), Start.AddSeconds(2)).Changed).RestoredAtUtc);
    }
    [Fact] public void RuleChangeEndsTheOldEpisodeWithoutFakingRecovery()
    {
        var r = High with { DelaySeconds = 0 }; var state = AlarmEngine.Advance(null, r, Measurement.Good(101), Start).Runtime;
        var ended = AlarmEngine.Advance(state, r with { Threshold = 200 }, Measurement.Good(101), Start.AddSeconds(1));
        var old = Assert.Single(ended.Changed); Assert.Equal("Ended", old.State); Assert.Null(old.RestoredAtUtc);
    }
    [Fact] public void ThresholdOrderingAndNullAlarmSettingsAreRejected()
    {
        Assert.NotEmpty(AlarmEngine.Validate(new() { Level = new() { Low = 100, High = 10 } }, "test"));
        Assert.NotEmpty(AlarmEngine.Validate(null, "test"));
        Assert.Empty(AlarmEngine.Validate(new() { Level = new() { Low = 0, High = 100, Hysteresis = 0 } }, "test"));
    }
    [Fact] public async Task AcknowledgementRecoveryRestartAndNewEpisodeKeepSeparateHistory()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var store = new SqliteAlarmStore(f.PathToDb); var rule = High with { DelaySeconds = 0 };
        await store.EvaluateAsync([new(rule, Measurement.Good(101))], Start); var record = Assert.Single(await store.QueryAsync(new()));
        Assert.Equal(1, await store.AcknowledgeAsync([record.Id], Operator, Start.AddSeconds(1))); Assert.Equal(0, await store.AcknowledgeAsync([record.Id], Operator, Start.AddSeconds(2)));
        store = new(f.PathToDb); await store.ResetPendingAsync(); await store.EvaluateAsync([new(rule, new(null, ValueQuality.Offline))], Start.AddSeconds(3));
        var active = Assert.Single(await store.QueryAsync(new())); Assert.Equal("ActiveAcknowledged", active.State); Assert.Equal(ValueQuality.Offline, active.LastQuality);
        await store.EvaluateAsync([new(rule, Measurement.Good(95))], Start.AddSeconds(4)); Assert.Empty(await store.QueryAsync(new()));
        var old = Assert.Single(await store.QueryAsync(new("history"))); Assert.Equal("Closed", old.State); Assert.Equal(Operator.DisplayName, old.AcknowledgedByName);
        await store.EvaluateAsync([new(rule, Measurement.Good(101))], Start.AddSeconds(5)); Assert.NotEqual(old.Id, Assert.Single(await store.QueryAsync(new())).Id);
    }
    [Fact] public async Task RecoveredUnacknowledgedEpisodeCanBeConfirmedAfterANewAlarmStarts()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var store = new SqliteAlarmStore(f.PathToDb); var rule = High with { DelaySeconds = 0 };
        await store.EvaluateAsync([new(rule, Measurement.Good(101))], Start); var first = Assert.Single(await store.QueryAsync(new()));
        await store.EvaluateAsync([new(rule, Measurement.Good(95))], Start.AddSeconds(1));
        Assert.Equal("RecoveredUnacknowledged", Assert.Single(await store.QueryAsync(new())).State);
        await store.EvaluateAsync([new(rule, Measurement.Good(101))], Start.AddSeconds(2)); await store.AcknowledgeAsync([first.Id], Operator, Start.AddSeconds(3));
        var current = Assert.Single(await store.QueryAsync(new())); Assert.NotEqual(first.Id, current.Id); Assert.Null(current.AcknowledgedAtUtc);
    }
    [Fact] public async Task DisabledTankEndsMonitoringAndConfirmationBatchRollsBackOnUnknownId()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var store = new SqliteAlarmStore(f.PathToDb); var rule = High with { DelaySeconds = 0 };
        await store.EvaluateAsync([new(rule, Measurement.Good(101))], Start); var record = Assert.Single(await store.QueryAsync(new()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcknowledgeAsync([record.Id, Guid.NewGuid()], Operator, Start));
        Assert.Null(Assert.Single(await store.QueryAsync(new())).AcknowledgedAtUtc);
        await store.EvaluateAsync([], Start.AddSeconds(1)); var ended = Assert.Single(await store.QueryAsync(new("all"))); Assert.Equal("Ended", ended.State); Assert.Null(ended.RestoredAtUtc);
    }
    [Fact] public async Task PendingTimeIsResetWhenServiceRestartsAndReadOnlyUserCannotAcknowledge()
    {
        await using var f = new StoreFixture(); await f.Initialize(); var store = new SqliteAlarmStore(f.PathToDb);
        await store.EvaluateAsync([new(High, Measurement.Good(101))], Start); await store.ResetPendingAsync();
        await store.EvaluateAsync([new(High, Measurement.Good(101))], Start.AddHours(1)); Assert.Empty(await store.QueryAsync(new()));
        await store.EvaluateAsync([new(High, Measurement.Good(101))], Start.AddHours(1).AddSeconds(10)); var record = Assert.Single(await store.QueryAsync(new()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AcknowledgeAsync([record.Id], Operator with { Role = AccountRole.Viewer }, Start));
    }
}
