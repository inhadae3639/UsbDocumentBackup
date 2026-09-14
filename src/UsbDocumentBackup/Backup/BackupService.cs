using UsbDocumentBackup.Storage;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.Backup;

public enum ScanReason
{
    /// <summary>Volume just appeared. Content is verified rather than trusted from metadata.</summary>
    NewConnection,

    /// <summary>A watcher reported a create/modify/rename. Content is verified.</summary>
    ChangeEvent,

    /// <summary>Routine sweep of a still-connected volume. Uses the metadata fast path.</summary>
    PeriodicRescan,
}

public sealed record BackupSummary(int NewVersions, int Unchanged, int Promoted, int Skipped, int Failed)
{
    public static readonly BackupSummary Empty = new(0, 0, 0, 0, 0);
}

/// <summary>Outcome of asking for one backup to be uploaded.</summary>
public enum UploadRequestResult
{
    Queued,
    AlreadyUploaded,
    NotFound,
    NotReady,
    Failed,
}

/// <summary>What happened to one document we were asked to back up by name.</summary>
public enum DocumentBackupResult
{
    NewVersion,
    Promoted,
    Unchanged,
    SourceGone,
    Failed,
    Cancelled,
}

/// <summary>
/// Turns a volume into verified archive copies. Nothing here writes to, renames or deletes a
/// source file, and no destructive action is ever taken against an existing backup.
///
/// Every target document is copied, but only presentations we have evidence were opened land in
/// the retained tier and get queued for Drive. Everything else is held in the temporary tier as a
/// local safety net.
/// </summary>
public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly BackupRepository _repository;
    private readonly DocumentScanner _scanner;
    private readonly FileCopier _copier;
    private readonly Log _log;
    private readonly TimeProvider _time;

    public BackupService(
        AppPaths paths,
        BackupRepository repository,
        DocumentScanner scanner,
        FileCopier copier,
        Log log,
        TimeProvider? time = null)
    {
        _paths = paths;
        _repository = repository;
        _scanner = scanner;
        _copier = copier;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public async Task<BackupSummary> BackupVolumeAsync(
        DeviceRecord device,
        string rootPath,
        ScanReason reason,
        CancellationToken cancellationToken)
    {
        ScanResult scan;
        try
        {
            scan = _scanner.Scan(rootPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return BackupSummary.Empty;
        }

        foreach (var skip in scan.Skips)
        {
            _repository.LogIssue(skip.Kind, device.Id, skip.Path, skip.Detail, _time.GetUtcNow());
        }

        RecordOpenDocuments(device, scan);
        var openedPaths = _repository.ListOpenedPaths(device.Id);

        var newVersions = 0;
        var unchanged = 0;
        var promoted = 0;
        var skipped = scan.Skips.Count;
        var failed = 0;

        foreach (var document in scan.Documents)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var tier = TierFor(document.RelativePath, openedPaths);
            var previous = _repository.GetLatestComplete(device.Id, document.RelativePath);

            // A routine sweep of a volume that is still plugged in trusts size and timestamp so we
            // are not re-reading the whole stick every few minutes. A new connection or a change
            // event always reads the bytes, because metadata alone proves nothing.
            if (reason == ScanReason.PeriodicRescan
                && previous is not null
                && previous.SizeBytes == document.Length
                && previous.SourceModifiedUtc.UtcDateTime == document.LastWriteUtc.UtcDateTime)
            {
                // ...but a deck that has since been opened still has to move up a tier, and that
                // needs no read at all: the verified bytes are already in the archive.
                if (tier == BackupTier.Retained && previous.Tier == BackupTier.Temporary)
                {
                    promoted += Promote(previous) ? 1 : 0;
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            var outcome = await BackupDocumentAsync(device, document, previous, tier, cancellationToken)
                .ConfigureAwait(false);

            switch (outcome)
            {
                case DocumentOutcome.NewVersion:
                    newVersions++;
                    break;
                case DocumentOutcome.Promoted:
                    promoted++;
                    break;
                case DocumentOutcome.Unchanged:
                    unchanged++;
                    break;
                case DocumentOutcome.Cancelled:
                    return new BackupSummary(newVersions, unchanged, promoted, skipped, failed);
                default:
                    failed++;
                    break;
            }
        }

        return new BackupSummary(newVersions, unchanged, promoted, skipped, failed);
    }

    /// <summary>
    /// Backs up one presentation that PowerPoint told us was opened, wherever it lives: a USB
    /// stick, the desktop, a synced OneDrive folder, a network share. It always lands in the
    /// retained tier and is queued for Drive.
    ///
    /// The metadata fast path matters more here than in a volume scan, because this runs against
    /// PowerPoint's whole recent list on every sweep. An unchanged deck that is already retained
    /// costs one stat call, not a re-read.
    /// </summary>
    public async Task<DocumentBackupResult> BackupOpenedDocumentAsync(
        DeviceRecord device,
        string volumeRoot,
        string fullPath,
        CancellationToken cancellationToken)
    {
        SourceDocument document;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return DocumentBackupResult.SourceGone;
            }

            document = new SourceDocument(
                info.FullName,
                Path.GetRelativePath(volumeRoot, info.FullName),
                info.Length,
                info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _repository.LogIssue("OpenedDocumentUnreadable", device.Id, fullPath, ex.Message, _time.GetUtcNow());
            return DocumentBackupResult.Failed;
        }

        _repository.MarkOpened(device.Id, document.RelativePath, _time.GetUtcNow());

        var previous = _repository.GetLatestComplete(device.Id, document.RelativePath);
        if (previous is not null
            && previous.SizeBytes == document.Length
            && previous.SourceModifiedUtc.UtcDateTime == document.LastWriteUtc.UtcDateTime)
        {
            if (previous.Tier == BackupTier.Retained)
            {
                return DocumentBackupResult.Unchanged;
            }

            return Promote(previous) ? DocumentBackupResult.Promoted : DocumentBackupResult.Failed;
        }

        var outcome = await BackupDocumentAsync(device, document, previous, BackupTier.Retained, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            DocumentOutcome.NewVersion => DocumentBackupResult.NewVersion,
            DocumentOutcome.Promoted => DocumentBackupResult.Promoted,
            DocumentOutcome.Unchanged => DocumentBackupResult.Unchanged,
            DocumentOutcome.Cancelled => DocumentBackupResult.Cancelled,
            _ => DocumentBackupResult.Failed,
        };
    }

    /// <summary>
    /// Turns owner files found during the scan into durable "this was opened" facts. An owner file
    /// we cannot attribute to a document is logged rather than dropped, because that would silently
    /// downgrade a presentation the user did open.
    /// </summary>
    private void RecordOpenDocuments(DeviceRecord device, ScanResult scan)
    {
        if (scan.LockFiles.Count == 0)
        {
            return;
        }

        var namesByDirectory = scan.Documents
            .GroupBy(d => Path.GetDirectoryName(d.RelativePath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var lockPath in scan.LockFiles)
        {
            var directory = Path.GetDirectoryName(lockPath) ?? string.Empty;
            var lockName = Path.GetFileName(lockPath);

            if (!namesByDirectory.TryGetValue(directory, out var siblings))
            {
                _repository.LogIssue("LockFileUnmatched", device.Id, lockPath, "No documents in that folder.", _time.GetUtcNow());
                continue;
            }

            var candidates = OfficeLockFile.ResolveCandidates(lockName, siblings.Select(s => Path.GetFileName(s.RelativePath)));
            var presentations = candidates.Where(DocumentScanner.IsPresentation).ToList();

            if (presentations.Count == 0)
            {
                _repository.LogIssue(
                    "LockFileUnmatched",
                    device.Id,
                    lockPath,
                    "Could not match this owner file to a presentation in the same folder.",
                    _time.GetUtcNow());
                continue;
            }

            // More than one document fits the owner file's name. Retaining and uploading all of
            // them would put decks the user never opened into their Drive, so the ambiguity is
            // recorded instead. PowerPoint's own recent-files list resolves these exactly.
            if (presentations.Count > 1)
            {
                _repository.LogIssue(
                    "LockFileAmbiguous",
                    device.Id,
                    lockPath,
                    "Matches " + presentations.Count + " presentations; waiting for PowerPoint's recent-file list to confirm.",
                    _time.GetUtcNow());
                continue;
            }

            var relativePath = string.IsNullOrEmpty(directory) ? presentations[0] : Path.Combine(directory, presentations[0]);
            _repository.MarkOpened(device.Id, relativePath, _time.GetUtcNow());
            _log.Info($"Observed {relativePath} open on {device.DisplayName}; it will be retained and uploaded.");
        }
    }

    private static BackupTier TierFor(string relativePath, HashSet<string> openedPaths) =>
        DocumentScanner.IsPresentation(relativePath) && openedPaths.Contains(relativePath)
            ? BackupTier.Retained
            : BackupTier.Temporary;

    private enum DocumentOutcome
    {
        NewVersion,
        Promoted,
        Unchanged,
        Failed,
        Cancelled,
    }

    private async Task<DocumentOutcome> BackupDocumentAsync(
        DeviceRecord device,
        SourceDocument document,
        BackupRecord? previous,
        BackupTier tier,
        CancellationToken cancellationToken)
    {
        if (!await _copier.WaitForStableAsync(document.FullPath, cancellationToken).ConfigureAwait(false))
        {
            _repository.LogIssue("SourceUnstable", device.Id, document.FullPath, "Still being written; will retry.", _time.GetUtcNow());
            return DocumentOutcome.Failed;
        }

        var backupId = Id.New();
        var localRelativePath = ArchiveLayout.PathFor(tier, device.Id, backupId, Path.GetFileName(document.FullPath));
        var destination = _paths.ResolveArchivePath(localRelativePath);

        var outcome = await _copier.CopyAsync(document.FullPath, destination, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            TryRemoveDirectory(Path.GetDirectoryName(destination));
            if (outcome.Status == CopyStatus.Cancelled)
            {
                return DocumentOutcome.Cancelled;
            }

            _repository.LogIssue(outcome.Status.ToString(), device.Id, document.FullPath, outcome.Message, _time.GetUtcNow());
            return DocumentOutcome.Failed;
        }

        // The copy is the only read of the source, so an unchanged document costs exactly the same
        // read as a changed one -- but it never becomes a second archived version or a second upload.
        if (previous is not null && string.Equals(previous.Sha256, outcome.Sha256, StringComparison.Ordinal))
        {
            TryDeleteFile(destination + FileCopier.PartialSuffix);
            TryRemoveDirectory(Path.GetDirectoryName(destination));

            if (tier == BackupTier.Retained && previous.Tier == BackupTier.Temporary)
            {
                return Promote(previous) ? DocumentOutcome.Promoted : DocumentOutcome.Failed;
            }

            return DocumentOutcome.Unchanged;
        }

        var now = _time.GetUtcNow();
        var record = new BackupRecord(
            backupId,
            device.Id,
            document.RelativePath,
            Path.GetFileName(document.FullPath),
            outcome.BytesCopied,
            document.LastWriteUtc,
            now,
            outcome.Sha256!,
            localRelativePath,
            BackupState.Pending,
            tier);

        // Row first, then rename, then Complete. A crash between any two of those steps leaves a
        // Pending row that startup recovery can resolve by looking at what is actually on disk.
        _repository.Insert(record);

        try
        {
            FileCopier.Promote(destination);
        }
        catch (IOException ex)
        {
            _repository.DeleteBackup(backupId);
            TryDeleteFile(destination + FileCopier.PartialSuffix);
            TryRemoveDirectory(Path.GetDirectoryName(destination));
            _repository.LogIssue("PromoteFailed", device.Id, document.FullPath, ex.Message, now);
            return DocumentOutcome.Failed;
        }

        _repository.MarkCompleteAndEnqueue(backupId, tier, now);
        _log.Info($"Backed up {document.RelativePath} from {device.DisplayName} as {backupId} ({tier}).");
        return DocumentOutcome.NewVersion;
    }

    /// <summary>
    /// Sends one already-backed-up file to Drive on request, whatever tier it is in.
    ///
    /// The automatic rules are deliberately narrow -- only presentations PowerPoint recorded as
    /// opened since monitoring began -- so there has to be a way to say "this one as well" without
    /// widening them for everything.
    /// </summary>
    public UploadRequestResult RequestUpload(string backupId)
    {
        var backup = _repository.GetBackup(backupId);
        if (backup is null)
        {
            return UploadRequestResult.NotFound;
        }

        if (backup.State != BackupState.Complete)
        {
            return UploadRequestResult.NotReady;
        }

        if (backup.Tier == BackupTier.Temporary)
        {
            return Promote(backup) ? UploadRequestResult.Queued : UploadRequestResult.Failed;
        }

        var upload = _repository.GetUpload(backupId);
        if (upload?.State == UploadState.Done)
        {
            return UploadRequestResult.AlreadyUploaded;
        }

        // Retained but stalled, or queued and simply not reached yet. Either way, make it due now.
        _repository.RequeueUpload(backupId, _time.GetUtcNow());
        return UploadRequestResult.Queued;
    }

    /// <summary>
    /// Moves an already-verified temporary copy into the retained tree and queues its upload.
    /// The user's document is not touched: these bytes were checked when they were first copied.
    ///
    /// The database is updated <b>before</b> the file moves. If the process dies in between, the row
    /// points at a retained path that does not exist yet, and startup recovery finds the bytes at
    /// the temporary path this same backup id maps to and completes the move. Doing it the other
    /// way round would leave the row pointing at a path whose file had already been moved away.
    /// </summary>
    private bool Promote(BackupRecord record)
    {
        var from = _paths.ResolveArchivePath(record.LocalRelativePath);
        if (!File.Exists(from))
        {
            _repository.LogIssue("PromoteFailed", record.DeviceId, record.RelativePath, "Archive copy is missing.", _time.GetUtcNow());
            return false;
        }

        var to = ArchiveLayout.PathFor(BackupTier.Retained, record.DeviceId, record.Id, record.FileName);
        _repository.PromoteToRetained(record.Id, to, _time.GetUtcNow());

        if (!TryMove(from, _paths.ResolveArchivePath(to)))
        {
            // The row is already retained, so the sweep will not touch it and startup recovery
            // will move the bytes. Nothing is lost by returning here.
            _repository.LogIssue(
                "PromoteMovePending",
                record.DeviceId,
                record.RelativePath,
                "Tier updated but the archive file has not moved yet; recovery will finish it.",
                _time.GetUtcNow());
            return false;
        }

        TryRemoveDirectory(Path.GetDirectoryName(from));
        _log.Info($"Promoted {record.RelativePath} to the retained tier; queued for upload.");
        return true;
    }

    internal static bool TryMove(string from, string to)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes an archive folder only while it is empty, so no backup can be lost here.</summary>
    private static void TryRemoveDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
