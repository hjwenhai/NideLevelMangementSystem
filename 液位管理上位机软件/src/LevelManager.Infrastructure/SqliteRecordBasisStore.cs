using System.Text.Json;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Infrastructure;

public sealed partial class SqliteConfigurationStore : IRecordBasisStore
{
    public async Task<RecordBasis> GetRecordBasisAsync(Guid tankId,long configurationRevision,long calculationRevision,CancellationToken ct=default)
    {
        if(tankId==Guid.Empty||configurationRevision<0||calculationRevision<0)throw new ArgumentException("记录依据编号无效");
        await using var connection=await OpenAsync(ct);
        async Task<(string Payload,DateTimeOffset At)> Read(string sql,long revision)
        {
            await using var command=connection.CreateCommand();command.CommandText=sql;command.Parameters.AddWithValue("$revision",revision);
            await using var row=await command.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct))throw new ArgumentException("未找到指定版本的记录依据，不使用当前配置替代历史版本");
            return(row.GetString(0),DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(1)));
        }
        var configRow=await Read("SELECT Payload,AtUtcMs FROM configuration_versions WHERE Revision=$revision",configurationRevision);
        var config=JsonSerializer.Deserialize<SystemConfiguration>(configRow.Payload,Json)??throw new InvalidDataException("原配置记录无法读取");
        var tank=config.Tanks.FirstOrDefault(t=>t.Id==tankId)??throw new ArgumentException("该配置记录中没有此储罐");
        var channel=config.Channels.First(c=>c.Id==tank.ChannelId);
        if(calculationRevision==0)return new(configurationRevision,configRow.At,tank,channel,config.HistoryIntervalSeconds,config.ReadingExpirySeconds,0,null,null,null,null,[],"本条数据没有启用计量计算依据。");
        var calcRow=await Read("SELECT Payload,AtUtcMs FROM calculation_versions WHERE Id=$revision",calculationRevision);
        // MeteringService serializes its snapshot with default names and numeric enums.
        var calc=JsonSerializer.Deserialize<CalculationBasisPayload>(calcRow.Payload,new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new InvalidDataException("原计量依据无法读取");
        if(calc.TankId!=tankId)throw new ArgumentException("计量依据不属于所选储罐");
        List<RecordTableBasis> tables=[];
        foreach(var reference in new[]{calc.Main,calc.Pressure,calc.Small}.Where(r=>r is not null))
        {
            var table=await GetTableAsync(reference!.Id,reference.Revision,ct)??throw new InvalidDataException("原计量依据引用的罐容表版本缺失");
            tables.Add(new(table.Id,table.Revision,table.Number,table.Name,table.Kind,table.HeightUnit,table.VolumeUnit,table.Points.Count+table.DecimalRows.Count,table.ReferenceDensityKgM3));
        }
        return new(configurationRevision,configRow.At,tank,channel,config.HistoryIntervalSeconds,config.ReadingExpirySeconds,calculationRevision,calcRow.At,calc.Settings,calc.Pressure1,calc.Pressure2,tables,
            "修正后当前体积＝主表查得体积＋静压力表修正量×实际液体密度÷参考密度；净体积＝修正后当前体积－水体积；产品质量（t）＝净体积（m³）×实际液体密度（kg/m³）÷1000。基础体积按该版本的主表插值或主表下节点＋小数增量计算，两者不叠加；不足1mm按所选规则处理。密度取该版本配置的人工工况或仪表实测值，20℃标准密度独立保留。"+(calc.Settings.WaterHeightMm.HasValue?"水位为该版本人工填写值，水体积按基础罐容算法计算，不重复叠加整体静压力修正。":"此旧版本直接采用保存的人工水体积。")+"");
    }
    private sealed record TableVersion(Guid Id,long Revision);
    private sealed record CalculationBasisPayload(Guid TankId,MeteringSettings Settings,PressureCalibration Pressure1,PressureCalibration Pressure2,TableVersion? Main,TableVersion? Pressure,TableVersion? Small);
}
