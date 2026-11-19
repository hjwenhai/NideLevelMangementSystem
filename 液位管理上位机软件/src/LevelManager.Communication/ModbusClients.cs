using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Communication;

public sealed class ModbusDeviceException(byte function, byte code)
    : IOException($"Modbus功能{function:X2}异常码{code:X2}")
{
    public byte Function { get; } = function;
    public byte Code { get; } = code;
    public bool IsExplicitRejection => Code is 1 or 2 or 3 or 6;
    public string RejectionDescription => Code switch
    {
        1 => "不支持该功能",
        2 => "地址或接口写入许可不符合要求",
        3 => "写入值、允许动作或硬件写入设置不符合要求",
        6 => "设备忙或结果尚未复位",
        _ => "设备返回异常"
    };
}

public interface IRegisterClient : IReadOnlyRegisterClient
{
    ValueTask<ushort[]> ReadHoldingRegistersAsync(byte unit, ushort start, ushort count, CancellationToken ct);
    ValueTask WriteSingleRegisterAsync(byte unit, ushort start, ushort value, CancellationToken ct);
    ValueTask WriteMultipleRegistersAsync(byte unit, ushort start, ReadOnlyMemory<ushort> values, CancellationToken ct);
    ValueTask WriteCoilAsync(byte unit, ushort start, bool value, CancellationToken ct);
    ValueTask<bool[]> ReadCoilsAsync(byte unit, ushort start, ushort count, CancellationToken ct);
    ValueTask<bool[]> ReadDiscreteInputsAsync(byte unit, ushort start, ushort count, CancellationToken ct);
}

public abstract class ModbusRegisterClient : IRegisterClient
{
    protected abstract ValueTask<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken ct);
    public abstract ValueTask DisposeAsync();
    private static byte[] ReadPdu(byte fc, ushort start, ushort count, int maximum = 125)
    {
        if (count < 1 || count > maximum || (uint)start + count > 65536) throw new ArgumentOutOfRangeException(nameof(count));
        var pdu = new byte[5]; pdu[0] = fc;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start); BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        return pdu;
    }
    private static void Check(byte[] pdu, byte function)
    {
        if (pdu.Length == 2 && pdu[0] == (function | 0x80)) throw new ModbusDeviceException(function, pdu[1]);
        if (pdu.Length < 2 || pdu[0] != function) throw new InvalidDataException("Modbus功能码或响应长度不符");
    }
    private async ValueTask<ushort[]> Read(byte unit, byte fc, ushort start, ushort count, CancellationToken ct)
    {
        var response = await ExchangeAsync(unit, ReadPdu(fc, start, count), ct); Check(response, fc);
        if (response.Length != 2 + count * 2 || response[1] != count * 2) throw new InvalidDataException("Modbus寄存器数量不符");
        var words = new ushort[count];
        for (var i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2));
        return words;
    }
    public ValueTask<ushort[]> ReadInputRegistersAsync(byte unit, ushort start, ushort count, CancellationToken ct) => Read(unit, 4, start, count, ct);
    public ValueTask<ushort[]> ReadHoldingRegistersAsync(byte unit, ushort start, ushort count, CancellationToken ct) => Read(unit, 3, start, count, ct);
    public async ValueTask WriteSingleRegisterAsync(byte unit, ushort start, ushort value, CancellationToken ct)
    {
        var request = ReadPdu(6, start, 1); BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), value);
        var reply = await ExchangeAsync(unit, request, ct); Check(reply, 6);
        if (!reply.AsSpan().SequenceEqual(request)) throw new InvalidDataException("写入回显不符，结果需要核查");
    }
    public async ValueTask WriteMultipleRegistersAsync(byte unit, ushort start, ReadOnlyMemory<ushort> values, CancellationToken ct)
    {
        var header = ReadPdu(16, start, checked((ushort)values.Length), 123);
        var request = new byte[6 + values.Length * 2]; header.CopyTo(request, 0); request[5] = checked((byte)(values.Length * 2));
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6 + i * 2), values.Span[i]);
        var reply = await ExchangeAsync(unit, request, ct); Check(reply, 16);
        if (!reply.AsSpan().SequenceEqual(header)) throw new InvalidDataException("写入确认不符，结果需要核查");
    }
    public async ValueTask WriteCoilAsync(byte unit, ushort start, bool value, CancellationToken ct)
    {
        var request = ReadPdu(5, start, 1); BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), value ? (ushort)0xff00 : (ushort)0);
        var reply = await ExchangeAsync(unit, request, ct); Check(reply, 5);
        if (!reply.AsSpan().SequenceEqual(request)) throw new InvalidDataException("线圈写入确认不符，结果需要核查");
    }
    private async ValueTask<bool[]> ReadBits(byte unit,byte function,ushort start,ushort count,CancellationToken ct)
    {
        var reply=await ExchangeAsync(unit,ReadPdu(function,start,count,2000),ct);Check(reply,function);var bytes=(count+7)/8;
        if(reply.Length!=bytes+2||reply[1]!=bytes)throw new InvalidDataException("输入位数量不符");
        return Enumerable.Range(0,count).Select(i=>(reply[2+i/8]&(1<<(i%8)))!=0).ToArray();
    }
    public ValueTask<bool[]> ReadCoilsAsync(byte unit,ushort start,ushort count,CancellationToken ct)=>ReadBits(unit,1,start,count,ct);
    public ValueTask<bool[]> ReadDiscreteInputsAsync(byte unit,ushort start,ushort count,CancellationToken ct)=>ReadBits(unit,2,start,count,ct);

}

