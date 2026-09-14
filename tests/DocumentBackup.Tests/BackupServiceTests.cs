using System.Security.Cryptography;
using DocumentBackup.Backup;
using DocumentBackup.Storage;
using Xunit;

namespace DocumentBackup.Tests;

public sealed class BackupServiceTests
{
    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [Fact]
    public async Task An_opened_presentation_is_retained_and_queued_for_drive()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.WritePptx("발표 자료/학회 최종 발표.pptx", "안녕하세요");
        workspace.WriteLockFileFor("발표 자료/학회 최종 발표.pptx");

        var device = workspace.RegisterDevice();
        var summary = await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        Assert.Equal(1, summary.NewVersions);

        var record = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Retained, record.Tier);
        Assert.Equal(BackupState.Complete, record.State);
        Assert.Equal(Sha256Of(source), record.Sha256);
        Assert.StartsWith(AppPaths.RetainedFolder + Path.DirectorySeparatorChar, record.LocalRelativePath, StringComparison.Ordinal);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(workspace.ArchivePathOf(record)));
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task A_presentation_that_was_never_opened_is_only_kept_temporarily_and_never_queued()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("한 번도 안 연 발표.pptx", "unopened");

        var device = workspace.RegisterDevice();
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var record = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Temporary, record.Tier);
        Assert.StartsWith(AppPaths.TemporaryFolder + Path.DirectorySeparatorChar, record.LocalRelativePath, StringComparison.Ordinal);

        // The bytes are still safely on disk; they just are not Drive's problem.
        Assert.True(File.Exists(workspace.ArchivePathOf(record)));
        Assert.Equal(0, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task A_pdf_is_not_backed_up_at_all()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePdf("배포 자료.pdf", "handout");
        workspace.WritePptx("발표.pptx", "deck");

        var device = workspace.RegisterDevice();
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        // A PDF has no Office owner file and never appears in PowerPoint's recent list, so it could
        // never reach the retained tier. Copying it only consumed disk.
        var record = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal("발표.pptx", record.FileName);
    }

    [Fact]
    public async Task Opening_a_deck_later_promotes_the_existing_copy_without_reading_the_source_again()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("나중에 연 발표.pptx", "content");
        var device = workspace.RegisterDevice();
        var service = workspace.CreateBackupService();

        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var before = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Temporary, before.Tier);

        // The user opens it in PowerPoint: the owner file appears next to the document.
        workspace.WriteLockFileFor("나중에 연 발표.pptx");
        var summary = await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.PeriodicRescan, CancellationToken.None);

        Assert.Equal(1, summary.Promoted);
        Assert.Equal(0, summary.NewVersions);

        // Same backup id and same bytes, moved into the retained tree and queued.
        var after = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(BackupTier.Retained, after.Tier);
        Assert.StartsWith(AppPaths.RetainedFolder + Path.DirectorySeparatorChar, after.LocalRelativePath, StringComparison.Ordinal);
        Assert.True(File.Exists(workspace.ArchivePathOf(after)));
        Assert.False(File.Exists(workspace.Paths.ResolveArchivePath(before.LocalRelativePath)));
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task A_deck_stays_retained_after_the_owner_file_disappears()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표를 마친 자료.pptx", "v1");
        var lockFile = workspace.WriteLockFileFor("발표를 마친 자료.pptx");
        var device = workspace.RegisterDevice();
        var service = workspace.CreateBackupService();

        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        // PowerPoint closes and removes its owner file; the document was still opened once.
        File.Delete(lockFile);
        workspace.WritePptx("발표를 마친 자료.pptx", "v2 - 발표 후 수정");
        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.ChangeEvent, CancellationToken.None);

        var versions = workspace.Repository.ListVersions(device.Id, "발표를 마친 자료.pptx");
        Assert.Equal(2, versions.Count);
        Assert.All(versions, v => Assert.Equal(BackupTier.Retained, v.Tier));
        Assert.Equal(2, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task The_owner_file_itself_is_never_backed_up()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("학회 발표 자료.pptx", "content");
        workspace.WriteLockFileFor("학회 발표 자료.pptx");

        var device = workspace.RegisterDevice();
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var record = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal("학회 발표 자료.pptx", record.FileName);
    }

    [Fact]
    public async Task Backup_never_modifies_the_source()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.WritePptx("자료/원본.pptx", "content");

        var before = new FileInfo(source);
        var beforeBytes = File.ReadAllBytes(source);
        var beforeWrite = before.LastWriteTimeUtc;
        var beforeAttributes = before.Attributes;

        var device = workspace.RegisterDevice();
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var after = new FileInfo(source);
        Assert.True(after.Exists);
        Assert.Equal(beforeBytes, File.ReadAllBytes(source));
        Assert.Equal(beforeWrite, after.LastWriteTimeUtc);
        Assert.Equal(beforeAttributes, after.Attributes);
    }

    [Fact]
    public async Task Rescanning_unchanged_content_does_not_create_a_second_version()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "same");
        var device = workspace.RegisterDevice();
        var service = workspace.CreateBackupService();

        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        // A reconnect re-reads the bytes; identical content must not become a second archived copy.
        var second = await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        Assert.Equal(0, second.NewVersions);
        Assert.Equal(1, second.Unchanged);
        Assert.Single(workspace.Repository.Search(null));
    }

    [Fact]
    public async Task Changed_content_creates_a_new_version_and_keeps_the_old_one()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "1차");
        var device = workspace.RegisterDevice();
        var service = workspace.CreateBackupService();

        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var first = Assert.Single(workspace.Repository.Search(null));

        workspace.WritePptx("발표.pptx", "2차 수정본");
        var second = await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.ChangeEvent, CancellationToken.None);

        Assert.Equal(1, second.NewVersions);

        var versions = workspace.Repository.ListVersions(device.Id, "발표.pptx");
        Assert.Equal(2, versions.Count);

        // The earlier version is still on disk and still readable: no overwriting, no cleanup.
        Assert.True(File.Exists(workspace.ArchivePathOf(first)));
        Assert.NotEqual(versions[0].Sha256, versions[1].Sha256);
    }

    [Fact]
    public async Task Periodic_rescan_skips_files_whose_size_and_timestamp_are_unchanged()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "same");
        var device = workspace.RegisterDevice();
        var service = workspace.CreateBackupService();

        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var summary = await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.PeriodicRescan, CancellationToken.None);

        Assert.Equal(0, summary.NewVersions);
        Assert.Equal(1, summary.Unchanged);
    }

    [Fact]
    public async Task Two_devices_with_the_same_file_name_are_archived_separately()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateBackupService();

        var firstRoot = Path.Combine(workspace.Root, "usb-a");
        var secondRoot = Path.Combine(workspace.Root, "usb-b");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        File.WriteAllBytes(Path.Combine(firstRoot, "발표.pptx"), SampleFiles.MinimalPptx("A 자료"));
        File.WriteAllBytes(Path.Combine(secondRoot, "발표.pptx"), SampleFiles.MinimalPptx("B 자료"));

        var deviceA = workspace.RegisterDevice("A");
        var deviceB = workspace.RegisterDevice("B");

        await service.BackupVolumeAsync(deviceA, firstRoot, ScanReason.NewConnection, CancellationToken.None);
        await service.BackupVolumeAsync(deviceB, secondRoot, ScanReason.NewConnection, CancellationToken.None);

        var all = workspace.Repository.Search(null);
        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Select(r => r.DeviceId).Distinct().Count());
        Assert.Equal(2, all.Select(r => r.Sha256).Distinct().Count());

        // Each device's copy is a distinct file, so neither can overwrite the other.
        Assert.Equal(2, all.Select(r => workspace.ArchivePathOf(r)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, r => Assert.True(File.Exists(workspace.ArchivePathOf(r))));
    }

    [Fact]
    public async Task A_file_locked_for_exclusive_writing_is_retried_rather_than_completed()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.WritePptx("잠긴 발표.pptx", "locked");
        var device = workspace.RegisterDevice();

        // FileShare.None is what an application holding the file exclusively looks like to us.
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var summary = await workspace.CreateBackupService()
                .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

            Assert.Equal(0, summary.NewVersions);
            Assert.Equal(1, summary.Failed);
        }

        Assert.Empty(workspace.Repository.Search(null));
        Assert.Empty(workspace.Repository.ListPending());
        Assert.Contains(workspace.Repository.RecentIssues(), i => i.Kind == nameof(CopyStatus.SourceUnreadable));

        // Once the lock is gone the next scan picks it up.
        var retry = await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        Assert.Equal(1, retry.NewVersions);
    }

    [Fact]
    public async Task A_full_archive_disk_does_not_produce_a_completed_backup()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "content");
        var device = workspace.RegisterDevice();

        // A free-space reserve larger than the disk makes every copy refuse to start.
        var copier = new FileCopier(RateLimiter.Unlimited, minimumFreeBytes: long.MaxValue / 2, TimeSpan.Zero);
        var summary = await workspace.CreateBackupService(copier)
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        Assert.Equal(0, summary.NewVersions);
        Assert.Equal(1, summary.Failed);
        Assert.Empty(workspace.Repository.Search(null));
        Assert.Contains(workspace.Repository.RecentIssues(), i => i.Kind == nameof(CopyStatus.DestinationUnavailable));
    }

    [Fact]
    public async Task Cancelling_mid_scan_leaves_no_pending_row_and_no_partial_file()
    {
        using var workspace = new TestWorkspace();
        for (var i = 0; i < 5; i++)
        {
            workspace.WriteFile($"발표{i}.pptx", RandomNumberGenerator.GetBytes(512 * 1024));
        }

        var device = workspace.RegisterDevice();
        using var cts = new CancellationTokenSource();

        // Throttled hard, then cancelled: this is what unplugging the stick mid-copy looks like.
        var copier = workspace.CreateCopier(bytesPerSecond: 256 * 1024);
        var task = workspace.CreateBackupService(copier)
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await task;

        Assert.Empty(workspace.Repository.ListPending());
        Assert.Empty(Directory.EnumerateFiles(workspace.Paths.ArchiveRoot, "*.partial", SearchOption.AllDirectories));

        // Whatever did finish before the cancellation is a real, verified backup.
        foreach (var record in workspace.Repository.Search(null))
        {
            Assert.Equal(Sha256Of(workspace.ArchivePathOf(record)), record.Sha256);
        }
    }
}
