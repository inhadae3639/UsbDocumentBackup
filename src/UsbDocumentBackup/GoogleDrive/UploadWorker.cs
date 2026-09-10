using System.Globalization;
using UsbDocumentBackup.Backup;
using UsbDocumentBackup.Storage;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.GoogleDrive;

/// <summary>
/// Uploads verified local copies to Drive, one at a time.
///
/// Three properties matter, and each is enforced by a specific step below:
/// <list type="number">
/// <item>A version is never duplicated. The Drive file id is reserved and written to the database
/// before any bytes move, and every retry starts by asking Drive whether that id already exists.</item>
/// <item>An interrupted transfer resumes. The session URI and byte offset are persisted, so a
/// shutdown mid-upload continues rather than restarting.</item>
/// <item>Hopeless failures stop. Running out of storage or losing authorisation is surfaced instead
/// of retried forever.</item>
/// </list>
/// </summary>
public sealed class UploadWorker
{
    /// <summary>Drive requires resumable chunks to be a multiple of 256 KiB.</summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    private const int MaxTransientAttempts = 12;

    private readonly AppPaths _paths;
    private readonly BackupRepository _repository;
    private readonly Log _log;
    private readonly TimeProvider _time;
    private readonly Func<IDriveClient?> _clientFactory;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Action _onNeedsReconnect;

