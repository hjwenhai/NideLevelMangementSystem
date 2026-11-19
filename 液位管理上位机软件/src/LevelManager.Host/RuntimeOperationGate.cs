namespace Naide.LevelManager.Host;
public sealed class RuntimeOperationGate
{
    public SemaphoreSlim Gate {get;}=new(1);
}
