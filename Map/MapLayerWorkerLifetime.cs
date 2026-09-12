using System.Threading;

namespace FastMap.Map;

// Covers the map worker tick and each independently scheduled page task.
// Retired layers may still receive ticks through vanilla's previous list snapshot.
internal sealed class MapLayerWorkerLifetime
{
    private readonly object sync = new();
    private bool stopped;
    private int active;

    public bool TryEnter()
    {
        lock (sync)
        {
            if (stopped) return false;
            active++;
            return true;
        }
    }

    public void Exit()
    {
        lock (sync)
        {
            if (--active == 0) Monitor.PulseAll(sync);
        }
    }

    // Called on the client thread, never by a worker holding an entry.
    public void StopAndWait()
    {
        lock (sync)
        {
            stopped = true;
            while (active != 0) Monitor.Wait(sync);
        }
    }
}