    public UploadWorker(
        AppPaths paths,
        BackupRepository repository,
        AppSettings settings,
        SettingsStore settingsStore,
        Func<IDriveClient?> clientFactory,
        Log log,
        Action? onNeedsReconnect = null,
        TimeProvider? time = null)
    {
        _paths = paths;
        _repository = repository;
        _settings = settings;
        _settingsStore = settingsStore;
        _clientFactory = clientFactory;
        _log = log;
        _onNeedsReconnect = onNeedsReconnect ?? (() => { });
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Drains the queue. Returns how many uploads completed. Does nothing at all when no account is
    /// connected: work simply stays queued, which is what keeps local backups usable on their own.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var client = _clientFactory();
        if (client is null)
        {
            return 0;
        }

        var completed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var claimed = _repository.ClaimNextUpload(_time.GetUtcNow());
            if (claimed is null)
            {
                break;
            }

            var (backup, upload) = claimed.Value;
            try
            {
                if (await UploadOneAsync(client, backup, upload, cancellationToken).ConfigureAwait(false))
                {
                    completed++;
                }
            }
            catch (OperationCanceledException)
            {
                // Put it back so a shutdown does not strand the row in Uploading.
                _repository.MarkUploadRetry(backup.Id, "Cancelled.", _time.GetUtcNow());
                break;
            }
            catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex)
            {
                // Refreshing the access token failed. An OAuth project still in testing expires its
                // refresh token every seven days, so this is a routine event, not a transient one:
                // no amount of backoff will fix it and the whole queue is blocked until someone
                // reconnects. The local copies stay put because Drive never confirmed them.
                _repository.MarkUploadNeedsAttention(
                    backup.Id,
                    "Google 재연결이 필요합니다 (" + (ex.Error?.Error ?? "invalid_grant") + ").");
                _onNeedsReconnect();
                _log.Warn("Google authorisation is no longer valid; uploads paused until reconnected.");
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"Upload of {backup.FileName} failed unexpectedly.", ex);
                Retry(backup.Id, upload.Attempts, ex.Message);
            }
        }

        return completed;
    }

    private async Task<bool> UploadOneAsync(
        IDriveClient client,
        BackupRecord backup,
        UploadRecord upload,
        CancellationToken cancellationToken)
    {
        var localPath = _paths.ResolveArchivePath(backup.LocalRelativePath);
        if (!File.Exists(localPath))
        {
            // The sweep only releases a local copy after Drive confirms it, so this means the file
            // was lost some other way. There is nothing to upload and retrying cannot help.
            _repository.MarkUploadNeedsAttention(backup.Id, "로컬 보관본이 없어 올릴 파일이 없습니다.");
            _log.Warn($"Cannot upload {backup.FileName}: the archive copy at {localPath} is gone.");
            return false;
        }

        string accountKey;
        try
        {
            accountKey = await client.GetAccountKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Retry(backup.Id, upload.Attempts, "Could not reach Drive: " + ex.Message);
            return false;
        }

        // A reconnect that picked a different account must not quietly push a backlog of one
        // person's presentations into another account.
        if (upload.AccountKey is { Length: > 0 } previous
            && !previous.Equals(accountKey, StringComparison.OrdinalIgnoreCase))
        {
            _repository.MarkUploadNeedsAttention(
                backup.Id,
                $"{previous} 계정용으로 대기 중인데 지금 연결된 계정은 {accountKey} 입니다.");
            _log.Warn($"Not uploading {backup.FileName}: queued for {previous}, connected as {accountKey}.");
            return false;
        }

        var folderId = await EnsureFolderAsync(client, backup, upload, cancellationToken).ConfigureAwait(false);
        if (folderId is null)
        {
            Retry(backup.Id, upload.Attempts, "Could not create the Drive folder.");
            return false;
        }

        // Reserve the id and write it down first. If everything after this dies, the retry asks
        // Drive about this exact id rather than uploading a second copy.
        var fileId = upload.DriveFileId;
        if (string.IsNullOrEmpty(fileId))
        {
            fileId = await client.GenerateFileIdAsync(cancellationToken).ConfigureAwait(false);
            _repository.SaveUploadTarget(backup.Id, folderId, fileId, accountKey);
        }
        else
        {
            _repository.SaveUploadTarget(backup.Id, folderId, fileId, accountKey);
        }

        // Did a previous attempt already succeed and we simply never heard the answer?
        var existing = await client.TryGetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Size == backup.SizeBytes)
        {
            _repository.MarkUploadDone(backup.Id, fileId);
            _log.Info($"{backup.FileName} was already in Drive; no duplicate created.");
            return true;
        }

        var totalBytes = backup.SizeBytes;
        var sessionUri = upload.ResumeUri;
        long offset = 0;

        if (!string.IsNullOrEmpty(sessionUri))
        {
            var resumed = await client.GetResumeOffsetAsync(sessionUri, totalBytes, cancellationToken).ConfigureAwait(false);
            if (resumed is null)
            {
                // Sessions expire. A new one is required; the reserved file id still guards against
                // a duplicate.
                sessionUri = null;
                _repository.SaveUploadProgress(backup.Id, null, 0);
            }
            else if (resumed.Value >= totalBytes)
            {
                _repository.MarkUploadDone(backup.Id, fileId);
                return true;
            }
            else
            {
                offset = resumed.Value;
            }
        }

        if (string.IsNullOrEmpty(sessionUri))
        {
            sessionUri = await client.StartResumableUploadAsync(
                fileId,
                DriveFileName(backup),
                folderId,
                totalBytes,
                AppProperties(backup),
                cancellationToken).ConfigureAwait(false);

            offset = 0;
            _repository.SaveUploadProgress(backup.Id, sessionUri, 0);
        }

        var limiter = new RateLimiter(_settings.UploadBytesPerSecond);

        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = (int)Math.Min(ChunkSize, totalBytes - offset);
            await using var chunk = new ChunkStream(localPath, offset, length, limiter, cancellationToken);

            var result = await client
                .UploadChunkAsync(sessionUri, chunk, offset, totalBytes, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case DriveOutcome.Completed:
                    _repository.MarkUploadDone(backup.Id, fileId);
                    _log.Info($"Uploaded {backup.FileName} to Drive.");
                    return true;

                case DriveOutcome.Incomplete:
                    offset += length;
                    _repository.SaveUploadProgress(backup.Id, sessionUri, offset);
                    break;

                case DriveOutcome.Transient:
                    _repository.SaveUploadProgress(backup.Id, sessionUri, offset);
                    Retry(backup.Id, upload.Attempts, result.Message ?? "Transient Drive error.", result.RetryAfter);
                    return false;

                case DriveOutcome.NeedsAttention:
                    _repository.MarkUploadNeedsAttention(backup.Id, result.Message ?? "Drive가 업로드를 거부했습니다.");
                    _log.Warn($"Drive refused {backup.FileName}: {result.Message}");
                    if (result.Message?.Contains("reconnect", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _onNeedsReconnect();
                    }

                    return false;

                default:
                    _repository.MarkUploadRetry(backup.Id, "Cancelled.", _time.GetUtcNow());
                    return false;
            }
        }

        // Every byte was accepted with 308s and Drive never said "done"; ask it directly.
        var final = await client.TryGetFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (final is not null && final.Size == totalBytes)
        {
            _repository.MarkUploadDone(backup.Id, fileId);
            return true;
        }

        Retry(backup.Id, upload.Attempts, "Drive never confirmed the completed upload.");
        return false;
    }

    private async Task<string?> EnsureFolderAsync(
        IDriveClient client,
        BackupRecord backup,
        UploadRecord upload,
        CancellationToken cancellationToken)
    {
        if (upload.DriveFolderId is { Length: > 0 } known)
        {
            return known;
        }

        try
        {
            // Both folders are remembered by id, never re-found by name. A Drive id survives the
            // user renaming or moving the folder, so tidying up in Drive does not split the
            // backups across an old folder and a freshly created one. The only case that forces a
            // new folder is the remembered one being deleted or trashed.
            var rootId = _settings.DriveFolderId;
            if (!string.IsNullOrEmpty(rootId)
                && !await client.FolderExistsAsync(rootId, cancellationToken).ConfigureAwait(false))
            {
                _log.Warn("The Drive backup folder is gone; creating a new one.");
                rootId = null;
            }

            if (string.IsNullOrEmpty(rootId))
            {
                var folderName = string.IsNullOrWhiteSpace(_settings.DriveFolderName)
                    ? "USB Document Backups"
                    : _settings.DriveFolderName.Trim();
                rootId = await client.EnsureFolderAsync(folderName, null, cancellationToken).ConfigureAwait(false);
                _settings.DriveFolderId = rootId;
                _settingsStore.Save(_settings);
            }

            // One subfolder per device so the web view is navigable without this app.
            var deviceFolderId = _repository.GetDeviceDriveFolder(backup.DeviceId);
            if (!string.IsNullOrEmpty(deviceFolderId)
                && !await client.FolderExistsAsync(deviceFolderId, cancellationToken).ConfigureAwait(false))
            {
                deviceFolderId = null;
            }

            if (string.IsNullOrEmpty(deviceFolderId))
            {
                var device = _repository.ListDevices().FirstOrDefault(d => d.Id == backup.DeviceId);
                var deviceName = Sanitize(device?.DisplayName ?? backup.DeviceId);
                deviceFolderId = await client.EnsureFolderAsync(deviceName, rootId, cancellationToken).ConfigureAwait(false);
                _repository.SaveDeviceDriveFolder(backup.DeviceId, deviceFolderId);
            }

            return deviceFolderId;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _log.Warn($"Could not prepare the Drive folder: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Each version is an independent file, so the name carries the backup time. Without it the
    /// Drive web view would show a stack of identically named files.
    /// </summary>
    internal static string DriveFileName(BackupRecord backup)
    {
        var stem = Path.GetFileNameWithoutExtension(backup.FileName);
        var extension = Path.GetExtension(backup.FileName);
        var stamp = backup.BackedUpUtc.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        return Sanitize($"{stem} ({stamp}){extension}");
    }

    /// <summary>Identifying details, so a file found in Drive can be traced back to its origin.</summary>
    private Dictionary<string, string> AppProperties(BackupRecord backup)
    {
        var device = _repository.ListDevices().FirstOrDefault(d => d.Id == backup.DeviceId);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["backupId"] = backup.Id,
            ["device"] = device?.DisplayName ?? backup.DeviceId,
            ["originalPath"] = Truncate(backup.RelativePath, 120),
            ["backedUpUtc"] = backup.BackedUpUtc.ToString("o", CultureInfo.InvariantCulture),
            ["sha256"] = backup.Sha256,
        };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[^max..];

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "backup" : cleaned;
    }

    private void Retry(string backupId, int attempts, string error, TimeSpan? retryAfter = null)
    {
        if (attempts >= MaxTransientAttempts)
        {
            _repository.MarkUploadNeedsAttention(
                backupId,
                $"{attempts}회 시도 후 중단했습니다. 마지막 오류: {error}");
            return;
        }

        // Exponential backoff with jitter, capped, and always honouring a server's Retry-After.
        var backoff = TimeSpan.FromSeconds(Math.Min(600, Math.Pow(2, Math.Min(attempts, 9))));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5000));
        var delay = retryAfter is { } served && served > backoff ? served : backoff;

        _repository.MarkUploadRetry(backupId, error, _time.GetUtcNow() + delay + jitter);
    }
}

/// <summary>
/// Reads one slice of the archive file, throttled. A chunk is streamed rather than buffered so a
/// large deck never has to fit in memory.
/// </summary>
internal sealed class ChunkStream : Stream
{
    private readonly FileStream _file;
    private readonly RateLimiter _limiter;
    private readonly CancellationToken _cancellationToken;
    private long _remaining;

    public ChunkStream(string path, long offset, int length, RateLimiter limiter, CancellationToken cancellationToken)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        _file.Seek(offset, SeekOrigin.Begin);
        _remaining = length;
        _limiter = limiter;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _remaining;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var window = buffer[..(int)Math.Min(buffer.Length, _remaining)];
        var read = await _file.ReadAsync(window, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _remaining -= read;
            await _limiter.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), _cancellationToken).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}
