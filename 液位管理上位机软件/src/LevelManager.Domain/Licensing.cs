using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Naide.LevelManager.Domain;
public static class ProductTrust
{
    public const string ProductId="Naide.LevelManager.2026";
    // Only the public verification key is compiled into the customer application.
    public const string PublicKeyPem="""
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERhRYixi0kqsj3kaOCGWRqRTXvmkc
        DOMFlcQKciH1g96BIwqSBDSx8ZcxS4ZqndJU0wN9XjLyp3KdVmN+/Vumdg==
        -----END PUBLIC KEY-----
        """;
    public const string AverageModule="average-temperature";
    public static readonly IReadOnlyDictionary<InstrumentCommand,string> OptionalOperations = new Dictionary<InstrumentCommand,string>{
        [InstrumentCommand.MeasureII]="operation-interface",[InstrumentCommand.MeasureIII]="operation-bottom",
        [InstrumentCommand.Lower]="operation-lower",[InstrumentCommand.Density]="operation-density",[InstrumentCommand.RemoteLevel]="operation-level-adjust"};
    public static bool CommandAvailable(InstrumentCommand command,IReadOnlyList<string> modules) => !OptionalOperations.TryGetValue(command,out var module)||modules.Contains(module);
    public static bool KnownModule(string module) => module==AverageModule||OptionalOperations.Values.Contains(module);
}
public sealed record ProductBranding
{
    public string SoftwareName {get;init;}="液位管理系统";
    public string ManufacturerName {get;init;}="";
    public string ManufacturerInformation {get;init;}="";
    public string? LogoDataUrl {get;init;}
    public string? IconDataUrl {get;init;}
}
public sealed record ProductLicense
{
    public int SchemaVersion {get;init;}=1;
    public string ProductId {get;init;}=ProductTrust.ProductId;
    public required Guid LicenseId {get;init;}
    public required string InstallationId {get;init;}
    public required string CustomerName {get;init;}
    public required long Sequence {get;init;}
    public required DateTimeOffset IssuedAtUtc {get;init;}
    public DateTimeOffset? ExpiresAtUtc {get;init;}
    public bool RepairClockGuard {get;init;}
    public IReadOnlyList<string> EnabledModules {get;init;}=[];
    public ProductBranding Branding {get;init;}=new();
}
public sealed record LicenseEnvelope(string PayloadBase64,string SignatureBase64);
public sealed record VerifiedLicense(ProductLicense License,string Sha256);
public sealed class LicenseValidationException(string message):Exception(message);
public static class LicenseVerifier
{
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static VerifiedLicense Verify(string envelopeJson,string installationId,string publicKeyPem)
    {
        try
        {
            if(envelopeJson.Length>6*1024*1024)throw new LicenseValidationException("授权文件过大");
            var envelope=JsonSerializer.Deserialize<LicenseEnvelope>(envelopeJson,Json)??throw new LicenseValidationException("授权文件为空");
            var payload=Convert.FromBase64String(envelope.PayloadBase64);var signature=Convert.FromBase64String(envelope.SignatureBase64);
            if(payload.Length>4*1024*1024||signature.Length!=64)throw new LicenseValidationException("授权数据或签名长度无效");
            using var key=ECDsa.Create();key.ImportFromPem(publicKeyPem);if(key.KeySize!=256||!key.VerifyData(payload,signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))throw new LicenseValidationException("授权签名校验失败");
            var license=JsonSerializer.Deserialize<ProductLicense>(payload,Json)??throw new LicenseValidationException("授权内容为空");
            if(license.SchemaVersion!=1||license.ProductId!=ProductTrust.ProductId)throw new LicenseValidationException("授权不属于本产品或版本不支持");
            if(license.InstallationId!=installationId||installationId.Length!=64||!installationId.All(Uri.IsHexDigit))throw new LicenseValidationException("授权与本机安装编号不符");
            if(license.LicenseId==Guid.Empty||license.Sequence<1||string.IsNullOrWhiteSpace(license.CustomerName)||license.CustomerName.Length>100||license.IssuedAtUtc==default||license.IssuedAtUtc.Offset!=TimeSpan.Zero||license.ExpiresAtUtc.HasValue&&(license.ExpiresAtUtc<=license.IssuedAtUtc||license.ExpiresAtUtc.Value.Offset!=TimeSpan.Zero))throw new LicenseValidationException("授权编号、日期或客户资料无效");
            if(license.EnabledModules is null||license.EnabledModules.Count>6||license.EnabledModules.Distinct().Count()!=license.EnabledModules.Count||license.EnabledModules.Any(m=>!ProductTrust.KnownModule(m)))throw new LicenseValidationException("授权模块配置无效");
            ValidateBranding(license.Branding);
            return new(license,Convert.ToHexString(SHA256.HashData(payload)));
        }
        catch(Exception e)when(e is FormatException or JsonException or CryptographicException or ArgumentException or NullReferenceException or OverflowException)
        {throw new LicenseValidationException("授权格式或签名无效");}
    }
    public static void ValidateBranding(ProductBranding b)
    {
        if(b is null||string.IsNullOrWhiteSpace(b.SoftwareName)||b.SoftwareName.Length>100||b.ManufacturerName is null||b.ManufacturerName.Length>100||b.ManufacturerInformation is null||b.ManufacturerInformation.Length>2000)throw new LicenseValidationException("软件名称或厂家资料无效");
        Image(b.LogoDataUrl,false);Image(b.IconDataUrl,true);
    }
    private static void Image(string? url,bool icon)
    {
        if(url is null)return;var comma=url.IndexOf(',');if(comma<0)throw new LicenseValidationException("品牌图像格式无效");var type=url[..comma];
        if(type!="data:image/png;base64"&&type!="data:image/jpeg;base64"&&(!icon||type!="data:image/x-icon;base64"))throw new LicenseValidationException("品牌图像仅支持PNG/JPEG，程序图标另支持ICO");
        var data=Convert.FromBase64String(url[(comma+1)..]);if(data.Length is <24 or >1048576)throw new LicenseValidationException("品牌图像应不超过1MB");int width=0,height=0;
        if(type=="data:image/png;base64")
        {
            if(!data.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||!data.AsSpan(12,4).SequenceEqual("IHDR"u8))throw new LicenseValidationException("PNG头部无效");
            width=checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)));height=checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20)));
        }
        else if(type=="data:image/x-icon;base64")
        {
            if(BinaryPrimitives.ReadUInt16LittleEndian(data)!=0||BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2))!=1||BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) is <1 or >16)throw new LicenseValidationException("ICO头部无效");
            var count=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));if(data.Length<6+16*count)throw new LicenseValidationException("ICO目录不完整");
            for(var i=0;i<count;i++){var at=6+16*i;var offset=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at+12));var length=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at+8));if(offset<6+16*count||length==0||offset+(ulong)length>(ulong)data.Length)throw new LicenseValidationException("ICO图像数据不完整");}
            width=data[6]==0?256:data[6];height=data[7]==0?256:data[7];
        }
        else
        {
            if(data[0]!=255||data[1]!=216)throw new LicenseValidationException("JPEG头部无效");
            for(var i=2;i+8<data.Length;)
            {
                if(data[i++]!=255)break;while(i<data.Length&&data[i]==255)i++;if(i>=data.Length)break;var marker=data[i++];if(marker is 217 or 218)break;if(marker is 1 or >=208 and <=215)continue;
                if(i+2>data.Length)break;var length=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i));if(length<2||i+length>data.Length)break;
                if(marker is 192 or 193 or 194 or 195 or 197 or 198 or 199 or 201 or 202 or 203 or 205 or 206 or 207){if(length<8)break;height=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i+3));width=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i+5));break;}i+=length;
            }
        }
        if(width<1||height<1||width>(icon?1024:4096)||height>(icon?1024:4096))throw new LicenseValidationException("品牌图像尺寸超出允许范围");
    }
}
public enum ProductAuthorizationState { Unlicensed,Active,Expired,ClockError,Invalid }
public sealed record AuthorizationWatermark(DateTimeOffset ObservedAtUtc,long HighestSequence=0,string? LicenseHash=null,bool ClockProblem=false,Guid? LastMotionMarker=null,bool BusinessRecoveryRequired=false);
public sealed record ClockObservation(DateTimeOffset EffectiveUtc,AuthorizationWatermark Watermark,bool NeedsSave);
public sealed class ProtectedClock
{
    private readonly TimeProvider clock;private readonly DateTimeOffset anchor;private readonly long started;private AuthorizationWatermark watermark;
    public ProtectedClock(TimeProvider provider,AuthorizationWatermark state){clock=provider;watermark=state;anchor=provider.GetUtcNow();started=provider.GetTimestamp();}
    public ClockObservation Observe()
    {
        var actual=clock.GetUtcNow();var expected=anchor+clock.GetElapsedTime(started);var bad=watermark.ClockProblem||actual<watermark.ObservedAtUtc-TimeSpan.FromMinutes(5)||(actual-expected).Duration()>TimeSpan.FromMinutes(5);
        var effective=new[]{actual,expected,watermark.ObservedAtUtc}.Max();var next=watermark with{ObservedAtUtc=effective,ClockProblem=bad};var changed=next!=watermark;watermark=next;return new(effective,next,changed);
    }
    public void UpdateMetadata(AuthorizationWatermark state)
    {watermark=state with{ObservedAtUtc=state.ObservedAtUtc>watermark.ObservedAtUtc?state.ObservedAtUtc:watermark.ObservedAtUtc,ClockProblem=state.ClockProblem||watermark.ClockProblem};}
}
public sealed record ProductAuthorizationStatus(ProductAuthorizationState State,DateTimeOffset EffectiveUtc,DateTimeOffset? ExpiresAtUtc,int ReminderDays,string Message)
{
    public bool AllowsLicensedActions=>State==ProductAuthorizationState.Active;
    public static ProductAuthorizationStatus Evaluate(VerifiedLicense? license,ClockObservation observation,string? invalidReason=null)
    {
        var now=observation.EffectiveUtc;var expiry=license?.License.ExpiresAtUtc;
        if(invalidReason is not null)return new(ProductAuthorizationState.Invalid,now,expiry,0,invalidReason);
        if(observation.Watermark.ClockProblem)return new(ProductAuthorizationState.ClockError,now,expiry,0,"系统时间或授权状态异常，请联系供方核查；监控、报警和必要处置继续");
        if(license is null)return new(ProductAuthorizationState.Unlicensed,now,null,0,"尚未导入本机授权；监控、报警和必要处置继续");
        if(expiry.HasValue&&now>=expiry.Value)return new(ProductAuthorizationState.Expired,now,expiry,0,"授权已到期；监控、归档、报警及必要停止和核查继续");
        var remaining=expiry-now;var reminder=remaining.HasValue&&remaining<=TimeSpan.FromDays(1)?1:remaining.HasValue&&remaining<=TimeSpan.FromDays(3)?3:remaining.HasValue&&remaining<=TimeSpan.FromDays(7)?7:0;
        return new(ProductAuthorizationState.Active,now,expiry,reminder,expiry.HasValue?reminder>0?"授权将在"+reminder+"天内到期":"授权有效":"永久授权");
    }
}
