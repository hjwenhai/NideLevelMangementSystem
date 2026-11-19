using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;
public sealed class SqliteControlStore(string databasePath):IControlStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,ForeignKeys=true,DefaultTimeout=10}.ToString());
        try{await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous=FULL";await cmd.ExecuteNonQueryAsync(ct);return c;}catch{await c.DisposeAsync();throw;}
    }
    private static async Task Save(SqliteConnection c,SqliteTransaction tx,ControlRecord r,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO control_records(Id,TankId,Cycle,State,AtUtcMs,ConfigurationRevision,Payload) VALUES($id,$tank,$cycle,$state,$at,$revision,$payload) ON CONFLICT(Id) DO UPDATE SET State=excluded.State,Payload=excluded.Payload";
        cmd.Parameters.AddWithValue("$id",r.Id.ToString());cmd.Parameters.AddWithValue("$tank",r.Target.Tank.Id.ToString());cmd.Parameters.AddWithValue("$cycle",r.Cycle.ToString());cmd.Parameters.AddWithValue("$state",r.State.ToString());cmd.Parameters.AddWithValue("$at",r.CreatedAtUtc.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$revision",r.Target.ConfigurationRevision);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(r,Json));await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task Audit(SqliteConnection c,SqliteTransaction tx,ControlRecord r,CancellationToken ct)
    {
        var review=r.State==ControlState.Reviewed;await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,TankId,TankCode,Content,Result,Details) VALUES($at,$category,$actor,$name,$tank,$code,$text,$result,$details)";
        cmd.Parameters.AddWithValue("$at",(r.ReviewedAtUtc??r.FinishedAtUtc??r.WriteIntentAtUtc??r.CreatedAtUtc).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$category",r.State==ControlState.Unknown?"任务错误":"操作日志");cmd.Parameters.AddWithValue("$actor",review?r.ReviewerId!:r.ActorId);cmd.Parameters.AddWithValue("$name",review?r.ReviewerName!:r.ActorName);cmd.Parameters.AddWithValue("$tank",r.Target.Tank.Id.ToString());cmd.Parameters.AddWithValue("$code",r.Target.Tank.Code);cmd.Parameters.AddWithValue("$text",(review?"人工核查 ":r.IsAutomaticReset?"系统自动复位网关结果 ":r.IsReset?"复位网关结果 ":"仪表操作 ")+(r.IsReset?(r.Cycle==ControlCycle.Normal?"正常动作结果通道":"独立停止结果通道"):CommandEncoding.Name(r.Command!.Value)));cmd.Parameters.AddWithValue("$result",r.Result);cmd.Parameters.AddWithValue("$details",JsonSerializer.Serialize(new{EventCode="control."+r.State,ControlId=r.Id,r.Target,r.Command,r.Cycle,r.State,r.WriteIntentAtUtc,r.ReviewReason,r.IsReset,r.IsAutomaticReset,r.GatewayResultSnapshot,r.GatewayResetExpectation,r.RelatedControlId,r.IsRecovery,r.FirstDispatchAtUtc,r.TargetLevelMm,r.AdjustmentReason,r.LevelAdjustment},Json));await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task CreateAsync(ControlRecord record,CancellationToken ct=default)
    {
        if(record.State is not(ControlState.Prepared or ControlState.Queued)||record.Id==Guid.Empty||record.Target.AcquisitionSessionId==Guid.Empty||(!record.Command.HasValue&&!record.IsReset)||record.Command.HasValue&&!Enum.IsDefined(record.Command.Value)||record.IsReset&&!record.Target.Channel.IsGateway)throw new ArgumentException("控制请求无效");
        if(record.Command==InstrumentCommand.RemoteLevel&&!record.IsReset){if(!record.TargetLevelMm.HasValue||string.IsNullOrWhiteSpace(record.AdjustmentReason)||record.AdjustmentReason.Length>1000||record.Target.Channel.Protocol==AcquisitionProtocol.IfxL25)throw new ArgumentException("液位修改参数或协议无效");RemoteLevelRules.ToRaw(record.TargetLevelMm.Value);}
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Payload FROM control_records WHERE TankId=$tank AND Cycle=$cycle AND State IN ('Prepared','Queued','Checking','WriteIntent','Submitted','Unknown')";
        cmd.Parameters.AddWithValue("$tank",record.Target.Tank.Id.ToString());cmd.Parameters.AddWithValue("$cycle",record.Cycle.ToString());if(await cmd.ExecuteScalarAsync(ct) is string blockerPayload)
        {
            var blocker=JsonSerializer.Deserialize<ControlRecord>(blockerPayload,Json)!;
            var action=blocker.IsReset?"结果复位":blocker.Command.HasValue?CommandEncoding.Name(blocker.Command.Value):"原命令未记录";
            var cycle=blocker.Cycle==ControlCycle.Normal?"正常动作通道":"独立停止通道";
            var state=blocker.State==ControlState.Unknown?"结果未知，需核查":"执行中或收尾中";
            throw new InvalidOperationException($"{blocker.Target.Tank.Code} {cycle}被记录 {blocker.Id} 拦截。{blocker.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}，{action}，{state}。请查看这条记录");
        }
        if(record.IsAutomaticReset)
        {
            if(!record.IsReset||!record.RelatedControlId.HasValue||record.GatewayResetExpectation is null||record.GatewayResultSnapshot is not null)throw new ArgumentException("自动复位缺少本次原操作及周期依据");
            cmd.CommandText="SELECT Payload FROM control_records WHERE Id=$parent";cmd.Parameters.AddWithValue("$parent",record.RelatedControlId.Value.ToString());
            var payload=await cmd.ExecuteScalarAsync(ct) as string;var parent=payload is null?null:JsonSerializer.Deserialize<ControlRecord>(payload,Json);
            if(parent is null||parent.IsReset||parent.State is not(ControlState.ReadbackConfirmed or ControlState.Rejected)||parent.GatewayResultSnapshot is null||
                parent.GatewayResultSnapshot!=record.GatewayResetExpectation||parent.Command!=record.Command||parent.Cycle!=record.Cycle||parent.ActorId!=record.ActorId||
                JsonSerializer.Serialize(parent.Target,Json)!=JsonSerializer.Serialize(record.Target,Json))throw new InvalidOperationException("自动复位只能引用已保存且明确结束的本次原操作，不得接管外部或未知结果");
            var state=record.GatewayResetExpectation.Channel(record.Cycle).State;
            if(state is not(3 or 4))throw new InvalidOperationException("自动复位仅允许本次已确认或明确失败结果");
            if(parent.State==ControlState.ReadbackConfirmed&&state!=3||parent.State==ControlState.Rejected&&state!=4)throw new InvalidOperationException("原操作结论与网关终态不一致，不能自动复位");
        }
        cmd.CommandText="SELECT count(*) FROM control_records WHERE Id=$id";cmd.Parameters.AddWithValue("$id",record.Id.ToString());if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0)throw new ArgumentException("控制请求编号已存在");
        await Save(c,tx,record,ct);await Audit(c,tx,record,ct);await tx.CommitAsync(ct);
    }
    public async Task UpdateAsync(ControlRecord record,CancellationToken ct=default)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Payload FROM control_records WHERE Id=$id";cmd.Parameters.AddWithValue("$id",record.Id.ToString());
        if(await cmd.ExecuteScalarAsync(ct) is not string payload)throw new ArgumentException("控制记录不存在");var before=JsonSerializer.Deserialize<ControlRecord>(payload,Json)!;
        if(record.LevelAdjustment is not null&&record.LevelAdjustment.TargetLevelMm!=record.TargetLevelMm)throw new InvalidOperationException("校准证据与原目标不一致");
        if(before.LevelAdjustment is not null&&record.LevelAdjustment is null)throw new InvalidOperationException("不能清除原校准证据");
        if(before.LevelAdjustment?.NewOffsetMm is not null&&(record.LevelAdjustment?.CurrentLevelMm!=before.LevelAdjustment.CurrentLevelMm||record.LevelAdjustment?.PreviousOffsetMm!=before.LevelAdjustment.PreviousOffsetMm||record.LevelAdjustment?.NewOffsetMm!=before.LevelAdjustment.NewOffsetMm))throw new InvalidOperationException("不能覆盖写入前校准计算依据");
        if(record.TargetLevelMm!=before.TargetLevelMm||record.AdjustmentReason!=before.AdjustmentReason)throw new InvalidOperationException("不能改变原目标液位及修改原因");
        if(record.GatewayResetExpectation!=before.GatewayResetExpectation)throw new InvalidOperationException("不能改变复位前确认的网关周期");
        if(record.IsAutomaticReset!=before.IsAutomaticReset||before.GatewayResultSnapshot is not null&&record.GatewayResultSnapshot!=before.GatewayResultSnapshot)throw new InvalidOperationException("不能改变自动复位标记或原结果周期依据");
        if(record.GatewayResultSnapshot is not null&&(record.IsReset||!record.Target.Channel.IsGateway||record.State is not(ControlState.ReadbackConfirmed or ControlState.Rejected)))throw new InvalidOperationException("仅明确结束的网关原操作可以保存自动收尾依据");
        if(record.IsRecovery!=before.IsRecovery||before.FirstDispatchAtUtc.HasValue&&before.FirstDispatchAtUtc!=record.FirstDispatchAtUtc||record.IsReset!=before.IsReset||record.RelatedControlId!=before.RelatedControlId)throw new InvalidOperationException("不能改变原复位对象");
        if(record.ActorId!=before.ActorId||record.ActorName!=before.ActorName||record.CreatedAtUtc!=before.CreatedAtUtc||record.MeasurementTaskId!=before.MeasurementTaskId||before.WriteIntentAtUtc.HasValue&&record.WriteIntentAtUtc!=before.WriteIntentAtUtc)throw new InvalidOperationException("不能覆盖原操作人、时间或写入证据");
        var allowed=before.State==record.State||before.State switch{
            ControlState.Prepared=>record.State is ControlState.Queued or ControlState.Checking or ControlState.Rejected,
            ControlState.Queued=>record.State is ControlState.Checking or ControlState.Rejected,
            ControlState.Checking=>record.State is ControlState.WriteIntent or ControlState.Rejected ||record.IsReset&&record.State==ControlState.ReadbackConfirmed&&record.WriteIntentAtUtc is null,
            ControlState.WriteIntent=>record.State is ControlState.Submitted or ControlState.Rejected or ControlState.Unknown,
            ControlState.Submitted=>record.State is ControlState.ReadbackConfirmed or ControlState.Rejected or ControlState.Unknown,
            _=>false};
        if(!allowed)throw new InvalidOperationException("控制状态不能倒退或绕过写入前记录");
        if(record.State is ControlState.WriteIntent or ControlState.Submitted&&record.WriteIntentAtUtc is null)throw new InvalidOperationException("缺少写入前记录时间");
        if(before.State is ControlState.Reviewed or ControlState.ReadbackConfirmed or ControlState.Rejected)throw new InvalidOperationException("操作已结束，不能覆盖原结果");
        if(record.Target!=before.Target&&JsonSerializer.Serialize(record.Target,Json)!=JsonSerializer.Serialize(before.Target,Json)||record.Command!=before.Command||record.Cycle!=before.Cycle)throw new InvalidOperationException("不能改变原控制对象或动作");
        await Save(c,tx,record,ct);await Audit(c,tx,record,ct);await tx.CommitAsync(ct);
    }
    public async Task<ControlRecord?> GetAsync(Guid id,CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM control_records WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());return await cmd.ExecuteScalarAsync(ct) is string payload?JsonSerializer.Deserialize<ControlRecord>(payload,Json):null;
    }
    public async Task<IReadOnlyList<ControlRecord>> ListAsync(Guid? tankId=null,bool unresolvedOnly=false,CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM control_records WHERE ($tank IS NULL OR TankId=$tank)"+(unresolvedOnly?" AND State IN ('Prepared','Queued','Checking','WriteIntent','Submitted','Unknown')":"")+" ORDER BY AtUtcMs DESC,Id LIMIT 1000";cmd.Parameters.AddWithValue("$tank",(object?)tankId?.ToString()??DBNull.Value);List<ControlRecord> rows=[];await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<ControlRecord>(reader.GetString(0),Json)!);return rows;
    }
    public async Task RecoverAsync(DateTimeOffset now,CancellationToken ct=default)
    {
        foreach(var record in await ListAsync(unresolvedOnly:true,ct:ct))
        {
            if(record.State==ControlState.Unknown)continue;
            var uncertain=record.WriteIntentAtUtc.HasValue||record.State is ControlState.WriteIntent or ControlState.Submitted or ControlState.Unknown;
            await UpdateAsync(record with{State=uncertain?ControlState.Unknown:ControlState.Rejected,FinishedAtUtc=now,Result=uncertain?"服务退出前已进入写入阶段，结果未知，需人工核查；未重发":"服务退出前尚未发送，已终止；未重放"},ct);
        }
    }
    public async Task ReviewAsync(Guid id,AuditActor reviewer,string reason,DateTimeOffset now,CancellationToken ct=default)
    {
        reviewer.Require(AppPermission.ControlReview);if(string.IsNullOrWhiteSpace(reason)||reason.Length>1000)throw new ArgumentException("请填写现场核查结论，最多1000字");
        await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Payload FROM control_records WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        if(await cmd.ExecuteScalarAsync(ct) is not string payload)throw new ArgumentException("控制记录不存在");var record=JsonSerializer.Deserialize<ControlRecord>(payload,Json)!;if(record.State!=ControlState.Unknown)throw new InvalidOperationException("仅可人工核查结果未知的记录");
        record=record with{State=ControlState.Reviewed,ReviewReason=reason.Trim(),ReviewerId=reviewer.AccountId,ReviewerName=reviewer.DisplayName,ReviewedAtUtc=now,Result="人工核查已记录；不停止、复位或重发动作"};await Save(c,tx,record,ct);await Audit(c,tx,record,ct);await tx.CommitAsync(ct);
    }
}
