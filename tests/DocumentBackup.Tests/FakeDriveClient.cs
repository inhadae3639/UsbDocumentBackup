using DocumentBackup.GoogleDrive;

namespace DocumentBackup.Tests;

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

    private readonly Dictionary<string, string> _folderIdsByPath = new(StringComparer.Ordinal);
    private int _folderCounter;

    public Task<string> EnsureFolderAsync(string name, string? parentId, CancellationToken cancellationToken)
    {
        var path = (parentId ?? string.Empty) + "/" + name;

        // A folder that is still there is found by name. One that was trashed is not, and Drive
        // hands back a brand new id for the replacement -- which is what makes a stale stored id
        // genuinely stale.
        if (_folderIdsByPath.TryGetValue(path, out var existing) && !TrashedFolders.Contains(existing))
        {
            return Task.FromResult(existing);
        }

        var id = "folder-" + (++_folderCounter);
        _folderIdsByPath[path] = id;
        CreatedFolders.Add(id);
        return Task.FromResult(id);
    }

    /// <summary>Folder ids the user deleted or moved to the trash in Drive.</summary>
    public HashSet<string> TrashedFolders { get; } = new(StringComparer.Ordinal);

    public int FolderExistenceChecks { get; private set; }

    public Task<bool> FolderExistsAsync(string folderId, CancellationToken cancellationToken)
    {
        FolderExistenceChecks++;
        return Task.FromResult(CreatedFolders.Contains(folderId) && !TrashedFolders.Contains(folderId));
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

    /// <summary>Content-Range values Drive was asked to accept, for assertions.</summary>
    public List<(long Offset, long Length, long Total)> ChunkRanges { get; } = [];

    public async Task<DriveResult> UploadChunkAsync(
        string sessionUri,
        Stream content,
        long offset,
        long chunkLength,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        ChunkRanges.Add((offset, chunkLength, totalBytes));
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
        var body = buffer.ToArray();

        // Drive validates the declared range against the body and the bytes it already holds. The
        // earlier fake accepted anything, which let a wrong Content-Range ship: real uploads of
        // files bigger than one chunk came back as
        // "There were N byte(s) in the request body. There should be ...".
        if (body.Length != chunkLength)
        {
            return new DriveResult(
                DriveOutcome.NeedsAttention,
                $"Drive returned 400: There were {body.Length} byte(s) in the request body. "
                + $"There should be {chunkLength}.");
        }

        if (offset != session.Received.Count)
        {
            return new DriveResult(
                DriveOutcome.NeedsAttention,
                $"Drive returned 400: expected the chunk to start at {session.Received.Count} but it started at {offset}.");
        }

        if (offset + chunkLength > totalBytes)
        {
            return new DriveResult(
                DriveOutcome.NeedsAttention,
                "Drive returned 400: the range runs past the declared total size.");
        }

        session.Received.AddRange(body);

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