public sealed class ModbusTcpRegisterClient(string host, int port, int timeoutMs) : ModbusRegisterClient
{
    private readonly SemaphoreSlim mutex = new(1);
    private TcpClient? client;
    private ushort transaction;
    private bool disposed;
    protected override async ValueTask<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken ct)
    {
        if (unit is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(unit));
        await mutex.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(timeoutMs);
            try
            {
                if (client is null) { client = new TcpClient { NoDelay = true }; await client.ConnectAsync(IPAddress.Parse(host), port, timeout.Token); }
                var stream = client.GetStream(); var id = unchecked(++transaction); var request = new byte[7 + pdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(request, id); BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), checked((ushort)(pdu.Length + 1)));
                request[6] = unit; pdu.CopyTo(request, 7); await stream.WriteAsync(request, timeout.Token);
                var header = new byte[7]; await stream.ReadExactlyAsync(header, timeout.Token);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (BinaryPrimitives.ReadUInt16BigEndian(header) != id || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 || header[6] != unit || length is < 2 or > 254)
                    throw new InvalidDataException("TCP事务号、协议号、站号或长度不符");
                var reply = new byte[length - 1]; await stream.ReadExactlyAsync(reply, timeout.Token);
                return reply;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Drop(); throw new TimeoutException("网关TCP读取或写入超时，写入结果不得自动重发"); }
            catch { Drop(); throw; }
        }
        finally { mutex.Release(); }
    }
    private void Drop() { client?.Dispose(); client = null; }
    public override async ValueTask DisposeAsync()
    {
        await mutex.WaitAsync(); try { disposed = true; Drop(); } finally { mutex.Release(); }
    }
}

