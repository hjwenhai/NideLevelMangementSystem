using System.IO.Compression;
using System.Text.Json;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;

if (args.Length != 2) throw new ArgumentException("setup|lock|restore <isolated scenario directory>");
var root = Path.GetFullPath(args[1]);
var allowed = Path.Combine(Directory.GetCurrentDirectory(), "output", "restore-desk-qa_20261031") + Path.DirectorySeparatorChar;
if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Scenario must stay inside this task's QA directory");
var data = Path.Combine(root, "business"); var zipPath = Path.Combine(root, "隔离验证业务备份.zip");
if (args[0] == "setup")
{
    if (Directory.Exists(root)) throw new IOException("Do not overwrite a scenario");
    Directory.CreateDirectory(data); var db = Path.Combine(data, "level-manager.db"); var store = new SqliteConfigurationStore(db); await store.InitializeAsync();
    var admin = new AuditActor("qa", "隔离恢复验证", AccountRole.Administrator);
    await store.CreateInitialAdministratorAsync(new("qa", "qa", "隔离验证", AccountRole.Administrator, "not-an-operational-password", true));
    var channel = new ChannelDefinition { Name = "仅记录，未连接仪表", PortName = "COM99" };
    var config = await store.SaveAsync(new() { Channels = [channel], Tanks = [new() { Code = "QA-101", ChannelId = channel.Id }, new() { Code = "QA-102", ChannelId = channel.Id, Address = 2 }] }, admin);
    await new SqliteHistoryStore(db).AppendAsync(Enumerable.Range(0, 120).Select(i => new HistorySample { TankId = config.Tanks[i % 2].Id, AtUtcMs = DateTimeOffset.UtcNow.AddMinutes(-120 + i).ToUnixTimeMilliseconds(), ReceivedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ConfigurationRevision = config.Revision, CalculationRevision = 0, AcquisitionSessionId = Guid.NewGuid() }).ToArray());
    var backup = await OnlineDatabaseBackup.CreateAsync(db, Path.Combine(root, "backup.db"));
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        zip.CreateEntryFromFile(Path.Combine(root, "backup.db"), "业务数据备份.db", CompressionLevel.Optimal);
        using var writer = new StreamWriter(zip.CreateEntry("备份说明.json").Open());
        await writer.WriteAsync(JsonSerializer.Serialize(new DatabaseBackupManifest(ProductTrust.ProductId, backup.SchemaVersion, backup.ConfigurationRevision, backup.SnapshotAtUtc, backup.Bytes, backup.Sha256), LicenseVerifier.Json));
    }
    await store.SaveAsync(config with { ReadingExpirySeconds = 31 }, admin);
    var auth = data + ".authorization"; Directory.CreateDirectory(auth); await File.WriteAllTextAsync(Path.Combine(auth, "evidence.dat"), "independent-qa-evidence");
    using var inspected = await BusinessBackupInspection.OpenAsync(zipPath);
    await File.WriteAllTextAsync(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(new { dataRoot = data, backup = zipPath, backupRevision = 1, currentRevision = 2, historyRows = inspected.Overview.HistoryRows, realDeviceAccess = false }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Fixture ready: " + root);
}
else if (args[0] == "lock")
{
    using var guard = new FileStream(Path.Combine(data, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    await File.WriteAllTextAsync(Path.Combine(root, "lock-ready.json"), "{\"simulatedBackgroundLock\":true}");
    while (!File.Exists(Path.Combine(root, "stop-lock.flag"))) await Task.Delay(500);
}
else if (args[0] == "restore")
{
    using var inspected = await BusinessBackupInspection.OpenAsync(zipPath); var result = await inspected.RestoreAsync(data, true);
    var config = await new SqliteConfigurationStore(Path.Combine(data, "level-manager.db")).GetAsync(); var rows = (await new SqliteHistoryStore(Path.Combine(data, "level-manager.db")).StatisticsAsync()).Rows;
    if (config.Revision != 1 || rows != 120 || !File.Exists(Path.Combine(result.SafetyDirectory, "level-manager.db")) || await File.ReadAllTextAsync(Path.Combine(data + ".authorization", "evidence.dat")) != "independent-qa-evidence") throw new InvalidDataException("Isolated restore verification failed");
    await File.WriteAllTextAsync(Path.Combine(root, "restore-result.json"), JsonSerializer.Serialize(new { passed = true, config.Revision, historyRows = rows, result.SafetyDirectory, result.RequiresFieldReview, authorizationUnchanged = true, realDeviceAccess = false }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS: isolated restoration, original preserved, authorization unchanged");
}
else throw new ArgumentException("Unknown scenario action");
