using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Application;

public interface ICapacityTableStore
{
    Task<IReadOnlyList<CapacityTableSummary>> ListTablesAsync(CancellationToken ct = default);
    Task<CapacityTable?> GetTableAsync(Guid id, long? revision = null, CancellationToken ct = default);
    Task<CapacityTable> SaveTableAsync(CapacityTable table, AuditActor actor, CancellationToken ct = default);
    Task DeleteTableAsync(Guid id,long revision,AuditActor actor,CancellationToken ct=default);
    Task<long> GetCalculationRevisionAsync(string payload, CancellationToken ct = default);
}
public sealed class MeteringService(ICapacityTableStore tables)
{
    public async Task<ReadingSnapshot> CalculateAsync(TankDefinition tank, ReadingSnapshot reading, CancellationToken ct)
    {
        var settings = tank.Metering;
        async Task<CapacityTable?> Table(Guid? id) => id.HasValue ? await tables.GetTableAsync(id.Value, ct: ct) : null;
        var main = await Table(settings.MainTableId); var pressure = await Table(settings.StaticPressureTableId); var small = await Table(settings.DecimalTableId);
        var result = MeteringCalculator.Calculate(settings, reading.LevelMm, reading.InstrumentDensityKgM3, main, pressure, small);
        var revision = 0L;
        if (settings.Enabled || tank.Pressure1.Enabled || tank.Pressure2.Enabled)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 2, tankId = tank.Id, settings, pressure1=tank.Pressure1, pressure2=tank.Pressure2,
                main = main is null ? null : new { main.Id, main.Revision },
                pressure = pressure is null ? null : new { pressure.Id, pressure.Revision },
                small = small is null ? null : new { small.Id, small.Revision },
                formula = "current=selected_base_method+static*selected_operating_density/reference;net=current-water;mass_t=net*selected_operating_density/1000"
            });
            revision = await tables.GetCalculationRevisionAsync(payload, ct);
        }
        return reading with { Pressure1KPa=PressureCalculator.Calculate(tank.Pressure1,reading.Analog1Ma),Pressure2KPa=PressureCalculator.Calculate(tank.Pressure2,reading.Analog2Ma),WaterVolumeM3=result.WaterVolumeM3,CurrentVolumeM3 = result.CurrentVolumeM3, NetVolumeM3 = result.NetVolumeM3, MassT = result.MassT, CalculationRevision = revision, CalculationNotes = result.Notes };
    }
}

