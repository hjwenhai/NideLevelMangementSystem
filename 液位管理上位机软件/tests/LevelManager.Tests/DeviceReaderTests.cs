using Naide.LevelManager.Communication;
using Naide.LevelManager.Domain;
using Xunit;

namespace Naide.LevelManager.Tests;

public sealed class RegisterFixture(Func<byte, ushort, ushort, ushort[]> read) : IReadOnlyRegisterClient
{
    public List<(byte Unit, ushort Start, ushort Count)> Requests { get; } = [];
    public ValueTask<ushort[]> ReadInputRegistersAsync(byte unit, ushort start, ushort count, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Requests.Add((unit, start, count)); return ValueTask.FromResult(read(unit, start, count)); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class DeviceReaderTests
{
    private static DeviceBinding Binding(AcquisitionProtocol p, int slot = 1, TemperatureMode t = TemperatureMode.Ats)
    {
        var c = new ChannelDefinition { Protocol = p, UnitId = 1 };
        return new(c, new TankDefinition { ChannelId = c.Id, Address = slot, TemperatureMode = t, AtsPointCount = 3 }, 1, Guid.NewGuid());
    }
    [Fact] public async Task DirectIr26IsDistinctFromPointI6AndAtsIncludesNormalZeroAboveLiquid()
    {
        var raw = new ushort[31]; raw[1] = 12345; raw[6] = 0; raw[7] = 1000; raw[8] = 2000;
        raw[22] = 0; raw[23] = 7; raw[24] = 2; raw[26] = 2700;
        await using var client = new RegisterFixture((_, start, _) => start == 0 ? raw : [8510, 1, 0]);
        var result = await new DeviceReader(AcquisitionProtocol.DirectRtu).ReadAsync(client, Binding(AcquisitionProtocol.DirectRtu), default);
        Assert.Equal(27, result.InstantTemperatureC.Value); Assert.Equal(10, result.AverageTemperatureC.Value);
        Assert.Equal(0, result.AtsPoints[0].TemperatureC.Value); Assert.Equal(ValueQuality.Fault, result.AtsPoints[1].TemperatureC.Quality);
        Assert.Equal(851, result.InstrumentDensityKgM3.Value); Assert.True(result.FreshPositionReadConfirmed);
    }
    [Theory]
    [InlineData(0x8000, 2700, 27)]
    [InlineData(0x2000, 2700, 27)]
    [InlineData(0x8060, 2700, null)]
    [InlineData(0x8000, 0x8000, null)]
    public async Task DirectLevelAndTemperaturesUseTheirOwnValidityWithInstrumentFault(int fault, int temperatureWord, int? expectedTemperature)
    {
        var raw = new ushort[31]; raw[1] = 100; raw[26] = (ushort)temperatureWord;
        raw[6] = 2500; raw[23] = 1;
        await using var client = new RegisterFixture((_, start, _) => start == 0 ? raw : [8510, 1, (ushort)fault]);
        var result = await new DeviceReader(AcquisitionProtocol.DirectRtu).ReadAsync(client, Binding(AcquisitionProtocol.DirectRtu), default);
        Assert.Equal((ushort)fault, result.InstrumentFault);
        Assert.Equal(10, result.LevelMm.Value); Assert.True(result.FreshPositionReadConfirmed);
        Assert.Equal((decimal?)expectedTemperature, result.InstantTemperatureC.Value);
        Assert.Equal(25, result.AverageTemperatureC.Value);
    }
    [Fact] public async Task DirectOutOfRangeLevelStillHasNoValue()
    {
        var raw = new ushort[31]; raw[0] = 16;
        await using var client = new RegisterFixture((_, start, _) => start == 0 ? raw : [8510, 1, 0x8000]);
        var result = await new DeviceReader(AcquisitionProtocol.DirectRtu).ReadAsync(client, Binding(AcquisitionProtocol.DirectRtu), default);
        Assert.Equal(ValueQuality.Fault, result.LevelMm.Quality);
        Assert.Null(result.LevelMm.Value); Assert.False(result.FreshPositionReadConfirmed);
    }
    [Fact] public async Task NoThermometerMasksTemperaturesEvenIfRegistersContainZero()
    {
        await using var client = new RegisterFixture((_, start, _) => start == 0 ? new ushort[31] : [0, 1, 0]);
        var result = await new DeviceReader(AcquisitionProtocol.DirectRtu).ReadAsync(client, Binding(AcquisitionProtocol.DirectRtu, t: TemperatureMode.None), default);
        Assert.Equal(ValueQuality.Unconfigured, result.InstantTemperatureC.Quality); Assert.Null(result.InstantTemperatureC.Value); Assert.Empty(result.AtsPoints);
    }
    [Fact] public async Task IfxSlot32UsesOneUnitAndCorrectOffsetsAndDensityUnit()
    {
        await using var client = new RegisterFixture((unit, start, count) =>
        {
            Assert.Equal(1, unit);
            return start switch { 465 => [0, 12345, 2500, 1], 477 => [0, 0], 473 => [851], _ => throw new InvalidDataException() };
        });
        var result = await new DeviceReader(AcquisitionProtocol.IfxL25).ReadAsync(client, Binding(AcquisitionProtocol.IfxL25, 32, TemperatureMode.Wireless), default);
        Assert.Equal(1234.5m, result.LevelMm.Value); Assert.Equal(851, result.InstrumentDensityKgM3.Value);
        Assert.False(result.FreshPositionReadConfirmed); Assert.Equal(3, client.Requests.Count);
    }
    [Fact] public async Task IfxMissingFieldStatusInvalidatesCachedValues()
    {
        await using var client = new RegisterFixture((_, start, _) => start switch { 0 => [0, 10000, 2500, 1], 12 => throw new IOException("现场状态读取失败"), 8 => [851], _ => throw new InvalidDataException() });
        var result = await new DeviceReader(AcquisitionProtocol.IfxL25).ReadAsync(client, Binding(AcquisitionProtocol.IfxL25, t: TemperatureMode.Wireless), default);
        Assert.Null(result.LevelMm.Value); Assert.Null(result.InstrumentDensityKgM3.Value); Assert.Contains("缓存", result.Diagnostic);
    }
    [Theory]
    [InlineData(0x1000, 0, 1000, 25, 24)]
    [InlineData(0x1802, 0, 1000, null, 24)]
    [InlineData(0x1000, 3, null, null, null)]
    public async Task IfxGaugeFaultDoesNotInvalidateLevelOrAtsButCommunicationFailureDoes(
        int fault, int communicationError, int? level, int? temperature, int? averageTemperature)
    {
        var points = new ushort[20]; points[0] = 2400; points[1] = 0x8000; points[2] = 0x8000;
        await using var client = new RegisterFixture((_, start, _) => start switch
        {
            0 => [0, 10000, 2500, 1], 12 => [(ushort)fault, (ushort)communicationError], 8 => [851], 500 => points,
            _ => throw new InvalidDataException()
        });
        var result = await new DeviceReader(AcquisitionProtocol.IfxL25).ReadAsync(client, Binding(AcquisitionProtocol.IfxL25), default);
        Assert.Equal((ushort)fault, result.InstrumentFault);
        Assert.Equal((decimal?)level, result.LevelMm.Value);
        Assert.Equal((decimal?)temperature, result.InstantTemperatureC.Value);
        Assert.Equal((decimal?)averageTemperature, result.AverageTemperatureC.Value);
        Assert.False(result.FreshPositionReadConfirmed);
    }
    [Fact] public async Task IfxPointNibbleErrorsAndDensitySentinelRemainDistinct()
    {
        var points = new ushort[20]; points[0] = 0; points[1] = 1000; points[2] = 2000; points[16] = 0x40;
        await using var client = new RegisterFixture((_, start, _) => start switch { 0 => [0, 10000, 2500, 1], 12 => [0, 0], 8 => [0x8000], 500 => points, _ => throw new InvalidDataException() });
        var result = await new DeviceReader(AcquisitionProtocol.IfxL25).ReadAsync(client, Binding(AcquisitionProtocol.IfxL25), default);
        Assert.Equal(10, result.AverageTemperatureC.Value); Assert.Equal(ValueQuality.Fault, result.AtsPoints[1].TemperatureC.Quality);
        Assert.Null(result.InstrumentDensityKgM3.Value);
    }
    private static (ushort[] Header, ushort[] Summary, ushort[] Detail) GatewayWords()
    {
        var h = new ushort[30]; h[2] = 3; h[3] = 64; h[9] = 99; h[27] = 16;
        var s = new ushort[24]; s[0] = 3 | 4 | 256; s[1] = 127; s[3] = 42; s[5] = 10000; s[6] = 2700; s[7] = 8510; s[9] = 1; s[21] = 1; s[16] = 0xffff; s[17] = 0xffff;
        var d = new ushort[144]; d[55] = 1; d[57] = 42; d[6] = 0; d[7] = 1000; d[8] = 2000; d[23] = 7;
        for (var i = 0; i < 4; i++) d[58 + i] = 0xffff;
        for (var i = 0; i < 6; i++) { var at = 64 + 12 * i; d[at] = 9; d[at + 3] = 11; d[at + 9] = 100; }
        return (h, s, d);
    }
    private static RegisterFixture GatewayClient(ushort[] header, ushort[] summary, ushort[] detail)
        => new((_, start, count) => start switch
        {
            0 => header.ToArray(), 6 => header[6..10], 1000 => summary.ToArray(),
            >= 3000 and < 3144 => detail.Skip(start - 3000).Take(count).ToArray(),
            _ => throw new InvalidDataException()
        });
    [Theory]
    [InlineData(AcquisitionProtocol.GatewayTcp, 0, 0)]
    [InlineData(AcquisitionProtocol.GatewayTcp, 3, 0)]
    [InlineData(AcquisitionProtocol.GatewayTcp, 4, 0)]
    [InlineData(AcquisitionProtocol.GatewayRtu, 9, 60000)]
    public async Task GatewayStatusUsesFastIr49To52GroupNotOptional16PointDensityGroup(AcquisitionProtocol protocol, int optionalState, int optionalAge)
    {
        var (h, s, d) = GatewayWords(); s[9] = 1;
        d[64 + 4 * 12] = (ushort)optionalState; d[64 + 4 * 12 + 9] = (ushort)optionalAge;
        await using var client = GatewayClient(h, s, d);
        var reading = await new DeviceReader(protocol).ReadAsync(client, Binding(protocol), default);
        Assert.Equal((ushort)1, reading.InstrumentStatus); Assert.Equal((ushort)0, reading.InstrumentFault);
        Assert.True(reading.LevelMm.IsValid);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(3, 0)] [InlineData(4, 0)] [InlineData(9, 60000)]
    public async Task GatewayMissingOrExpiredActualStatusGroupCannotBorrowFreshOptionalDensityGroup(int statusState, int statusAge)
    {
        var (h, s, d) = GatewayWords();
        d[64 + 2 * 12] = (ushort)statusState; d[64 + 2 * 12 + 9] = (ushort)statusAge;
        await using var client = GatewayClient(h, s, d);
        var reading = await new DeviceReader(AcquisitionProtocol.GatewayTcp).ReadAsync(client, Binding(AcquisitionProtocol.GatewayTcp), default);
        Assert.Null(reading.InstrumentStatus); Assert.Null(reading.InstrumentFault);
        Assert.True(reading.LevelMm.IsValid);
    }
    [Fact] public async Task GatewayFieldValidityOverridesUnrelatedStaleGroupAndAge()
    {
        var (h, s, d) = GatewayWords(); await using var client = GatewayClient(h, s, d);
        var result = await new DeviceReader(AcquisitionProtocol.GatewayTcp).ReadAsync(client, Binding(AcquisitionProtocol.GatewayTcp), default);
        Assert.Equal(1000, result.LevelMm.Value); Assert.Equal(27, result.InstantTemperatureC.Value); Assert.Equal(10, result.AverageTemperatureC.Value);
        Assert.Equal(11u, result.LevelSequence); Assert.Equal(99UL, result.GatewayBootId); Assert.True(result.FreshPositionReadConfirmed);
    }
    [Fact] public async Task GatewayPointFaultDoesNotRemoveOtherNormalAtsPoints()
    {
        var (h, s, d) = GatewayWords(); d[24] = 2; s[10] = 0x20;
        await using var client = GatewayClient(h, s, d);
        var result = await new DeviceReader(AcquisitionProtocol.GatewayTcp).ReadAsync(client, Binding(AcquisitionProtocol.GatewayTcp), default);
        Assert.Equal(10, result.AverageTemperatureC.Value); Assert.Null(result.InstantTemperatureC.Value);
    }
    [Theory]
    [InlineData(AcquisitionProtocol.GatewayTcp, 0x8000, 27)]
    [InlineData(AcquisitionProtocol.GatewayRtu, 0x8000, 27)]
    [InlineData(AcquisitionProtocol.GatewayTcp, 0x8060, null)]
    [InlineData(AcquisitionProtocol.GatewayRtu, 0x2000, 27)]
    public async Task GatewayReportedFaultPreservesIndependentlyValidLevelAndAts(AcquisitionProtocol protocol, int fault, int? expectedTemperature)
    {
        var (h, s, d) = GatewayWords(); s[10] = (ushort)fault;
        await using var client = GatewayClient(h, s, d);
        var result = await new DeviceReader(protocol).ReadAsync(client, Binding(protocol), default);
        Assert.Equal((ushort)fault, result.InstrumentFault);
        Assert.Equal(1000, result.LevelMm.Value); Assert.True(result.FreshPositionReadConfirmed);
        Assert.Equal((decimal?)expectedTemperature, result.InstantTemperatureC.Value);
        Assert.Equal(10, result.AverageTemperatureC.Value);
    }
    [Fact] public async Task GatewayReadErrorIsNotRenewedAsValidCachedLevel()
    {
        var (h, s, d) = GatewayWords(); d[64] |= 2; s[10] = 0x8000;
        await using var client = GatewayClient(h, s, d);
        var result = await new DeviceReader(AcquisitionProtocol.GatewayRtu).ReadAsync(client, Binding(AcquisitionProtocol.GatewayRtu), default);
        Assert.Null(result.LevelMm.Value); Assert.Equal(ValueQuality.Fault, result.LevelMm.Quality); Assert.False(result.FreshPositionReadConfirmed);
        Assert.Equal(27, result.InstantTemperatureC.Value);
    }
    [Fact] public async Task GatewayBindingChangesCannotMixDifferentSources()
    {
        var (h, s, d) = GatewayWords(); d[55] = 2;
        await using var client = GatewayClient(h, s, d);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new DeviceReader(AcquisitionProtocol.GatewayTcp).ReadAsync(client, Binding(AcquisitionProtocol.GatewayTcp), default));
    }
    [Fact] public async Task GatewaySimulationAndWrongMapAreRejected()
    {
        var (h, s, d) = GatewayWords(); h[19] = 1;
        await using var client = GatewayClient(h, s, d);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new DeviceReader(AcquisitionProtocol.GatewayTcp).ReadAsync(client, Binding(AcquisitionProtocol.GatewayTcp), default));
        h[19] = 0; h[2] = 2;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new DeviceReader(AcquisitionProtocol.GatewayTcp).ReadAsync(client, Binding(AcquisitionProtocol.GatewayTcp), default));
    }
}
