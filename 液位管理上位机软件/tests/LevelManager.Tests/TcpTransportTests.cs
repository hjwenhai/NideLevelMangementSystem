using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Naide.LevelManager.Communication;
using Xunit;

namespace Naide.LevelManager.Tests;

public class TcpTransportTests
{
    private static async Task<byte[]> Request(NetworkStream stream)
    {
        var header = new byte[7]; await stream.ReadExactlyAsync(header);
        var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1]; await stream.ReadExactlyAsync(body);
        return [.. header, .. body];
    }
    private static byte[] Reply(byte[] request, params byte[] pdu)
    {
        var header = request[..7]; BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(pdu.Length + 1)); return [.. header, .. pdu];
    }
    [Fact] public async Task HandlesFragmentedTcpFramesAndBothRegisterFunctions()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            using var socket = await server.AcceptTcpClientAsync(); var stream = socket.GetStream();
            for (var i = 0; i < 2; i++)
            {
                var request = await Request(stream); Assert.Equal(i == 0 ? 4 : 3, request[7]);
                var response = Reply(request, request[7], 2, 0x12, 0x34);
                foreach (var b in response) { await stream.WriteAsync(new[] { b }); await Task.Delay(1); }
            }
        });
        await using var client = new ModbusTcpRegisterClient("127.0.0.1", port, 2000);
        Assert.Equal((ushort)0x1234, (await client.ReadInputRegistersAsync(1, 0, 1, default))[0]);
        Assert.Equal((ushort)0x1234, (await client.ReadHoldingRegistersAsync(1, 0, 1, default))[0]); await task;
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task RejectsWrongTransactionOrUnit(bool transaction)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var task = Task.Run(async () => { using var socket = await server.AcceptTcpClientAsync(); var request = await Request(socket.GetStream()); var response = Reply(request, 4, 2, 0, 99); if (transaction) response[1] ^= 1; else response[6] = 2; await socket.GetStream().WriteAsync(response); });
        await using var client = new ModbusTcpRegisterClient("127.0.0.1", port, 1000);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await client.ReadInputRegistersAsync(1, 0, 1, default)); await task;
    }
    [Fact] public async Task TimeoutClosesConnectionAndLateReplyCannotBecomeNextReading()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            using var first = await server.AcceptTcpClientAsync(); _ = await Request(first.GetStream());
            using var next = await server.AcceptTcpClientAsync(); var request = await Request(next.GetStream());
            await next.GetStream().WriteAsync(Reply(request, 4, 2, 0, 22));
            try { await first.GetStream().WriteAsync(new byte[20]); } catch (IOException) { }
        });
        await using var client = new ModbusTcpRegisterClient("127.0.0.1", port, 150);
        await Assert.ThrowsAsync<TimeoutException>(async () => await client.ReadInputRegistersAsync(1, 0, 1, default));
        Assert.Equal((ushort)22, (await client.ReadInputRegistersAsync(1, 0, 1, default))[0]); await task;
    }
    [Fact] public async Task ModbusExceptionsAreTypedAndWriteIsNeverAutomaticallyRetried()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port; var writes = 0;
        var task = Task.Run(async () =>
        {
            using var socket = await server.AcceptTcpClientAsync(); var stream = socket.GetStream();
            var read = await Request(stream); await stream.WriteAsync(Reply(read, 0x84, 2));
            var write = await Request(stream); Assert.Equal(6, write[7]); Interlocked.Increment(ref writes);
        });
        await using var client = new ModbusTcpRegisterClient("127.0.0.1", port, 1000);
        var error = await Assert.ThrowsAsync<ModbusDeviceException>(async () => await client.ReadInputRegistersAsync(1, 0, 1, default)); Assert.Equal(2, error.Code);
        await Assert.ThrowsAnyAsync<IOException>(async () => await client.WriteSingleRegisterAsync(1, 30000, 1, default));
        await task; Assert.Equal(1, writes);
    }
}
