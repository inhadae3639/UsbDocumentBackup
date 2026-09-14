namespace UsbDocumentBackup.GoogleDrive;

/// <summary>Why an upload attempt stopped, which decides whether we retry and how soon.</summary>
public enum DriveOutcome
{
    /// <summary>The file is in Drive with the right size.</summary>
    Completed,

    /// <summary>Progress was made or the session moved on; call again to continue.</summary>
    Incomplete,

    /// <summary>Network trouble, rate limiting or a 5xx. Retry with backoff.</summary>
    Transient,

    /// <summary>Out of Drive space, permission denied, or the token is no longer valid.
    /// Retrying on a timer will never fix these, so they surface to the user instead.</summary>
    NeedsAttention,

    /// <summary>Shutting down or paused.</summary>
    Cancelled,
}

public sealed record DriveResult(DriveOutcome Outcome, string? Message = null, TimeSpan? RetryAfter = null)
{
    public static readonly DriveResult Completed = new(DriveOutcome.Completed);
}

/// <summary>Metadata Drive keeps about a file we uploaded.</summary>
public sealed record DriveFileInfo(string Id, string Name, long Size);

/// <summary>
/// The Drive operations this app needs, kept behind an interface so the upload worker's retry,
/// resume and deduplication behaviour can be tested against injected failures without a network
/// or a real Google account.
/// </summary>
public interface IDriveClient
{
    /// <summary>Identifies the connected account so a reconnect cannot silently switch accounts.</summary>
    Task<string> GetAccountKeyAsync(CancellationToken cancellationToken);

    /// <summary>Finds or creates a folder, returning its id. Idempotent.</summary>
    Task<string> EnsureFolderAsync(string name, string? parentId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a folder we remembered by id is still usable. False when it was deleted or moved to
    /// the trash, which is what tells the caller to make a fresh one instead of failing forever.
    /// </summary>
    Task<bool> FolderExistsAsync(string folderId, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves a file id up front. Reusing it on a retry is what stops a connection dropped after
    /// a successful upload from producing a second copy of the same version.
    /// </summary>
    Task<string> GenerateFileIdAsync(CancellationToken cancellationToken);

    /// <summary>Returns the file if Drive already has it, or null. Used to settle "did it land?".</summary>
    Task<DriveFileInfo?> TryGetFileAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a resumable upload and returns the session URI, which the caller persists so the
    /// transfer survives a restart.
    /// </summary>
    Task<string> StartResumableUploadAsync(
        string fileId,
        string name,
        string parentId,
        long totalBytes,
        IReadOnlyDictionary<string, string> appProperties,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks Drive how many bytes of an existing session it already holds. Null means the session
    /// is gone and a fresh one is needed.
    /// </summary>
    Task<long?> GetResumeOffsetAsync(string sessionUri, long totalBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one chunk. Reports completion when Drive accepts the final byte.
    /// </summary>
    /// <param name="chunkLength">
    /// How many bytes <paramref name="content"/> actually yields. Drive rejects the request when
    /// the declared Content-Range does not match the body it received, so this cannot be inferred
    /// from the total size.
    /// </param>
    Task<DriveResult> UploadChunkAsync(
        string sessionUri,
        Stream content,
        long offset,
        long chunkLength,
        long totalBytes,
        CancellationToken cancellationToken);

    /// <summary>Downloads a file we previously uploaded, for restoring after the local copy is gone.</summary>
    Task<DriveResult> DownloadAsync(string fileId, string destinationPath, CancellationToken cancellationToken);
}
