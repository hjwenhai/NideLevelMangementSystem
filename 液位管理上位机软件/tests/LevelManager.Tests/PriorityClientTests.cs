using Naide.LevelManager.Communication;
using Xunit;
namespace Naide.LevelManager.Tests;
public class PriorityClientTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task QueuedWriteRechecksCurrentRoleAndScheduledAuthorityBeforeTouchingDevice(bool scheduled)
    {
        await using var f=new StoreFixture();await f.Initialize();var user=new Naide.LevelManager.Application.UserAccount("queue-qa","queueqa","排队验证",Naide.LevelManager.Application.AccountRole.Operator,"opaque",true,4);
        var device=new Device();await using var client=new PriorityRegisterClient(device);var first=client.ReadInputRegistersAsync(1,0,1,default).AsTask();await device.Entered.Task;
        var checks=0;Task write;
        using(new RegisterWriteGuardScope(async token=>
        {
            checks++;var actor=await Naide.LevelManager.Application.PermissionPolicy.ResolveAsync(f.Store,user,token);
            Naide.LevelManager.Application.PermissionPolicy.Require(actor,scheduled?Naide.LevelManager.Application.AppPermission.AverageTemperatureStart:Naide.LevelManager.Application.AppPermission.InstrumentControl);
            if(scheduled)Naide.LevelManager.Application.PermissionPolicy.RequireScheduledAuthority(user,(await f.Store.GetPermissionsAsync(token)).Revision,0,4);
        }))write=client.WriteSingleRegisterAsync(1,100,1,default).AsTask();
        Assert.Equal(0,checks);var matrix=await f.Store.GetPermissionsAsync();
        matrix=matrix with{Roles=matrix.Roles.Select(g=>g.Role==(scheduled?Naide.LevelManager.Application.AccountRole.Viewer:Naide.LevelManager.Application.AccountRole.Operator)?g with{Permissions=g.Permissions.Where(p=>p!=(scheduled?Naide.LevelManager.Application.AppPermission.DataExport:Naide.LevelManager.Application.AppPermission.InstrumentControl)).ToArray()}:g).ToArray()};
        await f.Store.SavePermissionsAsync(matrix,new("a","管理员",Naide.LevelManager.Application.AccountRole.Administrator));device.Release.SetResult();await first;
        await Assert.ThrowsAsync<RegisterDispatchPreventedException>(()=>write);Assert.Equal(1,checks);Assert.Equal(0,device.Writes);
    }
    private sealed class Device : IRegisterClient
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<ushort> Calls=[];
        public int Writes;
        public async ValueTask<ushort[]> ReadInputRegistersAsync(byte u,ushort s,ushort n,CancellationToken ct){Calls.Add(s);if(s==0){Entered.SetResult();await Release.Task.WaitAsync(ct);}return new ushort[n];}
        public ValueTask<ushort[]> ReadHoldingRegistersAsync(byte u,ushort s,ushort n,CancellationToken ct)=>ReadInputRegistersAsync(u,s,n,ct);
        public ValueTask<bool[]> ReadCoilsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>ValueTask.FromResult(new bool[n]);
        public ValueTask<bool[]> ReadDiscreteInputsAsync(byte u,ushort s,ushort n,CancellationToken ct)=>ReadCoilsAsync(u,s,n,ct);
        public ValueTask WriteSingleRegisterAsync(byte u,ushort s,ushort v,CancellationToken ct){Calls.Add(s);Writes++;return ValueTask.CompletedTask;}
        public ValueTask WriteMultipleRegistersAsync(byte u,ushort s,ReadOnlyMemory<ushort> v,CancellationToken ct)=>WriteSingleRegisterAsync(u,s,0,ct);
        public ValueTask WriteCoilAsync(byte u,ushort s,bool v,CancellationToken ct)=>WriteSingleRegisterAsync(u,s,0,ct);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    [Fact]public async Task StopPassesQueuedBackgroundReadsButDoesNotInterleaveCurrentFrame()
    {
        var device=new Device();await using var client=new PriorityRegisterClient(device);var first=client.ReadInputRegistersAsync(1,0,1,default).AsTask();await device.Entered.Task;
        var background=client.ReadInputRegistersAsync(1,1,1,default).AsTask();Task stop;
        using(new RegisterPriorityScope(RegisterPriority.Stop))stop=client.WriteSingleRegisterAsync(1,50,1,default).AsTask();
        Assert.Equal(new ushort[]{0},device.Calls);device.Release.SetResult();await Task.WhenAll(first,background,stop);Assert.Equal(new ushort[]{0,50,1},device.Calls);
    }
    [Fact]public async Task WriteAdmissionIsCheckedAtDispatchAndNotOnlyAtQueueTime()
    {
        var device=new Device();await using var client=new PriorityRegisterClient(device);var first=client.ReadInputRegistersAsync(1,0,1,default).AsTask();await device.Entered.Task;var allowed=true;var guarded=0;Task write;
        using(new RegisterWriteGuardScope(_=>{guarded++;if(!allowed)throw new InvalidOperationException("排队期间已到期");return Task.CompletedTask;}))write=client.WriteSingleRegisterAsync(1,50,1,default).AsTask();
        Assert.Equal(0,guarded);allowed=false;device.Release.SetResult();await first;var error=await Assert.ThrowsAsync<RegisterDispatchPreventedException>(()=>write);Assert.Contains("排队期间已到期",error.Message);Assert.Equal(1,guarded);Assert.Equal(0,device.Writes);Assert.Null(RegisterWriteGuardScope.Current);
    }
    [Fact]public async Task CancelledQueuedWriteIsNeverSentAndScopeRestoresPriority()
    {
        var device=new Device();await using var client=new PriorityRegisterClient(device);var first=client.ReadInputRegistersAsync(1,0,1,default).AsTask();await device.Entered.Task;
        using var cancel=new CancellationTokenSource();Task write;
        using(new RegisterPriorityScope(RegisterPriority.Control))write=client.WriteSingleRegisterAsync(1,50,1,cancel.Token).AsTask();
        Assert.Equal(RegisterPriority.Background,RegisterPriorityScope.Current);cancel.Cancel();device.Release.SetResult();await first;await Assert.ThrowsAsync<RegisterDispatchPreventedException>(()=>write);Assert.Equal(0,device.Writes);
    }
}
