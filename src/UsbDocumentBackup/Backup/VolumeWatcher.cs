using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.Backup;

/// <summary>
/// Watches a connected volume while it is plugged in.
///
/// Two things matter here. Document changes turn into a request to rescan, and an Office owner
/// file appearing means a presentation was just opened -- the signal that decides whether it is
/// kept and uploaded. Callbacks never do file work themselves; they only ask the coordinator for a
/// sweep, so a burst of events cannot pile up copies.
/// </summary>
public sealed class VolumeWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Log _log;

    public VolumeWatcher(string rootPath, Log log)
    {
        _log = log;
        RootPath = rootPath;

        _watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            // The buffer is small and easy to overrun on a busy volume; Error below covers that.
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += OnError;
    }

    public string RootPath { get; }

    /// <summary>A document was created, modified or renamed. Worth a rescan.</summary>
    public event Action? ChangeDetected;

    /// <summary>
    /// An Office owner file appeared or vanished next to a document. The argument is the owner
    /// file's path relative to the volume root.
    /// </summary>
    public event Action<string>? LockFileSeen;

    public void Start()
    {
        try
        {
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Watching is an optimisation over the periodic sweep, never a requirement.
            _log.Warn($"Could not watch {RootPath}: {ex.Message}");
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Handle(e.FullPath, e.Name);

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        // A vanished owner file means the document was just closed, which is the best moment to
        // capture its final state. A deleted document itself is only news for the record; we never
        // propagate a deletion to the archive.
        if (e.Name is not null && OfficeLockFile.IsLockFileName(Path.GetFileName(e.Name)))
        {
            LockFileSeen?.Invoke(e.Name);
            ChangeDetected?.Invoke();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => Handle(e.FullPath, e.Name);

    private void Handle(string fullPath, string? relativeName)
    {
        if (relativeName is null)
        {
            return;
        }

        var fileName = Path.GetFileName(relativeName);
        if (OfficeLockFile.IsLockFileName(fileName))
        {
            LockFileSeen?.Invoke(relativeName);
            ChangeDetected?.Invoke();
            return;
        }

        if (DocumentScanner.IsTargetFile(fileName))
        {
            ChangeDetected?.Invoke();
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or a lost handle: events were missed, so fall back to a full rescan.
        _log.Warn($"Watcher on {RootPath} reported an error; scheduling a full rescan. {e.GetException().Message}");
        ChangeDetected?.Invoke();
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnChanged;
        _watcher.Changed -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Deleted -= OnDeleted;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
