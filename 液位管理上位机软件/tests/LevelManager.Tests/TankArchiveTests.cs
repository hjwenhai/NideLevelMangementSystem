using System.Text.Json;
using System.Text.Json.Nodes;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;

namespace Naide.LevelManager.Tests;

public class TankArchiveTests
{
    private static readonly AuditActor Admin = new("archive-qa", "档案测试", AccountRole.Administrator);

    [Fact]
    public void OldConfigurationWithoutNewArchiveFieldsStillLoads()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(Fixtures.Configuration(), options)!;
        foreach (var tank in json["tanks"]!.AsArray())
            foreach (var key in new[] { "name", "productNumber", "batch", "diameterMm", "nominalCapacityM3", "notes" })
                tank!.AsObject().Remove(key);
        var loaded = json.Deserialize<SystemConfiguration>(options)!;
        Assert.Empty(ConfigurationRules.Validate(loaded));
        Assert.Equal("", loaded.Tanks[0].Name);
        Assert.Null(loaded.Tanks[0].DiameterMm);
        Assert.Null(loaded.Tanks[0].NominalCapacityM3);
    }

    [Fact]
    public async Task ArchiveAndGroupMembershipSurviveSaveBackupAndReopen()
    {
        await using var f = new StoreFixture();
        await f.Initialize();
        var c = Fixtures.Configuration();
        var tank = c.Tanks[0] with { Name = "柴油储罐1", ProductNumber = "P-柴油", Batch = "首批", DiameterMm = 18000.5m, NominalCapacityM3 = 3000.25m, Notes = "检修记录备注" };
        c = c with { Tanks = [tank], Groups = [new() { Name = "首批设备", Kind = GroupKind.Batch, TankIds = [tank.Id] }] };
        var saved = await f.Store.SaveAsync(c, Admin);
        var backup = Path.Combine(f.Root, "archive-backup.db");
        await f.Store.BackupAsync(backup);
        var restored = new SqliteConfigurationStore(backup);
        await restored.InitializeAsync();
        var reopened = await restored.GetAsync();
        Assert.Equal(saved.Revision, reopened.Revision);
        Assert.Equal("柴油储罐1", reopened.Tanks[0].Name);
        Assert.Equal("P-柴油", reopened.Tanks[0].ProductNumber);
        Assert.Equal("首批", reopened.Tanks[0].Batch);
        Assert.Equal(18000.5m, reopened.Tanks[0].DiameterMm);
        Assert.Equal(3000.25m, reopened.Tanks[0].NominalCapacityM3);
        Assert.Equal("检修记录备注", reopened.Tanks[0].Notes);
        Assert.Equal(GroupKind.Batch, reopened.Groups[0].Kind);
        Assert.Equal(tank.Id, Assert.Single(reopened.Groups[0].TankIds));
    }

    [Theory]
    [InlineData(null, true)] [InlineData(0d, false)] [InlineData(-1d, false)] [InlineData(18000.5, true)] [InlineData(1000001d, false)]
    public void DiameterIsOptionalButMustBePositiveAndBounded(double? value, bool valid)
    {
        var c = Fixtures.Configuration();
        c = c with { Tanks = [c.Tanks[0] with { DiameterMm = value.HasValue ? (decimal)value.Value : null }] };
        Assert.Equal(valid, ConfigurationRules.Validate(c).All(e => !e.Field.EndsWith("/diameterMm")));
    }

    [Theory]
    [InlineData(null, true)] [InlineData(0d, false)] [InlineData(-1d, false)] [InlineData(3000.25, true)] [InlineData(1000000001d, false)]
    public void NominalCapacityIsOptionalButMustBePositiveAndBounded(double? value, bool valid)
    {
        var c = Fixtures.Configuration();
        c = c with { Tanks = [c.Tanks[0] with { NominalCapacityM3 = value.HasValue ? (decimal)value.Value : null }] };
        Assert.Equal(valid, ConfigurationRules.Validate(c).All(e => !e.Field.EndsWith("/nominalCapacityM3")));
    }

    [Fact]
    public void OptionalArchiveStringsAcceptEmptyButRejectOversize()
    {
        var c = Fixtures.Configuration();
        Assert.Empty(ConfigurationRules.Validate(c));
        c = c with { Tanks = [c.Tanks[0] with { Name = new('a', 101), ProductNumber = new('p', 101), Batch = new('b', 101), Notes = new('n', 1001) }] };
        var errors = ConfigurationRules.Validate(c);
        foreach (var key in new[] { "name", "productNumber", "batch", "notes" })
            Assert.Contains(errors, e => e.Field.EndsWith("/" + key));
    }
}
