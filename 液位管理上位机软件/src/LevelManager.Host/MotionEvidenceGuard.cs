using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class MotionEvidenceGuard(IMotionEvidenceStore store,ProductAuthorization authorization,IControlStore controls,IWirelessTaskStore tasks,TimeProvider time,string restoreRequest,ILogger<MotionEvidenceGuard> logger):BackgroundService
{
    private readonly SemaphoreSlim commit=new(1);private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);public Task Ready=>ready.Task;
    public bool RequiresReview=>authorization.BusinessEvidence.BusinessRecoveryRequired;
    public void RequireNewActionAllowed(){if(!ready.Task.IsCompletedSuccessfully||RequiresReview)throw new InvalidOperationException("业务数据与独立写入证据不一致，请先在系统管理核查恢复后的现场设备；停止和必要恢复仍可使用");}
    // Serialize the database marker and protected watermark in the same order across
    // all physical channels. A restored snapshot containing the last marker must
    // therefore contain all earlier committed intents, including parallel channels.
    public async Task CommitAsync(MotionEvidence evidence,Func<Task> saveIntent,CancellationToken ct)
    {
        await Ready.WaitAsync(ct);await commit.WaitAsync(ct);try{await saveIntent();await store.CreateAsync(evidence,ct);authorization.SaveBusinessEvidence(evidence.Id,RequiresReview);}finally{commit.Release();}
    }
    public async Task ReviewAsync(AuditActor actor,string reason,bool confirmed,CancellationToken ct)
    {
        if(actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("仅管理员可核查恢复后的业务状态");if(!confirmed||string.IsNullOrWhiteSpace(reason)||reason.Length>1000)throw new ArgumentException("请填写现场核查结论并确认");await Ready.WaitAsync(ct);await commit.WaitAsync(ct);
        try
        {
            if((await controls.ListAsync(unresolvedOnly:true,ct:ct)).Count>0||(await tasks.ListAsync(activeOnly:true,ct:ct)).Count>0)throw new InvalidOperationException("请先逐项处理执行中或结果待核查的操作和任务");
            var marker=new MotionEvidence(Guid.NewGuid(),Guid.NewGuid(),"recovery-review",time.GetUtcNow(),null,actor.AccountId,actor.DisplayName,reason.Trim());await store.CreateAsync(marker,ct);if(File.Exists(restoreRequest))File.Delete(restoreRequest);authorization.SaveBusinessEvidence(marker.Id,false);
        }finally{commit.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{var state=authorization.BusinessEvidence;if(File.Exists(restoreRequest)){authorization.SaveBusinessEvidence(state.LastMotionMarker,true);File.Delete(restoreRequest);state=authorization.BusinessEvidence;}if(state.LastMotionMarker.HasValue?!await store.ExistsAsync(state.LastMotionMarker.Value,ct):await store.CountAsync(ct)>0)authorization.SaveBusinessEvidence(state.LastMotionMarker,true);ready.TrySetResult();return;}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}catch(Exception e){logger.LogCritical(e,"独立业务证据核验失败，暂不开放新动作");await Task.Delay(1000,ct);}
        }
    }
}
