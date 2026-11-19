using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class ConfigurationCoordinator(RuntimeOperationGate runtime,ConfigurationService configurations,
    AcquisitionService acquisition,IControlStore controls,IWirelessTaskStore wirelessTasks,ProductAuthorization authorization,DataJobService dataJobs,IUserStore users,IRolePermissionStore permissions)
{
    public async Task<SystemConfiguration> SaveAsync(SystemConfiguration proposed,AuditActor actor,CancellationToken ct,Guid? retentionBackupId=null)
    {
        ConfigurationRules.RequireValid(proposed);
        await runtime.Gate.WaitAsync(ct);try
        {
            var account=await users.FindByIdAsync(actor.AccountId,ct);if(account is null||!account.Enabled)throw new UnauthorizedAccessException("当前账号已失效");actor=await permissions.ResolveAsync(account,ct);actor.Require(AppPermission.ConfigurationEdit);
            var before=await configurations.GetAsync(ct);if(before.Revision!=proposed.Revision)throw new RevisionConflictException();
            if(proposed.RetentionMonths<before.RetentionMonths){if(retentionBackupId is null)throw new InvalidOperationException("缩短期限会清理旧历史，请先备份，并在系统状态选择备份、明确确认后保存");await dataJobs.RequireRetentionBackupAsync(retentionBackupId.Value,before.Revision,ct);}
            if(!authorization.HasAverageModule&&proposed.Tanks.Any(t=>t.Controls.WirelessMeasurementEnabled&&before.Tanks.FirstOrDefault(b=>b.Id==t.Id)?.Controls.WirelessMeasurementEnabled!=true))throw new InvalidOperationException("配置含尚未开放的功能");
            foreach(var t in proposed.Tanks){
                var old=before.Tanks.FirstOrDefault(x=>x.Id==t.Id);
                var restored=old is null&&await configurations.WasTankDefinedAsync(t.Id,ct);
                if(old is null&&!restored&&string.IsNullOrWhiteSpace(t.InstrumentTag))throw new ConfigurationException([new("tanks/"+t.Id+"/instrumentTag","新增储罐须填写唯一仪表位号")]);
                if(!restored&&(old is null||!string.Equals(old.InstrumentTag.Trim(),t.InstrumentTag.Trim(),StringComparison.OrdinalIgnoreCase))&&!string.IsNullOrWhiteSpace(t.InstrumentTag)&&proposed.Tanks.Any(x=>x.Id!=t.Id&&string.Equals(x.InstrumentTag.Trim(),t.InstrumentTag.Trim(),StringComparison.OrdinalIgnoreCase)))throw new ConfigurationException([new("tanks/"+t.Id+"/instrumentTag","仪表位号重复")]);
                if(!restored&&t.Controls.AllowedCommands.Any(c=>!ProductTrust.CommandAvailable(c,authorization.EnabledModules)&&!(old?.Controls.AllowedCommands.Contains(c)??false)))throw new ConfigurationException([new("tanks/"+t.Id+"/controls","所选操作尚未获得供方授权")]);
            }
            var scheduled=new List<SnapshotSchedule>();
            foreach(var schedule in proposed.Schedules)
            {
                if(schedule.Kind!=ScheduledTaskKind.AverageTemperature){scheduled.Add(schedule with{AutomaticOperationConfirmed=false,AuthorizedById=null,AuthorizedByName=null,AuthorizedSecurityVersion=0,AuthorizedPermissionRevision=0});continue;}
                var old=before.Schedules.FirstOrDefault(s=>s.Id==schedule.Id);if(!authorization.HasAverageModule&&(old is null||old.Kind!=schedule.Kind||schedule.Enabled))throw new InvalidOperationException("配置含尚未开放的功能");
                if(schedule.Enabled&&schedule.TankIds!.Any(id=>WirelessDevice.SupportReason(proposed.Channels.First(c=>c.Id==proposed.Tanks.First(t=>t.Id==id).ChannelId).Protocol)is not null))throw new InvalidOperationException("所选协议尚未开放自动平均测温，请先停用此定时任务或选择已支持的直连储罐");
                var changed=old is null||old.Kind!=schedule.Kind||old.Time!=schedule.Time||old.Points!=schedule.Points||old.Enabled!=schedule.Enabled||!(old.TankIds??[]).Order().SequenceEqual((schedule.TankIds??[]).Order())||old.AuthorizedById!=schedule.AuthorizedById||old.AuthorizedSecurityVersion!=schedule.AuthorizedSecurityVersion;
                if(schedule.Enabled&&changed){actor.Require(AppPermission.AverageTemperatureStart);if(!schedule.AutomaticOperationConfirmed)throw new ArgumentException("定时测温会自动下降、停止并恢复测量Ⅰ，请明确确认自动操作授权");var user=await users.FindByIdAsync(actor.AccountId,ct)??throw new UnauthorizedAccessException("授权账号已失效");scheduled.Add(schedule with{AuthorizedById=user.Id,AuthorizedByName=user.DisplayName,AuthorizedSecurityVersion=user.SecurityVersion,AuthorizedPermissionRevision=(await permissions.GetPermissionsAsync(ct)).Revision,AutomaticOperationConfirmed=true});}
                else scheduled.Add(schedule with{AuthorizedById=old?.AuthorizedById,AuthorizedByName=old?.AuthorizedByName,AuthorizedSecurityVersion=old?.AuthorizedSecurityVersion??0,AuthorizedPermissionRevision=old?.AuthorizedPermissionRevision??0,AutomaticOperationConfirmed=schedule.Enabled&&(old?.AutomaticOperationConfirmed??false)});
            }
            proposed=proposed with{Schedules=scheduled};
            var pending=await wirelessTasks.ListAsync(activeOnly:true,ct:ct);
            if(pending.Any(task=>{var next=proposed.Tanks.FirstOrDefault(t=>t.Id==task.Target.Tank.Id);return next is null||!next.Controls.WirelessMeasurementEnabled||next.TemperatureMode!=TemperatureMode.Wireless;}))throw new InvalidOperationException("有执行中或待核查的测温任务，暂不能撤销其测温配置");
            var additions = proposed.Tanks.ExceptBy(before.Tanks.Select(t => t.Id), t => t.Id).ToArray();
            var newChannels = proposed.Channels.ExceptBy(before.Channels.Select(c => c.Id), c => c.Id).ToArray();
            var reopens = !AcquisitionService.CanApplyWithoutReopening(before, proposed);
            var unresolved = reopens || additions.Length > 0 || newChannels.Length > 0
                ? await controls.ListAsync(unresolvedOnly: true, ct: ct) : [];
            if(reopens && (unresolved.Count > 0 || pending.Count > 0))
                throw new InvalidOperationException("存在执行中或结果待核查的仪表操作，暂不能重开采集通道；请先处理原操作。显示、分组和单位仍可修改。");
            var addedEndpoints = newChannels.Select(c => c.PhysicalKey).Concat(
                additions.Select(t => proposed.Channels.First(c => c.Id == t.ChannelId).PhysicalKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var held = unresolved.Select(r => r.Target).Concat(pending.Select(task => task.Target))
                .FirstOrDefault(target => addedEndpoints.Contains(target.Channel.PhysicalKey));
            if (held is not null)
                throw new InvalidOperationException($"储罐 {held.Tank.Code} 所在通道有执行中或待核查的操作，暂不能向该通道增加储罐。可单独保存使用其他物理端点的新通道及储罐。");
            var saved=await configurations.SaveAsync(proposed,actor,ct);await acquisition.ReconfigureAsync(saved,CancellationToken.None);return saved;
        }finally{runtime.Gate.Release();}
    }
}
