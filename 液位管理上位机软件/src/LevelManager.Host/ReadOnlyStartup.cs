using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class ReadOnlyStartup(AcquisitionService acquisition,IConfigurationStore configurations,ControlService controls,WirelessTaskService tasks,MotionEvidenceGuard evidence,RuntimeOperationGate runtime,IEventWriter events,ILogger<ReadOnlyStartup> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  try
  {
   await Task.WhenAll(controls.Ready,tasks.Ready,evidence.Ready).WaitAsync(ct);await runtime.Gate.WaitAsync(ct);try
   {
    var config=await configurations.GetAsync(ct);if(!config.StartReadOnlyAcquisitionAtStartup)return;
    if(evidence.RequiresReview){await events.WriteEventAsync(new("系统事件","acquisition.startup.held","system","系统","恢复后需现场核查，本次未自动开启采集","需人工启动"),ct);return;}
    if(!config.Tanks.Any(t=>t.Enabled&&config.Channels.Any(c=>c.Id==t.ChannelId&&c.Enabled)))return;
    await acquisition.BeginAsync(new("system","系统启动只读采集",AccountRole.Administrator),ct);
   }finally{runtime.Gate.Release();}
  }catch(OperationCanceledException)when(ct.IsCancellationRequested){}catch(Exception e){logger.LogError(e,"启动时只读采集未完成");await events.WriteEventAsync(new("系统事件","acquisition.startup.failed","system","系统","启动时只读采集未完成，请手动核对通讯后启动","未自动重放仪表动作"),CancellationToken.None);}
 }
}
