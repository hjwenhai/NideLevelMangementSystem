using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class MotionEvidenceTests
{
    [Fact]public async Task MarkerAndAuditAreAtomicAndDuplicateEvidenceCannotBeOverwritten()
    {
        await using var f=new StoreFixture();await f.Initialize();var store=new SqliteMotionEvidence(f.PathToDb);var marker=new MotionEvidence(Guid.NewGuid(),Guid.NewGuid(),"instrument-command",DateTimeOffset.UtcNow,null,"a","隔离核验");await store.CreateAsync(marker);Assert.True(await store.ExistsAsync(marker.Id));Assert.Equal(1,await store.CountAsync());await Assert.ThrowsAnyAsync<Exception>(()=>store.CreateAsync(marker));Assert.Equal(1,await store.CountAsync());
        await f.Scalar("CREATE TRIGGER deny_motion_audit BEFORE INSERT ON operation_events BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;");var denied=marker with{Id=Guid.NewGuid()};await Assert.ThrowsAnyAsync<Exception>(()=>store.CreateAsync(denied));Assert.False(await store.ExistsAsync(denied.Id));
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.Parse("2026-10-28T00:00:00Z");public long Ticks;public override DateTimeOffset GetUtcNow()=>Now;public override long GetTimestamp()=>Ticks;public override long TimestampFrequency=>1000;
    }
    [Fact]public void UpdatingEvidenceMetadataDoesNotRestartTheMonotonicTimeAnchor()
    {
        var time=new Clock();var initial=time.Now;var clock=new ProtectedClock(time,new(initial));time.Now=initial.AddMinutes(-2);time.Ticks=60000;var observed=clock.Observe();clock.UpdateMetadata(observed.Watermark with{LastMotionMarker=Guid.NewGuid()});time.Ticks=120000;
        var next=clock.Observe();Assert.Equal(initial.AddMinutes(2),next.EffectiveUtc);Assert.NotNull(next.Watermark.LastMotionMarker);Assert.False(next.Watermark.ClockProblem);
    }
}
