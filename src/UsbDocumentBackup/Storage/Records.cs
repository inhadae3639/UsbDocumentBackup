namespace UsbDocumentBackup.Storage;

/// <summary>How confident we are that a volume is really attached over USB.</summary>
public enum BusKind
{
    /// <summary>Storage bus type reported USB.</summary>
    Usb,

    /// <summary>Storage bus type reported an internal bus (SATA, NVMe, ...).</summary>
    Internal,

    /// <summary>Bus type could not be determined. Never silently treated as a success.</summary>
    Unknown,
}

public sealed record DeviceRecord(
    string Id,
    string? VolumeId,
    string Fingerprint,
    string DisplayName,
    DateTimeOffset LastSeenUtc,
    BusKind BusKind);

public enum BackupState
{
    /// <summary>Row exists but the archive file is not proven complete yet.</summary>
    Pending,

    /// <summary>Archive file was written, re-read and hash-verified.</summary>
    Complete,
}

/// <summary>
/// How long a backup is kept and whether it goes to Drive.
/// </summary>
public enum BackupTier
{
    /// <summary>
    /// A document we have no evidence was ever opened: any PDF, and any presentation whose Office
    /// lock file we never saw. Held locally as a safety net and swept after the retention window.
    /// Never uploaded.
    /// </summary>
    Temporary,

    /// <summary>
    /// A presentation that was actually opened, proven by its Office lock file appearing next to
    /// it. Queued for Drive. Its local copy is held until Drive confirms the upload, and only then
    /// does it become eligible for the same sweep; the row itself is kept forever so the hash and
    /// the Drive file id stay available for a restore.
    /// </summary>
    Retained,
}

public sealed record BackupRecord(
    string Id,
    string DeviceId,
    string RelativePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset SourceModifiedUtc,
    DateTimeOffset BackedUpUtc,
    string Sha256,
    string LocalRelativePath,
    BackupState State,
    BackupTier Tier);

public enum UploadState
{
    Waiting,
    Uploading,
    Done,
    NeedsAttention,
}

public sealed record UploadRecord(
    string BackupId,
    UploadState State,
    string? DriveFolderId,
    string? DriveFileId,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    string? LastError,
    string? AccountKey,
    string? ResumeUri = null,
    long UploadedBytes = 0);

/// <summary>
/// A problem worth showing the user: an unreadable file, a skipped folder, or a volume whose
/// bus type could not be determined.
/// </summary>
public sealed record ScanIssue(
    long Id,
    DateTimeOffset OccurredUtc,
    string? DeviceId,
    string? Path,
    string Kind,
    string? Detail);
