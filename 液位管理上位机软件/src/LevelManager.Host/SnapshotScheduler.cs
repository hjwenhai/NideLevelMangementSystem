using System.Text.Json;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class SnapshotScheduler(IConfigurationStore configs,IDataJobStore jobs,DataJobService service,TimeProvider clock,ProductAuthorization authorization,ILogger<SnapshotScheduler> logger,WirelessTaskService wireless,IUserStore users,IRolePermissionStore permissions):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(service.Ready,wireless.Ready).WaitAsync(stoppingToken);
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config=await configs.GetAsync(stoppingToken);var now=clock.GetUtcNow();
                foreach(var schedule in config.Schedules.Where(_=>authorization.Status().AllowsLicensedActions))
                {
                    var day=SnapshotSchedulePolicy.DueDate(schedule,now);if(day is null)continue;
                    if(schedule.Kind==ScheduledTaskKind.AverageTemperature)
                    {
                        if(!authorization.HasAverageModule)continue;
                        var dispatch=new DataJob{Kind=DataJobKind.AverageTemperatureDispatch,State=DataJobState.Running,ActorId=schedule.AuthorizedById??"system",ActorName=schedule.AuthorizedByName??"定时任务",CreatedAtUtc=now,RequestJson=JsonSerializer.Serialize(new{schedule.Id,schedule.Name,schedule.Points,schedule.TankIds,config.Revision})};
                        if(!await jobs.CreateScheduledAsync(schedule.Id,day,dispatch,stoppingToken))continue;
                        var accepted=new List<Guid>();var errors=new List<string>();
                        try
                        {
                            var user=schedule.AuthorizedById is null?null:await users.FindByIdAsync(schedule.AuthorizedById,stoppingToken);
                            if(!schedule.AutomaticOperationConfirmed||user is null||!user.Enabled||!(await permissions.ResolveAsync(user,stoppingToken)).Allows(AppPermission.AverageTemperatureStart)||user.SecurityVersion!=schedule.AuthorizedSecurityVersion||(await permissions.GetPermissionsAsync(stoppingToken)).Revision!=schedule.AuthorizedPermissionRevision)throw new UnauthorizedAccessException("原自动操作授权账号或权限已改变，请管理员重新编辑确认此定时任务");
                            foreach(var tankId in schedule.TankIds!)
                            {
                                if(SnapshotSchedulePolicy.DueDate(schedule,clock.GetUtcNow())!=day){errors.Add("已错过执行分钟，未申请剩余储罐，不补发");break;}
                                try{var task=await wireless.RequestAsync(tankId,config.Revision,schedule.Points,true,await permissions.ResolveAsync(user,stoppingToken),stoppingToken,schedule.AuthorizedPermissionRevision,schedule.AuthorizedSecurityVersion);accepted.Add(task.Id);}catch(Exception e)when(e is InvalidOperationException or ArgumentException or UnauthorizedAccessException or AuthorizationRestrictedException){errors.Add(config.Tanks.First(t=>t.Id==tankId).Code+"："+e.Message);}
                            }
                        }catch(Exception e)when(e is InvalidOperationException or ArgumentException or UnauthorizedAccessException){errors.Add(e.Message);}
                        await jobs.UpdateAsync(dispatch with{State=accepted.Count>0?DataJobState.Completed:DataJobState.Failed,Rows=accepted.Count,FinishedAtUtc=clock.GetUtcNow(),RequestJson=JsonSerializer.Serialize(new{schedule.Id,schedule.Name,AcceptedTaskIds=accepted}),Error="仅申请完成，实际进度请查看测温任务。"+(errors.Count>0?string.Join("；",errors):"已受理 "+accepted.Count+" 罐")},stoppingToken);continue;
                    }
                    var job=new DataJob{Kind=DataJobKind.Snapshot,ActorId="system",ActorName="定时任务",CreatedAtUtc=now,RequestJson=JsonSerializer.Serialize(new SnapshotRequest(schedule.Name+" "+day,schedule.TankIds),new JsonSerializerOptions(JsonSerializerDefaults.Web))};
                    await jobs.CreateScheduledAsync(schedule.Id,day,job,stoppingToken);
                }
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"定时快照调度失败，下轮重试");}
            try{await Task.Delay(TimeSpan.FromSeconds(1),clock,stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
        }
    }
}
