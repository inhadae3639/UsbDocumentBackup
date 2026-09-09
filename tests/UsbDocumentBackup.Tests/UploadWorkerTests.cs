using UsbDocumentBackup.Backup;
using UsbDocumentBackup.GoogleDrive;
using UsbDocumentBackup.Storage;
using Xunit;

namespace UsbDocumentBackup.Tests;

/// <summary>
/// Upload behaviour against an injected Drive. These cover the failure paths that are impractical
/// to force against a real account: a lost success response, an expired resumable session, a full
/// Drive, and revoked authorisation.
/// </summary>
public sealed class UploadWorkerTests
{
    private static async Task<(BackupRecord Backup, UploadWorker Worker, FakeDriveClient Drive)> ArrangeAsync(
        TestWorkspace workspace,
        string name = "학회 발표 자료.pptx",
        string body = "발표 내용")
    {
        workspace.WritePptx(name, body);
        workspace.WriteLockFileFor(name);
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var backup = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Retained, backup.Tier);

        var drive = new FakeDriveClient();
        var settings = new AppSettings { UploadBytesPerSecond = 0 };
        var worker = new UploadWorker(
            workspace.Paths,
            workspace.Repository,
            settings,
            new SettingsStore(Path.Combine(workspace.Root, "settings.json")),
            () => drive,
            workspace.Log);

