using System.Security.Cryptography;
using DocumentBackup.Backup;
using DocumentBackup.Storage;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// The retained tier is driven by "PowerPoint opened this", not by where the file is stored.
/// These tests exercise that path directly, using folders that are deliberately not the fake USB.
/// </summary>
public sealed class OpenedDocumentTests
{
    private static (string Root, string Path) WriteOutsideTheStick(TestWorkspace workspace, string name, string body)
    {
        var root = Path.Combine(workspace.Root, "documents");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, SampleFiles.MinimalPptx(body));
        return (root, path);
    }

    [Fact]
    public async Task A_presentation_opened_outside_any_usb_stick_is_retained_and_queued()
    {
        using var workspace = new TestWorkspace();
        var (root, path) = WriteOutsideTheStick(workspace, "학회 최종 발표.pptx", "내용");
        var device = workspace.RegisterDevice("이 PC");

        var result = await workspace.CreateBackupService()
            .BackupOpenedDocumentAsync(device, root, path, CancellationToken.None);

        Assert.Equal(DocumentBackupResult.NewVersion, result);

        var record = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Retained, record.Tier);
        Assert.Equal("학회 최종 발표.pptx", record.FileName);
        Assert.StartsWith(AppPaths.RetainedFolder + Path.DirectorySeparatorChar, record.LocalRelativePath, StringComparison.Ordinal);
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            record.Sha256);
    }

    [Fact]
    public async Task Re_checking_an_unchanged_opened_presentation_does_not_read_it_again()
    {
        using var workspace = new TestWorkspace();
        var (root, path) = WriteOutsideTheStick(workspace, "발표.pptx", "same");
        var device = workspace.RegisterDevice("이 PC");
        var service = workspace.CreateBackupService();

        await service.BackupOpenedDocumentAsync(device, root, path, CancellationToken.None);
        var second = await service.BackupOpenedDocumentAsync(device, root, path, CancellationToken.None);

        Assert.Equal(DocumentBackupResult.Unchanged, second);
        Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task Editing_an_opened_presentation_adds_a_version_and_a_second_upload()
    {
        using var workspace = new TestWorkspace();
        var (root, path) = WriteOutsideTheStick(workspace, "발표.pptx", "1차");
        var device = workspace.RegisterDevice("이 PC");
        var service = workspace.CreateBackupService();

        await service.BackupOpenedDocumentAsync(device, root, path, CancellationToken.None);
        File.WriteAllBytes(path, SampleFiles.MinimalPptx("2차 - 발표 직전 수정"));

        var second = await service.BackupOpenedDocumentAsync(device, root, path, CancellationToken.None);

        Assert.Equal(DocumentBackupResult.NewVersion, second);
        Assert.Equal(2, workspace.Repository.Search(null).Count);
        Assert.Equal(2, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task A_deck_already_held_temporarily_is_promoted_without_a_second_read()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "content");
        var device = workspace.RegisterDevice();
        var service = workspace.CreateBackupService();

        // First it is only found by the USB scan, with no evidence it was ever opened.
        await service.BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var before = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Temporary, before.Tier);

        // Then PowerPoint's recent list reports it. Same bytes, so only the tier moves.
        var result = await service.BackupOpenedDocumentAsync(
            device,
            workspace.VolumeRoot,
            Path.Combine(workspace.VolumeRoot, "발표.pptx"),
            CancellationToken.None);

        Assert.Equal(DocumentBackupResult.Promoted, result);

        var after = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(BackupTier.Retained, after.Tier);
        Assert.True(File.Exists(workspace.ArchivePathOf(after)));
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task A_recent_entry_whose_file_has_been_deleted_is_reported_not_invented()
    {
        using var workspace = new TestWorkspace();
        var device = workspace.RegisterDevice("이 PC");

        var result = await workspace.CreateBackupService().BackupOpenedDocumentAsync(
            device,
            workspace.Root,
            Path.Combine(workspace.Root, "사라진 발표.pptx"),
            CancellationToken.None);

        Assert.Equal(DocumentBackupResult.SourceGone, result);
        Assert.Empty(workspace.Repository.Search(null));
    }

    [Fact]
    public async Task An_opened_document_is_never_swept_even_while_it_is_still_temporary()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표.pptx", "content");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);
        var record = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Temporary, record.Tier);

        // The deck was opened, but the stick was pulled before the next scan could promote it.
        workspace.Repository.MarkOpened(device.Id, "발표.pptx", DateTimeOffset.UtcNow);

        using (var connection = workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backups SET backed_up_utc = $when WHERE id = $id;";
            command.Parameters.AddWithValue("$when", DateTimeOffset.UtcNow.AddDays(-30).ToString("o"));
            command.Parameters.AddWithValue("$id", record.Id);
            command.ExecuteNonQuery();
        }

        var report = workspace.CreateSweepService(7).Run();

        Assert.Equal(0, report.TemporaryRemoved);
        Assert.True(File.Exists(workspace.ArchivePathOf(record)));
    }

    [Fact]
    public async Task An_ambiguous_owner_file_does_not_retain_a_deck_that_was_never_opened()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("가나표자료.pptx", "A");
        workspace.WritePptx("다라표자료.pptx", "B");

        // "~$표자료.pptx" fits both names under the truncated-name rule.
        workspace.WriteText("~$표자료.pptx", "owner file");

        var device = workspace.RegisterDevice();
        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        Assert.All(workspace.Repository.Search(null), r => Assert.Equal(BackupTier.Temporary, r.Tier));
        Assert.Equal(0, workspace.Repository.CountUploads(UploadState.Waiting));
        Assert.Contains(workspace.Repository.RecentIssues(), i => i.Kind == "LockFileAmbiguous");
    }
}
