using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;
namespace Naide.LevelManager.Tests;
public class ScheduleTests
{
    [Fact]public void DailyWindowUsesBeijingTimeAndNeverCatchesUpOutsideTheMinute()
    {
        var s=new SnapshotSchedule{Name="日终",Time="23:55"};var zone=TimeZoneInfo.CreateCustomTimeZone("test-China",TimeSpan.FromHours(8),"China","China");
        Assert.Equal("2026-10-28",SnapshotSchedulePolicy.DueDate(s,DateTimeOffset.Parse("2026-10-28T15:55:00Z"),zone));
        Assert.Equal("2026-10-28",SnapshotSchedulePolicy.DueDate(s,DateTimeOffset.Parse("2026-10-28T15:55:59Z"),zone));
        Assert.Null(SnapshotSchedulePolicy.DueDate(s,DateTimeOffset.Parse("2026-10-28T15:56:00Z"),zone));
        Assert.Null(SnapshotSchedulePolicy.DueDate(s with{Enabled=false},DateTimeOffset.Parse("2026-10-28T15:55:00Z"),zone));
    }
    [Fact]public async Task DailyClaimAndQueueAreAtomicAndSurviveRestartWithoutDuplicateRuns()
    {
        await using var f=new StoreFixture();await f.Initialize();var store=new SqliteDataJobStore(f.PathToDb);var id=Guid.NewGuid();var at=DateTimeOffset.UtcNow;
        DataJob Job()=>new(){Kind=DataJobKind.Snapshot,ActorId="system",ActorName="定时任务",CreatedAtUtc=at,RequestJson="{}"};
        Assert.True(await store.CreateScheduledAsync(id,"2026-10-28",Job()));Assert.False(await store.CreateScheduledAsync(id,"2026-10-28",Job()));
        await store.RecoverInterruptedAsync(at);Assert.False(await new SqliteDataJobStore(f.PathToDb).CreateScheduledAsync(id,"2026-10-28",Job()));Assert.Single(await store.ListAsync());
    }
    [Fact]public void UnknownMembersDuplicateNamesAndBadTimesAreRejected()
    {
        var config=Fixtures.Configuration();Assert.NotEmpty(SnapshotSchedulePolicy.Validate([new(){Name="a",Time="25:00"}],config.Tanks));
        Assert.NotEmpty(SnapshotSchedulePolicy.Validate([new(){Name="a",TankIds=[Guid.NewGuid()]}],config.Tanks));
        Assert.NotEmpty(SnapshotSchedulePolicy.Validate([new(){Name="a"},new(){Name="a"}],config.Tanks));
    }
}
