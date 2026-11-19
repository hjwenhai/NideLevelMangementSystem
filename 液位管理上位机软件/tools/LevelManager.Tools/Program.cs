using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;

if(args.FirstOrDefault()=="leap-window")return await LeapWindowBenchmark.Run(args);
if(args.FirstOrDefault()=="excel-scale")return await ExcelScaleBenchmark.Run(args);
if(args.FirstOrDefault()=="online-scale")return await OnlineScaleBenchmark.Run(args);
if(args.FirstOrDefault()=="restore")return await RestoreCommand.Run(args);
if(args.FirstOrDefault() is "authorization-keygen" or "authorization-issue")return VendorAuthorization.Run(args);

if (args.Length == 0 || args[0] != "capacity")
{
    Console.WriteLine("capacity --output <new absolute directory> [--days 365] [--tanks 128]");
    return 2;
}
string Option(string key, string fallback) => Array.IndexOf(args, key) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
var output = Option("--output", "");
var days = int.Parse(Option("--days", "365"), CultureInfo.InvariantCulture);
var tankCount = int.Parse(Option("--tanks", "128"), CultureInfo.InvariantCulture);
if (!Path.IsPathFullyQualified(output) || days is < 1 or > 366 || tankCount is < 1 or > 128)
    throw new ArgumentException("指定新的绝对输出目录，天数1～366，罐数1～128");
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new ArgumentException("容量验证必须使用新的空目录，不覆盖已有数据");
Directory.CreateDirectory(output);
var drive = new DriveInfo(Path.GetPathRoot(output)!);
var expectedRows = ConfigurationRules.EstimateRows(tankCount, 60, days);
if (drive.AvailableFreeSpace < expectedRows * 400L * 3 + 8L * 1024 * 1024 * 1024)
    throw new IOException("空间不足以同时保留源库、备份与恢复验证副本");
