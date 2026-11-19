using System.Security.Cryptography;
using System.Text.Json;
using Naide.LevelManager.Domain;
using Naide.LevelManager.LicenseDesk;
using Xunit;

namespace Naide.LevelManager.Tests;

public sealed class LicenseDeskTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Level-LicenseDesk-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static InstallationRequest Request => new(new('A', 64), "r12");
    private string Records => Path.Combine(root, "records");
    private string Public => key.ExportSubjectPublicKeyInfoPem();
    private string KeyPath { get { Directory.CreateDirectory(root); var path = Path.Combine(root, "test-only.pem"); File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem()); return path; } }
    private static ProductLicense Candidate(long sequence = 1, bool average = true) => Issuance.Build(Request, "测试客户", sequence, true, null, average, new() { SoftwareName = "测试软件", ManufacturerName = "测试厂家" }, DateTimeOffset.UtcNow);
    public void Dispose() { key.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact] public void RequestValidatesProductAndMachineIdBeforeAnySigning()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "request.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { productId = ProductTrust.ProductId, installationId = Request.InstallationId, softwareVersion = "r12" }));
        Assert.Equal(Request, Issuance.ReadRequest(path));
        File.WriteAllText(path, "{\"productId\":\"other\",\"installationId\":\"A\"}"); Assert.Throws<ArgumentException>(() => Issuance.ReadRequest(path));
        File.WriteAllText(path, JsonSerializer.Serialize(new { productId = ProductTrust.ProductId, installationId = new string('Q', 64) })); Assert.Throws<ArgumentException>(() => Issuance.ReadRequest(path));
        File.WriteAllText(path, "[]"); Assert.Throws<ArgumentException>(() => Issuance.ReadRequest(path));
        File.WriteAllText(path, new string('x', 65537)); Assert.Throws<ArgumentException>(() => Issuance.ReadRequest(path));
    }
    [Fact] public void ExpiryIsExplicitAndBeijingTimeIsIndependentOfHostTimezone()
    {
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z"); var expiry = new DateTime(2026, 11, 30, 23, 59, 59);
        var license = Issuance.Build(Request, "客户", 1, false, expiry, true, new(), now);
        Assert.Equal(DateTimeOffset.Parse("2026-11-30T15:59:59Z"), license.ExpiresAtUtc);
        Assert.Contains("2026-11-30 23:59:59", Issuance.Summary(license));
        Assert.Throws<ArgumentException>(() => Issuance.Build(Request, "客户", 1, null, expiry, true, new(), now));
        Assert.Throws<ArgumentException>(() => Issuance.Build(Request, "客户", 1, false, null, true, new(), now));
        Assert.Throws<ArgumentException>(() => Issuance.Build(Request, "客户", 1, false, new(2026, 1, 1), true, new(), now));
        Assert.Null(Issuance.Build(Request, "客户", 1, true, null, false, new(), now).ExpiresAtUtc);
    }
    [Fact] public void SequenceCustomerAndBrandingAreValidated()
    {
        Assert.Throws<ArgumentException>(() => Issuance.Build(Request, "", 1, true, null, true, new(), DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => Issuance.Build(Request, "客户", 4, true, null, true, new(), DateTimeOffset.UtcNow, 4));
        Assert.Throws<LicenseValidationException>(() => Issuance.Build(Request, "客户", 1, true, null, true, new() { SoftwareName = "" }, DateTimeOffset.UtcNow));
    }
    [Fact] public void SignedFileImportsWithUnchangedVerifierAndContainsNoPrivateKey()
    {
        var license = Candidate(); var output = Path.Combine(root, "customer.json"); var result = Issuance.Issue(license, KeyPath, output, Records, Public);
        var verified = LicenseVerifier.Verify(File.ReadAllText(output), Request.InstallationId, Public).License;
        Assert.Equal(license.LicenseId, verified.LicenseId); Assert.Equal("测试厂家", verified.Branding.ManufacturerName); Assert.Contains(ProductTrust.AverageModule, verified.EnabledModules);
        Assert.Equal(File.ReadAllText(output), File.ReadAllText(result.RecordPath)); Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(output));
        Assert.Equal(1, Issuance.HighestSequence(Records, Request.InstallationId, Public)); Assert.Null(result.ExportError);
        Assert.Throws<LicenseValidationException>(() => Issuance.ReadLicense(output, new('B', 64), Public));
    }
    [Fact] public void WrongKeyNeverProducesARecordOrCustomerFile()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256); var path = KeyPath; var output = Path.Combine(root, "customer.json");
        Assert.Throws<LicenseValidationException>(() => Issuance.Issue(Candidate(), path, output, Records, other.ExportSubjectPublicKeyInfoPem()));
        Assert.False(File.Exists(output)); Assert.Empty(Directory.GetFiles(Records, "*.license.json"));
    }
    [Fact] public void DuplicateAndLowerSequencesAreRejectedAndRecordsNeverOverwritten()
    {
        var path = KeyPath; var first = Path.Combine(root, "customer.json"); Issuance.Issue(Candidate(5), path, first, Records, Public);
        var bytes = File.ReadAllBytes(first);
        Assert.Throws<IOException>(() => Issuance.Issue(Candidate(6), path, first, Records, Public));
        Assert.Throws<ArgumentException>(() => Issuance.Issue(Candidate(5), path, Path.Combine(root, "next.json"), Records, Public));
        Assert.Equal(bytes, File.ReadAllBytes(first)); Assert.Single(Directory.GetFiles(Records, "*.license.json"));
        Issuance.Issue(Candidate(6), path, Path.Combine(root, "next.json"), Records, Public); Assert.Equal(6, Issuance.HighestSequence(Records, Request.InstallationId, Public));
    }
    [Fact] public void FailedCustomerExportRetainsRecoverableSignedSupplierRecord()
    {
        var path = KeyPath; var blocker = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocker, "keep");
        var result = Issuance.Issue(Candidate(), path, Path.Combine(blocker, "license.json"), Records, Public);
        Assert.NotNull(result.ExportError); Assert.True(File.Exists(result.RecordPath));
        Assert.Equal("测试客户", Issuance.ReadLicense(result.RecordPath, Request.InstallationId, Public).CustomerName);
        Assert.Equal("keep", File.ReadAllText(blocker));
    }
    [Fact] public void ExpiredOrRepairLicensesCannotBeIssuedByDailyDesktopTool()
    {
        var path = KeyPath; var output = Path.Combine(root, "customer.json");
        Assert.Throws<ArgumentException>(() => Issuance.Issue(Candidate() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, path, output, Records, Public));
        Assert.Throws<ArgumentException>(() => Issuance.Issue(Candidate() with { RepairClockGuard = true }, path, output, Records, Public));
        Assert.False(File.Exists(output));
    }
    [Fact] public void ImageValidationAndRenewalPreserveBrandingBytesAndModuleChoice()
    {
        Directory.CreateDirectory(root); var image = Path.Combine(root, "logo.png");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6x8AAAAASUVORK5CYII="));
        var logo = Issuance.ReadImage(image, false); var path = KeyPath;
        var old = Candidate() with { Branding = new() { SoftwareName = "品牌软件", LogoDataUrl = logo } };
        var output = Path.Combine(root, "old.json"); Issuance.Issue(old, path, output, Records, Public);
        var loaded = Issuance.ReadLicense(output, Request.InstallationId, Public);
        var renewal = Issuance.Build(Request, loaded.CustomerName, 2, true, null, loaded.EnabledModules.Contains(ProductTrust.AverageModule), loaded.Branding, DateTimeOffset.UtcNow, loaded.Sequence);
        Assert.Equal(logo, renewal.Branding.LogoDataUrl); Assert.Equal(loaded.EnabledModules, renewal.EnabledModules);
        File.WriteAllBytes(image, new byte[24]); Assert.Throws<LicenseValidationException>(() => Issuance.ReadImage(image, false));
    }
}
