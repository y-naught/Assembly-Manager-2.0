using System.Threading;

namespace AssemblyManagerPlugin.Services;

/// <summary>
/// Prevents Gazelle's own document mutations from being interpreted as user edits by the
/// document-wide event watcher. Rhino normally executes these mutations on its UI thread,
/// but an interlocked process-wide depth also protects callbacks delivered on another thread.
/// </summary>
public static class AssemblyLinkMutationGate
{
    private static int _depth;

    public static bool IsSuppressed => Volatile.Read(ref _depth) > 0;

    public static IDisposable Enter()
    {
        Interlocked.Increment(ref _depth);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _depth);
        }
    }
}
