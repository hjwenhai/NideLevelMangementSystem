using System.Text.Json;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Infrastructure;
public sealed class SqliteInstrumentFaultStore(string path):IInstrumentFaultStore
{
 private async Task<SqliteConnection> Open(CancellationToken ct){var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,ForeignKeys=true,DefaultTimeout=5}.ToString());try{await db.OpenAsync(ct);return db;}catch{await db.DisposeAsync();throw;}}
 public async Task<InstrumentFaultState?> GetAsync(Guid tank,CancellationToken ct=default){await using var db=await Open(ct);await using var command=db.CreateCommand();command.CommandText="SELECT Payload FROM instrument_fault_state WHERE TankId=$id";command.Parameters.AddWithValue("$id",tank.ToString());var value=await command.ExecuteScalarAsync(ct);return value is string json?JsonSerializer.Deserialize<InstrumentFaultState>(json):null;}
 public async Task SaveAsync(InstrumentFaultState state,CancellationToken ct=default){await using var db=await Open(ct);await using var command=db.CreateCommand();command.CommandText="PRAGMA synchronous=FULL;INSERT INTO instrument_fault_state(TankId,Payload)VALUES($id,$payload)ON CONFLICT(TankId)DO UPDATE SET Payload=excluded.Payload";command.Parameters.AddWithValue("$id",state.TankId.ToString());command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(state));await command.ExecuteNonQueryAsync(ct);}
}
