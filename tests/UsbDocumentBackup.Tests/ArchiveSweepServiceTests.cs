using Microsoft.Data.Sqlite;
using UsbDocumentBackup.Backup;
using UsbDocumentBackup.Storage;
using Xunit;

namespace UsbDocumentBackup.Tests;

/// <summary>
/// The sweep is the only code that deletes a finished backup, so each rule is pinned down here:
/// temporary copies go on age alone, retained presentations only once Drive has them.
/// </summary>
public sealed class ArchiveSweepServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider At(DateTimeOffset instant) => new(instant);

    private static void Backdate(TestWorkspace workspace, string backupId, DateTimeOffset when)
    {
        using var connection = workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backups SET backed_up_utc = $when WHERE id = $id;";
        command.Parameters.AddWithValue("$when", when.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$id", backupId);
        command.ExecuteNonQuery();
    }

    private static void MarkUploaded(TestWorkspace workspace, string backupId, string driveFileId)
    {
        using var connection = workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE uploads SET state = 'Done', drive_file_id = $file WHERE backup_id = $id;";
        command.Parameters.AddWithValue("$file", driveFileId);
        command.Parameters.AddWithValue("$id", backupId);
        command.ExecuteNonQuery();
    }

    private static async Task<BackupRecord> BackUpAsync(TestWorkspace workspace, DeviceRecord device)
    {
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        return Assert.Single(workspace.Repository.Search(null));
    }

    [Fact]
    public async Task A_temporary_backup_older_than_the_window_is_removed_with_its_row()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("안 연 발표.pptx", "unopened");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        Assert.Equal(BackupTier.Temporary, record.Tier);
        var path = workspace.ArchivePathOf(record);

        Backdate(workspace, record.Id, Now.AddDays(-8));
        var report = workspace.CreateSweepService(7, At(Now)).Run();

        Assert.Equal(1, report.TemporaryRemoved);
        Assert.False(File.Exists(path));
        Assert.Null(workspace.Repository.GetBackup(record.Id));
    }

    [Fact]
    public async Task A_temporary_backup_inside_the_window_is_kept()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("안 연 발표.pptx", "unopened");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        Backdate(workspace, record.Id, Now.AddDays(-3));

        var report = workspace.CreateSweepService(7, At(Now)).Run();

        Assert.Equal(0, report.TemporaryRemoved);
        Assert.True(File.Exists(workspace.ArchivePathOf(record)));
    }

    [Fact]
    public async Task A_retained_presentation_is_never_released_while_its_upload_is_still_waiting()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("학회 발표 자료.pptx", "content");
        workspace.WriteLockFileFor("학회 발표 자료.pptx");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        Assert.Equal(BackupTier.Retained, record.Tier);
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));

        // Far past the window, but Drive has never confirmed it: the local copy is all there is.
        Backdate(workspace, record.Id, Now.AddDays(-400));
        var report = workspace.CreateSweepService(7, At(Now)).Run();

        Assert.Equal(0, report.RetainedLocalCopiesReleased);
        Assert.True(File.Exists(workspace.ArchivePathOf(record)));
    }

    [Fact]
    public async Task A_retained_presentation_that_needs_attention_is_also_kept()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("학회 발표 자료.pptx", "content");
        workspace.WriteLockFileFor("학회 발표 자료.pptx");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        using (var connection = workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE uploads SET state = 'NeedsAttention' WHERE backup_id = $id;";
            command.Parameters.AddWithValue("$id", record.Id);
            command.ExecuteNonQuery();
        }

        Backdate(workspace, record.Id, Now.AddDays(-400));
        var report = workspace.CreateSweepService(7, At(Now)).Run();

        Assert.Equal(0, report.RetainedLocalCopiesReleased);
        Assert.True(File.Exists(workspace.ArchivePathOf(record)));
    }

    [Fact]
    public async Task A_retained_presentation_confirmed_in_drive_gives_up_its_local_copy_but_keeps_its_row()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("학회 발표 자료.pptx", "content");
        workspace.WriteLockFileFor("학회 발표 자료.pptx");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        var path = workspace.ArchivePathOf(record);

        MarkUploaded(workspace, record.Id, "drive-file-1");
        Backdate(workspace, record.Id, Now.AddDays(-8));

        var report = workspace.CreateSweepService(7, At(Now)).Run();

        Assert.Equal(1, report.RetainedLocalCopiesReleased);
        Assert.False(File.Exists(path));

        // The row survives so the hash and the Drive id are still there for a restore.
        var kept = workspace.Repository.GetBackup(record.Id);
        Assert.NotNull(kept);
        Assert.Equal(record.Sha256, kept!.Sha256);
        Assert.Equal(BackupTier.Retained, kept.Tier);
    }

    [Fact]
    public async Task An_uploaded_presentation_inside_the_window_still_keeps_its_local_copy()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("학회 발표 자료.pptx", "content");
        workspace.WriteLockFileFor("학회 발표 자료.pptx");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        MarkUploaded(workspace, record.Id, "drive-file-1");
        Backdate(workspace, record.Id, Now.AddDays(-2));

        var report = workspace.CreateSweepService(7, At(Now)).Run();

        Assert.Equal(0, report.RetainedLocalCopiesReleased);
        Assert.True(File.Exists(workspace.ArchivePathOf(record)));
    }

    [Fact]
    public async Task A_retention_window_of_zero_disables_the_sweep_entirely()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("안 연 발표.pptx", "unopened");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        Backdate(workspace, record.Id, Now.AddDays(-1000));

        var report = workspace.CreateSweepService(0, At(Now)).Run();

        Assert.Equal(new SweepReport(0, 0, 0), report);
        Assert.True(File.Exists(workspace.ArchivePathOf(record)));
    }

    [Fact]
    public async Task A_temporary_row_pointing_outside_the_temporary_folder_is_refused()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("안 연 발표.pptx", "unopened");
        var device = workspace.RegisterDevice();

        var record = await BackUpAsync(workspace, device);
        Assert.Equal(BackupTier.Temporary, record.Tier);

        // Point a temporary row at the retained tree and put a file there. Only the path check
        // stands between the age-only rule and a presentation that is waiting to be uploaded.
        var retainedRelative = ArchiveLayout.PathFor(BackupTier.Retained, device.Id, record.Id, record.FileName);
        var retainedPath = workspace.Paths.ResolveArchivePath(retainedRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(retainedPath)!);
        File.Move(workspace.ArchivePathOf(record), retainedPath);

        using (var connection = workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backups SET local_relative_path = $path WHERE id = $id;";
            command.Parameters.AddWithValue("$path", retainedRelative);
            command.Parameters.AddWithValue("$id", record.Id);
            command.ExecuteNonQuery();
        }

        Backdate(workspace, record.Id, Now.AddDays(-8));
        var report = workspace.CreateSweepService(7, At(Now)).Run();

        Assert.Equal(0, report.TemporaryRemoved);
        Assert.True(File.Exists(retainedPath));
        Assert.Contains(workspace.Repository.RecentIssues(), i => i.Kind == "SweepPathMismatch");
    }
}

/// <summary>A clock the sweep tests can move without waiting seven days.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
