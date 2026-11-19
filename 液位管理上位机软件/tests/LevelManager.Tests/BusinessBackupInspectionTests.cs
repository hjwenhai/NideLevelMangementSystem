using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
using Xunit;

namespace Naide.LevelManager.Tests;

public sealed class BusinessBackupInspectionTests
{
    private static readonly AuditActor Admin = new("a", "恢复桌面验证", AccountRole.Administrator);
    private static async Task<(string Root, SqliteConfigurationStore Store, string Zip)> Prepare(StoreFixture fixture)
    {
        var root = Path.Combine(fixture.Root, "business"); Directory.CreateDirectory(root);
        var store = new SqliteConfigurationStore(Path.Combine(root, "level-manager.db")); await store.InitializeAsync();
        await store.CreateInitialAdministratorAsync(new("a", "admin", "恢复验证", AccountRole.Administrator, "test-hash", true));
        await store.SaveAsync(Fixtures.Configuration(2), Admin);
        var db = Path.Combine(fixture.Root, "backup.db"); var copy = await OnlineDatabaseBackup.CreateAsync(Path.Combine(root, "level-manager.db"), db);
        var zip = Path.Combine(fixture.Root, "业务备份.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(db, "业务数据备份.db", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(archive.CreateEntry("备份说明.json").Open());
            await writer.WriteAsync(JsonSerializer.Serialize(new DatabaseBackupManifest(ProductTrust.ProductId, copy.SchemaVersion, copy.ConfigurationRevision, copy.SnapshotAtUtc, copy.Bytes, copy.Sha256), LicenseVerifier.Json));
        }
        return (root, store, zip);
    }
    private static async Task AlterManifest(string zipPath, Func<DatabaseBackupManifest, DatabaseBackupManifest> alter)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update); var entry = zip.GetEntry("备份说明.json")!;
        DatabaseBackupManifest old; using (var reader = new StreamReader(entry.Open())) old = JsonSerializer.Deserialize<DatabaseBackupManifest>(await reader.ReadToEndAsync(), LicenseVerifier.Json)!;
        entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("备份说明.json").Open()); await writer.WriteAsync(JsonSerializer.Serialize(alter(old), LicenseVerifier.Json));
    }

    [Fact]
    public async Task PreviewReadsTheActualBackupAndPreventsFileReplacementUntilDisposed()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f);
        using (var inspected = await BusinessBackupInspection.OpenAsync(x.Zip))
        {
            Assert.Equal(2, inspected.Overview.Tanks); Assert.Equal(1, inspected.Overview.Accounts); Assert.Equal(0, inspected.Overview.HistoryRows);
            Assert.Equal(1, inspected.Manifest.ConfigurationRevision); Assert.Equal(64, inspected.ArchiveSha256.Length);
            if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => new FileStream(x.Zip, FileMode.Open, FileAccess.Write, FileShare.Read));
        }
        using var writable = new FileStream(x.Zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(writable.CanWrite); Assert.Equal(1, (await x.Store.GetAsync()).Revision);
    }

    [Theory]
    [InlineData("hash")][InlineData("product")][InlineData("future")][InlineData("revision")][InlineData("authorization")][InlineData("time")]
    public async Task InvalidBackupsCannotArmTheRestore(string kind)
    {
        await using var f = new StoreFixture(); var x = await Prepare(f);
        await AlterManifest(x.Zip, m => kind switch { "hash" => m with { Sha256 = new string('0', 64) }, "product" => m with { ProductId = "other-product" }, "future" => m with { SchemaVersion = 999 }, "revision" => m with { ConfigurationRevision = 99 }, "time" => m with { SnapshotAtUtc = default }, _ => m with { AuthorizationIncluded = true } });
        await Assert.ThrowsAsync<InvalidDataException>(() => BusinessBackupInspection.OpenAsync(x.Zip));
        Assert.Equal(1, (await x.Store.GetAsync()).Revision); Assert.False(File.Exists(Path.Combine(x.Root, "restoration-review.required")));
    }

    [Fact]
    public async Task ExtraZipEntriesAreRejectedWithoutTouchingCurrentData()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f);
        using (var zip = ZipFile.Open(x.Zip, ZipArchiveMode.Update)) zip.CreateEntry("../not-a-backup.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => BusinessBackupInspection.OpenAsync(x.Zip));
        Assert.Equal(2, (await x.Store.GetAsync()).Tanks.Count);
    }

    [Fact]
    public async Task BackgroundStartingAfterInspectionStillBlocksRestore()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f); using var inspected = await BusinessBackupInspection.OpenAsync(x.Zip);
        using (var running = new FileStream(Path.Combine(x.Root, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert.NotNull(BusinessBackupInspection.TargetBlockReason(x.Root));
            await Assert.ThrowsAnyAsync<IOException>(() => inspected.RestoreAsync(x.Root, true));
        }
        Assert.Null(BusinessBackupInspection.TargetBlockReason(x.Root)); Assert.Equal(1, (await x.Store.GetAsync()).Revision);
    }

    [Fact]
    public async Task RestoreNeedsConfirmationAndKeepsOriginalDataAndAuthorization()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f);
        await x.Store.SaveAsync((await x.Store.GetAsync()) with { ReadingExpirySeconds = 31 }, Admin);
        var auth = x.Root + ".authorization"; Directory.CreateDirectory(auth); var authFile = Path.Combine(auth, "evidence.dat"); await File.WriteAllTextAsync(authFile, "independent authorization must not change");
        using var inspected = await BusinessBackupInspection.OpenAsync(x.Zip);
        await Assert.ThrowsAsync<ArgumentException>(() => inspected.RestoreAsync(x.Root, false)); Assert.Equal(2, (await x.Store.GetAsync()).Revision);
        var result = await inspected.RestoreAsync(x.Root, true);
        Assert.Equal(1, (await x.Store.GetAsync()).Revision); Assert.Equal(2, result.Tanks); Assert.True(result.RequiresFieldReview);
        Assert.True(File.Exists(Path.Combine(result.SafetyDirectory, "level-manager.db"))); Assert.True(File.Exists(Path.Combine(x.Root, "restoration-review.required")));
        Assert.Equal("independent authorization must not change", await File.ReadAllTextAsync(authFile));
    }

    [Fact]
    public async Task EmptyAndAuthorizationDirectoriesCannotBeRestoreTargets()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f); var empty = Path.Combine(f.Root, "empty"); Directory.CreateDirectory(empty);
        await Assert.ThrowsAsync<ArgumentException>(() => BusinessBackupInspection.InspectTargetAsync(empty));
        var auth = x.Root + ".authorization"; Directory.CreateDirectory(auth); File.Copy(Path.Combine(x.Root, "level-manager.db"), Path.Combine(auth, "level-manager.db"));
        await Assert.ThrowsAsync<ArgumentException>(() => BusinessBackupInspection.InspectTargetAsync(auth));
        Assert.False(File.Exists(Path.Combine(empty, "level-manager.db")));
    }

    [Fact]
    public async Task CancelledInspectionReleasesItsFileAndCannotRestore()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BusinessBackupInspection.OpenAsync(x.Zip, ct: cancelled.Token));
        using (var file = new FileStream(x.Zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(file.CanWrite);
        var inspected = await BusinessBackupInspection.OpenAsync(x.Zip); inspected.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inspected.RestoreAsync(x.Root, true));
    }

    [Fact]
    public async Task InitializedFreshInstallCanBeInspectedAsTargetWithoutCreatingAnAccount()
    {
        await using var f = new StoreFixture(); await f.Initialize();
        // The desktop targets the conventional database filename, just as the installed host does.
        SqliteConfigurationStore.ReleasePools();
        await using (var c = new SqliteConnection("Data Source=" + f.PathToDb)) { await c.OpenAsync(); await using var command = c.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; await command.ExecuteNonQueryAsync(); }
        File.Copy(f.PathToDb, Path.Combine(f.Root, "level-manager.db"));
        var info = await BusinessBackupInspection.InspectTargetAsync(f.Root);
        Assert.Equal(0, info.Accounts); Assert.Equal(0, info.Tanks);
    }

    [Fact]
    public async Task CorruptOriginalDatabaseCanBeRestoredButItsOriginalBytesAreRetained()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f); SqliteConfigurationStore.ReleasePools();
        var path = Path.Combine(x.Root, "level-manager.db"); var damaged = new byte[128]; damaged[0] = 127;
        await File.WriteAllBytesAsync(path, damaged);
        var overview = await BusinessBackupInspection.InspectTargetAsync(x.Root); Assert.False(overview.SummaryAvailable);
        using var inspected = await BusinessBackupInspection.OpenAsync(x.Zip); var result = await inspected.RestoreAsync(x.Root, true);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(result.SafetyDirectory, "level-manager.db")));
        Assert.Equal(1, (await x.Store.GetAsync()).Revision);
    }

    [Fact]
    public async Task HigherVersionOriginalDatabaseCannotBeDowngradedByTheDesktopTool()
    {
        await using var f = new StoreFixture(); var x = await Prepare(f);
        await using (var c = new SqliteConnection("Data Source=" + Path.Combine(x.Root, "level-manager.db"))) { await c.OpenAsync(); await using var command = c.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; await command.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<NotSupportedException>(() => BusinessBackupInspection.InspectTargetAsync(x.Root));
        using var inspected = await BusinessBackupInspection.OpenAsync(x.Zip);
        await Assert.ThrowsAsync<NotSupportedException>(() => inspected.RestoreAsync(x.Root, true));
    }
}
