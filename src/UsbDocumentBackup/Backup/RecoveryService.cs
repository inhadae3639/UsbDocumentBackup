using UsbDocumentBackup.Storage;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.Backup;

public sealed record RecoveryReport(
    int Completed,
    int Discarded,
    int OrphanPartialsRemoved,
    int UploadsRequeued,
    int PromotionsFinished);

/// <summary>
/// Runs once at startup. The archive and the database are not updated atomically, so anything left
/// half-finished by a crash, a forced shutdown or a yanked USB stick is reconciled here.
/// An incomplete file is never presented as a finished backup.
/// </summary>
public sealed class RecoveryService
{
    private readonly AppPaths _paths;
    private readonly BackupRepository _repository;
    private readonly Log _log;
    private readonly TimeProvider _time;

    public RecoveryService(AppPaths paths, BackupRepository repository, Log log, TimeProvider? time = null)
    {
        _paths = paths;
        _repository = repository;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public async Task<RecoveryReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var completed = 0;
        var discarded = 0;
        var keptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pending in _repository.ListPending())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var finalPath = _paths.ResolveArchivePath(pending.LocalRelativePath);
            var partialPath = finalPath + FileCopier.PartialSuffix;

            // Crash after the rename but before the row was marked complete: the bytes are there
            // and provably correct, so finish the job instead of throwing the work away.
            if (File.Exists(finalPath) && await MatchesAsync(finalPath, pending, cancellationToken).ConfigureAwait(false))
            {
                _repository.MarkCompleteAndEnqueue(pending.Id, pending.Tier, _time.GetUtcNow());
                keptPaths.Add(Path.GetFullPath(finalPath));
                completed++;
                continue;
            }

            // Anything else is unproven. Drop the row and the bytes; the next scan copies it again.
            TryDeleteFile(partialPath);
            TryDeleteFile(finalPath);
            TryRemoveDirectory(Path.GetDirectoryName(finalPath));
            _repository.DeleteBackup(pending.Id);
            discarded++;
        }

        var orphans = RemoveOrphanPartials(keptPaths);
        var promotions = await FinishInterruptedPromotionsAsync(cancellationToken).ConfigureAwait(false);
        var requeued = _repository.ResetInterruptedUploads(_time.GetUtcNow());

        var report = new RecoveryReport(completed, discarded, orphans, requeued, promotions);
        if (completed + discarded + orphans + requeued + promotions > 0)
        {
            _log.Info(
                $"Recovery: completed={completed} discarded={discarded} orphanPartials={orphans} "
                + $"uploadsRequeued={requeued} promotionsFinished={promotions}.");
        }

        return report;
    }

    /// <summary>
    /// Promotion updates the database before it moves the file, so a crash in between leaves a
    /// retained row whose file is still sitting at its old temporary path. The layout is a pure
    /// function of the backup id, so the bytes can be found and moved into place here.
    ///
    /// Only backups whose upload has not completed are considered: for one Drive already has, a
    /// missing local file is the sweep having released it on purpose, not a broken promotion.
    /// </summary>
    private async Task<int> FinishInterruptedPromotionsAsync(CancellationToken cancellationToken)
    {
        var finished = 0;

        foreach (var record in _repository.ListRetainedAwaitingUpload())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var expected = _paths.ResolveArchivePath(record.LocalRelativePath);
            if (File.Exists(expected))
            {
                continue;
            }

            var strandedRelative = ArchiveLayout.OtherTierPath(
                BackupTier.Retained,
                record.DeviceId,
                record.Id,
                record.FileName);
            var stranded = _paths.ResolveArchivePath(strandedRelative);

            if (!File.Exists(stranded)
                || !await MatchesAsync(stranded, record, cancellationToken).ConfigureAwait(false))
            {
                _repository.LogIssue(
                    "RetainedCopyMissing",
                    record.DeviceId,
                    record.RelativePath,
                    "Retained backup has no local file and no recoverable copy; Drive is the only hope.",
                    _time.GetUtcNow());
                continue;
            }

            if (BackupService.TryMove(stranded, expected))
            {
                TryRemoveDirectory(Path.GetDirectoryName(stranded));
                finished++;
                _log.Info($"Finished an interrupted promotion of {record.RelativePath}.");
            }
        }

        return finished;
    }

    private async Task<bool> MatchesAsync(string path, BackupRecord record, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length != record.SizeBytes)
            {
                return false;
            }

            var hash = await FileCopier.ComputeHashAsync(path, RateLimiter.Unlimited, cancellationToken).ConfigureAwait(false);
            return string.Equals(hash, record.Sha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sweeps ".partial" files that no pending row refers to. Only ever touches our own temporary
    /// files -- finished backups are left alone even when they look unreferenced.
    /// </summary>
    private int RemoveOrphanPartials(HashSet<string> keptPaths)
    {
        if (!Directory.Exists(_paths.ArchiveRoot))
        {
            return 0;
        }

        var removed = 0;
        IEnumerable<string> partials;
        try
        {
            partials = Directory.EnumerateFiles(_paths.ArchiveRoot, "*" + FileCopier.PartialSuffix, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not sweep the archive for partial files: {ex.Message}");
            return 0;
        }

        foreach (var partial in partials.ToList())
        {
            if (keptPaths.Contains(Path.GetFullPath(partial)))
            {
                continue;
            }

            if (TryDeleteFile(partial))
            {
                removed++;
                TryRemoveDirectory(Path.GetDirectoryName(partial));
            }
        }

        return removed;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
