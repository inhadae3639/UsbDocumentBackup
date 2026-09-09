using System.Threading;

namespace UsbDocumentBackup.Windows;

/// <summary>
/// Keeps a single tray instance per Windows user. Startup registration plus a manual launch must
/// not end up with two processes writing the same archive.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstance(Mutex? mutex, bool acquired)
    {
        _mutex = mutex;
        Acquired = acquired;
    }

    public bool Acquired { get; }

    public static SingleInstance TryAcquire(string name = "Local\\UsbDocumentBackup.SingleInstance")
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance died without releasing; we now own it.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return new SingleInstance(null, false);
        }

        return new SingleInstance(mutex, true);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (Acquired)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned any more; nothing to release.
            }
        }

        _mutex.Dispose();
    }
}
