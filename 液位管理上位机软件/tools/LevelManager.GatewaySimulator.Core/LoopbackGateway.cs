using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Naide.LevelManager.Simulator;

public sealed class LoopbackGateway(GatewayModel model,byte unit=1):IAsyncDisposable
{
    private readonly ConcurrentDictionary<TcpClient,Task> clients=new();
    private readonly CancellationTokenSource lifetime=new();
    private TcpListener? listener;
    private Task? accept;
    private long requests,writes;
    public long Requests=>Interlocked.Read(ref requests);
    public long Writes=>Interlocked.Read(ref writes);
    public int Port=>((IPEndPoint)(listener?.LocalEndpoint??throw new InvalidOperationException("尚未启动"))).Port;
    public void Start(int port)
    {
        if(unit is <1 or >247)throw new ArgumentOutOfRangeException(nameof(unit));
        if(listener is not null)throw new InvalidOperationException("模拟器已启动");
        var candidate=new TcpListener(IPAddress.Loopback,port);candidate.Start();listener=candidate;accept=Accept(lifetime.Token);
    }
    private async Task Accept(CancellationToken ct)
    {
        try{while(!ct.IsCancellationRequested){var c=await listener!.AcceptTcpClientAsync(ct);if(clients.Count>=24){c.Dispose();continue;}var t=Serve(c,ct);clients[c]=t;_=t.ContinueWith(_=>{clients.TryRemove(c,out var ignored);c.Dispose();},TaskScheduler.Default);}}
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){ }
    }
    private async Task Serve(TcpClient client,CancellationToken stop)
    {
        try
        {
            using var stream=client.GetStream();var head=new byte[6];
            while(!stop.IsCancellationRequested)
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await stream.ReadExactlyAsync(head,deadline.Token);
                var length=BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));if(length is <2 or >254||head[2]!=0||head[3]!=0)return;
                var body=new byte[length];await stream.ReadExactlyAsync(body,deadline.Token);
                if(body[0]!=unit)return;
                var pdu=Handle(body.AsSpan(1));var response=new byte[7+pdu.Length];head.AsSpan(0,4).CopyTo(response);BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4),(ushort)(pdu.Length+1));response[6]=unit;pdu.CopyTo(response.AsSpan(7));
                await stream.WriteAsync(response,deadline.Token);
            }
        }
        catch(Exception e)when(e is IOException or OperationCanceledException or SocketException or ObjectDisposedException){ }
    }
    public byte[] Handle(ReadOnlySpan<byte> pdu)
    {
        if(pdu.IsEmpty)return [0x80,3];
        Interlocked.Increment(ref requests);var function=pdu[0];
        try
        {
            var payload=pdu.ToArray();
            ushort Word(int offset)=>BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset,2));
            if(function is 3 or 4)
            {
                if(pdu.Length!=5)throw new SimulatorProtocolException(3);
                var words=model.Read(Word(1),Word(3));var result=new byte[2+words.Length*2];result[0]=function;result[1]=(byte)(words.Length*2);for(var i=0;i<words.Length;i++)BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2+i*2),words[i]);return result;
            }
            if(function==6){if(pdu.Length!=5)throw new SimulatorProtocolException(3);model.Write(Word(1),Word(3));Interlocked.Increment(ref writes);return pdu.ToArray();}
            if(function==16)
            {
                if(pdu.Length<6)throw new SimulatorProtocolException(3);var count=Word(3);if(count!=2||pdu[5]!=4||pdu.Length!=10)throw new SimulatorProtocolException(3);
                model.Stage(Word(1),[Word(6),Word(8)]);Interlocked.Increment(ref writes);return pdu[..5].ToArray();
            }
            throw new SimulatorProtocolException(1);
        }
        catch(SimulatorProtocolException e){return [(byte)(function|128),e.Code];}
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();listener?.Stop();foreach(var c in clients.Keys)c.Dispose();if(accept is not null)await accept;
        await Task.WhenAll(clients.Values.ToArray());lifetime.Dispose();
    }
}
