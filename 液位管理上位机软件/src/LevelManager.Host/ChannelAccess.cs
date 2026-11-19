using System.Collections.Concurrent;
using Naide.LevelManager.Communication;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class ChannelAccess
{
    private sealed record Entry(ChannelDefinition Channel,Guid Session,IRegisterClient Client,CancellationToken Lifetime);
    private readonly ConcurrentDictionary<Guid,Entry> channels=new();
    private sealed class Registration(Action release):IDisposable{public void Dispose()=>release();}
    public IDisposable Register(ChannelDefinition channel,Guid session,IRegisterClient client,CancellationToken lifetime)
    {
        var entry=new Entry(channel,session,client,lifetime);if(!channels.TryAdd(channel.Id,entry))throw new InvalidOperationException("物理采集通道已注册");
        return new Registration(()=>((ICollection<KeyValuePair<Guid,Entry>>)channels).Remove(new(channel.Id,entry)));
    }
    public async Task<T> UseAsync<T>(ControlTarget target,RegisterPriority priority,Func<IRegisterClient,CancellationToken,Task<T>> action,CancellationToken ct)
    {
        if(!channels.TryGetValue(target.Channel.Id,out var entry)||entry.Session!=target.AcquisitionSessionId||entry.Channel with{Name=""}!=target.Channel with{Name=""})throw new InvalidOperationException("原采集通道或会话已改变，未发送新动作");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,entry.Lifetime);using var scope=new RegisterPriorityScope(priority);
        return await action(entry.Client,linked.Token);
    }
}
