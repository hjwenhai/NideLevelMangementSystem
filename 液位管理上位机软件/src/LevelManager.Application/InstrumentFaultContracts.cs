using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public interface IInstrumentFaultStore
{
 Task<InstrumentFaultState?> GetAsync(Guid tank,CancellationToken ct=default);
 Task SaveAsync(InstrumentFaultState state,CancellationToken ct=default);
}
