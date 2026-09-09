using System.Security.Cryptography;

namespace UsbDocumentBackup.Backup;

public enum CopyStatus
{
    /// <summary>Written, re-read and hash-verified.</summary>
    Copied,

    /// <summary>Size or timestamp moved while we were reading; the copy is not a usable snapshot.</summary>
    SourceChangedDuringCopy,

    /// <summary>Locked, denied, or vanished. Worth retrying later; never a completed backup.</summary>
    SourceUnreadable,

    /// <summary>The archive disk refused the write, most often for lack of space.</summary>
    DestinationUnavailable,

    /// <summary>The device went away or the user paused; nothing was completed.</summary>
    Cancelled,
}

public sealed record CopyOutcome(CopyStatus Status, string? Sha256, long BytesCopied, string? Message)
{
    public bool Succeeded => Status == CopyStatus.Copied;
}

/// <summary>
/// Copies one source document into the archive without ever writing to the source.
/// The copy lands on a ".partial" file, is verified by re-reading it from disk, and is only then
/// renamed to its final name, so an interrupted copy can never look like a finished backup.
/// </summary>
public sealed class FileCopier
{
    private const int BufferSize = 256 * 1024;
    public const string PartialSuffix = ".partial";

    private readonly RateLimiter _sourceReadLimiter;
    private readonly long _minimumFreeBytes;
    private readonly TimeSpan _stabilityDelay;

    public FileCopier(RateLimiter sourceReadLimiter, long minimumFreeBytes, TimeSpan? stabilityDelay = null)
    {
        _sourceReadLimiter = sourceReadLimiter;
        _minimumFreeBytes = minimumFreeBytes;
        _stabilityDelay = stabilityDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Waits for a file that looks like it is still being written to settle. This reduces wasted
    /// copies of a document mid-save; it is not a guarantee that no write follows.
    /// </summary>
    public async Task<bool> WaitForStableAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var first = new FileInfo(path);
            if (!first.Exists)
            {
                return false;
            }

            var length = first.Length;
            var written = first.LastWriteTimeUtc;

            await Task.Delay(_stabilityDelay, cancellationToken).ConfigureAwait(false);

            var second = new FileInfo(path);
            return second.Exists && second.Length == length && second.LastWriteTimeUtc == written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<CopyOutcome> CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var partialPath = destinationPath + PartialSuffix;
        var destinationDirectory = Path.GetDirectoryName(destinationPath);

        long expectedLength;
        DateTime expectedWriteUtc;
        try
        {
            var before = new FileInfo(sourcePath);
            if (!before.Exists)
            {
                return new CopyOutcome(CopyStatus.SourceUnreadable, null, 0, "Source no longer exists.");
            }

            expectedLength = before.Length;
            expectedWriteUtc = before.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CopyOutcome(CopyStatus.SourceUnreadable, null, 0, ex.Message);
        }

        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            try
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CopyOutcome(CopyStatus.DestinationUnavailable, null, 0, ex.Message);
            }

            var spaceProblem = CheckFreeSpace(destinationDirectory, expectedLength);
            if (spaceProblem is not null)
            {
                return new CopyOutcome(CopyStatus.DestinationUnavailable, null, 0, spaceProblem);
            }
        }

        string streamedHash;
        long copied;
        try
        {
            (streamedHash, copied) = await CopyStreamAsync(sourcePath, partialPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            return new CopyOutcome(CopyStatus.Cancelled, null, 0, "Cancelled.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            TryDelete(partialPath);
            return new CopyOutcome(CopyStatus.SourceUnreadable, null, 0, ex.Message);
        }
        catch (IOException ex)
        {
            TryDelete(partialPath);
            // Covers both a sharing violation on the source and a full or disconnected archive disk.
            var status = File.Exists(sourcePath) ? CopyStatus.SourceUnreadable : CopyStatus.DestinationUnavailable;
            return new CopyOutcome(status, null, 0, ex.Message);
        }

        // The source must look exactly as it did before the read; otherwise what we captured is a
        // mix of two versions. This detects the common cases, not every possible interleaving.
        try
        {
            var after = new FileInfo(sourcePath);
            if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != expectedWriteUtc)
            {
                TryDelete(partialPath);
                return new CopyOutcome(CopyStatus.SourceChangedDuringCopy, null, 0, "Source changed while copying.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            return new CopyOutcome(CopyStatus.SourceUnreadable, null, 0, ex.Message);
        }

        if (copied != expectedLength)
        {
            TryDelete(partialPath);
            return new CopyOutcome(
                CopyStatus.SourceChangedDuringCopy,
                null,
                copied,
                $"Expected {expectedLength} bytes but read {copied}.");
        }

        // Re-read what actually landed on disk rather than trusting the write path.
        string verifiedHash;
        try
        {
            var written = new FileInfo(partialPath);
            if (!written.Exists || written.Length != expectedLength)
            {
                TryDelete(partialPath);
                return new CopyOutcome(CopyStatus.DestinationUnavailable, null, copied, "Archive copy has the wrong size.");
            }

            verifiedHash = await ComputeHashAsync(partialPath, RateLimiter.Unlimited, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            return new CopyOutcome(CopyStatus.Cancelled, null, copied, "Cancelled.");
        }
        catch (IOException ex)
        {
            TryDelete(partialPath);
            return new CopyOutcome(CopyStatus.DestinationUnavailable, null, copied, ex.Message);
        }

        if (!string.Equals(verifiedHash, streamedHash, StringComparison.Ordinal))
        {
            TryDelete(partialPath);
            return new CopyOutcome(CopyStatus.DestinationUnavailable, null, copied, "Archive copy failed hash verification.");
        }

        return new CopyOutcome(CopyStatus.Copied, verifiedHash, copied, null);
    }

    /// <summary>
    /// Promotes a verified ".partial" file to its final name. Kept separate from the copy so the
    /// caller can record the backup row first and reconcile after a crash.
    /// </summary>
    public static void Promote(string destinationPath) =>
        File.Move(destinationPath + PartialSuffix, destinationPath, overwrite: false);

    public static async Task<string> ComputeHashAsync(string path, RateLimiter limiter, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await limiter.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<(string Hash, long Bytes)> CopyStreamAsync(
        string sourcePath,
        string partialPath,
        CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite|Delete keeps PowerPoint free to keep writing or delete the file while
        // we read; we detect that afterwards rather than blocking the application.
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var destination = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await _sourceReadLimiter.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);

        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
    }

    private string? CheckFreeSpace(string destinationDirectory, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < requiredBytes + _minimumFreeBytes)
            {
                return $"Not enough free space on {root}: {available} bytes available, "
                    + $"{requiredBytes} needed plus a {_minimumFreeBytes} byte reserve.";
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // If we cannot measure it, let the write itself fail rather than blocking the backup.
            return null;
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind for the startup sweep to clean up.
        }
    }
}
