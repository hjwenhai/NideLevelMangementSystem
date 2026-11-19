using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class InstrumentFaultTracking(IInstrumentFaultStore store,TimeProvider clock,ILogger<InstrumentFaultTracking> logger)
{
 private sealed record Cached(InstrumentFaultState State,bool Dirty);
 private readonly ConcurrentDictionary<Guid,Cached> cache=new();
 public async Task<ReadingSnapshot> ApplyAsync(TankDefinition tank,ChannelDefinition channel,ReadingSnapshot reading,CancellationToken ct)
 {
  if(!reading.InstrumentFault.HasValue)return reading with{InstrumentFaults=[]};var key=JsonSerializer.Serialize(new{channel.Protocol,channel.PhysicalKey,channel.UnitId,tank.Address,reading.GatewayBindingVersion});
  if(!cache.TryGetValue(tank.Id,out var before)){before=new(await store.GetAsync(tank.Id,ct)??new(tank.Id,key,[]),false);cache[tank.Id]=before;}
  var previous=before.State.BindingKey==key?before.State.Faults:[];var faults=InstrumentFaultCatalog.Observe(reading.InstrumentFault.Value,channel.Protocol,previous,clock.GetUtcNow());var changed=before.State.BindingKey!=key||!previous.SequenceEqual(faults);var next=new Cached(new(tank.Id,key,faults),before.Dirty||changed&&(previous.Count>0||faults.Count>0));cache[tank.Id]=next;
  if(next.Dirty)try{await store.SaveAsync(next.State,ct);next=next with{Dirty=false};cache[tank.Id]=next;}catch(Exception e)when(e is SqliteException or IOException){logger.LogError(e,"故障首次检测时间暂未保存");}
  return reading with{InstrumentFaults=faults,FaultTrackingPersisted=!next.Dirty,Diagnostic=reading.Diagnostic+(faults.Count>0?(reading.Diagnostic.Length>0?"；":"")+string.Join("；",faults.Select(f=>f.Code+" "+f.Description)):"")};
 }
}
