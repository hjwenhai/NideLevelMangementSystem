using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Communication;

public sealed class DeviceReader(AcquisitionProtocol protocol, TimeProvider? timeProvider = null, bool controlRead = false) : IDeviceReader
{
    public AcquisitionProtocol Protocol => protocol;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static Measurement Unconfigured(string reason) => new(null, ValueQuality.Unconfigured, reason);
    private static Measurement Fault(string reason) => new(null, ValueQuality.Fault, reason);
    private static Measurement Temperature(ushort value) => value == 0x8000 ? Measurement.Missing("温度缺测") : Measurement.Good(ModbusReadCodec.TemperatureC(value));
    private static Measurement Level(ushort high, ushort low)
    {
        var value = ModbusReadCodec.LevelMm(high, low);
        return value is < -9999.9m or > 99999.9m ? Fault("液位超出协议范围") : Measurement.Good(value);
    }
    private static Measurement Mean(IReadOnlyList<TemperaturePoint> points)
    {
        var values = points.Where(p => p.TemperatureC.IsValid).Select(p => p.TemperatureC.Value!.Value).ToArray();
        return values.Length == 0 ? Measurement.Missing("没有正常温度点") : Measurement.Good(values.Average());
    }
    private static IReadOnlyList<TemperaturePoint> FwPoints(ushort[] raw, int count)
        => Enumerable.Range(0, count).Select(i => new TemperaturePoint(i + 1,
            (raw[23] & (1 << i)) == 0 ? Unconfigured("测点未使用") :
            ((raw[24] | raw[25]) & (1 << i)) != 0 ? Fault("测点故障或超量程") : Temperature(raw[6 + i]))).ToArray();
    private static ReadingSnapshot ApplyFwFaults(ReadingSnapshot result)
    {
        var fault = result.InstrumentFault ?? 0;
        if ((fault & 0x0060) != 0) result = result with { InstantTemperatureC = Fault("温度计断线或超量程") };
        if ((fault & 0x8000) != 0) return result with
        {
            InstrumentDensityKgM3 = Fault("仪表内部错误"),
            Analog1Ma = Fault("仪表内部错误"),
            Analog2Ma = Fault("仪表内部错误")
        };
        if ((fault & 0x0800) != 0) result = result with { InstrumentDensityKgM3 = Fault("密度测量异常") };
        return result;
    }
    private ReadingSnapshot Initial(DeviceBinding b) => new()
    {
        TankId = b.Tank.Id, ConfigurationRevision = b.ConfigurationRevision,
        AcquisitionSessionId = b.AcquisitionSessionId, ReceivedAtUtc = clock.GetUtcNow()
    };
    public async ValueTask<ReadingSnapshot> ReadAsync(IReadOnlyRegisterClient client, DeviceBinding binding, CancellationToken ct)
    {
        if (binding.Channel.Protocol != protocol) throw new ArgumentException("采集绑定与协议不匹配");
        var result = protocol switch
        {
            AcquisitionProtocol.DirectRtu => await Direct(client, binding, ct),
            AcquisitionProtocol.IfxL25 => await Ifx(client, binding, ct),
            _ => await Gateway(client, binding, ct)
        };
        result = result with { ReceivedAtUtc = clock.GetUtcNow() };
        return result.ForCurrentState(clock.GetUtcNow(), binding.AcquisitionSessionId, binding.ConfigurationRevision,
            binding.ExpirySeconds, true, binding.Tank.Enabled && binding.Channel.Enabled, binding.Tank.TemperatureMode);
    }
    private static async Task<ushort[]?> Optional(IReadOnlyRegisterClient client, byte unit, ushort start, ushort count, List<string> failures, CancellationToken ct)
    {
        try
        {
            var result = await client.ReadInputRegistersAsync(unit, start, count, ct);
            if (result.Length != count) throw new InvalidDataException("寄存器数量不符");
            return result;
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException)
        { failures.Add($"IR{start}:{error.Message}"); return null; }
    }
    private async Task<ReadingSnapshot> Direct(IReadOnlyRegisterClient client, DeviceBinding b, CancellationToken ct)
    {
        List<string> failures = []; var unit = checked((byte)b.Tank.Address);
        var raw = await Optional(client, unit, 0, 31, failures, ct);
        var densityStatus = await Optional(client, unit, 50, 3, failures, ct);
        var result = Initial(b);
        if (raw is not null)
        {
            var points = b.Tank.TemperatureMode == TemperatureMode.Ats ? FwPoints(raw, b.Tank.AtsPointCount) : [];
            result = result with
            {
                LevelMm = Level(raw[0], raw[1]), InstantTemperatureC = Temperature(raw[26]),
                Analog1Ma = raw[27] <= 30000 ? Measurement.Good(raw[27] / 1000m) : Fault("模拟量电流超量程"),
                Analog2Ma = raw[28] <= 30000 ? Measurement.Good(raw[28] / 1000m) : Fault("模拟量电流超量程"),
                AtsPoints = points, AverageTemperatureC = b.Tank.TemperatureMode == TemperatureMode.Ats ? Mean(points) : Unconfigured("等待无线测温任务结果"),
                FreshPositionReadConfirmed = true
            };
        }
        if (densityStatus is not null) result = result with
        {
            InstrumentDensityKgM3 = Measurement.Good(ModbusReadCodec.DirectOrGatewayDensityKgM3(densityStatus[0])),
            InstrumentStatus = densityStatus[1], InstrumentFault = densityStatus[2]
        };
        if (!controlRead) result = ApplyFwFaults(result);
        return result with
        {
            Diagnostic = string.Join("；", failures),
            FreshPositionReadConfirmed = result.FreshPositionReadConfirmed && result.LevelMm.IsValid
        };
    }
    private async Task<ReadingSnapshot> Ifx(IReadOnlyRegisterClient client, DeviceBinding b, CancellationToken ct)
    {
        if (b.Tank.Address is < 1 or > 32 || b.Channel.UnitId != 1) throw new ArgumentException("IFX站号或内部罐号无效");
        List<string> failures = []; var offset = (ushort)(15 * (b.Tank.Address - 1));
        var main = await Optional(client, 1, offset, 4, failures, ct);
        var errors = await Optional(client, 1, (ushort)(offset + 12), 2, failures, ct);
        var density = await Optional(client, 1, (ushort)(offset + 8), 1, failures, ct);
        var result = Initial(b);
        if (main is not null) result = result with { LevelMm = Level(main[0], main[1]), InstantTemperatureC = Temperature(main[2]), InstrumentStatus = main[3] };
        if (density is not null) result = result with { InstrumentDensityKgM3 = density[0] == 0x8000 ? Measurement.Missing("尚无密度测量结果") : unchecked((short)density[0]) < 0 ? Fault("IFX密度无效") : Measurement.Good(ModbusReadCodec.IfxDensityKgM3(density[0])) };
        if (b.Tank.TemperatureMode == TemperatureMode.Ats)
        {
            var pointsRaw = await Optional(client, 1, (ushort)(500 + 20 * (b.Tank.Address - 1)), 20, failures, ct);
            var points = Enumerable.Range(0, b.Tank.AtsPointCount).Select(i => new TemperaturePoint(i + 1,
                pointsRaw is null ? Measurement.Missing("多点温度未读到") : ((pointsRaw[16 + i / 4] >> ((i % 4) * 4)) & 6) != 0 ? Fault("测点断线或短路") : Temperature(pointsRaw[i]))).ToArray();
            result = result with { AtsPoints = points, AverageTemperatureC = Mean(points) };
        }
        if (errors is null || (errors[1] & 15) != 0)
        {
            var reason = errors is null ? "未确认IFX现场通讯状态，缓存数值无效" : "IFX现场通讯错误";
            var quality = errors is null ? ValueQuality.Fault : ValueQuality.Offline;
            var bad = new Measurement(null, quality, reason);
            result = result with { LevelMm = bad, InstantTemperatureC = bad, InstrumentDensityKgM3 = bad, AverageTemperatureC = bad, AtsPoints = result.AtsPoints.Select(p => p with { TemperatureC = bad }).ToArray(), Diagnostic = reason,
                InstrumentStatus = null };
        }
        else
        {
            if ((errors[0] & 0x0802) != 0) result = result with { InstantTemperatureC = Fault("IFX温度计未连接或超量程") };
            if (!controlRead && (errors[0] & 0x1000) != 0) result = result with { InstrumentDensityKgM3 = Fault("IFX仪表内部错误") };
        }
        // L25 does not expose a source update counter in this table. A cache read is
        // usable for monitoring with field status, but is not proof of a new position sample.
        return result with { InstrumentFault = errors?[0], Diagnostic = result.Diagnostic + string.Join("；", failures), FreshPositionReadConfirmed = false };
    }
    private static uint U32(ushort[] w, int i) => ((uint)w[i] << 16) | w[i + 1];
    private static ulong U64(ushort[] w, int i) => ((ulong)U32(w, i) << 32) | U32(w, i + 2);
    private async Task<ReadingSnapshot> Gateway(IReadOnlyRegisterClient client, DeviceBinding b, CancellationToken ct)
    {
        var unit = checked((byte)b.Channel.UnitId);
        var header = await client.ReadInputRegistersAsync(unit, 0, 30, ct);
        if (header.Length != 30 || header[2] != 3 || b.Tank.Address < 1 || b.Tank.Address > header[3] || header[3] > 64)
            throw new InvalidDataException("网关点表版本、容量或内部编号不匹配");
        if (header[19] != 0) throw new InvalidDataException("网关处于模拟模式，不能作为现场采集数据");
        var boot = U64(header, 6);
        var summaryBase = checked((ushort)(1000 + 24 * (b.Tank.Address - 1)));
        var detailBase = checked((ushort)(3000 + 144 * (b.Tank.Address - 1)));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = await client.ReadInputRegistersAsync(unit, summaryBase, 24, ct);
            ushort[] raw, metadata;
            if (b.Tank.TemperatureMode == TemperatureMode.Ats || b.Tank.Pressure1.Enabled || b.Tank.Pressure2.Enabled)
            {
                var first = await client.ReadInputRegistersAsync(unit, detailBase, 96, ct);
                var second = await client.ReadInputRegistersAsync(unit, (ushort)(detailBase + 96), 48, ct);
                raw = [.. first, .. second]; metadata = raw[54..144];
            }
            else { raw = []; metadata = await client.ReadInputRegistersAsync(unit, (ushort)(detailBase + 54), 90, ct); }
            var after = await client.ReadInputRegistersAsync(unit, summaryBase, 24, ct);
            if (before.Length != 24 || after.Length != 24 || metadata.Length != 90) throw new InvalidDataException("网关摘要长度不符");
            if (U32(before, 2) != U32(after, 2) || U32(metadata, 2) != U32(after, 2) || U32(metadata, 0) != U32(after, 20)) continue;
            var bootAfter = await client.ReadInputRegistersAsync(unit, 6, 4, ct);
            if (bootAfter.Length != 4 || U64(bootAfter, 0) != boot) throw new InvalidDataException("网关在读取期间重启，等待新会话读数");
            var q = after[0]; var valid = after[1];
            Measurement G(int fieldBit, int group, Func<Measurement> read)
            {
                if ((q & 16) != 0) return Unconfigured("网关槽位未安装或停用");
                if ((q & 3) != 3) return new(null, ValueQuality.Offline, "网关现场数据未就绪");
                var at = 10 + group * 12; var state = metadata[at];
                if ((state & 4) != 0) return Unconfigured("寄存器数据组不支持");
                if ((state & 1) == 0) return Measurement.Missing("数据组尚无读数");
                if ((state & 2) != 0) return Fault("网关数据组读取失败");
                if ((valid & (1 << fieldBit)) == 0 || U32(metadata, at + 8) > b.ExpirySeconds * 1000L) return new(null, ValueQuality.Expired, "网关字段无效或过期");
                return read();
            }
            var result = Initial(b) with
            {
                LevelMm = G(0, 0, () => Level(after[4], after[5])),
                InstantTemperatureC = G(1, 1, () => Temperature(after[6])),
                InstrumentDensityKgM3 = G(2, 2, () => Measurement.Good(ModbusReadCodec.DirectOrGatewayDensityKgM3(after[7]))),
                // IR51/52 belong to fast group 2 (IR49..52), not optional group 4 (IR33..48).
                InstrumentStatus = G(4,2,()=>Measurement.Good(after[9])).IsValid ? after[9] : null,
                InstrumentFault = G(5,2,()=>Measurement.Good(after[10])).IsValid ? after[10] : null,
                RawQuality = q, RawValidity = valid, DeviceDataAgeMs = U32(after, 16),
                GatewayBootId = boot, GatewayBindingVersion = U32(after, 20), LevelSequence = U32(metadata, 12),
                SourceTimeConfirmed = after[22] == 1 && (q & 128) == 0,
                FreshPositionReadConfirmed = true
            };
            var source = U64(after, 12);
            if (source > 0 && source <= 253402300799999UL && result.SourceTimeConfirmed) result = result with { SourceAtUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)source) };
            if (!controlRead) result = ApplyFwFaults(result);
            if (b.Tank.TemperatureMode == TemperatureMode.Ats)
            {
                var points = FwPoints(raw, b.Tank.AtsPointCount);
                var group = 64 + 3 * 12;
                var groupValid = (raw[group] & 7) == 1 && U32(raw, group + 8) <= b.ExpirySeconds * 1000L;
                bool Known(int address) => (raw[58 + address / 16] & (1 << (15 - address % 16))) != 0;
                points = points.Select(p => p with { TemperatureC = !groupValid || !Known(p.Index + 5) || !G(1, 1, () => Measurement.Good(0)).IsValid || !Known(23) || !Known(24) || !Known(25) ? Measurement.Missing("ATS数据组未确认有效或过期") : p.TemperatureC }).ToArray();
                if (!result.LevelMm.IsValid && (q & 3) != 3) points = points.Select(p => p with { TemperatureC = new(null, ValueQuality.Offline, "网关现场离线") }).ToArray();
                result = result with { AtsPoints = points, AverageTemperatureC = Mean(points) };
            }
            if(raw.Length==144&&(b.Tank.Pressure1.Enabled||b.Tank.Pressure2.Enabled))
            {
                var at=64+5*12;var good=(raw[at]&7)==1&&U32(raw,at+8)<=b.ExpirySeconds*1000L;
                Measurement Analog(int address)
                {
                    if((q&16)!=0)return Unconfigured("槽位未安装或停用");
                    if((q&3)!=3)return new(null,ValueQuality.Offline,"网关现场离线");
                    if(((result.InstrumentFault??0)&0x8000)!=0)return Fault("仪表内部错误");
                    if((raw[at]&4)!=0)return Unconfigured("网关模拟量组未启用");
                    if((raw[at]&2)!=0)return Fault("网关模拟量组读取失败");
                    if(!good||(raw[58+address/16]&(1<<(15-address%16)))==0)return new(null,ValueQuality.Expired,"网关模拟量未确认有效或过期");
                    return raw[address]>30000?Fault("输入电流超出设备范围"):Measurement.Good(raw[address]/1000m);
                }
                result=result with{Analog1Ma=Analog(27),Analog2Ma=Analog(28)};
            }
            return result with { FreshPositionReadConfirmed = result.FreshPositionReadConfirmed && result.LevelMm.IsValid };
        }
        throw new InvalidDataException("网关多帧数据更新或绑定变化，未组成一致快照");
    }
}
