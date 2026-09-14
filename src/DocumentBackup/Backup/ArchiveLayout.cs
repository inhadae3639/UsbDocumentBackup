using DocumentBackup.Storage;

namespace DocumentBackup.Backup;

/// <summary>
/// The one place that decides where a backup file lives.
///
/// The path is a pure function of tier, device, backup id and file name. That matters for recovery:
/// if a promotion is interrupted between the database update and the file move, startup can compute
/// exactly where the bytes would have been left and finish the move.
/// </summary>
public static class ArchiveLayout
{
    public static string PathFor(BackupTier tier, string deviceId, string backupId, string fileName) =>
        Path.Combine(FolderFor(tier), deviceId, backupId, fileName);

    public static string FolderFor(BackupTier tier) =>
        tier == BackupTier.Retained ? AppPaths.RetainedFolder : AppPaths.TemporaryFolder;

    /// <summary>The path the same backup would occupy in the other tier.</summary>
    public static string OtherTierPath(BackupTier tier, string deviceId, string backupId, string fileName) =>
        PathFor(tier == BackupTier.Retained ? BackupTier.Temporary : BackupTier.Retained, deviceId, backupId, fileName);
}
