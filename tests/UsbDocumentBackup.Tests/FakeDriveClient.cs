using UsbDocumentBackup.GoogleDrive;

namespace UsbDocumentBackup.Tests;

/// <summary>
/// An in-memory Drive that can be told to fail in the specific ways the real one does: dropping a
/// success response, expiring a resumable session, running out of storage, revoking access.
/// </summary>
public sealed class FakeDriveClient : IDriveClient
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private int _idCounter;
    private int _sessionCounter;

    private sealed class Session
    {
        public required string FileId { get; init; }

        public required string Name { get; init; }

        public required long TotalBytes { get; init; }

        public List<byte> Received { get; } = [];

        public bool Expired { get; set; }
    }

    public string AccountKey { get; set; } = "tester@example.com";

    public List<string> CreatedFolders { get; } = [];

    public int GeneratedIds { get; private set; }

    public int StartedSessions { get; private set; }

    /// <summary>Files as Drive holds them, keyed by file id.</summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public Dictionary<string, string> NamesByFileId { get; } = new(StringComparer.Ordinal);

    // ---- injected failures ----

    /// <summary>Store the bytes but report a network error, as if the response was lost.</summary>
    public bool SwallowNextSuccessResponse { get; set; }

    /// <summary>Expire the session on the next status query.</summary>
    public bool ExpireNextSession { get; set; }

    /// <summary>Fail the next chunk with a retryable error.</summary>
    public int TransientChunkFailures { get; set; }

    public bool OutOfStorage { get; set; }

    public bool AuthorizationRevoked { get; set; }

    /// <summary>The refresh token itself is gone, as after the seven-day testing-mode expiry.</summary>
    public bool RefreshTokenExpired { get; set; }

    public Task<string> GetAccountKeyAsync(CancellationToken cancellationToken)
    {
        if (RefreshTokenExpired)
        {
            throw new Google.Apis.Auth.OAuth2.Responses.TokenResponseException(
                new Google.Apis.Auth.OAuth2.Responses.TokenErrorResponse { Error = "invalid_grant" });
        }

        return Task.FromResult(AccountKey);
    }

    public Task<string> EnsureFolderAsync(string name, string? parentId, CancellationToken cancellationToken)
    {
        var id = "folder:" + (parentId is null ? string.Empty : parentId + "/") + name;
        if (!CreatedFolders.Contains(id))
        {
            CreatedFolders.Add(id);
        }

        return Task.FromResult(id);
    }

    public Task<string> GenerateFileIdAsync(CancellationToken cancellationToken)
    {
        GeneratedIds++;
        return Task.FromResult("file-" + (++_idCounter));
    }

    public Task<DriveFileInfo?> TryGetFileAsync(string fileId, CancellationToken cancellationToken) =>
        Task.FromResult(_files.TryGetValue(fileId, out var bytes)
            ? new DriveFileInfo(fileId, NamesByFileId.GetValueOrDefault(fileId, fileId), bytes.Length)
            : null);

    public Task<string> StartResumableUploadAsync(
        string fileId,
        string name,
        string parentId,
        long totalBytes,
        IReadOnlyDictionary<string, string> appProperties,
        CancellationToken cancellationToken)
    {
        StartedSessions++;
        var uri = "https://upload.example/session/" + (++_sessionCounter);
        _sessions[uri] = new Session { FileId = fileId, Name = name, TotalBytes = totalBytes };
        NamesByFileId[fileId] = name;
        return Task.FromResult(uri);
    }

    public Task<long?> GetResumeOffsetAsync(string sessionUri, long totalBytes, CancellationToken cancellationToken)
    {
        if (ExpireNextSession)
        {
            ExpireNextSession = false;
            if (_sessions.TryGetValue(sessionUri, out var expiring))
            {
                expiring.Expired = true;
            }

            return Task.FromResult<long?>(null);
        }

        if (!_sessions.TryGetValue(sessionUri, out var session) || session.Expired)
        {
            return Task.FromResult<long?>(null);
        }

        return Task.FromResult<long?>(session.Received.Count);
    }

    public async Task<DriveResult> UploadChunkAsync(
        string sessionUri,
        Stream content,
        long offset,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (AuthorizationRevoked)
        {
            return new DriveResult(DriveOutcome.NeedsAttention, "Google rejected the credentials; reconnect the account.");
        }

        if (OutOfStorage)
        {
            return new DriveResult(DriveOutcome.NeedsAttention, "The Google account is out of Drive storage.");
        }

        if (TransientChunkFailures > 0)
        {
            TransientChunkFailures--;
            return new DriveResult(DriveOutcome.Transient, "Drive returned 503.", TimeSpan.Zero);
        }

        if (!_sessions.TryGetValue(sessionUri, out var session) || session.Expired)
        {
            return new DriveResult(DriveOutcome.Transient, "Session gone.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        session.Received.AddRange(buffer.ToArray());

        if (session.Received.Count < session.TotalBytes)
        {
            return new DriveResult(DriveOutcome.Incomplete);
        }

        _files[session.FileId] = session.Received.ToArray();

        if (SwallowNextSuccessResponse)
        {
            SwallowNextSuccessResponse = false;
            // The bytes are safely stored but the caller never learns that, which is exactly the
            // situation the reserved file id is meant to survive.
            return new DriveResult(DriveOutcome.Transient, "Connection dropped before the response arrived.");
        }

        return DriveResult.Completed;
    }

    public Task<DriveResult> DownloadAsync(string fileId, string destinationPath, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue(fileId, out var bytes))
        {
            return Task.FromResult(new DriveResult(DriveOutcome.NeedsAttention, "No such file."));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, bytes);
        return Task.FromResult(DriveResult.Completed);
    }
}
