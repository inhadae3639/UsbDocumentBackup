using System.Security.Cryptography;
using UsbDocumentBackup.Backup;
using UsbDocumentBackup.Storage;
using Xunit;

namespace UsbDocumentBackup.Tests;

/// <summary>
/// The archive and the database are updated in separate steps, so these tests inject a crash at
/// each step boundary and check that startup recovery tells finished work from unfinished work.
/// </summary>
public sealed class RecoveryServiceTests
{
    private static BackupRecord PendingRecord(
        TestWorkspace workspace,
        DeviceRecord device,
        byte[] content,
        out string finalPath,
        BackupTier tier = BackupTier.Retained)
    {
        var backupId = Id.New();
        var localRelative = Path.Combine(
            tier == BackupTier.Retained ? AppPaths.RetainedFolder : AppPaths.TemporaryFolder,
            device.Id,
            backupId,
            "발표.pptx");
        finalPath = workspace.Paths.ResolveArchivePath(localRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var record = new BackupRecord(
            backupId,
            device.Id,
            "발표.pptx",
            "발표.pptx",
            content.Length,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            localRelative,
            BackupState.Pending,
            tier);

        workspace.Repository.Insert(record);
        return record;
    }

    [Fact]
    public async Task A_crash_after_the_rename_but_before_the_commit_is_completed()
    {
        using var workspace = new TestWorkspace();
        var device = workspace.RegisterDevice();
        var content = SampleFiles.MinimalPptx("발표 내용");

        var record = PendingRecord(workspace, device, content, out var finalPath);
        File.WriteAllBytes(finalPath, content);

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(1, report.Completed);
        Assert.Equal(BackupState.Complete, workspace.Repository.GetBackup(record.Id)!.State);
        Assert.True(File.Exists(finalPath));

        // Finishing recovery must also queue the upload that the crash prevented.
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task A_crash_before_the_rename_discards_the_unproven_copy()
    {
        using var workspace = new TestWorkspace();
        var device = workspace.RegisterDevice();
        var content = SampleFiles.MinimalPptx("발표 내용");

        var record = PendingRecord(workspace, device, content, out var finalPath);
        var partialPath = finalPath + FileCopier.PartialSuffix;
        File.WriteAllBytes(partialPath, content[..(content.Length / 2)]);

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(1, report.Discarded);
        Assert.Null(workspace.Repository.GetBackup(record.Id));
        Assert.False(File.Exists(partialPath));
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task A_renamed_file_whose_content_does_not_match_is_not_accepted()
    {
        using var workspace = new TestWorkspace();
        var device = workspace.RegisterDevice();
        var content = SampleFiles.MinimalPptx("발표 내용");

        var record = PendingRecord(workspace, device, content, out var finalPath);

        // Right size, wrong bytes: metadata alone must not be enough to call this a backup.
        var corrupted = (byte[])content.Clone();
        corrupted[^1] ^= 0xFF;
        File.WriteAllBytes(finalPath, corrupted);

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(0, report.Completed);
        Assert.Equal(1, report.Discarded);
        Assert.Null(workspace.Repository.GetBackup(record.Id));
    }

    [Fact]
    public async Task An_orphaned_partial_file_is_swept_but_finished_backups_are_left_alone()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "완료된 백업");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var completed = Assert.Single(workspace.Repository.Search(null));
        var completedPath = workspace.ArchivePathOf(completed);

        var orphan = Path.Combine(workspace.Paths.TemporaryRoot, device.Id, "orphan", "무엇인가.pptx.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllBytes(orphan, [1, 2, 3]);

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(1, report.OrphanPartialsRemoved);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(completedPath));
        Assert.Equal(BackupState.Complete, workspace.Repository.GetBackup(completed.Id)!.State);
    }

    [Fact]
    public async Task An_upload_left_in_flight_is_returned_to_the_queue()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "업로드 대기");
        workspace.WriteLockFileFor("발표.pptx");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var backup = Assert.Single(workspace.Repository.Search(null));
        using (var connection = workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE uploads SET state = 'Uploading' WHERE backup_id = $id;";
            command.Parameters.AddWithValue("$id", backup.Id);
            command.ExecuteNonQuery();
        }

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(1, report.UploadsRequeued);
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
        Assert.Equal(0, workspace.Repository.CountUploads(UploadState.Uploading));
    }

    [Fact]
    public async Task An_interrupted_promotion_is_finished_from_the_stranded_temporary_copy()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "승격 중 중단");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var record = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Temporary, record.Tier);

        var temporaryPath = workspace.ArchivePathOf(record);
        var retainedRelative = ArchiveLayout.PathFor(BackupTier.Retained, device.Id, record.Id, record.FileName);

        // Exactly the crash the promotion order is designed for: the row is already retained, but
        // the process died before the bytes moved.
        workspace.Repository.PromoteToRetained(record.Id, retainedRelative, DateTimeOffset.UtcNow);
        Assert.True(File.Exists(temporaryPath));
        Assert.False(File.Exists(workspace.Paths.ResolveArchivePath(retainedRelative)));

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(1, report.PromotionsFinished);
        Assert.True(File.Exists(workspace.Paths.ResolveArchivePath(retainedRelative)));
        Assert.False(File.Exists(temporaryPath));

        // And the promoted backup restores correctly afterwards.
        var restore = await workspace.CreateRestoreService()
            .RestoreAsync(record.Id, Path.Combine(workspace.Root, "restored"));
        Assert.True(restore.Succeeded, restore.Message);
    }

    [Fact]
    public async Task A_retained_backup_with_no_recoverable_copy_is_reported_rather_than_hidden()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "content");
        workspace.WriteLockFileFor("학회 발표 자료.pptx");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var record = Assert.Single(workspace.Repository.Search(null));

        workspace.Repository.PromoteToRetained(
            record.Id,
            ArchiveLayout.PathFor(BackupTier.Retained, device.Id, record.Id, record.FileName),
            DateTimeOffset.UtcNow);
        File.Delete(workspace.ArchivePathOf(record));

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(0, report.PromotionsFinished);
        Assert.Contains(workspace.Repository.RecentIssues(), i => i.Kind == "RetainedCopyMissing");
    }

    [Fact]
    public async Task Recovery_is_a_no_op_when_nothing_was_interrupted()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "정상");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var report = await workspace.CreateRecoveryService().RunAsync();

        Assert.Equal(new RecoveryReport(0, 0, 0, 0, 0), report);
        Assert.Single(workspace.Repository.Search(null));
    }
}
