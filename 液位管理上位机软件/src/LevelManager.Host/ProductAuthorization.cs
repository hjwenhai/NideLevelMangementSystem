using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Win32;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class AuthorizationRestrictedException(string message):Exception(message);
public sealed class ProductAuthorization:BackgroundService
{
    private sealed record Installation(string RandomId,string MachineHash);
    private readonly object sync=new();private readonly string root;private readonly TimeProvider time;private readonly ILogger<ProductAuthorization> logger;private readonly IDataProtector protector;
    private ProtectedClock clock;private AuthorizationWatermark watermark;private VerifiedLicense? verified;private string? invalidReason,saveError;private DateTimeOffset lastSaved;private bool expirySaved,lastSavedClockProblem;
    public string InstallationId {get;private set;}="";
    public DateTimeOffset ProcessStartedAtUtc {get;}
    public ProductAuthorization(string authorizationRoot,TimeProvider provider,ILogger<ProductAuthorization> log)
    {
        root=Path.GetFullPath(authorizationRoot);time=provider;logger=log;ProcessStartedAtUtc=time.GetUtcNow();Directory.CreateDirectory(root);
        var protection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"keys")),builder=>{builder.SetApplicationName(ProductTrust.ProductId+".authorization");if(OperatingSystem.IsWindows())builder.ProtectKeysWithDpapi(true);});protector=protection.CreateProtector("installation-clock","v1");
        watermark=new(time.GetUtcNow());clock=new(time,watermark);
        try
        {
            var installationPath=Path.Combine(root,"installation.dat");var clockPath=Path.Combine(root,"clock.dat");var machine=MachineHash();Installation installation;
            if(File.Exists(installationPath))installation=ReadProtected<Installation>("installation.dat");
            else
            {
                if(File.Exists(clockPath)||File.Exists(Path.Combine(root,"license.json")))throw new InvalidDataException("安装状态不完整，不自动重置授权");installation=new(Guid.NewGuid().ToString("N"),machine);SaveProtected("installation.dat",installation);SaveProtected("clock.dat",watermark);
            }
            if(installation.MachineHash!=machine)throw new InvalidDataException("安装状态与当前电脑不符");InstallationId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProductTrust.ProductId+"|"+installation.RandomId+"|"+machine)));
            watermark=ReadProtected<AuthorizationWatermark>("clock.dat");lastSaved=watermark.ObservedAtUtc;lastSavedClockProblem=watermark.ClockProblem;clock=new(time,watermark);
            var licensePath=Path.Combine(root,"license.json");if(File.Exists(licensePath))
            {
                if(new FileInfo(licensePath).Length>6*1024*1024)throw new InvalidDataException("授权文件过大");verified=LicenseVerifier.Verify(File.ReadAllText(licensePath),InstallationId,ProductTrust.PublicKeyPem);
                if(verified.License.Sequence!=watermark.HighestSequence||verified.Sha256!=watermark.LicenseHash)throw new InvalidDataException("授权文件与保护状态不一致，请重新导入供方授权");
            }
            else if(watermark.HighestSequence!=0)throw new InvalidDataException("授权文件缺失，不自动清除原授权状态");
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException or LicenseValidationException)
        {invalidReason="授权状态异常："+e.Message;verified=null;
            try{var file=Path.Combine(root,"license.json");if(InstallationId.Length==64&&File.Exists(file)&&new FileInfo(file).Length<=6*1024*1024){var known=LicenseVerifier.Verify(File.ReadAllText(file),InstallationId,ProductTrust.PublicKeyPem);watermark=watermark with{HighestSequence=Math.Max(watermark.HighestSequence,known.License.Sequence),ClockProblem=true};clock=new(time,watermark);}}catch(Exception recovery)when(recovery is IOException or LicenseValidationException){}
            logger.LogError(e,"授权状态载入失败，保留监控与必要处置");}
    }
    private static string MachineHash()
    {
        string? id=null;if(OperatingSystem.IsWindows()){using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");id=key?.GetValue("MachineGuid") as string;}
        if(string.IsNullOrWhiteSpace(id))id=Environment.MachineName+"|"+Environment.OSVersion.Platform;return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }
    private T ReadProtected<T>(string name)
    {
        var path=Path.Combine(root,name);if(!File.Exists(path)||new FileInfo(path).Length>65536)throw new InvalidDataException("授权保护状态缺失或大小异常");return JsonSerializer.Deserialize<T>(protector.Unprotect(File.ReadAllBytes(path)),LicenseVerifier.Json)??throw new InvalidDataException("授权保护状态为空");
    }
    private void Atomic(string name,byte[] bytes)
    {
        var path=Path.Combine(root,name);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private void SaveProtected<T>(string name,T value)=>Atomic(name,protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value,LicenseVerifier.Json)));
    public ProductAuthorizationStatus Status()
    {
        lock(sync)
        {
            var observed=clock.Observe();watermark=observed.Watermark;var expired=verified?.License.ExpiresAtUtc is {} expiry&&observed.EffectiveUtc>=expiry;
            if(invalidReason is null&&(observed.EffectiveUtc-lastSaved>=TimeSpan.FromSeconds(1)||watermark.ClockProblem!=lastSavedClockProblem||expired&&!expirySaved||saveError is not null))
            {
                try{SaveProtected("clock.dat",watermark);lastSaved=observed.EffectiveUtc;expirySaved=expired;lastSavedClockProblem=watermark.ClockProblem;saveError=null;}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or CryptographicException){saveError="授权保护状态未能保存，请核对磁盘或权限";logger.LogError(e,"授权时钟状态保存失败，受限能力不开放");}
            }
            return ProductAuthorizationStatus.Evaluate(verified,observed,invalidReason??saveError);
        }
    }
    public AuthorizationWatermark BusinessEvidence{get{lock(sync){var state=clock.Observe().Watermark;watermark=state;return state;}}}
    public void SaveBusinessEvidence(Guid? marker,bool reviewRequired)
    {
        lock(sync){var next=clock.Observe().Watermark with{LastMotionMarker=marker,BusinessRecoveryRequired=reviewRequired};if(invalidReason is not null)next=next with{ClockProblem=true};SaveProtected("clock.dat",next);watermark=next;clock.UpdateMetadata(next);lastSaved=next.ObservedAtUtc;}
    }
    public ProductBranding Branding {get{lock(sync)return verified?.License.Branding??new();}}
    public IReadOnlyList<string> EnabledModules {get{lock(sync)return verified?.License.EnabledModules??[];}}
    public bool HasAverageModule=>EnabledModules.Contains(ProductTrust.AverageModule);
    public void RequireLicensed(string action)
    {var status=Status();if(!status.AllowsLicensedActions)throw new AuthorizationRestrictedException(action+"暂不可用："+status.Message);}
    public void RequireAverageModule(){if(!HasAverageModule)throw new InvalidOperationException("接口尚未开放");}
    public object InstallationRequest()=>new{productId=ProductTrust.ProductId,installationId=InstallationId,requestedAtUtc=time.GetUtcNow(),softwareVersion="0.3.0"};
    public async Task ImportAsync(string envelope,AuditActor actor,IEventWriter audit,CancellationToken ct)
    {
        if(actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("仅管理员可导入供方授权");if(string.IsNullOrEmpty(InstallationId))throw new InvalidOperationException("安装状态已损坏，请联系供方维护");var candidate=LicenseVerifier.Verify(envelope,InstallationId,ProductTrust.PublicKeyPem);
        await audit.WriteEventAsync(new("操作日志","authorization.import.intent",actor.AccountId,actor.DisplayName,"导入供方授权","已校验签名，准备保存",Parameters:new{candidate.License.LicenseId,candidate.License.Sequence,candidate.License.ExpiresAtUtc}),ct);
        lock(sync)
        {
            var observed=clock.Observe();var license=candidate.License;
            if(license.Sequence<watermark.HighestSequence||license.Sequence==watermark.HighestSequence&&candidate.Sha256!=watermark.LicenseHash)throw new InvalidOperationException("不能导入较旧授权或相同序号的不同授权");
            if(license.IssuedAtUtc>time.GetUtcNow().AddMinutes(5))throw new InvalidOperationException("授权签发时间晚于当前电脑时间，请先核对电脑时间");
            var repairing=observed.Watermark.ClockProblem||invalidReason is not null;
            if(repairing&&(!license.RepairClockGuard||license.Sequence<=watermark.HighestSequence))throw new InvalidOperationException("时钟或保护状态异常，需供方签发更高序号的维护授权");
            var next=new AuthorizationWatermark(repairing?time.GetUtcNow():observed.EffectiveUtc,license.Sequence,candidate.Sha256,false,watermark.LastMotionMarker,watermark.BusinessRecoveryRequired);
            try{Atomic("license.json",Encoding.UTF8.GetBytes(envelope));SaveProtected("clock.dat",next);watermark=next;if(repairing)clock=new(time,next);else clock.UpdateMetadata(next);verified=candidate;invalidReason=null;saveError=null;lastSaved=next.ObservedAtUtc;lastSavedClockProblem=false;expirySaved=false;}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or CryptographicException){invalidReason="授权保存未完成，请重新导入供方授权";throw new InvalidOperationException("授权未确认保存成功："+e.Message);}
        }
        await audit.WriteEventAsync(new("操作日志","authorization.imported",actor.AccountId,actor.DisplayName,"导入供方授权","已保存",Parameters:new{candidate.License.LicenseId,candidate.License.Sequence,candidate.License.ExpiresAtUtc}),CancellationToken.None);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested){_=Status();try{await Task.Delay(1000,stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}}
    }
}
