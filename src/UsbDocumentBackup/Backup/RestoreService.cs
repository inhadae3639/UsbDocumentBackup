using UsbDocumentBackup.GoogleDrive;
using UsbDocumentBackup.Storage;

namespace UsbDocumentBackup.Backup;

public enum RestoreStatus
{
    Restored,
    ArchiveFileMissing,
    HashMismatch,
    DestinationUnavailable,
    Cancelled,
}

public sealed record RestoreResult(RestoreStatus Status, string? RestoredPath, string? Message)
{
    public bool Succeeded => Status == RestoreStatus.Restored;
}

/// <summary>
/// Copies an archived version back out to a folder the user picks. An existing file with the same
/// name is never overwritten, and the restored copy is hash-checked before it is reported as good.
/// </summary>
public sealed class RestoreService
{
    private readonly AppPaths _paths;
    private readonly BackupRepository _repository;
    private readonly Func<IDriveClient?> _driveClientFactory;

    public RestoreService(AppPaths paths, BackupRepository repository, Func<IDriveClient?>? driveClientFactory = null)
    {
        _paths = paths;
        _repository = repository;
        _driveClientFactory = driveClientFactory ?? (() => null);
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupId,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        var record = _repository.GetBackup(backupId);
        if (record is null)
        {
            return new RestoreResult(RestoreStatus.ArchiveFileMissing, null, "No such backup.");
        }

        var source = _paths.ResolveArchivePath(record.LocalRelativePath);
        string target;

        if (!File.Exists(source))
        {
            // The sweep releases a local copy once Drive confirms it, so this is the expected path
            // for an older backup rather than an error.
            return await RestoreFromDriveAsync(record, targetDirectory, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            target = MakeUniquePath(Path.Combine(targetDirectory, record.FileName));
            File.Copy(source, target, overwrite: false);
        }
        catch (OperationCanceledException)
        {
            return new RestoreResult(RestoreStatus.Cancelled, null, "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RestoreResult(RestoreStatus.DestinationUnavailable, null, ex.Message);
        }

        var hash = await FileCopier.ComputeHashAsync(target, RateLimiter.Unlimited, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, record.Sha256, StringComparison.Ordinal))
        {
            return new RestoreResult(RestoreStatus.HashMismatch, target, "Restored file did not match the recorded SHA-256.");
        }

        return new RestoreResult(RestoreStatus.Restored, target, null);
    }

    /// <summary>
    /// Pulls the version back down from Drive. The downloaded bytes are hash-checked against the
    /// recorded SHA-256 exactly like a local restore, so a truncated download cannot pass as good.
    /// </summary>
    private async Task<RestoreResult> RestoreFromDriveAsync(
        BackupRecord record,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var upload = _repository.GetUpload(record.Id);
        if (upload?.DriveFileId is not { Length: > 0 } fileId || upload.State != UploadState.Done)
        {
            return new RestoreResult(
                RestoreStatus.ArchiveFileMissing,
                null,
                "The local copy is gone and this version was never confirmed in Drive.");
        }

        var client = _driveClientFactory();
        if (client is null)
        {
            return new RestoreResult(
                RestoreStatus.ArchiveFileMissing,
                null,
                "The local copy is gone. Connect the Google account to download it from Drive.");
        }

        string target;
        try
        {
            Directory.CreateDirectory(targetDirectory);
            target = MakeUniquePath(Path.Combine(targetDirectory, record.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RestoreResult(RestoreStatus.DestinationUnavailable, null, ex.Message);
        }

        var download = await client.DownloadAsync(fileId, target, cancellationToken).ConfigureAwait(false);
        if (download.Outcome != DriveOutcome.Completed)
        {
            return new RestoreResult(RestoreStatus.DestinationUnavailable, null, download.Message ?? "Drive download failed.");
        }

        var hash = await FileCopier.ComputeHashAsync(target, RateLimiter.Unlimited, cancellationToken).ConfigureAwait(false);
        return string.Equals(hash, record.Sha256, StringComparison.Ordinal)
            ? new RestoreResult(RestoreStatus.Restored, target, "Downloaded from Drive and verified.")
            : new RestoreResult(RestoreStatus.HashMismatch, target, "The file downloaded from Drive did not match the recorded SHA-256.");
    }

    /// <summary>Appends " (2)", " (3)", ... rather than replacing a file that is already there.</summary>
    internal static string MakeUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var i = 2; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a free file name.");
    }
}
