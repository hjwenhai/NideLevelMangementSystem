using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.LicenseDesk;

internal sealed record InstallationRequest(string InstallationId, string SoftwareVersion);
internal sealed record IssueResult(string ExportPath, string RecordPath, string? ExportError);

// Supplier-only code. Customer binaries do not reference this project or contain a signing key.
internal static class Issuance
{
    internal static readonly IReadOnlyDictionary<InstrumentCommand,string> OperationNames=new Dictionary<InstrumentCommand,string>{[InstrumentCommand.MeasureII]="测量Ⅱ（界面）",[InstrumentCommand.MeasureIII]="测量Ⅲ（罐底）",[InstrumentCommand.Lower]="下降",[InstrumentCommand.Density]="远程密度",[InstrumentCommand.RemoteLevel]="远程液位修改"};
    internal static InstallationRequest ReadRequest(string path)
    {
        using var document = JsonDocument.Parse(ReadBounded(path, 65536));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("productId", out var product)
            || product.ValueKind != JsonValueKind.String || product.GetString() != ProductTrust.ProductId)
            throw new ArgumentException("安装请求不属于本液位管理系统，请选择客户从软件授权页面保存的请求文件。");
        if (!root.TryGetProperty("installationId", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: 64 } installation || !installation.All(Uri.IsHexDigit))
            throw new ArgumentException("安装请求缺少有效的64位安装编号。");
        return new(installation, root.TryGetProperty("softwareVersion", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() ?? "" : "");
    }

    internal static ProductLicense Build(InstallationRequest request, string customer, long sequence,
        bool? permanent, DateTime? beijingExpiry, bool average, ProductBranding branding, DateTimeOffset now, long previousSequence = 0,IReadOnlyList<InstrumentCommand>? operations=null)
    {
        if (request.InstallationId.Length != 64 || !request.InstallationId.All(Uri.IsHexDigit)) throw new ArgumentException("请先选择有效安装请求。");
        if (string.IsNullOrWhiteSpace(customer) || customer.Trim().Length > 100) throw new ArgumentException("请填写客户名称（最多100字）。");
        if (sequence < 1 || sequence <= previousSequence) throw new ArgumentException("本次授权序号必须大于该电脑已知的历史序号。");
        if (permanent is null) throw new ArgumentException("请明确选择按日期到期或永久授权。");
        DateTimeOffset? expiry = null;
        if (permanent == false)
        {
            if (beijingExpiry is null) throw new ArgumentException("请选择到期日期和时间。");
            expiry = new DateTimeOffset(DateTime.SpecifyKind(beijingExpiry.Value, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUniversalTime();
            if (expiry <= now) throw new ArgumentException("授权到期时间必须晚于当前时间。");
        }
        LicenseVerifier.ValidateBranding(branding);
        return new() { LicenseId = Guid.NewGuid(), InstallationId = request.InstallationId, CustomerName = customer.Trim(),
            Sequence = sequence, IssuedAtUtc = now.ToUniversalTime(), ExpiresAtUtc = expiry,
            EnabledModules = (average ? new[]{ProductTrust.AverageModule} : Array.Empty<string>()).Concat((operations??[]).Select(c=>ProductTrust.OptionalOperations.TryGetValue(c,out var module)?module:throw new ArgumentException("不是选配操作"))).Distinct().ToArray(), Branding = branding };
    }

    internal static string ReadBounded(string path, int limit)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择文件的完整路径。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new ArgumentException("所选文件过大或类型不正确。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    internal static ProductLicense ReadLicense(string path, string installation, string publicKey = ProductTrust.PublicKeyPem)
        => LicenseVerifier.Verify(ReadBounded(path, 6 * 1024 * 1024), installation, publicKey).License;

    internal static string ReadImage(string path, bool icon)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1048576) throw new ArgumentException("图像不能超过1MB。");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".ico" when icon => "image/x-icon", _ => throw new ArgumentException("请选择PNG、JPEG图像；程序图标也支持ICO。") };
        var data = "data:" + type + ";base64," + Convert.ToBase64String(bytes);
        LicenseVerifier.ValidateBranding(icon ? new() { IconDataUrl = data } : new() { LogoDataUrl = data });
        return data;
    }

