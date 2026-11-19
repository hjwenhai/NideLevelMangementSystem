using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
namespace Naide.LevelManager.Host;

public sealed record TankImportCommit(string PreviewId,bool Confirmed);
public sealed class TankConfigurationTransfer(TimeProvider clock)
{
    private sealed record Pending(string ActorId,DateTimeOffset ExpiresAt,SystemConfiguration Proposed);
    private readonly Dictionary<string,Pending> pending=[];
    public string Remember(AuditActor actor,SystemConfiguration config)
    {
        lock(pending){foreach(var key in pending.Where(p=>p.Value.ExpiresAt<=clock.GetUtcNow()).Select(p=>p.Key).ToArray())pending.Remove(key);if(pending.Count>=32)throw new InvalidOperationException("导入预览过多，请稍后重试");var id=Guid.NewGuid().ToString("N");pending.Add(id,new(actor.AccountId,clock.GetUtcNow().AddMinutes(10),config));return id;}
    }
    public SystemConfiguration Take(string id,AuditActor actor)
    {
        lock(pending){if(string.IsNullOrEmpty(id)||!pending.TryGetValue(id,out var p)||p.ActorId!=actor.AccountId||p.ExpiresAt<=clock.GetUtcNow())throw new InvalidOperationException("导入预览已过期、已使用或不属于当前账号，请重新上传预览");pending.Remove(id);return p.Proposed;}
    }
}
public static class TankConfigurationTransferEndpoints
{
    public static void MapTankConfigurationTransfer(this WebApplication app)
    {
        app.MapGet("/api/tank-configuration/export",async(bool? template,HttpContext ctx,IConfigurationStore configs,ICapacityTableStore tables,ProductAuthorization authorization,CancellationToken ct)=>
        {
            ctx.CurrentActor().Require(AppPermission.DataExport);authorization.RequireLicensed("储罐配置导出");
            return Results.File(TankConfigurationExcel.Export(await configs.GetAsync(ct),await tables.ListTablesAsync(ct),template??false,authorization.HasAverageModule,authorization.EnabledModules),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",template==true?"储罐配置导入模板.xlsx":"储罐配置.xlsx");
        }).RequireAuthorization("ConfigurationEdit");
        app.MapPost("/api/tank-configuration/import-preview",async(HttpRequest request,HttpContext ctx,IConfigurationStore configs,ICapacityTableStore tables,TankConfigurationTransfer transfer,ProductAuthorization authorization,CancellationToken ct)=>
        {
            authorization.RequireLicensed("储罐配置导入");using var memory=new MemoryStream();var buffer=new byte[65536];int count;
            while((count=await request.Body.ReadAsync(buffer,ct))>0){if(memory.Length+count>4*1024*1024)throw new ArgumentException("配置Excel不能超过4MiB");await memory.WriteAsync(buffer.AsMemory(0,count),ct);}
            var config=await configs.GetAsync(ct);TankConfigurationPreview preview;
            try{preview=TankConfigurationExcel.Preview(memory.ToArray(),config,await tables.ListTablesAsync(ct),ct,authorization.HasAverageModule);}
            catch(Exception e)when(e is DocumentFormat.OpenXml.Packaging.OpenXmlPackageException||e.GetType().Namespace?.StartsWith("ExcelDataReader.Exceptions",StringComparison.Ordinal)==true){throw new ArgumentException("配置Excel格式无法读取，请使用本系统导出的xlsx模板",e);}
            var id=preview.Issues.Count==0?transfer.Remember(ctx.CurrentActor(),preview.Configuration):null;
            return Results.Ok(new{previewId=id,configurationRevision=config.Revision,preview.Added,preview.Updated,preview.Issues,preview.ChangedTanks,differences=preview.Differences??[],expiresInSeconds=600,note="未列出储罐保留；仅保存配置，不发送仪表动作。修改绑定时会按当前条件重建采集会话。"});
        }).RequireAuthorization("ConfigurationEdit");
        app.MapPost("/api/tank-configuration/import-commit",async(TankImportCommit input,HttpContext ctx,TankConfigurationTransfer transfer,ConfigurationCoordinator coordinator,ProductAuthorization authorization,CancellationToken ct)=>
        {
            if(!input.Confirmed)throw new ArgumentException("请检查预览并确认保存配置");authorization.RequireLicensed("确认储罐配置导入");var actor=ctx.CurrentActor();
            return Results.Ok(await coordinator.SaveAsync(transfer.Take(input.PreviewId,actor),actor,ct));
        }).RequireAuthorization("ConfigurationEdit");
    }
}
