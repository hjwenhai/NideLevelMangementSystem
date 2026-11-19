using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Infrastructure;
namespace Naide.LevelManager.Host;

public sealed class DataJobService(IDataJobStore jobs,IConfigurationStore configurations,AcquisitionService acquisition,
    IHistoryStore history,TimeProvider clock,ProductAuthorization authorization,string exportRoot,string databasePath,ILogger<DataJobService> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate=new(1);
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready=>ready.Task;
    private CancellationTokenSource? current;
    private Guid? currentId;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};
    public string DirectoryFor(Guid id)=>Path.Combine(exportRoot,id.ToString("N"));
    public async Task<Guid> ExportAsync(HistoryExportRequest request,AuditActor actor,CancellationToken ct)
    {
        actor.Require(AppPermission.HistoryRead);actor.Require(AppPermission.DataExport);HistoryQualityPolicy.Validate(request.QualityQuantity,request.Quality);HistoryExportPolicy.Validate(request.SelectedQuantities);
        await Ready.WaitAsync(ct);authorization.RequireLicensed("新的数据任务");
        if(request.StartUtcMs>=request.EndUtcMs||request.EndUtcMs-request.StartUtcMs>367L*86400000)throw new ArgumentException("导出时间范围不超过一年，结束时间应晚于开始时间");
        _=DateTimeOffset.FromUnixTimeMilliseconds(request.StartUtcMs);_=DateTimeOffset.FromUnixTimeMilliseconds(request.EndUtcMs);
        var config=await configurations.GetAsync(ct);if(request.TankId.HasValue&&!config.Tanks.Any(t=>t.Id==request.TankId))throw new ArgumentException("储罐不存在");
        // A job is finite: future archive rows must not continually enter its range.
        request=request with{EndUtcMs=Math.Min(request.EndUtcMs,clock.GetUtcNow().ToUnixTimeMilliseconds())};if(request.StartUtcMs>=request.EndUtcMs)throw new ArgumentException("开始时间不能在未来");
        var job=new DataJob{Kind=DataJobKind.HistoryExport,ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow(),RequestJson=JsonSerializer.Serialize(request,Json)};await jobs.CreateAsync(job,ct);return job.Id;
    }
    public async Task<Guid> SnapshotAsync(SnapshotRequest request,AuditActor actor,CancellationToken ct)
    {
        await Ready.WaitAsync(ct);authorization.RequireLicensed("新的数据任务");
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>100||request.TankIds is {Count:0 or >128}||request.TankIds?.Distinct().Count()!=request.TankIds?.Count)throw new ArgumentException("请填写快照名称，选择1～128个不同储罐或全部启用罐");
        var config=await configurations.GetAsync(ct);if(request.TankIds?.Any(id=>!config.Tanks.Any(t=>t.Id==id&&t.Enabled))==true)throw new ArgumentException("选中的储罐不存在或已停用");
        var job=new DataJob{Kind=DataJobKind.Snapshot,ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow(),RequestJson=JsonSerializer.Serialize(request,Json)};await jobs.CreateAsync(job,ct);return job.Id;
    }
    public async Task RequireRetentionBackupAsync(Guid jobId,long revision,CancellationToken ct)
    {
        authorization.RequireLicensed("缩短历史保留期限");var job=await jobs.GetAsync(jobId,ct);if(job is null||job.Kind!=DataJobKind.DatabaseBackup||job.State!=DataJobState.Completed||job.FileName is null)throw new ArgumentException("请选择已完成的业务数据库备份");
        var folder=Path.GetFullPath(DirectoryFor(jobId))+Path.DirectorySeparatorChar;var file=Path.GetFullPath(Path.Combine(folder,job.FileName));if(!file.StartsWith(folder,StringComparison.OrdinalIgnoreCase)||!File.Exists(file))throw new InvalidOperationException("所选备份文件已不存在，请重新备份");
        using var zip=System.IO.Compression.ZipFile.OpenRead(file);if(zip.Entries.Count!=2)throw new InvalidDataException("备份包文件清单无效");var db=zip.GetEntry("业务数据备份.db")??throw new InvalidDataException("缺少数据库备份");var entry=zip.GetEntry("备份说明.json")??throw new InvalidDataException("缺少备份说明");if(entry.Length is <1 or >65536||db.Length is <100 or >68719476736)throw new InvalidDataException("备份大小无效");
        using var reader=new StreamReader(entry.Open());var manifest=JsonSerializer.Deserialize<DatabaseBackupManifest>(await reader.ReadToEndAsync(ct),LicenseVerifier.Json)??throw new InvalidDataException("备份说明为空");var now=clock.GetUtcNow();if(manifest.ProductId!=ProductTrust.ProductId||manifest.AuthorizationIncluded||manifest.ConfigurationRevision!=revision||manifest.SnapshotAtUtc>now.AddMinutes(1)||manifest.SnapshotAtUtc<now.AddHours(-24)||manifest.Bytes!=db.Length||manifest.SchemaVersion!=SqliteConfigurationStore.CurrentSchema||manifest.Sha256 is null||manifest.Sha256.Length!=64)throw new InvalidOperationException("备份与当前配置版本不符或超过24小时，请按当前配置重新备份");
        await using var stream=db.Open();if(!string.Equals(Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream,ct)),manifest.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("备份SHA256校验失败，未修改历史保留期限");
    }
    public async Task<Guid> BackupAsync(AuditActor actor,CancellationToken ct)
    {
        actor.Require(AppPermission.BackupManage);await Ready.WaitAsync(ct);authorization.RequireLicensed("手动业务备份");
        var job=new DataJob{Kind=DataJobKind.DatabaseBackup,ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow(),RequestJson="{}"};await jobs.CreateAsync(job,ct);return job.Id;
    }
    public async Task CancelAsync(Guid id,AuditActor actor,CancellationToken ct)
    {
        await gate.WaitAsync(ct);try
        {
            var job=await jobs.GetAsync(id,ct)??throw new ArgumentException("任务不存在");if(job.Kind==DataJobKind.AverageTemperatureDispatch)throw new InvalidOperationException("此条只记录定时申请，取消请在测温任务中逐项处理");
            if(job.ActorId!=actor.AccountId&&actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("只能取消自己创建的任务");
            if(job.State==DataJobState.Queued)await jobs.UpdateAsync(job with{State=DataJobState.Cancelled,FinishedAtUtc=clock.GetUtcNow(),Error="用户取消"},ct);
            else if(job.State==DataJobState.Running&&currentId==id)current?.Cancel();
        }finally{gate.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered=false;
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(!recovered){await jobs.RecoverInterruptedAsync(clock.GetUtcNow(),stoppingToken);foreach(var previous in await jobs.ListAsync(stoppingToken))if(previous.Kind is DataJobKind.HistoryExport or DataJobKind.DatabaseBackup&&previous.State is DataJobState.Interrupted or DataJobState.Cancelled or DataJobState.Failed)CleanPartial(previous.Id);recovered=true;ready.TrySetResult();}
                DataJob? job;await gate.WaitAsync(stoppingToken);try{job=await jobs.ClaimAsync(clock.GetUtcNow(),stoppingToken);if(job is not null){currentId=job.Id;current=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);}}finally{gate.Release();}
                if(job is null){await Task.Delay(TimeSpan.FromSeconds(1),clock,stoppingToken);continue;}
                await Run(job,current!.Token,stoppingToken);
                await gate.WaitAsync(CancellationToken.None);try{current?.Dispose();current=null;currentId=null;}finally{gate.Release();}
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"数据任务处理失败，下轮重试");try{await Task.Delay(1000,stoppingToken);}catch(OperationCanceledException){break;}}
        }
    }
    private void CleanPartial(Guid id)
    {
        var root=Path.GetFullPath(exportRoot)+Path.DirectorySeparatorChar;var directory=Path.GetFullPath(DirectoryFor(id));
        if(!directory.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(directory))return;
        if((new DirectoryInfo(directory).Attributes&FileAttributes.ReparsePoint)!=0)return;
        foreach(var file in Directory.EnumerateFiles(directory))
        {
            var name=Path.GetFileName(file);if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)continue;
            if(name.StartsWith("历史数据_",StringComparison.Ordinal)&&name.EndsWith(".xlsx",StringComparison.Ordinal)||name is "历史数据分片.zip" or "历史数据分片.zip.partial" or "导出说明.json" or "业务数据备份.db" or "业务数据备份.zip" or "业务数据备份.zip.partial" or "备份说明.json")
            {try{File.Delete(file);}catch(IOException error){logger.LogWarning(error,"清理未完成导出文件失败");}}
        }
    }
    private async Task Run(DataJob job,CancellationToken ct,CancellationToken hostToken)
    {
        try
        {
            authorization.RequireLicensed("尚未执行的数据任务");
            if(job.Kind==DataJobKind.HistoryExport)
            {
                var request=JsonSerializer.Deserialize<HistoryExportRequest>(job.RequestJson,Json)!;var config=await configurations.GetAsync(ct);
                var directory=DirectoryFor(job.Id);Directory.CreateDirectory(exportRoot);var disk=new DriveInfo(Path.GetPathRoot(exportRoot)!);
                if(disk.AvailableFreeSpace<1024L*1024*1024)throw new IOException("可用磁盘空间不足1GB，请清理空间后重新导出");
                var result=await new HistoryExcelExporter(history).ExportAsync(directory,request,config,async rows=>{job=job with{Rows=rows};await jobs.UpdateAsync(job,ct);},ct);
                ct.ThrowIfCancellationRequested();authorization.RequireLicensed("发布历史导出文件");job=job with{State=DataJobState.Completed,Rows=result.Rows,FileName=result.FileName,FinishedAtUtc=clock.GetUtcNow()};await jobs.UpdateAsync(job,ct);
            }
            else if(job.Kind==DataJobKind.DatabaseBackup)
            {
                var directory=DirectoryFor(job.Id);Directory.CreateDirectory(directory);var drive=new DriveInfo(Path.GetPathRoot(databasePath)!);var estimate=new FileInfo(databasePath).Length+(File.Exists(databasePath+"-wal")?new FileInfo(databasePath+"-wal").Length:0);
                if(drive.AvailableFreeSpace<2*estimate+1024L*1024*1024)throw new IOException("可用空间不足以同时生成业务备份及下载包");
                var db=Path.Combine(directory,"业务数据备份.db");var result=await OnlineDatabaseBackup.CreateAsync(databasePath,db,async progress=>{job=job with{Rows=progress.CopiedPages};await jobs.UpdateAsync(job,ct);},ct);
                var manifest=new DatabaseBackupManifest(ProductTrust.ProductId,result.SchemaVersion,result.ConfigurationRevision,result.SnapshotAtUtc,result.Bytes,result.Sha256);
                await File.WriteAllTextAsync(Path.Combine(directory,"备份说明.json"),JsonSerializer.Serialize(manifest,Json),ct);var partial=Path.Combine(directory,"业务数据备份.zip.partial");
                await using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true))
                {
                    using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
                    foreach(var name in new[]{"业务数据备份.db","备份说明.json"})
                    {var entry=archive.CreateEntry(name,CompressionLevel.NoCompression);await using var write=entry.Open();await using var read=new FileStream(Path.Combine(directory,name),FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);await read.CopyToAsync(write,ct);}
                    await output.FlushAsync(ct);output.Flush(true);
                }
                authorization.RequireLicensed("发布业务备份下载包");var filename="业务数据备份.zip";File.Move(partial,Path.Combine(directory,filename));job=job with{State=DataJobState.Completed,FileName=filename,FinishedAtUtc=clock.GetUtcNow()};await jobs.UpdateAsync(job,ct);
                try{File.Delete(db);File.Delete(Path.Combine(directory,"备份说明.json"));}catch(IOException cleanup){logger.LogWarning(cleanup,"已完成备份的中间文件未清理 {JobId}",job.Id);}
            }
            else
            {
                var request=JsonSerializer.Deserialize<SnapshotRequest>(job.RequestJson,Json)!;var config=await configurations.GetAsync(ct);var view=await acquisition.CurrentAsync(ct);
                if(config.Revision!=view.ConfigurationRevision)throw new InvalidOperationException("储罐配置正在更新，请重新抓取快照");
                var ids=config.Tanks.Where(t=>t.Enabled&&config.Channels.Any(c=>c.Id==t.ChannelId&&c.Enabled)&&(request.TankIds is null||request.TankIds.Contains(t.Id))).Select(t=>t.Id).ToHashSet();
                if(ids.Count==0)throw new InvalidOperationException("没有符合条件的启用储罐");
                var snapshot=new DataSnapshot(Guid.NewGuid(),request.Name,clock.GetUtcNow(),config.Revision,job.ActorName,view.Readings.Where(r=>ids.Contains(r.TankId)).ToArray());
                authorization.RequireLicensed("保存数据快照");job=job with{State=DataJobState.Completed,SnapshotId=snapshot.Id,Rows=snapshot.Readings.Count,FinishedAtUtc=snapshot.AtUtc};await jobs.CompleteSnapshotAsync(job,snapshot,ct);
            }
        }
        catch(OperationCanceledException)
        {
            // Interrupted files are not published. A later start never replays a data job.
            job=job with{State=hostToken.IsCancellationRequested?DataJobState.Interrupted:DataJobState.Cancelled,FinishedAtUtc=clock.GetUtcNow(),Error="任务已中断，未发布导出文件"};if(!hostToken.IsCancellationRequested)await jobs.UpdateAsync(job,CancellationToken.None);
        }
        catch(Exception e)
        {logger.LogError(e,"数据任务失败 {JobId}",job.Id);job=job with{State=DataJobState.Failed,FinishedAtUtc=clock.GetUtcNow(),Error="任务未完成："+e.Message};await jobs.UpdateAsync(job,CancellationToken.None);}
        finally{if(job.Kind is DataJobKind.HistoryExport or DataJobKind.DatabaseBackup&&job.State!=DataJobState.Completed)CleanPartial(job.Id);}
    }
}
