using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocumentBackup.Backup;
using Xunit;

namespace DocumentBackup.Tests;

public sealed class RestoreServiceTests
{
    [Fact]
    public async Task A_deleted_original_can_be_restored_and_matches_the_recorded_hash()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.WritePptx("발표 자료/최종.pptx", "복원 대상");
        var originalBytes = File.ReadAllBytes(source);

        var device = workspace.RegisterDevice();
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var record = Assert.Single(workspace.Repository.Search(null));

        // Deleting our own throwaway test file stands in for the user emptying the recycle bin.
        File.Delete(source);
        Assert.False(File.Exists(source));

        var target = Path.Combine(workspace.Root, "restored");
        var result = await workspace.CreateRestoreService().RestoreAsync(record.Id, target);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(result.RestoredPath!));
        Assert.Equal(
            record.Sha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.RestoredPath!))).ToLowerInvariant());

        // The restored file is still a readable presentation package, not just matching bytes.
        using var archive = ZipFile.OpenRead(result.RestoredPath!);
        Assert.Contains(archive.Entries, e => e.FullName == "ppt/presentation.xml");
    }

    [Fact]
    public async Task Restoring_twice_keeps_the_existing_file_and_writes_a_new_name()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("보고서.pptx", "원본");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var record = Assert.Single(workspace.Repository.Search(null));

        var target = Path.Combine(workspace.Root, "restored");
        Directory.CreateDirectory(target);

        var occupied = Path.Combine(target, "보고서.pptx");
        File.WriteAllText(occupied, "사용자가 이미 가지고 있던 다른 파일", new UTF8Encoding(false));

        var result = await workspace.CreateRestoreService().RestoreAsync(record.Id, target);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotEqual(occupied, result.RestoredPath);
        Assert.Equal("보고서 (2).pptx", Path.GetFileName(result.RestoredPath));
        Assert.Equal("사용자가 이미 가지고 있던 다른 파일", File.ReadAllText(occupied, Encoding.UTF8));
    }

    [Fact]
    public async Task Restoring_an_older_version_returns_that_version_not_the_newest()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "1차 원고");
        var device = workspace.RegisterDevice();
        var service = workspace.CreateBackupService();

        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        workspace.WritePptx("발표.pptx", "2차 원고 - 내용이 많이 바뀜");
        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.ChangeEvent, CancellationToken.None);

        var versions = workspace.Repository.ListVersions(device.Id, "발표.pptx");
        Assert.Equal(2, versions.Count);
        var oldest = versions[^1];

        var result = await workspace.CreateRestoreService()
            .RestoreAsync(oldest.Id, Path.Combine(workspace.Root, "restored"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            oldest.Sha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.RestoredPath!))).ToLowerInvariant());
    }

    [Fact]
    public async Task A_missing_archive_file_reports_why_instead_of_claiming_success()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("보고서.pptx", "내용");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var record = Assert.Single(workspace.Repository.Search(null));

        File.Delete(workspace.ArchivePathOf(record));

        var result = await workspace.CreateRestoreService()
            .RestoreAsync(record.Id, Path.Combine(workspace.Root, "restored"));

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreStatus.ArchiveFileMissing, result.Status);
    }
}
