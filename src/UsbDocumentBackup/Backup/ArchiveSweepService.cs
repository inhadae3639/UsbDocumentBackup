using UsbDocumentBackup.Storage;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.Backup;

public sealed record SweepReport(int TemporaryRemoved, int RetainedLocalCopiesReleased, int Blocked);

/// <summary>
/// Frees disk space once a backup no longer needs to be held locally.
///
/// Two different rules, deliberately kept apart:
/// <list type="bullet">
/// <item>Temporary backups have no other copy anywhere, so they go purely on age, row and all.</item>
/// <item>Retained presentations are only released once Drive has confirmed the upload. Until then
/// they are kept no matter how old they are, because deleting one would leave no copy at all.
/// Their database row always survives, so the hash and the Drive file id remain available.</item>
/// </list>
/// The two tiers live in separate directories, so the age-only rule physically cannot reach a
/// presentation that has not been uploaded.
/// </summary>
public sealed class ArchiveSweepService
{
    private readonly AppPaths _paths;
    private readonly BackupRepository _repository;
    private readonly Log _log;
    private readonly TimeProvider _time;
    private readonly int _retentionDays;

    public ArchiveSweepService(
        AppPaths paths,
        BackupRepository repository,
        Log log,
        int retentionDays,
        TimeProvider? time = null)
    {
        _paths = paths;
        _repository = repository;
        _log = log;
        _retentionDays = retentionDays;
        _time = time ?? TimeProvider.System;
    }

    public SweepReport Run()
    {
        if (_retentionDays <= 0)
        {
            return new SweepReport(0, 0, 0);
        }

        var cutoff = _time.GetUtcNow().AddDays(-_retentionDays);

        var temporaryRemoved = 0;
        foreach (var record in _repository.ListExpiredTemporary(cutoff))
        {
            if (!IsUnderTemporaryRoot(record.LocalRelativePath))
            {
                // A temporary row that does not point into the temporary tree is a bug, not a
                // licence to delete something else.
                _repository.LogIssue(
                    "SweepPathMismatch",
                    record.DeviceId,
                    record.LocalRelativePath,
                    "Temporary backup outside the temporary folder; left alone.",
                    _time.GetUtcNow());
                continue;
            }

            // Drop the row only once the bytes are actually gone. Deleting it after a failed
            // delete would strand the file: no row means no future sweep would ever retry it.
            if (!DeleteArchiveFile(record))
            {
                continue;
            }

            _repository.DeleteBackup(record.Id);
            temporaryRemoved++;
        }

        var released = 0;
        var blocked = 0;
        foreach (var record in _repository.ListRetainedWithConfirmedUpload(cutoff))
        {
            var path = _paths.ResolveArchivePath(record.LocalRelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            if (!IsUnderRetainedRoot(record.LocalRelativePath))
            {
                blocked++;
                continue;
            }

            if (DeleteArchiveFile(record))
            {
                released++;
            }
        }

        if (temporaryRemoved + released + blocked > 0)
        {
            _log.Info(
                $"Sweep: temporary removed={temporaryRemoved} retained local copies released={released} blocked={blocked}.");
        }

        return new SweepReport(temporaryRemoved, released, blocked);
    }

    private static bool IsUnderTemporaryRoot(string localRelativePath) =>
        HasFirstSegment(localRelativePath, AppPaths.TemporaryFolder);

    private static bool IsUnderRetainedRoot(string localRelativePath) =>
        HasFirstSegment(localRelativePath, AppPaths.RetainedFolder);

    private static bool HasFirstSegment(string localRelativePath, string expected)
    {
        var segments = localRelativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[0].Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the archive file is gone afterwards, whether we deleted it or it had
    /// already vanished. False means it is still there and the caller must keep its record.</summary>
    private bool DeleteArchiveFile(BackupRecord record)
    {
        var path = _paths.ResolveArchivePath(record.LocalRelativePath);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var directory = Path.GetDirectoryName(path);
            if (directory is not null
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically a sharing violation because something still has the file open. Keep the
            // record so the next sweep tries again.
            _repository.LogIssue("SweepFailed", record.DeviceId, path, ex.Message, _time.GetUtcNow());
            return false;
        }
    }
}
