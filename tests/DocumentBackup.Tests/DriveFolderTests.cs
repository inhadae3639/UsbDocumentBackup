using DocumentBackup.Backup;
using DocumentBackup.GoogleDrive;
using DocumentBackup.Storage;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// People tidy up their Drive. Renaming or moving the backup folder must not send later uploads
/// somewhere else, and deleting it must not wedge the queue forever.
/// </summary>
public sealed class DriveFolderTests
{
    private static UploadWorker CreateWorker(TestWorkspace workspace, IDriveClient drive) => new(
        workspace.Paths,
        workspace.Repository,
        new AppSettings(),
        new SettingsStore(Path.Combine(workspace.Root, "settings.json")),
        () => drive,
        workspace.Log);

    /// <summary>
    /// Records the document as opened directly rather than writing an Office owner file. Two of
    /// these fixtures would otherwise share one owner-file name, which the ambiguity guard
    /// correctly refuses to attribute -- that behaviour has its own tests and is not what is
    /// being exercised here.
    /// </summary>
    private static async Task<BackupRecord> BackUpOpenedAsync(TestWorkspace workspace, string name, string body)
    {
        workspace.WritePptx(name, body);
        var device = workspace.Repository.ListDevices().FirstOrDefault() ?? workspace.RegisterDevice();
        workspace.Repository.MarkOpened(device.Id, name, DateTimeOffset.UtcNow);

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.ChangeEvent, CancellationToken.None);

        return workspace.Repository.Search(null)
            .Where(r => r.FileName == name)
            .OrderByDescending(r => r.BackedUpUtc)
            .First();
    }

    [Fact]
    public async Task Renaming_the_drive_folders_keeps_later_uploads_in_the_same_place()
    {
        using var workspace = new TestWorkspace();
        var drive = new FakeDriveClient();
        var worker = CreateWorker(workspace, drive);

        var first = await BackUpOpenedAsync(workspace, "1차 발표.pptx", "v1");
        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));

        var folderUsed = workspace.Repository.GetUpload(first.Id)!.DriveFolderId;
        Assert.NotNull(folderUsed);
        var deviceId = first.DeviceId;
        Assert.Equal(folderUsed, workspace.Repository.GetDeviceDriveFolder(deviceId));

        // The user renames both folders in Drive. A Drive id does not change when a folder is
        // renamed, so nothing about the stored ids becomes stale.
        var foldersBefore = drive.CreatedFolders.Count;

        var second = await BackUpOpenedAsync(workspace, "2차 발표.pptx", "v2");
        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));

        // Same folder, and no new folder was invented.
        Assert.Equal(folderUsed, workspace.Repository.GetUpload(second.Id)!.DriveFolderId);
        Assert.Equal(foldersBefore, drive.CreatedFolders.Count);
    }

    [Fact]
    public async Task A_second_upload_reuses_the_remembered_device_folder_without_looking_it_up_by_name()
    {
        using var workspace = new TestWorkspace();
        var drive = new FakeDriveClient();
        var worker = CreateWorker(workspace, drive);

        await BackUpOpenedAsync(workspace, "발표.pptx", "v1");
        await worker.RunAsync(CancellationToken.None);
        var createdAfterFirst = drive.CreatedFolders.Count;

        await BackUpOpenedAsync(workspace, "발표.pptx", "v2 크게 수정");
        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(createdAfterFirst, drive.CreatedFolders.Count);
        Assert.Equal(2, workspace.Repository.CountUploads(UploadState.Done));
    }

    [Fact]
    public async Task A_deleted_backup_folder_is_recreated_rather_than_failing_forever()
    {
        using var workspace = new TestWorkspace();
        var drive = new FakeDriveClient();
        var worker = CreateWorker(workspace, drive);

        var first = await BackUpOpenedAsync(workspace, "1차 발표.pptx", "v1");
        await worker.RunAsync(CancellationToken.None);
        var originalFolder = workspace.Repository.GetUpload(first.Id)!.DriveFolderId!;

        // The user empties the trash, or deletes the whole backup folder.
        foreach (var folder in drive.CreatedFolders.ToList())
        {
            drive.TrashedFolders.Add(folder);
        }

        var second = await BackUpOpenedAsync(workspace, "2차 발표.pptx", "v2");
        var completed = await worker.RunAsync(CancellationToken.None);

        // It notices the folder is gone, makes a new one, and the upload still succeeds.
        Assert.Equal(1, completed);
        Assert.Equal(UploadState.Done, workspace.Repository.GetUpload(second.Id)!.State);
        Assert.NotNull(workspace.Repository.GetUpload(second.Id)!.DriveFolderId);
        Assert.Equal(
            workspace.Repository.GetUpload(second.Id)!.DriveFolderId,
            workspace.Repository.GetDeviceDriveFolder(second.DeviceId));

        // And the stale id is not still being handed out.
        Assert.False(await drive.FolderExistsAsync(originalFolder, CancellationToken.None));
    }

    [Fact]
    public async Task Two_devices_get_their_own_folders()
    {
        using var workspace = new TestWorkspace();
        var drive = new FakeDriveClient();
        var worker = CreateWorker(workspace, drive);

        var rootA = Path.Combine(workspace.Root, "usb-a");
        var rootB = Path.Combine(workspace.Root, "usb-b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        File.WriteAllBytes(Path.Combine(rootA, "발표.pptx"), SampleFiles.MinimalPptx("A"));
        File.WriteAllBytes(Path.Combine(rootB, "발표.pptx"), SampleFiles.MinimalPptx("B"));

        var deviceA = workspace.RegisterDevice("스틱 A");
        var deviceB = workspace.RegisterDevice("스틱 B");
        workspace.Repository.MarkOpened(deviceA.Id, "발표.pptx", DateTimeOffset.UtcNow);
        workspace.Repository.MarkOpened(deviceB.Id, "발표.pptx", DateTimeOffset.UtcNow);

        var service = workspace.CreateBackupService();
        await service.BackupVolumeAsync(deviceA, rootA, ScanReason.NewConnection, CancellationToken.None);
        await service.BackupVolumeAsync(deviceB, rootB, ScanReason.NewConnection, CancellationToken.None);

        Assert.Equal(2, await worker.RunAsync(CancellationToken.None));

        var folderA = workspace.Repository.GetDeviceDriveFolder(deviceA.Id);
        var folderB = workspace.Repository.GetDeviceDriveFolder(deviceB.Id);
        Assert.NotNull(folderA);
        Assert.NotNull(folderB);
        Assert.NotEqual(folderA, folderB);
    }
}
