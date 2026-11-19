using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed record ReadOnlyAccessState(bool Enabled,IReadOnlyList<Guid> TankIds,string? Hash,DateTimeOffset? ChangedAtUtc);
public sealed record ReadOnlyAccessRequest(long ConfigurationRevision,bool Enabled,IReadOnlyList<Guid> TankIds,bool Confirmed);
public sealed class ReadOnlyIntegration
{
    private readonly string file;private readonly IDataProtector protector;private readonly IEventWriter audit;private readonly TimeProvider clock;private readonly SemaphoreSlim gate=new(1,1);private volatile ReadOnlyAccessState state=new(false,[],null,null);private string warning="";
    public ReadOnlyIntegration(string dataRoot,IDataProtectionProvider protection,IEventWriter events,TimeProvider time)
    {
        file=Path.Combine(dataRoot,"readonly-interface.dat");protector=protection.CreateProtector("LevelManager.ExternalReadOnly","v1");audit=events;clock=time;
        if(File.Exists(file))try{if(new FileInfo(file).Length>65536)throw new InvalidDataException();state=JsonSerializer.Deserialize<ReadOnlyAccessState>(protector.Unprotect(File.ReadAllBytes(file)),LicenseVerifier.Json)??throw new InvalidDataException();if(state.TankIds.Count>128||state.Enabled&&(state.Hash is null||state.Hash.Length!=64||!state.Hash.All(Uri.IsHexDigit)))throw new InvalidDataException();}catch(Exception e)when(e is IOException or CryptographicException or JsonException or InvalidDataException or ArgumentException or NullReferenceException){state=new(false,[],null,null);warning="对外接口配置无法验证或恢复后登录密钥已更新，接口已关闭；请管理员重新设置";}
    }
    public object Status()=>new{state.Enabled,state.TankIds,state.ChangedAtUtc,warning,endpoint="/api/external/v1/current",authentication="Authorization: Bearer <独立只读令牌>",tokenStored=false};
    public IReadOnlySet<Guid>? Authorize(string? authorization)
    {
        var current=state;if(!current.Enabled||current.Hash is null||authorization is null||!authorization.StartsWith("Bearer ",StringComparison.Ordinal)||authorization.Length>128)return null;var value=authorization[7..];if(!value.StartsWith("lm_read_",StringComparison.Ordinal)||value.Length!=51)return null;return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(value)),Convert.FromHexString(current.Hash))?current.TankIds.ToHashSet():null;
    }
    public async Task<object> ChangeAsync(ReadOnlyAccessRequest input,SystemConfiguration config,AuditActor actor,CancellationToken ct)
    {
        actor.Require(AppPermission.IntegrationManage);if(!input.Confirmed)throw new ArgumentException("请确认启用、重新生成或关闭对外只读接口");if(input.ConfigurationRevision!=config.Revision)throw new RevisionConflictException();if(input.TankIds is null||input.TankIds.Count>128||input.TankIds.Distinct().Count()!=input.TankIds.Count||input.TankIds.Any(id=>!config.Tanks.Any(t=>t.Id==id))||input.Enabled&&input.TankIds.Count==0)throw new ArgumentException("请选择允许读取的现有储罐，新增罐不会自动对外开放");
        await gate.WaitAsync(ct);try
        {
            var token=input.Enabled?"lm_read_"+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_'):null;var next=new ReadOnlyAccessState(input.Enabled,input.Enabled?input.TankIds.ToArray():[],token is null?null:Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))),clock.GetUtcNow());
            await audit.WriteEventAsync(new("操作日志","integration.access.changed",actor.AccountId,actor.DisplayName,input.Enabled?"生成对外只读令牌，旧令牌立即失效":"关闭对外只读接口并吊销令牌","已核准",Parameters:new{next.Enabled,next.TankIds}),ct);
            var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";try{await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,true)){await output.WriteAsync(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(next,LicenseVerifier.Json)),ct);await output.FlushAsync(ct);output.Flush(true);}File.Move(temporary,file,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}state=next;warning="";return new{state.Enabled,state.TankIds,state.ChangedAtUtc,token,message=token is null?"对外只读接口已关闭":"令牌仅本次显示，请交给指定接入方保管；后续只可重新生成"};
        }finally{gate.Release();}
    }
    public async Task<object> SnapshotAsync(IReadOnlySet<Guid> allowed,AcquisitionService acquisition,IConfigurationStore configs,IAlarmStore alarms,CancellationToken ct)
    {
        var view=await acquisition.CurrentAsync(ct);var config=await configs.GetAsync(ct);if(config.Revision!=view.ConfigurationRevision)throw new InvalidOperationException("当前配置正在更新，请重新读取");var summary=await alarms.SummaryAsync(ct);
        object Point(Measurement value,string unit,string source,DateTimeOffset? at,bool held=false,bool historical=false)=>new{value=value.Value,unit,quality=value.IsValid?(held?"Held":historical?"Historical":"Good"):value.Quality.ToString(),qualityCode=value.IsValid?(held?6:historical?7:0):(int)value.Quality,isCurrent=value.IsValid&&!held&&!historical,source,sampledAtUtc=at};
        return new{schemaVersion="1.0",generatedAtUtc=view.ServerAtUtc,view.Running,configurationRevision=view.ConfigurationRevision,timeBasis="无仪表确认时间时采用软件接收时间；冻结液面和上次任务均值保留原时间，不作为当前新测量",tanks=config.Tanks.Where(t=>allowed.Contains(t.Id)).Select(t=>{var r=view.Readings.First(v=>v.TankId==t.Id);var sourceAt=r.SourceTimeConfirmed?r.SourceAtUtc:r.ReceivedAtUtc==default?null:(DateTimeOffset?)r.ReceivedAtUtc;var held=r.FrozenSurfaceAtUtc.HasValue;var levelAt=r.FrozenSurfaceAtUtc??sourceAt;var historical=t.TemperatureMode==TemperatureMode.Wireless&&r.AverageTaskId.HasValue;return new{t.Id,t.Code,t.Enabled,t.Hidden,t.TemperatureMode,receivedAtUtc=r.ReceivedAtUtc==default?null:(DateTimeOffset?)r.ReceivedAtUtc,r.SourceTimeConfirmed,r.SourceAtUtc,r.InstrumentStatus,r.InstrumentFault,r.Diagnostic,r.AverageTaskId,r.AverageCompletedAtUtc,points=new{level=Point(r.LevelMm,"mm","液面",levelAt,held),floatPosition=Point(r.FloatPositionMm,"mm","实际位置P",sourceAt),instantTemperature=Point(r.InstantTemperatureC,"℃","IR26",sourceAt),averageTemperature=Point(r.AverageTemperatureC,"℃",historical?"最近成功任务":"ATS正常点平均",historical?r.AverageCompletedAtUtc:sourceAt,historical:historical),density=Point(r.InstrumentDensityKgM3,"kg/m³","仪表",sourceAt),currentVolume=Point(r.CurrentVolumeM3,"m³","主罐容表与静压力修正",levelAt,held),netVolume=Point(r.NetVolumeM3,"m³","扣水后当前体积",levelAt,held),mass=Point(r.MassT,"t","净体积乘仪表密度",levelAt,held),pressure1=Point(r.Pressure1KPa,"kPa","模拟输入1",sourceAt),pressure2=Point(r.Pressure2KPa,"kPa","模拟输入2",sourceAt)},atsPoints=r.AtsPoints.Select(p=>new{p.Index,temperature=Point(p.TemperatureC,"℃","ATS固定点",sourceAt)}),alarms=summary.Tanks.FirstOrDefault(a=>a.TankId==t.Id)??new TankAlarmSummary(t.Id,0,0,0)};}).ToArray()};
    }
}
