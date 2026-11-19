using System.Diagnostics;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Host;
public sealed record MaintenanceStatus(DateTimeOffset? CheckedAtUtc,long DeletedRows,long? CutoffUtcMs,long DatabaseBytes,long WalBytes,long AvailableBytes,string Warning);
public sealed class HistoryMaintenance(IConfigurationStore configs,IHistoryStore history,IEventWriter events,
    TimeProvider clock,ProductAuthorization authorization,string databasePath,ILogger<HistoryMaintenance> logger):BackgroundService
{
    public MaintenanceStatus Status{get;private set;}=new(null,0,null,0,0,0,"");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var anchor=clock.GetUtcNow();var started=clock.GetTimestamp();var lastWarning="";
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Leave startup recovery time; never run VACUUM against a collecting database.
                await Task.Delay(TimeSpan.FromSeconds(60),clock,stoppingToken);
                var now=clock.GetUtcNow();var expected=anchor+clock.GetElapsedTime(started);string warning="";long deleted=0;
                var config=await configs.GetAsync(stoppingToken);var cutoff=now.AddMonths(-config.RetentionMonths).ToUnixTimeMilliseconds();
                if(authorization.Status().State is Naide.LevelManager.Domain.ProductAuthorizationState.ClockError or Naide.LevelManager.Domain.ProductAuthorizationState.Invalid||(now-expected).Duration()>TimeSpan.FromMinutes(5))warning="系统时间发生异常变化，已暂停自动清理；请核对时间后重新启动服务。";
                else
                {
                    var watch=Stopwatch.StartNew();
                    for(var batch=0;batch<32&&watch.Elapsed<TimeSpan.FromSeconds(3);batch++)
                    {var count=await history.DeleteExpiredBatchAsync(cutoff,4096,stoppingToken);deleted+=count;if(count<4096)break;await Task.Yield();}
                }
                long Size(string path)=>File.Exists(path)?new FileInfo(path).Length:0;
                var free=new DriveInfo(Path.GetPathRoot(databasePath)!).AvailableFreeSpace;var wal=Size(databasePath+"-wal");
                if(free<1024L*1024*1024)warning+=(warning.Length>0?" ":"")+"磁盘可用空间不足1GB，请及时处理。";
                if(wal>512L*1024*1024)warning+=(warning.Length>0?" ":"")+"数据库WAL超过512MB，请检查长时间查询、备份或磁盘状态。";
                Status=new(now,deleted,cutoff,Size(databasePath),wal,free,warning);
                if(deleted>0)await events.WriteEventAsync(new("系统事件","history.retention.cleaned","system","系统","清理超过保存期限的历史","已清理",Parameters:new{DeletedRows=deleted,CutoffUtcMs=cutoff,config.RetentionMonths}),stoppingToken);
                if(warning!=lastWarning){if(warning.Length>0)await events.WriteEventAsync(new("系统事件","storage.warning","system","系统",warning,"需处理"),stoppingToken);lastWarning=warning;}
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){Status=Status with{Warning="历史维护未完成："+e.Message};logger.LogError(e,"历史维护失败");}
        }
    }
}
