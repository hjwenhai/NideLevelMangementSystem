using System.Text.Json;
using Naide.LevelManager.Application;

namespace Naide.LevelManager.Infrastructure;

public sealed partial class SqliteConfigurationStore : IEventWriter
{
    public async Task WriteEventAsync(EventWrite entry, CancellationToken ct = default)
    {
        if (entry.Category is not ("操作日志" or "系统事件" or "通讯异常" or "仪表诊断" or "任务错误") || string.IsNullOrWhiteSpace(entry.EventCode))
            throw new ArgumentException("事件分类或代码无效");
        await using var c = await OpenAsync(ct); await using var db = Context(c);
        db.Events.Add(new()
        {
            AtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Category = entry.Category,
            ActorId = entry.ActorId, ActorName = entry.ActorName, TankId = entry.TankId?.ToString(), TankCode = entry.TankCode,
            Content = entry.Content, Result = entry.Result,
            Details = JsonSerializer.Serialize(new { eventCode = entry.EventCode, parameters = entry.Parameters }, Json)
        });
        await db.SaveChangesAsync(ct);
    }
}
