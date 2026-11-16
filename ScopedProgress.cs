using System;
using System.Threading;

namespace Naufal_Windows_Tech_s_Powertoys;

// Progress<T> posts callbacks asynchronously. A queued callback from an operation
// that has ended must not replace its final result or a later operation's status.
internal sealed class ScopedProgress<T> : IProgress<T>, IDisposable
{
    private int _closed;
    private readonly IProgress<T> _progress;
    internal ScopedProgress(Action<T> report) => _progress = new Progress<T>(value =>
    {
        if (Volatile.Read(ref _closed) == 0) report(value);
    });
    public void Report(T value)
    {
        if (Volatile.Read(ref _closed) == 0) _progress.Report(value);
    }
    public void Dispose() => Interlocked.Exchange(ref _closed, 1);
}
