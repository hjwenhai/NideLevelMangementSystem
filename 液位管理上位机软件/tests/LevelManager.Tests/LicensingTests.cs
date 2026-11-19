using System.Security.Cryptography;
using System.Text.Json;
using Naide.LevelManager.Domain;
using Xunit;
namespace Naide.LevelManager.Tests;
public class LicensingTests
{
    private static ProductLicense License(DateTimeOffset? expiry=null)=>new(){LicenseId=Guid.NewGuid(),InstallationId=new('A',64),CustomerName="隔离测试客户",Sequence=1,IssuedAtUtc=DateTimeOffset.Parse("2026-01-01T00:00:00Z"),ExpiresAtUtc=expiry,EnabledModules=[ProductTrust.AverageModule]};
    private static string Sign(ProductLicense l,ECDsa key){var b=JsonSerializer.SerializeToUtf8Bytes(l,LicenseVerifier.Json);return JsonSerializer.Serialize(new LicenseEnvelope(Convert.ToBase64String(b),Convert.ToBase64String(key.SignData(b,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))),LicenseVerifier.Json);}
    [Fact]public void SignatureIsBoundToProductInstallationAndExactPayloadBytes()
    {
        using var k=ECDsa.Create(ECCurve.NamedCurves.nistP256);var l=License();var envelope=Sign(l,k);var v=LicenseVerifier.Verify(envelope,l.InstallationId,k.ExportSubjectPublicKeyInfoPem());Assert.Equal(l.LicenseId,v.License.LicenseId);Assert.Equal(64,v.Sha256.Length);
        Assert.Throws<LicenseValidationException>(()=>LicenseVerifier.Verify(envelope,new('B',64),k.ExportSubjectPublicKeyInfoPem()));using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);Assert.Throws<LicenseValidationException>(()=>LicenseVerifier.Verify(envelope,l.InstallationId,other.ExportSubjectPublicKeyInfoPem()));
        var parsed=JsonSerializer.Deserialize<LicenseEnvelope>(envelope,LicenseVerifier.Json)!;var tampered=JsonSerializer.Serialize(parsed with{PayloadBase64=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(l with{CustomerName="被改写"},LicenseVerifier.Json))},LicenseVerifier.Json);Assert.Throws<LicenseValidationException>(()=>LicenseVerifier.Verify(tampered,l.InstallationId,k.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<LicenseValidationException>(()=>LicenseVerifier.Verify(Sign(l with{ProductId="other"},k),l.InstallationId,k.ExportSubjectPublicKeyInfoPem()));
    }
    [Theory][InlineData(8,0)][InlineData(7,7)][InlineData(3,3)][InlineData(1,1)][InlineData(0,0)]
    public void RemindersAndExactExpiryUseAuthoritativeEffectiveTime(int days,int reminder)
    {
        var end=DateTimeOffset.Parse("2026-11-01T00:00:00Z");var now=end.AddDays(-days);var status=ProductAuthorizationStatus.Evaluate(new(License(end),"hash"),new(now,new(now),false));Assert.Equal(reminder,status.ReminderDays);Assert.Equal(days>0?ProductAuthorizationState.Active:ProductAuthorizationState.Expired,status.State);
    }
    [Fact]public void PermanentIsExplicitAndUnlicensedOrDamagedStateNeverGainsLicensedActions()
    {
        var now=DateTimeOffset.UtcNow;var observation=new ClockObservation(now,new(now),false);Assert.False(ProductAuthorizationStatus.Evaluate(null,observation).AllowsLicensedActions);Assert.True(ProductAuthorizationStatus.Evaluate(new(License(),"hash"),observation).AllowsLicensedActions);Assert.False(ProductAuthorizationStatus.Evaluate(new(License(),"hash"),observation,"状态损坏").AllowsLicensedActions);Assert.Equal(ProductAuthorizationState.ClockError,ProductAuthorizationStatus.Evaluate(new(License(),"hash"),observation with{Watermark=new(now,ClockProblem:true)}).State);
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.Parse("2026-10-28T00:00:00Z");public long Ticks;
        public override DateTimeOffset GetUtcNow()=>Now;public override long GetTimestamp()=>Ticks;public override long TimestampFrequency=>1000;
    }
    [Fact]public void SmallRollbackCannotReverseEffectiveTimeAndLargeRollbackStaysRestricted()
    {
        var time=new Clock();var protectedClock=new ProtectedClock(time,new(time.Now));time.Now=time.Now.AddMinutes(-2);time.Ticks=120000;var observed=protectedClock.Observe();Assert.False(observed.Watermark.ClockProblem);Assert.Equal(DateTimeOffset.Parse("2026-10-28T00:02:00Z"),observed.EffectiveUtc);
        time.Now=time.Now.AddMinutes(-10);time.Ticks+=1000;observed=protectedClock.Observe();Assert.True(observed.Watermark.ClockProblem);time.Now=observed.EffectiveUtc;Assert.True(protectedClock.Observe().Watermark.ClockProblem);
    }
    [Fact]public void RestartWithOlderWallClockCannotRenewObservedExpiry()
    {
        var expiry=DateTimeOffset.Parse("2026-10-28T00:00:00Z");var time=new Clock{Now=expiry.AddMinutes(-1)};var protectedClock=new ProtectedClock(time,new(expiry));var status=ProductAuthorizationStatus.Evaluate(new(License(expiry),"hash"),protectedClock.Observe());Assert.Equal(ProductAuthorizationState.Expired,status.State);
    }
    [Fact]public void BrandingRejectsExecutableDataAndUnboundedDimensions()
    {
        Assert.Throws<LicenseValidationException>(()=>LicenseVerifier.ValidateBranding(new(){LogoDataUrl="data:image/svg+xml;base64,PHN2Zz4="}));Assert.Throws<LicenseValidationException>(()=>LicenseVerifier.ValidateBranding(new(){SoftwareName=""}));Assert.Throws<LicenseValidationException>(()=>LicenseVerifier.ValidateBranding(new(){IconDataUrl="data:image/png;base64,"+Convert.ToBase64String(new byte[24])}));
    }
}