// Synchronous bounded reads avoid relying on SerialPort.BaseStream's incomplete
// cancellation behavior. One owner and one request at a time for each physical port.
public sealed class ModbusSerialRegisterClient : ModbusRegisterClient
{
    private static readonly ConcurrentDictionary<string, byte> Owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly SerialPort serial;
    private readonly string key;
    private readonly int timeoutMs, silenceMs;
    private readonly SemaphoreSlim mutex = new(1);
    private long quarantineUntil;
    private bool disposed;
    public ModbusSerialRegisterClient(ChannelDefinition channel)
    {
        key = ChannelDefinition.NormalizePort(channel.PortName); timeoutMs = channel.ReadTimeoutMs;
        silenceMs = Math.Max(4, (int)Math.Ceiling(3.5 * 11 * 1000 / channel.BaudRate));
        serial = new SerialPort(key, channel.BaudRate, channel.Parity switch { SerialParity.Even => Parity.Even, SerialParity.Odd => Parity.Odd, _ => Parity.None }, channel.DataBits, channel.StopBits == 2 ? StopBits.Two : StopBits.One)
        { ReadTimeout = 50, WriteTimeout = timeoutMs, Handshake = Handshake.None, DtrEnable = false, RtsEnable = false };
        if (!Owners.TryAdd(key, 0)) { serial.Dispose(); throw new IOException("串口已有采集通道占用"); }
        try { serial.Open(); } catch { serial.Dispose(); Owners.TryRemove(key, out _); throw; }
    }
    protected override async ValueTask<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken ct)
    {
        if (unit is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(unit));
        await mutex.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var remaining = quarantineUntil - Environment.TickCount64;
            if (remaining > 0) await Task.Delay(checked((int)remaining), ct);
            await Task.Delay(silenceMs, ct);
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested(); serial.DiscardInBuffer();
                var request = new byte[pdu.Length + 3]; request[0] = unit; pdu.CopyTo(request, 1);
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(request.Length - 2), ModbusReadCodec.Crc16(request.AsSpan(0, request.Length - 2)));
                var watch = Stopwatch.StartNew();
                try
                {
                    serial.Write(request, 0, request.Length);
                    byte ReadByte()
                    {
                        while (watch.ElapsedMilliseconds < timeoutMs)
                        {
                            ct.ThrowIfCancellationRequested();
                            try { return checked((byte)serial.ReadByte()); } catch (TimeoutException) { }
                        }
                        throw new TimeoutException("串口响应超时，当前事务未确认");
                    }
                    var frame = new List<byte> { ReadByte(), ReadByte(), ReadByte() };
                    var length = (frame[1] & 0x80) != 0 ? 5 : pdu[0] is 1 or 2 or 3 or 4 ? frame[2] + 5 : 8;
                    if (length is < 5 or > 256) throw new InvalidDataException("RTU响应长度无效");
                    while (frame.Count < length) frame.Add(ReadByte());
                    var bytes = frame.ToArray();
                    if (bytes[0] != unit || ModbusReadCodec.Crc16(bytes.AsSpan(0, bytes.Length - 2)) != BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2)))
                        throw new InvalidDataException("RTU响应站号或CRC不符");
                    return bytes[1..^2];
                }
                catch
                {
                    // RTU has no transaction identifier. Drain and quarantine after failure;
                    // writes are never retried here. Unbounded device delays need field checks.
                    quarantineUntil = Environment.TickCount64 + timeoutMs * 2L + silenceMs;
                    try { serial.DiscardInBuffer(); } catch (IOException) { }
                    throw;
                }
            }, ct);
        }
        finally { mutex.Release(); }
    }
    public override async ValueTask DisposeAsync()
    {
        await mutex.WaitAsync();
        try { if (!disposed) { disposed = true; serial.Dispose(); Owners.TryRemove(key, out _); } }
        finally { mutex.Release(); }
    }
}

public interface IRegisterClientFactory
{
    ValueTask<IRegisterClient> CreateAsync(ChannelDefinition channel, CancellationToken ct);
}
public sealed class RegisterClientFactory : IRegisterClientFactory
{
    public async ValueTask<IRegisterClient> CreateAsync(ChannelDefinition channel, CancellationToken ct)
        => channel.Protocol == AcquisitionProtocol.GatewayTcp
            ? new ModbusTcpRegisterClient(channel.Host, channel.TcpPort, channel.ReadTimeoutMs)
            : await Task.Run<IRegisterClient>(() => new ModbusSerialRegisterClient(channel), ct);
}
