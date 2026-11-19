using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;
public sealed class SqliteWirelessTaskStore(string path):IWirelessTaskStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,ForeignKeys=true,DefaultTimeout=10}.ToString());
        try{await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous=FULL";await cmd.ExecuteNonQueryAsync(ct);return c;}catch{await c.DisposeAsync();throw;}
    }
    private static async Task Audit(SqliteConnection c,SqliteTransaction tx,WirelessTaskRecord r,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,TankId,TankCode,Content,Result,Details) VALUES($at,$category,$actor,$name,$tank,$code,$text,$result,$details)";
        cmd.Parameters.AddWithValue("$at",(r.StageStartedAtUtc??r.CreatedAtUtc).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$category",r.Stage is WirelessStage.RequiresReview or WirelessStage.Failed?"任务错误":"操作日志");cmd.Parameters.AddWithValue("$actor",r.ReviewerId??r.ActorId);cmd.Parameters.AddWithValue("$name",r.ReviewerName??r.ActorName);cmd.Parameters.AddWithValue("$tank",r.Target.Tank.Id.ToString());cmd.Parameters.AddWithValue("$code",r.Target.Tank.Code);cmd.Parameters.AddWithValue("$text","平均温度测量 · "+r.Target.Tank.Code);cmd.Parameters.AddWithValue("$result",r.Message);cmd.Parameters.AddWithValue("$details",JsonSerializer.Serialize(new{EventCode="wireless."+r.Stage,TaskId=r.Id,r.Version,r.Stage,r.CurrentPoint,r.WriteIntentRecorded,r.ReviewReason},Json));await cmd.ExecuteNonQueryAsync(ct);
    }
    private static void Parameters(SqliteCommand cmd,WirelessTaskRecord r)
    {
        cmd.Parameters.AddWithValue("$id",r.Id.ToString());cmd.Parameters.AddWithValue("$tank",r.Target.Tank.Id.ToString());cmd.Parameters.AddWithValue("$stage",r.Stage.ToString());cmd.Parameters.AddWithValue("$holds",r.HoldsTank?1:0);cmd.Parameters.AddWithValue("$at",r.CreatedAtUtc.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$revision",r.Target.ConfigurationRevision);cmd.Parameters.AddWithValue("$version",r.Version);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(r,Json));
    }
    public async Task CreateAsync(WirelessTaskRecord r,CancellationToken ct=default)
    {
        if(r.Id==Guid.Empty||r.Version!=0||r.Stage!=WirelessStage.Queued||r.RequestedPoints is <1 or >16||r.Target.AcquisitionSessionId==Guid.Empty)throw new ArgumentException("测温任务参数无效");
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT count(*) FROM wireless_tasks WHERE TankId=$tank AND HoldsTank=1";cmd.Parameters.AddWithValue("$tank",r.Target.Tank.Id.ToString());if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0)throw new InvalidOperationException("该罐已有执行中或待核查的测温任务");
        cmd.Parameters.Clear();cmd.CommandText="INSERT INTO wireless_tasks(Id,TankId,Stage,HoldsTank,AtUtcMs,ConfigurationRevision,Version,Payload) VALUES($id,$tank,$stage,$holds,$at,$revision,$version,$payload)";Parameters(cmd,r);await cmd.ExecuteNonQueryAsync(ct);await Audit(c,tx,r,ct);await tx.CommitAsync(ct);
    }
    public async Task SaveAsync(WirelessTaskRecord r,CancellationToken ct=default)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Payload FROM wireless_tasks WHERE Id=$id";cmd.Parameters.AddWithValue("$id",r.Id.ToString());
        if(await cmd.ExecuteScalarAsync(ct) is not string payload)throw new ArgumentException("测温任务不存在");var before=JsonSerializer.Deserialize<WirelessTaskRecord>(payload,Json)!;
        if(r.Version!=before.Version+1)throw new RevisionConflictException();
        if(JsonSerializer.Serialize(before.Target,Json)!=JsonSerializer.Serialize(r.Target,Json)||before.ActorId!=r.ActorId||before.ActorName!=r.ActorName||before.CreatedAtUtc!=r.CreatedAtUtc||before.RequestedPoints!=r.RequestedPoints||before.RuleVersion!=r.RuleVersion)throw new InvalidOperationException("不能改变原测温任务及设备对象");
        if(!before.HoldsTank||before.Stage==WirelessStage.RequiresReview&&r.Stage!=WirelessStage.Reviewed)throw new InvalidOperationException("任务已结束或须人工核查，不能自动续跑");
        if(before.WriteIntentRecorded&&!r.WriteIntentRecorded)throw new InvalidOperationException("不能清除原任务的写入意图");
        if(before.Plan is not null&&JsonSerializer.Serialize(before.Plan,Json)!=JsonSerializer.Serialize(r.Plan,Json))throw new InvalidOperationException("开始执行后不能重算已冻结的液面和测点");
        if(before.PlanFrozenAtUtc.HasValue&&r.PlanFrozenAtUtc!=before.PlanFrozenAtUtc)throw new InvalidOperationException("不能覆盖液面冻结时间");
        if(r.Stage==WirelessStage.Completed)
        {
            if(r.Plan is null||!r.MeasureIRestored||r.Points.Count!=r.Plan.PlannedPoints||r.Points.Where((p,i)=>p.Number!=i+1||p.TargetMm!=r.Plan.TargetsMm[i]||!p.SampledAtUtc.HasValue||p.DwellElapsedSeconds<600||p.StoppedPositionMm is null||!WirelessMeasurementRules.WithinTolerance(p.StoppedPositionMm.Value,p.TargetMm)).Any())throw new InvalidOperationException("任务缺少完整测点、停留、到位或恢复证据");
            var expected=WirelessMeasurementRules.Average(r.Plan,r.Points.Select(p=>p.Temperature).ToArray(),true,false);if(expected.Quality!=r.AverageTemperature.Quality||expected.Value!=r.AverageTemperature.Value)throw new InvalidOperationException("平均结果与原始有效测点不一致");
        }
        foreach(var p in before.Points.Where(p=>p.SampledAtUtc.HasValue))if(!r.Points.Any(n=>n==p))throw new InvalidOperationException("已完成的原测点记录不能覆盖");
        cmd.Parameters.Clear();cmd.CommandText="UPDATE wireless_tasks SET Stage=$stage,HoldsTank=$holds,Version=$version,Payload=$payload WHERE Id=$id AND Version=$before";Parameters(cmd,r);cmd.Parameters.AddWithValue("$before",before.Version);if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new RevisionConflictException();await Audit(c,tx,r,ct);await tx.CommitAsync(ct);
    }
    public async Task<WirelessTaskRecord?> GetAsync(Guid id,CancellationToken ct=default)
    {await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM wireless_tasks WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());return await cmd.ExecuteScalarAsync(ct) is string p?JsonSerializer.Deserialize<WirelessTaskRecord>(p,Json):null;}
    public async Task<IReadOnlyList<WirelessTaskRecord>> ListAsync(Guid? tankId=null,bool activeOnly=false,CancellationToken ct=default)
    {await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM wireless_tasks WHERE ($tank IS NULL OR TankId=$tank)"+(activeOnly?" AND HoldsTank=1":"")+" ORDER BY AtUtcMs DESC,Id LIMIT 1000";cmd.Parameters.AddWithValue("$tank",(object?)tankId?.ToString()??DBNull.Value);List<WirelessTaskRecord> rows=[];await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<WirelessTaskRecord>(reader.GetString(0),Json)!);return rows;}
    public async Task RecoverAsync(DateTimeOffset now,CancellationToken ct=default)
    {
        foreach(var r in await ListAsync(activeOnly:true,ct:ct))
        {
            if(r.Stage==WirelessStage.RequiresReview)continue;
            await SaveAsync(r with{Version=r.Version+1,Stage=r.WriteIntentRecorded?WirelessStage.RequiresReview:WirelessStage.Failed,StageStartedAtUtc=now,FinishedAtUtc=now,DisplayDueAtUtc=null,AverageTemperature=Measurement.Missing("程序退出前任务未完成"),Message=r.WriteIntentRecorded?"原测温任务在程序退出前已进入写入阶段，需人工核查；未自动续跑或重发动作":"原任务尚未发送动作，已结束；未自动重放"},ct);
        }
    }
    public async Task<IReadOnlyList<WirelessTaskRecord>> LastSuccessfulAsync(CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM (SELECT Payload,row_number() OVER(PARTITION BY TankId ORDER BY AtUtcMs DESC,Id) AS n FROM wireless_tasks WHERE Stage='Completed' AND json_extract(Payload,'$.hasSuccessfulResult')=1) WHERE n=1";
        List<WirelessTaskRecord> rows=[];await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<WirelessTaskRecord>(reader.GetString(0),Json)!);return rows;
    }
    public async Task ReviewAsync(Guid id,AuditActor actor,string reason,DateTimeOffset now,CancellationToken ct=default)
    {
        actor.Require(AppPermission.ControlReview);if(string.IsNullOrWhiteSpace(reason)||reason.Length>1000)throw new ArgumentException("请填写现场核查结论，最多1000字");var r=await GetAsync(id,ct)??throw new ArgumentException("测温任务不存在");
        if(r.Stage!=WirelessStage.RequiresReview)throw new InvalidOperationException("本任务不属于待人工核查状态");await SaveAsync(r with{Version=r.Version+1,Stage=WirelessStage.Reviewed,StageStartedAtUtc=now,ReviewerId=actor.AccountId,ReviewerName=actor.DisplayName,ReviewReason=reason.Trim(),Message="人工核查已记录；未自动停止、恢复或重放任务"},ct);
    }
}
