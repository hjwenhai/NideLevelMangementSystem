using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;

public static class PermissionRouting
{
    public static async Task CheckAsync(HttpContext ctx)
    {
        if(ctx.User.Identity?.IsAuthenticated!=true)return;
        var actor=ctx.CurrentActor();var path=ctx.Request.Path.Value??"";
        if(path=="/api/history/export")actor.Require(AppPermission.HistoryRead);
        if(path=="/api/alarms"&&ctx.Request.Method=="GET"&&ctx.Request.Query["state"] is var state&&(state=="history"||state=="all"))actor.Require(AppPermission.HistoryRead);
        if(path.StartsWith("/api/snapshots/",StringComparison.Ordinal)&&ctx.Request.Method=="GET")actor.Require(AppPermission.HistoryRead);
        if(path.StartsWith("/api/data-jobs",StringComparison.Ordinal))
        {
            if(!actor.Allows(AppPermission.HistoryRead)&&!actor.Allows(AppPermission.BackupManage))throw new UnauthorizedAccessException("当前岗位无数据任务查询权限");
            var pieces=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
            if(pieces.Length>=4&&Guid.TryParse(pieces[2],out var id))
            {
                var job=await ctx.RequestServices.GetRequiredService<IDataJobStore>().GetAsync(id,ctx.RequestAborted);
                if(job?.Kind==DataJobKind.DatabaseBackup)actor.Require(AppPermission.BackupManage);
                else {actor.Require(AppPermission.HistoryRead);if(pieces[3]=="download")actor.Require(AppPermission.DataExport);}
            }
        }
    }
}
