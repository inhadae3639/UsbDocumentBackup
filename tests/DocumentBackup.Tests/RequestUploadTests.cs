using DocumentBackup.Backup;
using DocumentBackup.GoogleDrive;
using DocumentBackup.Storage;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// Picking one file from the status list and sending it to Drive. The automatic rules are narrow on
/// purpose, so this is the escape hatch for a deck that was never recorded as opened, or was opened
/// before monitoring began.
/// </summary>
public sealed class RequestUploadTests
{
    private static async Task<BackupRecord> BackUpAsync(TestWorkspace workspace, string name, bool opened)
    {
        workspace.WritePptx(name, name);
        var device = workspace.Repository.ListDevices().FirstOrDefault() ?? workspace.RegisterDevice();
        if (opened)
        {
            workspace.Repository.MarkOpened(device.Id, name, DateTimeOffset.UtcNow);
        }

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.ChangeEvent, CancellationToken.None);

        return workspace.Repository.Search(null).First(r => r.FileName == name);
    }

    [Fact]
    public async Task A_temporary_backup_is_promoted_and_queued()
    {
        using var workspace = new TestWorkspace();
        var backup = await BackUpAsync(workspace, "한 번도 안 연 발표.pptx", opened: false);
        Assert.Equal(BackupTier.Temporary, backup.Tier);
        Assert.Equal(0, workspace.Repository.CountUploads(UploadState.Waiting));

        var result = workspace.CreateBackupService().RequestUpload(backup.Id);

        Assert.Equal(UploadRequestResult.Queued, result);

        var after = workspace.Repository.GetBackup(backup.Id)!;
        Assert.Equal(BackupTier.Retained, after.Tier);
        Assert.StartsWith(AppPaths.RetainedFolder + Path.DirectorySeparatorChar, after.LocalRelativePath, StringComparison.Ordinal);
        Assert.True(File.Exists(workspace.ArchivePathOf(after)));
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task The_promoted_file_actually_reaches_drive()
    {
        using var workspace = new TestWorkspace();
        var backup = await BackUpAsync(workspace, "골라서 올린 발표.pptx", opened: false);

        Assert.Equal(UploadRequestResult.Queued, workspace.CreateBackupService().RequestUpload(backup.Id));

        var drive = new FakeDriveClient();
        var worker = new UploadWorker(
            workspace.Paths,
            workspace.Repository,
            new AppSettings { UploadBytesPerSecond = 0 },
            new SettingsStore(Path.Combine(workspace.Root, "settings.json")),
            () => drive,
            workspace.Log);

        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));

        var after = workspace.Repository.GetBackup(backup.Id)!;
        Assert.Equal(File.ReadAllBytes(workspace.ArchivePathOf(after)), Assert.Single(drive.Files).Value);
    }

    [Fact]
    public async Task A_stalled_upload_is_made_due_again()
    {
        using var workspace = new TestWorkspace();
        var backup = await BackUpAsync(workspace, "실패했던 발표.pptx", opened: true);
        Assert.Equal(BackupTier.Retained, backup.Tier);

        workspace.Repository.MarkUploadNeedsAttention(backup.Id, "어떤 이유로든 멈춤");
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.NeedsAttention));

        Assert.Equal(UploadRequestResult.Queued, workspace.CreateBackupService().RequestUpload(backup.Id));

        var upload = workspace.Repository.GetUpload(backup.Id)!;
        Assert.Equal(UploadState.Waiting, upload.State);
        Assert.Equal(0, upload.Attempts);
        Assert.NotNull(upload.NextAttemptUtc);
    }

    [Fact]
    public async Task Asking_again_for_something_already_in_drive_says_so_and_changes_nothing()
    {
        using var workspace = new TestWorkspace();
        var backup = await BackUpAsync(workspace, "이미 올라간 발표.pptx", opened: true);
        workspace.Repository.MarkUploadDone(backup.Id, "drive-file-1");

        var result = workspace.CreateBackupService().RequestUpload(backup.Id);

        Assert.Equal(UploadRequestResult.AlreadyUploaded, result);
        Assert.Equal(UploadState.Done, workspace.Repository.GetUpload(backup.Id)!.State);
    }

    [Fact]
    public void An_unknown_backup_is_reported_rather_than_throwing()
    {
        using var workspace = new TestWorkspace();
        Assert.Equal(UploadRequestResult.NotFound, workspace.CreateBackupService().RequestUpload("없는-아이디"));
    }

    [Fact]
    public async Task Promoting_one_file_leaves_the_others_alone()
    {
        using var workspace = new TestWorkspace();
        var chosen = await BackUpAsync(workspace, "고른 발표.pptx", opened: false);
        var other = await BackUpAsync(workspace, "안 고른 발표.pptx", opened: false);

        workspace.CreateBackupService().RequestUpload(chosen.Id);

        Assert.Equal(BackupTier.Retained, workspace.Repository.GetBackup(chosen.Id)!.Tier);
        Assert.Equal(BackupTier.Temporary, workspace.Repository.GetBackup(other.Id)!.Tier);
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }
}
