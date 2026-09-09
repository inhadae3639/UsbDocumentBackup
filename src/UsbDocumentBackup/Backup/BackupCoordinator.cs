using System.Threading.Channels;
using UsbDocumentBackup.Devices;
using UsbDocumentBackup.GoogleDrive;
using UsbDocumentBackup.Storage;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.Backup;

public sealed record StatusSnapshot(
    int RetainedComplete,
    int TemporaryComplete,
    int LocalPending,
    int UploadsDone,
    int UploadsWaiting,
    int UploadsNeedAttention,
    int RecentIssues,
    bool Paused,
    string Activity,
    DateTimeOffset? LastScanUtc);

/// <summary>
/// Owns the single local-copy worker. Scan requests are coalesced through a channel so device
/// events, watcher events, the periodic sweep and the tray menu can all ask for work without ever
/// running two copies at once or doing file work on the UI thread.
/// </summary>
public sealed class BackupCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(5);

    private readonly IVolumeProvider _volumeProvider;
    private readonly BackupRepository _repository;
    private readonly BackupService _backupService;
    private readonly ArchiveSweepService _sweepService;
    private readonly IOpenedDocumentSource _openedDocuments;
    private readonly UploadWorker _uploadWorker;
    private readonly Log _log;
    private readonly TimeProvider _time;
    private readonly bool _watchVolumes;

    private readonly Channel<ScanReason> _requests = Channel.CreateBounded<ScanReason>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, VolumeWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceIdByRoot = new(StringComparer.OrdinalIgnoreCase);

    private Task? _worker;
    private ITimer? _rescanTimer;
    private CancellationTokenSource? _currentDeviceScan;
    private string _activity = "Idle";
    private DateTimeOffset? _lastScanUtc;

    public BackupCoordinator(
        IVolumeProvider volumeProvider,
        BackupRepository repository,
        BackupService backupService,
        ArchiveSweepService sweepService,
        IOpenedDocumentSource openedDocuments,
        UploadWorker uploadWorker,
        Log log,
        TimeProvider? time = null,
        bool watchVolumes = true)
    {
        _volumeProvider = volumeProvider;
        _repository = repository;
        _backupService = backupService;
        _sweepService = sweepService;
        _openedDocuments = openedDocuments;
        _uploadWorker = uploadWorker;
        _log = log;
        _time = time ?? TimeProvider.System;
        _watchVolumes = watchVolumes;
    }

    public event Action? StatusChanged;

    public bool Paused { get; private set; }

    public void Start()
    {
        _worker ??= Task.Run(() => RunAsync(_shutdown.Token));
        _rescanTimer ??= _time.CreateTimer(_ => RequestScan(ScanReason.PeriodicRescan), null, RescanInterval, RescanInterval);
        RequestScan(ScanReason.NewConnection);
    }

    /// <summary>Queues a sweep. Safe to call from a device-change message, a watcher or a timer.</summary>
    public void RequestScan(ScanReason reason) => _requests.Writer.TryWrite(reason);

    /// <summary>
    /// Called when Windows reports a volume went away. The in-flight scan is cancelled so we stop
    /// reading a device that is no longer there; already-finished backups are untouched.
    /// </summary>
    public void CancelActiveDeviceScan()
    {
        lock (_gate)
        {
            _currentDeviceScan?.Cancel();
        }
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (paused)
        {
            CancelActiveDeviceScan();
        }
        else
        {
            RequestScan(ScanReason.NewConnection);
        }

        StatusChanged?.Invoke();
    }

    public StatusSnapshot GetStatus()
    {
        string activity;
        DateTimeOffset? lastScan;
        lock (_gate)
        {
            activity = _activity;
            lastScan = _lastScanUtc;
        }

        return new StatusSnapshot(
            _repository.CountBackups(BackupState.Complete, BackupTier.Retained),
            _repository.CountBackups(BackupState.Complete, BackupTier.Temporary),
            _repository.CountBackups(BackupState.Pending),
            _repository.CountUploads(UploadState.Done),
            _repository.CountUploads(UploadState.Waiting),
            _repository.CountUploads(UploadState.NeedsAttention),
            _repository.RecentIssues(50).Count,
            Paused,
            activity,
            lastScan);
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(shutdown).ConfigureAwait(false))
            {
                // Collapse a burst of requests into one sweep, keeping the strongest reason.
                var reason = ScanReason.PeriodicRescan;
                while (_requests.Reader.TryRead(out var queued))
                {
                    if (queued != ScanReason.PeriodicRescan)
                    {
                        reason = queued;
                    }
                }

                if (Paused)
                {
                    continue;
                }

                await SweepAsync(reason, shutdown).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log.Error("Backup worker stopped unexpectedly.", ex);
        }
    }

    private async Task SweepAsync(ScanReason reason, CancellationToken shutdown)
    {
        IReadOnlyList<VolumeInfo> volumes;
        try
        {
            volumes = _volumeProvider.GetVolumes();
        }
        catch (Exception ex)
        {
            _log.Error("Could not enumerate volumes.", ex);
            return;
        }

        var targets = volumes.Where(v => v.IsReady && v.IsBackupTarget).ToList();

        // Register devices and start watching before any copying. Doing this afterwards meant a
        // deck opened and closed during a long initial copy went completely unnoticed -- exactly
        // the window that overlaps with someone actually presenting.
        var devices = new Dictionary<string, DeviceRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in targets)
        {
            var device = _repository.UpsertDevice(
                volume.VolumeId,
                volume.Fingerprint,
                volume.DisplayName,
                volume.BusKind,
                _time.GetUtcNow());

            devices[volume.RootPath] = device;
            lock (_gate)
            {
                _deviceIdByRoot[volume.RootPath] = device.Id;
            }

            if (volume.ClassificationUncertain)
            {
                _repository.LogIssue(
                    "BusTypeUnknown",
                    device.Id,
                    volume.RootPath,
                    "Bus type could not be read; treated as USB because the volume is removable.",
                    _time.GetUtcNow());
            }
        }

        SyncWatchers(targets);

        // Presentations PowerPoint says were opened come first, and from anywhere on this PC --
        // not just the sticks we scan. They are the decks the user actually presented.
        await BackUpOpenedDocumentsAsync(volumes, shutdown).ConfigureAwait(false);

        foreach (var volume in targets)
        {
            if (shutdown.IsCancellationRequested || Paused)
            {
                break;
            }

            var device = devices[volume.RootPath];

            using var deviceScan = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            lock (_gate)
            {
                _currentDeviceScan = deviceScan;
                _activity = $"Scanning {volume.DisplayName}";
            }

            StatusChanged?.Invoke();

            try
            {
                var summary = await _backupService
                    .BackupVolumeAsync(device, volume.RootPath, reason, deviceScan.Token)
                    .ConfigureAwait(false);

                if (summary.NewVersions > 0 || summary.Promoted > 0 || summary.Failed > 0)
                {
                    _log.Info(
                        $"{volume.DisplayName}: {summary.NewVersions} new, {summary.Promoted} promoted, "
                        + $"{summary.Unchanged} unchanged, {summary.Skipped} skipped, {summary.Failed} failed.");
                }
            }
            catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
            {
                _log.Info($"Scan of {volume.DisplayName} was cancelled; the device is gone or we were paused.");
            }
            catch (Exception ex)
            {
                _log.Error($"Scan of {volume.DisplayName} failed.", ex);
            }
            finally
            {
                lock (_gate)
                {
                    _currentDeviceScan = null;
                }
            }
        }

        // Uploads run after the copying, so a deck being backed up locally is never delayed by a
        // slow network. With no account connected this returns immediately and the queue just waits.
        try
        {
            await _uploadWorker.RunAsync(shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Upload worker failed.", ex);
        }

        // Sweeping last means a copy Drive just confirmed can be released in the same pass.
        try
        {
            _sweepService.Run();
        }
        catch (Exception ex)
        {
            _log.Error("Archive sweep failed.", ex);
        }

        lock (_gate)
        {
            _activity = "Idle";
            _lastScanUtc = _time.GetUtcNow();
        }

        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Backs up every presentation in PowerPoint's recent list that we can still find on disk.
    /// Storage location is irrelevant: the volume is resolved from the path, and a device row is
    /// created for it whether it is a stick, an internal disk or a network share.
    /// </summary>
    private async Task BackUpOpenedDocumentsAsync(IReadOnlyList<VolumeInfo> volumes, CancellationToken shutdown)
    {
        IReadOnlyList<OpenedDocument> opened;
        try
        {
            opened = _openedDocuments.GetRecentlyOpened();
        }
        catch (Exception ex)
        {
            _log.Error("Could not read the list of opened presentations.", ex);
            return;
        }

        var newVersions = 0;
        var promoted = 0;

        foreach (var document in opened)
        {
            if (shutdown.IsCancellationRequested || Paused)
            {
                break;
            }

            var volume = ResolveVolume(volumes, document.FullPath);
            if (volume is null)
            {
                continue;
            }

            var device = _repository.UpsertDevice(
                volume.VolumeId,
                volume.Fingerprint,
                volume.DisplayName,
                volume.BusKind,
                _time.GetUtcNow());

            lock (_gate)
            {
                _activity = $"Checking {Path.GetFileName(document.FullPath)}";
            }

            try
            {
                var result = await _backupService
                    .BackupOpenedDocumentAsync(device, volume.RootPath, document.FullPath, shutdown)
                    .ConfigureAwait(false);

                switch (result)
                {
                    case DocumentBackupResult.NewVersion:
                        newVersions++;
                        break;
                    case DocumentBackupResult.Promoted:
                        promoted++;
                        break;
                    case DocumentBackupResult.Cancelled:
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error($"Backing up the opened presentation {document.FullPath} failed.", ex);
            }
        }

        if (newVersions > 0 || promoted > 0)
        {
            _log.Info($"Opened presentations: {newVersions} new, {promoted} promoted.");
        }
    }

    /// <summary>
    /// Finds the volume an absolute path sits on. Falls back to a synthetic entry keyed on the
    /// path root so a deck opened from a network share still gets a stable device identity rather
    /// than being silently skipped.
    /// </summary>
    private static VolumeInfo? ResolveVolume(IReadOnlyList<VolumeInfo> volumes, string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var match = volumes
            .Where(v => root.Equals(v.RootPath, StringComparison.OrdinalIgnoreCase))
            .MaxBy(v => v.RootPath.Length);

        if (match is not null)
        {
            return match;
        }

        return new VolumeInfo(
            root,
            VolumeId: null,
            Fingerprint: "path-root|" + root.ToLowerInvariant(),
            DisplayName: root,
            DriveType: DriveType.Unknown,
            BusKind: BusKind.Unknown,
            IsReady: true);
    }

    /// <summary>
    /// Keeps one watcher per connected target volume so an Office owner file appearing mid-session
    /// is noticed immediately rather than at the next five-minute sweep.
    /// </summary>
    private void SyncWatchers(IReadOnlyList<VolumeInfo> targets)
    {
        if (!_watchVolumes)
        {
            return;
        }

        var wanted = targets.Select(v => v.RootPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var root in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _watchers[root].Dispose();
                _watchers.Remove(root);
                _deviceIdByRoot.Remove(root);
            }

            foreach (var root in wanted.Where(r => !_watchers.ContainsKey(r)))
            {
                try
                {
                    var watcher = new VolumeWatcher(root, _log);
                    watcher.ChangeDetected += () => RequestScan(ScanReason.ChangeEvent);
                    watcher.LockFileSeen += relativeName => OnLockFileSeen(root, relativeName);
                    watcher.Start();
                    _watchers[root] = watcher;
                }
                catch (Exception ex)
                {
                    // Watching is an optimisation: the periodic rescan still finds everything, so a
                    // failure here must never stop the sweep. The type and HResult are logged
                    // because a real failure on a USB root produced an empty Message, which left
                    // nothing to diagnose. The root stays out of _watchers, so the next sweep retries.
                    _log.Warn(
                        $"Could not start a watcher on {root}: {ex.GetType().FullName} "
                        + $"HResult=0x{ex.HResult:X8} Message=\"{ex.Message}\" "
                        + $"Inner={ex.InnerException?.GetType().Name ?? "none"}");
                }
            }
        }
    }

    /// <summary>
    /// Resolves an Office owner file to the presentation beside it and records that the document
    /// was open. This runs on both creation and deletion of the owner file, because a document
    /// closed before the next scan would otherwise leave no trace that it was ever opened.
    /// </summary>
    private void OnLockFileSeen(string rootPath, string relativeName)
    {
        string? deviceId;
        lock (_gate)
        {
            _deviceIdByRoot.TryGetValue(rootPath, out deviceId);
        }

        if (deviceId is null)
        {
            return;
        }

        var relativeDirectory = Path.GetDirectoryName(relativeName) ?? string.Empty;
        var directory = Path.Combine(rootPath, relativeDirectory);

        try
        {
            var siblings = Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>().ToList();
            var candidates = OfficeLockFile
                .ResolveCandidates(Path.GetFileName(relativeName), siblings)
                .Where(DocumentScanner.IsPresentation)
                .ToList();

            if (candidates.Count != 1)
            {
                _repository.LogIssue(
                    candidates.Count == 0 ? "LockFileUnmatched" : "LockFileAmbiguous",
                    deviceId,
                    relativeName,
                    "Matched " + candidates.Count + " presentations in that folder.",
                    _time.GetUtcNow());
                return;
            }

            var relativePath = string.IsNullOrEmpty(relativeDirectory)
                ? candidates[0]
                : Path.Combine(relativeDirectory, candidates[0]);
            _repository.MarkOpened(deviceId, relativePath, _time.GetUtcNow());
            _log.Info($"Observed {relativePath} open; it will be retained and uploaded.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The stick may already be gone; the next scan will pick up whatever is still there.
            _log.Warn($"Could not resolve owner file {relativeName}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_rescanTimer is not null)
        {
            await _rescanTimer.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _requests.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }
}
