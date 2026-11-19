using System.Collections.Concurrent;
namespace Naide.LevelManager.Communication;

public enum RegisterPriority { Background,Control,Stop }
public sealed class RegisterDispatchPreventedException(Exception cause):IOException("本次请求未进入物理写入："+cause.Message,cause);
public sealed class RegisterWriteGuardScope:IDisposable
{
    private static readonly AsyncLocal<Func<CancellationToken,Task>?> Value=new();
    private readonly Func<CancellationToken,Task>? previous;
    public static Func<CancellationToken,Task>? Current=>Value.Value;
    public RegisterWriteGuardScope(Func<CancellationToken,Task> guard){previous=Value.Value;Value.Value=guard;}
    public void Dispose()=>Value.Value=previous;
}
public sealed class RegisterPriorityScope : IDisposable
{
    private static readonly AsyncLocal<RegisterPriority> CurrentValue=new();
    private readonly RegisterPriority previous;
    public static RegisterPriority Current=>CurrentValue.Value;
    public RegisterPriorityScope(RegisterPriority value){previous=CurrentValue.Value;CurrentValue.Value=value;}
    public void Dispose()=>CurrentValue.Value=previous;
}
// One physical owner and one in-flight frame. A stop bypasses queued background
// frames, but never interrupts bytes already being exchanged with the device.
public sealed class PriorityRegisterClient : IRegisterClient
{
    private interface IWork { Task Run(IRegisterClient client);void Abort(); }
    private sealed class Work<T>(Func<IRegisterClient,CancellationToken,ValueTask<T>> action,CancellationToken ct,bool writing):IWork
    {
        public readonly TaskCompletionSource<T> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Run(IRegisterClient client)
        {
            if(ct.IsCancellationRequested){if(writing)Completion.TrySetException(new RegisterDispatchPreventedException(new OperationCanceledException(ct)));else Completion.TrySetCanceled(ct);return;}
            try{Completion.TrySetResult(await action(client,ct));}
            catch(OperationCanceledException e){Completion.TrySetCanceled(e.CancellationToken);}
            catch(Exception e){Completion.TrySetException(e);}
        }
        public void Abort()=>Completion.TrySetException(new IOException("采集通道已关闭；已发送动作的结果需核查"));
    }
    private readonly IRegisterClient inner;
    private readonly ConcurrentQueue<IWork> normal=new(),control=new(),stop=new();
    private readonly SemaphoreSlim signal=new(0);
    private readonly CancellationTokenSource lifetime=new();
    private readonly Task worker;
    private readonly object sync=new();
    private bool disposed;
    private int queued;
    public PriorityRegisterClient(IRegisterClient inner){this.inner=inner;worker=Task.Run(Loop);}
    private ValueTask<T> Enqueue<T>(Func<IRegisterClient,CancellationToken,ValueTask<T>> action,CancellationToken ct,bool writing=false)
    {
        if(ct.IsCancellationRequested&&writing)throw new RegisterDispatchPreventedException(new OperationCanceledException(ct));ct.ThrowIfCancellationRequested();var work=new Work<T>(action,ct,writing);
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(queued>=1024&&RegisterPriorityScope.Current!=RegisterPriority.Stop||stop.Count>=128)throw new IOException("串口请求队列已满，请等待后重试读取");
            (RegisterPriorityScope.Current switch{RegisterPriority.Stop=>stop,RegisterPriority.Control=>control,_=>normal}).Enqueue(work);queued++;signal.Release();
        }
        return new(work.Completion.Task);
    }
    private async Task Loop()
    {
        var highBurst=0;
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                await signal.WaitAsync(lifetime.Token);IWork? work=null;
                if(stop.TryDequeue(out work))highBurst++;
                else if(highBurst>=8&&normal.TryDequeue(out work))highBurst=0;
                else if(control.TryDequeue(out work))highBurst++;
                else if(normal.TryDequeue(out work))highBurst=0;
                if(work is null)continue;lock(sync)queued--;await work.Run(inner);
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        finally{while(stop.TryDequeue(out var w))w.Abort();while(control.TryDequeue(out var w))w.Abort();while(normal.TryDequeue(out var w))w.Abort();}
    }
    public ValueTask<ushort[]> ReadInputRegistersAsync(byte u,ushort s,ushort n,CancellationToken ct)=>Enqueue((c,t)=>c.ReadInputRegistersAsync(u,s,n,t),ct);
    public ValueTask<ushort[]> ReadHoldingRegistersAsync(byte u,ushort s,ushort n,CancellationToken ct)=>Enqueue((c,t)=>c.ReadHoldingRegistersAsync(u,s,n,t),ct);
    public ValueTask<bool[]> ReadCoilsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>Enqueue((c,t)=>c.ReadCoilsAsync(u,s,n,t),ct);
    public ValueTask<bool[]> ReadDiscreteInputsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>Enqueue((c,t)=>c.ReadDiscreteInputsAsync(u,s,n,t),ct);
    private async ValueTask Write(Func<IRegisterClient,CancellationToken,ValueTask> action,CancellationToken ct)
    {
        var guard=RegisterWriteGuardScope.Current;
        _=await Enqueue(async(c,t)=>{try{if(guard is not null)await guard(t);t.ThrowIfCancellationRequested();}catch(Exception e)when(e is not OutOfMemoryException){throw new RegisterDispatchPreventedException(e);}await action(c,t);return true;},ct,true);
    }
    public ValueTask WriteSingleRegisterAsync(byte u,ushort s,ushort v,CancellationToken ct)=>Write((c,t)=>c.WriteSingleRegisterAsync(u,s,v,t),ct);
    public ValueTask WriteMultipleRegistersAsync(byte u,ushort s,ReadOnlyMemory<ushort> v,CancellationToken ct)=>Write((c,t)=>c.WriteMultipleRegistersAsync(u,s,v,t),ct);
    public ValueTask WriteCoilAsync(byte u,ushort s,bool v,CancellationToken ct)=>Write((c,t)=>c.WriteCoilAsync(u,s,v,t),ct);
    public async ValueTask DisposeAsync()
    {
        lock(sync){if(disposed)return;disposed=true;lifetime.Cancel();}
        await worker;await inner.DisposeAsync();lifetime.Dispose();signal.Dispose();
    }
}
