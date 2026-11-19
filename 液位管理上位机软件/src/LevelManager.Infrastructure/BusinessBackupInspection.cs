using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Infrastructure;

public sealed record BusinessDatabaseOverview(int Tanks, int Accounts, long ConfigurationRevision, long HistoryRows, bool SummaryAvailable = true);

/// <summary>A read-only inspection which locks the selected ZIP against writes/replacement until disposed.</summary>
public sealed class BusinessBackupInspection : IDisposable
{
    private readonly FileStream selectedFile;
    private static readonly JsonSerializerOptions ConfigurationJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    private bool disposed;
    public string BackupPath { get; }
    public DatabaseBackupManifest Manifest { get; }
    public BusinessDatabaseOverview Overview { get; }
    public string ArchiveSha256 { get; }
    private BusinessBackupInspection(FileStream file, string path, DatabaseBackupManifest manifest, BusinessDatabaseOverview overview, string sha)
        => (selectedFile, BackupPath, Manifest, Overview, ArchiveSha256) = (file, path, manifest, overview, sha);

    public static async Task<BusinessBackupInspection> OpenAsync(string backupPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(backupPath) || !File.Exists(backupPath)) throw new ArgumentException("请选择现有的业务备份ZIP文件。");
        RejectLinks(backupPath);
        var file = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var workspace = Path.Combine(Path.GetTempPath(), "Naide.LevelManager.Restore", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(workspace, "inspection.db");
        try
        {
            Directory.CreateDirectory(workspace);
            if ((File.GetAttributes(Path.GetDirectoryName(workspace)!) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时检查目录为链接，不能安全检查。");
            progress?.Report("正在读取备份说明…");
            DatabaseBackupManifest manifest;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count != 2 || zip.Entries.Count(e => e.FullName == "业务数据备份.db") != 1 || zip.Entries.Count(e => e.FullName == "备份说明.json") != 1) throw new InvalidDataException("这不是完整的业务备份ZIP，请选择从系统“创建业务备份”下载的原文件。");
                var data = zip.GetEntry("业务数据备份.db")!; var description = zip.GetEntry("备份说明.json")!;
                if (description.Length is < 1 or > 65536 || data.Length is < 100 or > 68719476736) throw new InvalidDataException("备份大小超出支持范围。");
                using (var reader = new StreamReader(description.Open())) manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(await reader.ReadToEndAsync(ct), LicenseVerifier.Json) ?? throw new InvalidDataException("备份说明为空。");
                if (manifest.ProductId != ProductTrust.ProductId || manifest.AuthorizationIncluded || manifest.Bytes != data.Length || manifest.SchemaVersion < 1 || manifest.SchemaVersion > SqliteConfigurationStore.CurrentSchema || manifest.ConfigurationRevision < 0 || manifest.SnapshotAtUtc == default || manifest.Sha256 is null || manifest.Sha256.Length != 64) throw new InvalidDataException("备份产品、版本或说明不符，请使用与本系统兼容的完整备份。");
                if (new DriveInfo(Path.GetPathRoot(workspace)!).AvailableFreeSpace < data.Length + 1024L * 1024 * 1024) throw new IOException("临时磁盘空间不足，无法安全检查备份。请释放空间后再试。");
                progress?.Report("正在校验备份文件；大备份可能需要几分钟…");
                await using var target = new FileStream(dbPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                await using var source = data.Open();
                var buffer = new byte[1024 * 1024]; long count = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0) { count += read; if (count > manifest.Bytes) throw new InvalidDataException("备份长度异常。"); await target.WriteAsync(buffer.AsMemory(0, read), ct); }
                if (count != manifest.Bytes) throw new InvalidDataException("备份不完整。");
            }
            await using (var data = File.OpenRead(dbPath)) if (Convert.ToHexString(await SHA256.HashDataAsync(data, ct)) != manifest.Sha256) throw new InvalidDataException("备份校验失败，文件可能已损坏；不会恢复或修改当前数据。");
            progress?.Report("正在检查数据库与读取备份摘要…");
            var overview = await ReadOverviewAsync(dbPath, manifest.SchemaVersion, verifyIntegrity: true, ct);
            if (overview.ConfigurationRevision != manifest.ConfigurationRevision) throw new InvalidDataException("备份说明与实际配置版本不一致。");
            file.Position = 0;
            var archiveSha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            return new(file, Path.GetFullPath(backupPath), manifest, overview, archiveSha);
        }
        catch { await file.DisposeAsync(); throw; }
        finally
        {
            // Only delete exact files created in our unpredictable, owned temporary workspace; never recurse.
            if (Directory.Exists(workspace) && (File.GetAttributes(workspace) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                {
                    var p = dbPath + suffix;
                    if (File.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0) { try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                }
                try { Directory.Delete(workspace, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task<DatabaseRestoreResult> RestoreAsync(string dataRoot, bool confirmed, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!confirmed) throw new ArgumentException("尚未明确确认恢复，当前数据未更改。");
        await InspectTargetAsync(dataRoot, ct);
        // The read handle stays open throughout: the user-confirmed file cannot be swapped after inspection.
        return await OfflineDatabaseRestore.RestoreAsync(BackupPath, Path.GetFullPath(dataRoot), true, ct);
    }

    public static async Task<BusinessDatabaseOverview> InspectTargetAsync(string dataRoot, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(dataRoot) || !Directory.Exists(dataRoot) || !File.Exists(Path.Combine(dataRoot, "level-manager.db"))) throw new ArgumentException("请选择原来的业务数据文件夹（其中应有level-manager.db），不能选择空文件夹或程序目录。");
        var p = Path.GetFullPath(dataRoot);
        if (p.TrimEnd(Path.DirectorySeparatorChar).EndsWith(".authorization", StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("不能选择授权目录或链接目录作为恢复目标。");
        RejectLinks(Path.Combine(p, "level-manager.db"));
        try { return await ReadOverviewAsync(Path.Combine(p, "level-manager.db"), expectedSchema: null, verifyIntegrity: false, ct); }
        catch (Exception e) when (e is SqliteException or JsonException or InvalidDataException or ConfigurationException)
        {
            // A damaged original database is a reason to restore, not a reason to bypass the valid ZIP checks.
            // The explicit target is still path-validated and the offline engine preserves all original files.
            return new(-1, -1, -1, -1, SummaryAvailable: false);
        }
    }

    public static string? TargetBlockReason(string dataRoot)
    {
        if (!Directory.Exists(dataRoot) || !File.Exists(Path.Combine(dataRoot, "level-manager.db"))) return "尚未找到有效的原业务数据目录。";
        try
        {
            var p = Path.Combine(dataRoot, "service.lock");
            if (File.Exists(p)) { using var guard = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            return null;
        }
        catch (UnauthorizedAccessException) { return "没有目录访问权限，请关闭工具后右键“以管理员身份运行”。"; }
        catch (IOException) { return "后台仍在运行或文件被占用。请先停止后台，再点“重新检查”；仅关闭客户端不够。"; }
    }

    private static async Task<BusinessDatabaseOverview> ReadOverviewAsync(string dbPath, int? expectedSchema, bool verifyIntegrity, CancellationToken ct)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        await c.OpenAsync(ct);
        async Task<object?> Scalar(string sql) { await using var command = c.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(ct); }
        await Scalar("PRAGMA trusted_schema=OFF");
        var schema = Convert.ToInt32(await Scalar("PRAGMA user_version"));
        if (schema > SqliteConfigurationStore.CurrentSchema) throw new NotSupportedException("原业务数据版本高于本工具，请使用对应新版恢复工具，不能在此降级覆盖。");
        if (schema < 1 || expectedSchema is not null && schema != expectedSchema) throw new InvalidDataException("数据版本不兼容，请使用对应版本的恢复工具。");
        if (verifyIntegrity)
        {
            if (Convert.ToString(await Scalar("PRAGMA integrity_check")) != "ok") throw new InvalidDataException("数据库完整性检查失败。");
            await using var command = c.CreateCommand(); command.CommandText = "PRAGMA foreign_key_check"; await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) throw new InvalidDataException("数据库关联检查失败。");
        }
        var payload = Convert.ToString(await Scalar("SELECT Payload FROM configuration WHERE Id=1")) ?? throw new InvalidDataException("缺少储罐配置。");
        var config = JsonSerializer.Deserialize<SystemConfiguration>(payload, ConfigurationJson) ?? throw new InvalidDataException("储罐配置无法识别。");
        ConfigurationRules.RequireValid(config);
        var revision = Convert.ToInt64(await Scalar("SELECT Revision FROM configuration WHERE Id=1"));
        if (config.Revision != revision) throw new InvalidDataException("实际配置与版本记录不一致。");
        var accounts = Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM users"));
        if (accounts == 0 && verifyIntegrity) throw new InvalidDataException("业务备份没有登录账号，不能作为完整恢复点。");
        var hasHistory = Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='history_samples'")) != 0;
        if (schema >= 2 && Convert.ToString(await Scalar("SELECT Value FROM database_properties WHERE Key='profile'")) != "operational") throw new InvalidDataException("不能使用容量测试样本代替正式业务备份。");
        return new(config.Tanks.Count, accounts, revision, hasHistory ? Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM history_samples")) : 0);
    }

    public void Dispose() { if (disposed) return; disposed = true; selectedFile.Dispose(); }

    private static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("备份或数据路径包含链接，请选择实际文件及实际数据目录。");
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
    }
}