        return (backup, worker, drive);
    }

    [Fact]
    public async Task A_queued_presentation_is_uploaded_and_marked_done()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        var completed = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, completed);
        Assert.Equal(UploadState.Done, workspace.Repository.GetUpload(backup.Id)!.State);

        var fileId = workspace.Repository.GetUpload(backup.Id)!.DriveFileId;
        Assert.NotNull(fileId);
        Assert.Equal(File.ReadAllBytes(workspace.ArchivePathOf(backup)), drive.Files[fileId!]);

        // The version's backup time is in the Drive name so the web view is navigable.
        Assert.Contains(backup.BackedUpUtc.ToLocalTime().ToString("yyyy-MM-dd"), drive.NamesByFileId[fileId!], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_uploaded_and_nothing_is_lost_when_no_account_is_connected()
    {
        using var workspace = new TestWorkspace();
        var (backup, _, _) = await ArrangeAsync(workspace);

        var offline = new UploadWorker(
            workspace.Paths,
            workspace.Repository,
            new AppSettings(),
            new SettingsStore(Path.Combine(workspace.Root, "settings.json")),
            () => null,
            workspace.Log);

        Assert.Equal(0, await offline.RunAsync(CancellationToken.None));
        Assert.Equal(UploadState.Waiting, workspace.Repository.GetUpload(backup.Id)!.State);
        Assert.True(File.Exists(workspace.ArchivePathOf(backup)));
    }

    [Fact]
    public async Task A_success_response_lost_in_transit_does_not_create_a_second_copy()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        // Drive stores the file but the reply never arrives.
        drive.SwallowNextSuccessResponse = true;
        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(UploadState.Waiting, workspace.Repository.GetUpload(backup.Id)!.State);
        Assert.Single(drive.Files);

        // The retry asks about the reserved id, finds the file, and stops.
        MakeDue(workspace, backup.Id);
        var completed = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, completed);
        Assert.Equal(UploadState.Done, workspace.Repository.GetUpload(backup.Id)!.State);
        Assert.Single(drive.Files);
        Assert.Equal(1, drive.GeneratedIds);
    }

    [Fact]
    public async Task An_expired_resumable_session_restarts_without_duplicating_the_file()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        drive.TransientChunkFailures = 1;
        await worker.RunAsync(CancellationToken.None);
        Assert.Equal(UploadState.Waiting, workspace.Repository.GetUpload(backup.Id)!.State);
        Assert.NotNull(workspace.Repository.GetUpload(backup.Id)!.ResumeUri);

        // Google documents these sessions as expiring; the retry must open a new one.
        drive.ExpireNextSession = true;
        MakeDue(workspace, backup.Id);
        var completed = await worker.RunAsync(CancellationToken.None);

        Assert.Equal(1, completed);
        Assert.Equal(2, drive.StartedSessions);
        Assert.Equal(1, drive.GeneratedIds);
        Assert.Single(drive.Files);
        Assert.Equal(File.ReadAllBytes(workspace.ArchivePathOf(backup)), drive.Files.Values.Single());
    }

    [Fact]
    public async Task A_transient_error_is_retried_later_rather_than_abandoned()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        drive.TransientChunkFailures = 1;
        await worker.RunAsync(CancellationToken.None);

        var upload = workspace.Repository.GetUpload(backup.Id)!;
        Assert.Equal(UploadState.Waiting, upload.State);
        Assert.Equal(1, upload.Attempts);
        Assert.NotNull(upload.NextAttemptUtc);

        // Backoff means it is not due immediately: a failing upload must not spin.
        Assert.True(upload.NextAttemptUtc > DateTimeOffset.UtcNow);
        Assert.Equal(0, await worker.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_full_drive_needs_attention_instead_of_retrying_forever()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        drive.OutOfStorage = true;
        await worker.RunAsync(CancellationToken.None);

        var upload = workspace.Repository.GetUpload(backup.Id)!;
        Assert.Equal(UploadState.NeedsAttention, upload.State);
        Assert.Null(upload.NextAttemptUtc);
        Assert.Contains("storage", upload.LastError!, StringComparison.OrdinalIgnoreCase);

        // The local copy stays put, because Drive does not have it.
        Assert.True(File.Exists(workspace.ArchivePathOf(backup)));
    }

    [Fact]
    public async Task Revoked_authorization_pauses_uploads_and_leaves_local_backups_alone()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        drive.AuthorizationRevoked = true;
        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(UploadState.NeedsAttention, workspace.Repository.GetUpload(backup.Id)!.State);
        Assert.True(File.Exists(workspace.ArchivePathOf(backup)));

        // Reconnecting puts the backlog back in the queue.
        drive.AuthorizationRevoked = false;
        workspace.Repository.RequeueAllNeedingAttention(DateTimeOffset.UtcNow);
        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reconnecting_as_a_different_account_does_not_silently_upload_the_backlog()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        drive.TransientChunkFailures = 1;
        await worker.RunAsync(CancellationToken.None);
        Assert.Equal("tester@example.com", workspace.Repository.GetUpload(backup.Id)!.AccountKey);

        drive.AccountKey = "someone.else@example.com";
        MakeDue(workspace, backup.Id);
        await worker.RunAsync(CancellationToken.None);

        var upload = workspace.Repository.GetUpload(backup.Id)!;
        Assert.Equal(UploadState.NeedsAttention, upload.State);
        Assert.Contains("someone.else@example.com", upload.LastError!, StringComparison.Ordinal);
        Assert.Empty(drive.Files);
    }

    [Fact]
    public async Task A_version_uploaded_after_an_edit_becomes_a_separate_drive_file()
    {
        using var workspace = new TestWorkspace();
        var (_, worker, drive) = await ArrangeAsync(workspace, "발표.pptx", "1차");

        await worker.RunAsync(CancellationToken.None);

        workspace.WritePptx("발표.pptx", "2차 - 크게 수정한 원고");
        var device = workspace.Repository.ListDevices().Single();
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.ChangeEvent, CancellationToken.None);

        await worker.RunAsync(CancellationToken.None);

        // Two independent files: the earlier version is never overwritten.
        Assert.Equal(2, drive.Files.Count);
        Assert.Equal(2, workspace.Repository.CountUploads(UploadState.Done));
    }

    [Fact]
    public async Task A_backup_whose_local_file_vanished_is_flagged_rather_than_retried()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        File.Delete(workspace.ArchivePathOf(backup));
        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(UploadState.NeedsAttention, workspace.Repository.GetUpload(backup.Id)!.State);
        Assert.Empty(drive.Files);
    }

    [Fact]
    public async Task A_released_local_copy_is_restored_by_downloading_it_from_drive()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace);

        await worker.RunAsync(CancellationToken.None);
        var originalBytes = File.ReadAllBytes(workspace.ArchivePathOf(backup));

        // Age it past the window; now that Drive has it, the sweep may release the local copy.
        using (var connection = workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backups SET backed_up_utc = $when WHERE id = $id;";
            command.Parameters.AddWithValue("$when", DateTimeOffset.UtcNow.AddDays(-30).ToString("o"));
            command.Parameters.AddWithValue("$id", backup.Id);
            command.ExecuteNonQuery();
        }

        var sweep = workspace.CreateSweepService(7).Run();
        Assert.Equal(1, sweep.RetainedLocalCopiesReleased);
        Assert.False(File.Exists(workspace.ArchivePathOf(backup)));

        var restore = new RestoreService(workspace.Paths, workspace.Repository, () => drive);
        var result = await restore.RestoreAsync(backup.Id, Path.Combine(workspace.Root, "restored"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(result.RestoredPath!));
    }

    [Fact]
    public async Task Restoring_a_released_copy_without_a_connection_says_so_instead_of_failing_silently()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, _) = await ArrangeAsync(workspace);

        await worker.RunAsync(CancellationToken.None);
        File.Delete(workspace.ArchivePathOf(backup));

        var restore = new RestoreService(workspace.Paths, workspace.Repository, () => null);
        var result = await restore.RestoreAsync(backup.Id, Path.Combine(workspace.Root, "restored"));

        Assert.False(result.Succeeded);
        Assert.Contains("Google", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Clears the backoff so a retry can be exercised without waiting for it.</summary>
    private static void MakeDue(TestWorkspace workspace, string backupId)
    {
        using var connection = workspace.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE uploads SET next_attempt_utc = NULL WHERE backup_id = $id;";
        command.Parameters.AddWithValue("$id", backupId);
        command.ExecuteNonQuery();
    }
}