    internal static long HighestSequence(string records, string installation, string publicKey = ProductTrust.PublicKeyPem)
    {
        if (!Directory.Exists(records)) return 0;
        long highest = 0;
        foreach (var file in Directory.EnumerateFiles(records, installation + "_*.license.json"))
            highest = Math.Max(highest, ReadLicense(file, installation, publicKey).Sequence);
        return highest;
    }

    internal static IssueResult Issue(ProductLicense license, string privateKeyPath, string output, string records, string publicKey = ProductTrust.PublicKeyPem)
    {
        if (!Path.IsPathFullyQualified(output) || !Path.IsPathFullyQualified(records)) throw new ArgumentException("请使用完整的输出路径。");
        if (File.Exists(output)) throw new IOException("已有同名文件，不覆盖历史授权，请使用新文件名。");
        if (license.RepairClockGuard) throw new ArgumentException("日常签发工具不开放时钟保护修复。");
        if (license.ExpiresAtUtc is {} expiry && expiry <= DateTimeOffset.UtcNow) throw new ArgumentException("授权期限已过，请重新设置。");
        Directory.CreateDirectory(records);
        using var gate = new FileStream(Path.Combine(records, ".issuance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (license.Sequence <= HighestSequence(records, license.InstallationId, publicKey)) throw new ArgumentException("已有相同或更大的签发序号，请重新载入记录并增加序号。");
        using var key = ECDsa.Create();
        key.ImportFromPem(ReadBounded(privateKeyPath, 16384));
        var payload = JsonSerializer.SerializeToUtf8Bytes(license, LicenseVerifier.Json);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = JsonSerializer.Serialize(new LicenseEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(signature)), LicenseVerifier.Json);
        _ = LicenseVerifier.Verify(envelope, license.InstallationId, publicKey);
        var record = Path.Combine(records, license.InstallationId + "_" + license.Sequence.ToString(CultureInfo.InvariantCulture) + "_" + license.LicenseId.ToString("N") + ".license.json");
        WriteNew(record, envelope);
        // Keep an independently verified supplier record even if a removable customer destination disappears.
        try { Directory.CreateDirectory(Path.GetDirectoryName(output)!); WriteNew(output, envelope); return new(output, record, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(output, record, "客户文件未导出，请从签发记录复制授权文件。原因：" + e.Message); }
    }

    private static void WriteNew(string path, string text)
    {
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); created = true;
            var bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes); stream.Flush(true);
        }
        catch { if (created) File.Delete(path); throw; }
    }

    internal static string Summary(ProductLicense license)
    {
        var term = license.ExpiresAtUtc is {} end ? end.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " 北京时间（UTC+08:00）" : "永久";
        return $"客户：{license.CustomerName}\r\n安装编号：{license.InstallationId}\r\n授权序号：{license.Sequence}\r\n授权期限：{term}" +
            $"\r\n无线浮子平均温度测量：{(license.EnabledModules.Contains(ProductTrust.AverageModule) ? "开通" : "不开通")}\r\n选配操作：{string.Join("、",ProductTrust.OptionalOperations.Where(p=>license.EnabledModules.Contains(p.Value)).Select(p=>OperationNames[p.Key]))}\r\n软件名称：{license.Branding.SoftwareName}\r\n厂家名称：{license.Branding.ManufacturerName}\r\n厂家Logo：{(license.Branding.LogoDataUrl is null ? "未设置" : "已设置")}\r\n程序图标：{(license.Branding.IconDataUrl is null ? "默认" : "已设置")}\r\n\r\n此文件仅适用于上述安装编号。只将签名授权文件交给客户。";
    }
}
