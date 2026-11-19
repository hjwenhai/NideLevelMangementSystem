from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig');assert old in s,(name,old[:100]);p.write_text(s.replace(old,new),encoding='utf-8')
p=next(p for p in (root/'src/LevelManager.Domain').glob('*.cs') if 'public sealed record WirelessTaskRecord' in p.read_text(encoding='utf-8-sig'))
s=p.read_text(encoding='utf-8-sig');s=s.replace('public sealed record WirelessTaskRecord\n{','public sealed record WirelessTaskRecord\n{\n    public long? ScheduledPermissionRevision {get;init;}\n    public long? ScheduledSecurityVersion {get;init;}');p.write_text(s,encoding='utf-8')
# This is under the same runtime gate as matrix commits, not just at HTTP receipt.
edit('src/LevelManager.Host/WirelessTaskService.cs','var config=await configs.GetAsync(ct);if(revision!=config.Revision)','PermissionPolicy.RequireScheduledAuthority(await users.FindByIdAsync(actor.AccountId,ct),(await permissions.GetPermissionsAsync(ct)).Revision,scheduledPermissionRevision,scheduledSecurityVersion);var config=await configs.GetAsync(ct);if(revision!=config.Revision)')
edit('src/LevelManager.Host/WirelessTaskService.cs','new WirelessTaskRecord{','new WirelessTaskRecord{ScheduledPermissionRevision=scheduledPermissionRevision,ScheduledSecurityVersion=scheduledSecurityVersion,')
edit('src/LevelManager.Host/SnapshotScheduler.cs','await permissions.ResolveAsync(user,stoppingToken),stoppingToken);','await permissions.ResolveAsync(user,stoppingToken),stoppingToken,schedule.AuthorizedPermissionRevision,schedule.AuthorizedSecurityVersion);')
edit('src/LevelManager.Host/WirelessDevice.cs','var user=await users.FindByIdAsync(task.ActorId,token);if(user','var user=await users.FindByIdAsync(task.ActorId,token);PermissionPolicy.RequireScheduledAuthority(user,(await permissions.GetPermissionsAsync(token)).Revision,task.ScheduledPermissionRevision,task.ScheduledSecurityVersion);if(user')
edit('src/LevelManager.Host/ControlService.cs','if(current.FirstDispatchAtUtc.HasValue)return;\n                var dispatchUser','if(current.FirstDispatchAtUtc.HasValue)return;\n                await runtime.Gate.WaitAsync(token);try\n                {\n                var dispatchUser')
edit('src/LevelManager.Host/ControlService.cs','current=dispatched;},token);\n            });','current=dispatched;},token);\n                }finally{runtime.Gate.Release();}\n            });')
print('r10 first-dispatch admission serialized; scheduled authority retained and rechecked')