var started = DateTimeOffset.UtcNow;
var timer = Stopwatch.StartNew();
var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z").ToUnixTimeMilliseconds();
var end = start + days * 86_400_000L;
var path = Path.Combine(output, "capacity-source.db");
var store = new SqliteConfigurationStore(path);
await store.InitializeAsync();
var channels = Enumerable.Range(0, (tankCount + 31) / 32).Select(i => new ChannelDefinition { Name = "容量测试通道" + i, PortName = "COM" + (i + 1) }).ToArray();
var config = await store.SaveAsync(new SystemConfiguration
{
    Channels = channels,
    Tanks = Enumerable.Range(0, tankCount).Select(i => new TankDefinition
    {
        Code = "CAP-" + (i + 1).ToString("000"), ChannelId = channels[i / 32].Id, Address = i % 32 + 1,
        TemperatureMode = TemperatureMode.Ats, AtsPointCount = 16
    }).ToArray()
}, new("capacity-runner", "隔离容量测试", AccountRole.Administrator));
var sessionId = Guid.NewGuid();
var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString();
await using var connection = new SqliteConnection(cs);
await connection.OpenAsync();
async Task<object?> Sql(string sql)
{
    await using var cmd = connection.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync();
}
await using(var initialize=connection.CreateCommand()){initialize.CommandText="PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA cache_size=-65536; PRAGMA wal_autocheckpoint=4096; UPDATE database_properties SET Value='capacity-test' WHERE Key='profile'; INSERT INTO calculation_versions(Id,Payload,AtUtcMs) VALUES(1,'{\"source\":\"isolated-capacity\",\"version\":1}',0);";await initialize.ExecuteNonQueryAsync();}
await using (var seed = connection.CreateCommand())
{
    seed.CommandText = "INSERT INTO history_sessions VALUES(1,$session);";
    seed.Parameters.AddWithValue("$session", sessionId.ToString()); await seed.ExecuteNonQueryAsync();
    for (var i = 0; i < config.Tanks.Count; i++)
    {
        seed.Parameters.Clear(); seed.CommandText = "INSERT INTO history_tanks VALUES($key,$id)";
        seed.Parameters.AddWithValue("$key", i + 1); seed.Parameters.AddWithValue("$id", config.Tanks[i].Id.ToString());
        await seed.ExecuteNonQueryAsync();
    }
}
var pointPayload = HistoryEncoding.EncodePoints(Enumerable.Range(1, 16).Select(i => new TemperaturePoint(i, Measurement.Good(20 + i / 10m))).ToArray());
var invalidPointPayload = HistoryEncoding.EncodePoints(Enumerable.Range(1, 16).Select(i => new TemperaturePoint(i, new(null, ValueQuality.Offline))).ToArray());
long invalidBits = 0;
for (var i = 0; i < HistoryEncoding.FieldCount; i++) invalidBits |= (long)ValueQuality.Offline << (i * 3);
void Progress(string stage, int completedDays = 0, object? details = null)
{
    var entry = new { stage, completedDays, days, tankCount, expectedRows, elapsedSeconds = timer.Elapsed.TotalSeconds, details };
    var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
    var target = Path.Combine(output, "progress.json"); File.WriteAllText(target + ".tmp", json); File.Move(target + ".tmp", target, true);
    Console.WriteLine(JsonSerializer.Serialize(entry));
}
Progress("generation-start");
for (var day = 0; day < days; day++)
{
    if (File.Exists(Path.Combine(output, "STOP"))) throw new OperationCanceledException("容量测试收到STOP文件，保留已提交样本和进度");
    if (drive.AvailableFreeSpace < 8L * 1024 * 1024 * 1024) throw new IOException("剩余空间不足8GiB，停止生成以保护磁盘");
    using var tx = connection.BeginTransaction();
    await using var insert = connection.CreateCommand(); insert.Transaction = tx;
    insert.CommandText = """
        WITH RECURSIVE minutes(n) AS (SELECT 0 UNION ALL SELECT n+1 FROM minutes WHERE n<1439)
        INSERT INTO history_samples(TankKey,AtUtcMs,ConfigurationRevision,CalculationRevision,SessionKey,ReceivedAtUtcMs,SourceAtUtcMs,SourceTimeConfirmed,DeviceDataAgeMs,LevelMicrometres,InterfaceMicrometres,InstantMilliC,AverageMilliC,AverageCompletedAtUtcMs,DensityMilliKgM3,CurrentVolumeMicroM3,NetVolumeMicroM3,MassMicroT,QualityBits,InstrumentStatus,InstrumentFault,RawQuality,RawValidity,DiagnosticCode,AtsPoints,Pressure1Pa,Pressure2Pa,PressureQualityBits,Analog1MicroAmp,Analog2MicroAmp,AnalogQualityBits)
        SELECT t.Id,$start+n*60000,1,1,1,$start+n*60000-150,$start+n*60000-200,1,200,
          CASE WHEN n%97!=0 THEN 4000000+t.Id*30000+n*50 END,
          CASE WHEN n%97!=0 THEN 100000 END,
          CASE WHEN n%97!=0 THEN 25000+t.Id*5+n%100 END,
          CASE WHEN n%97!=0 THEN 24500+t.Id*5 END,
          CASE WHEN n%97!=0 THEN $start+n*60000 END,
          CASE WHEN n%97!=0 THEN 832000+t.Id*10 END,
          CASE WHEN n%97!=0 THEN 2000000000+t.Id*100000 END,
          CASE WHEN n%97!=0 THEN 1990000000+t.Id*100000 END,
          CASE WHEN n%97!=0 THEN 1655680000+t.Id*83200 END,
          CASE WHEN n%97=0 THEN $invalid ELSE 0 END,
          CASE WHEN n%97!=0 THEN 1 END,CASE WHEN n%97!=0 THEN 0 END,
          CASE WHEN n%97=0 THEN 1 ELSE 0 END,CASE WHEN n%97=0 THEN 0 ELSE 65535 END,
          CASE WHEN n%97=0 THEN 'TIMEOUT' ELSE '' END,CASE WHEN n%97=0 THEN $invalidPoints ELSE $points END,
          CASE WHEN n%97!=0 THEN 25000+t.Id*10 END,CASE WHEN n%97!=0 THEN 50000+t.Id*10 END,CASE WHEN n%97=0 THEN 18 ELSE 0 END,
          CASE WHEN n%97!=0 THEN 12000 END,CASE WHEN n%97!=0 THEN 16000 END,CASE WHEN n%97=0 THEN 18 ELSE 0 END
        FROM history_tanks t CROSS JOIN minutes;
        """;
    insert.Parameters.AddWithValue("$start", start + day * 86_400_000L);
    insert.Parameters.AddWithValue("$invalidPoints", invalidPointPayload!);
    insert.Parameters.AddWithValue("$invalid", invalidBits); insert.Parameters.AddWithValue("$points", pointPayload!);
    await insert.ExecuteNonQueryAsync(); await tx.CommitAsync();
    if (day % 7 == 0 || day + 1 == days) Progress("generating", day + 1);
    else
    {
        var entry = JsonSerializer.Serialize(new { stage = "generating", completedDays = day + 1, days, tankCount, expectedRows, elapsedSeconds = timer.Elapsed.TotalSeconds });
        File.WriteAllText(Path.Combine(output, "progress.json"), entry);
    }
}
var generationSeconds = timer.Elapsed.TotalSeconds;
Progress("generation-count");
if (Convert.ToInt64(await Sql("SELECT count(*) FROM history_samples")) != expectedRows) throw new InvalidDataException("全年条数不一致");
await using (var counts = connection.CreateCommand())
{
    counts.CommandText = "SELECT TankKey,count(*) FROM history_samples GROUP BY TankKey";
    await using var rows = await counts.ExecuteReaderAsync(); var seen = 0;
    while (await rows.ReadAsync()) { if (rows.GetInt64(1) != days * 1440L) throw new InvalidDataException("逐罐数据数量不一致"); seen++; }
    if (seen != tankCount) throw new InvalidDataException("罐数不一致");
}
var history = new SqliteHistoryStore(path);
var queryTimes = new List<double>();
for (var i = 0; i < 5; i++)
{
    var watch = Stopwatch.StartNew();
    var page = await history.QueryAsync(new(start, end, config.Tanks[i % tankCount].Id, Limit: 2000));
    if (page.Rows.Count != Math.Min(2000, days * 1440)) throw new InvalidDataException("单罐查询结果不完整");
    queryTimes.Add(watch.Elapsed.TotalMilliseconds);
}
var wholeWatch = Stopwatch.StartNew();
var allPage = await history.QueryAsync(new(start, end, Limit: 2000));
var allPageMs = wholeWatch.Elapsed.TotalMilliseconds;
if (allPage.Rows.Any(p => p.AtsPoints.Count != 16)) throw new InvalidDataException("ATS载荷恢复错误");
Progress("concurrent-read-write");
var concurrentReads = 0; var concurrentMs = new List<double>();
var writer = Task.Run(async () =>
{
    for (var n = 0; n < 40; n++)
    {
        var now = end + n * 60000L;
        await history.AppendAsync(config.Tanks.Select(t => new HistorySample
        {
            TankId = t.Id, ConfigurationRevision = config.Revision, CalculationRevision = 1, AtUtcMs = now,
            AcquisitionSessionId = sessionId, ReceivedAtUtcMs = now, LevelMm = Measurement.Good(5000),
            InstantTemperatureC = Measurement.Good(0), InstrumentDensityKgM3 = Measurement.Good(832),
            CurrentVolumeM3 = Measurement.Good(2000), AtsPoints = [new(1, Measurement.Good(0))]
        }).ToArray());
        await Task.Delay(10);
    }
});
while (!writer.IsCompleted || concurrentReads < 10)
{
    var watch = Stopwatch.StartNew();
    await history.QueryAsync(new(start, end + 3600000, config.Tanks[concurrentReads % tankCount].Id, Limit: 500));
    concurrentMs.Add(watch.Elapsed.TotalMilliseconds); concurrentReads++;
    await Task.Delay(5);
}
await writer;
if (Convert.ToInt64(await Sql("SELECT count(*) FROM history_samples")) != expectedRows + tankCount * 40L) throw new InvalidDataException("并发写入条数不一致");
await Sql($"DELETE FROM history_samples WHERE AtUtcMs>={end}");
Progress("annual-single-tank-stream-export");
var exportWatch = Stopwatch.StartNew(); var exported = 0L;
using (var zip = ZipFile.Open(Path.Combine(output, "single-tank-annual.csv.zip"), ZipArchiveMode.Create))
await using (var zipStream = zip.CreateEntry("history.csv", CompressionLevel.Fastest).Open())
await using (var text = new StreamWriter(zipStream))
await using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT AtUtcMs,LevelMicrometres,InstantMilliC,NetVolumeMicroM3,MassMicroT,QualityBits FROM history_samples WHERE TankKey=1 ORDER BY AtUtcMs";
    await using var rows = await command.ExecuteReaderAsync();
    await text.WriteLineAsync("UTC milliseconds,level micrometres,temperature milliC,net volume micro m3,mass micro tonne,quality bits");
    while (await rows.ReadAsync())
    {
        await text.WriteLineAsync(string.Join(',', Enumerable.Range(0, 6).Select(i => rows.IsDBNull(i) ? "" : rows.GetInt64(i).ToString(CultureInfo.InvariantCulture))));
        exported++;
    }
}
if (exported != days * 1440L) throw new InvalidDataException("年度单罐导出数量错误");
var exportSeconds = exportWatch.Elapsed.TotalSeconds;
Progress("source-integrity");
if (Convert.ToString(await Sql("PRAGMA integrity_check")) != "ok" || await Sql("PRAGMA foreign_key_check") is not null) throw new InvalidDataException("源库完整性校验失败");
await Sql("PRAGMA wal_checkpoint(TRUNCATE)");
Progress("backup"); var backupWatch = Stopwatch.StartNew(); var backup = Path.Combine(output, "capacity-backup.db");
await store.BackupAsync(backup); var backupSeconds = backupWatch.Elapsed.TotalSeconds;
Progress("restore-copy"); var restoreWatch = Stopwatch.StartNew(); var restoredPath = Path.Combine(output, "capacity-restored.db");
File.Copy(backup, restoredPath);
Progress("restore-copy-hash");
async Task<string> Hash(string file) { await using var input = File.OpenRead(file); return Convert.ToHexString(await SHA256.HashDataAsync(input)); }
var backupHash = await Hash(backup); var restoredHash = await Hash(restoredPath);
if (backupHash != restoredHash) throw new InvalidDataException("备份与恢复副本SHA256不一致");
var restoredStore = new SqliteConfigurationStore(restoredPath); await restoredStore.InitializeAsync(allowCapacityProfile: true);
var restoredStats = await new SqliteHistoryStore(restoredPath).StatisticsAsync();
if (restoredStats.Rows != expectedRows || (await restoredStore.GetAsync()).Tanks.Count != tankCount) throw new InvalidDataException("实际恢复副本的数据或配置不一致");
var restoredPage = await new SqliteHistoryStore(restoredPath).QueryAsync(new(start, end, config.Tanks[^1].Id, Limit: 1));
if (restoredPage.Rows.Count != 1 || restoredPage.Rows[0].AtsPoints.Count != 16) throw new InvalidDataException("恢复后样本查询失败");
await using (var restoredCheck = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restoredPath, Pooling = false }.ToString()))
{
    await restoredCheck.OpenAsync(); await using var check = restoredCheck.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
    if (Convert.ToString(await check.ExecuteScalarAsync()) != "ok") throw new InvalidDataException("恢复副本完整性不通过");
}
var restoreSeconds = restoreWatch.Elapsed.TotalSeconds;
var result = new
{
    passed = true, profile = "isolated-capacity-test", startedUtc = started, completedUtc = DateTimeOffset.UtcNow,
    days, tankCount, expectedRows, restoredRows = restoredStats.Rows, atsPointsPerSample = 16, schemaVersion=SqliteConfigurationStore.CurrentSchema, pressureInputsPerSample=2, analogInputsPerSample=2,
    invalidMinutesPerDay = 15, generationSeconds, sourceBytes = new FileInfo(path).Length,
    sourceWalBytes = File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0,
    queryFirst2000SingleTankMs = queryTimes, queryFirst2000AllTanksMs = allPageMs,
    concurrentWrites = tankCount * 40, concurrentReads, concurrentQueryMaxMs = concurrentMs.Max(),
    annualSingleTankCsvRows = exported, exportSeconds, backupSeconds, restoreSeconds,
    native = await store.GetHealthAsync(), backupHash, restoredHash,
    peakProcessMemoryBytes = Process.GetCurrentProcess().PeakWorkingSet64,
    processorCount = Environment.ProcessorCount, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    elapsedSeconds = timer.Elapsed.TotalSeconds,
    limitations = new[] { "合成数据，不是真实设备采集", "CSV流式导出验证，不代替Excel/打印验收", "不代替72小时运行或实际断电测试", "本机结果，不代替客户电脑性能", "使用当前正式schema及16点温度/两路压力/两路电流，仍需实际设备和系统联调" }
};
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Progress("complete", days, result);
Console.WriteLine("PASS: complete capacity generation, concurrent queries/writes, backup and actual restored copy");
return 0;
