using System.Security.Cryptography;
using DocumentBackup.Backup;
using DocumentBackup.GoogleDrive;
using DocumentBackup.Storage;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// A file larger than one chunk goes up in several requests, and each has to declare exactly the
/// bytes it carries. Getting that wrong made every presentation over 8 MiB fail against the real
/// Drive with "There were 8388608 byte(s) in the request body", while small files worked, because
/// for those the chunk happens to be the whole file.
///
/// The chunk size is injected so this runs on kilobytes instead of megabytes.
/// </summary>
public sealed class MultiChunkUploadTests
{
    private const int ChunkSize = 1024;

    private static async Task<(BackupRecord Backup, UploadWorker Worker, FakeDriveClient Drive)>
        ArrangeAsync(TestWorkspace workspace, int fileSize)
    {
        // Random bytes so a mis-ordered or duplicated chunk cannot accidentally match.
        workspace.WriteFile("큰 발표.pptx", RandomNumberGenerator.GetBytes(fileSize));
        workspace.WriteLockFileFor("큰 발표.pptx");
        var device = workspace.RegisterDevice();

        await workspace.CreateBackupService()
            .BackupVolumeAsync(device, workspace.VolumeRoot, ScanReason.NewConnection, CancellationToken.None);

        var backup = Assert.Single(workspace.Repository.Search(null));
        Assert.Equal(BackupTier.Retained, backup.Tier);

        var drive = new FakeDriveClient();
        var worker = new UploadWorker(
            workspace.Paths,
            workspace.Repository,
            new AppSettings { UploadBytesPerSecond = 0 },
            new SettingsStore(Path.Combine(workspace.Root, "settings.json")),
            () => drive,
            workspace.Log,
            chunkSize: ChunkSize);

        return (backup, worker, drive);
    }

    [Fact]
    public async Task A_file_spanning_several_chunks_uploads_completely_and_intact()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace, (ChunkSize * 3) + 137);

        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));
        Assert.Equal(UploadState.Done, workspace.Repository.GetUpload(backup.Id)!.State);

        var uploaded = Assert.Single(drive.Files).Value;
        Assert.Equal(File.ReadAllBytes(workspace.ArchivePathOf(backup)), uploaded);
    }

    [Fact]
    public async Task Each_chunk_declares_exactly_the_bytes_it_carries()
    {
        using var workspace = new TestWorkspace();
        var total = (ChunkSize * 3) + 137;
        var (_, worker, drive) = await ArrangeAsync(workspace, total);

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(4, drive.ChunkRanges.Count);

        long expectedOffset = 0;
        foreach (var (offset, length, declaredTotal) in drive.ChunkRanges)
        {
            Assert.Equal(expectedOffset, offset);
            Assert.Equal(total, declaredTotal);
            Assert.Equal(Math.Min(ChunkSize, total - offset), length);
            expectedOffset += length;
        }

        // The chunks together account for the whole file, with nothing left over.
        Assert.Equal(total, expectedOffset);
    }

    [Fact]
    public async Task A_file_smaller_than_one_chunk_still_goes_in_a_single_request()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace, ChunkSize / 2);

        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));

        var range = Assert.Single(drive.ChunkRanges);
        Assert.Equal(0, range.Offset);
        Assert.Equal(backup.SizeBytes, range.Length);
        Assert.Equal(backup.SizeBytes, range.Total);
    }

    [Fact]
    public async Task A_file_that_is_an_exact_multiple_of_the_chunk_size_does_not_send_an_empty_chunk()
    {
        using var workspace = new TestWorkspace();
        var (_, worker, drive) = await ArrangeAsync(workspace, ChunkSize * 2);

        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));

        Assert.Equal(2, drive.ChunkRanges.Count);
        Assert.All(drive.ChunkRanges, r => Assert.Equal(ChunkSize, r.Length));
    }

    [Fact]
    public async Task An_interrupted_multi_chunk_upload_resumes_where_it_stopped()
    {
        using var workspace = new TestWorkspace();
        var (backup, worker, drive) = await ArrangeAsync(workspace, ChunkSize * 4);

        // Fail once partway so the transfer stops with some bytes already stored.
        drive.TransientChunkFailures = 1;
        await worker.RunAsync(CancellationToken.None);
        Assert.Equal(UploadState.Waiting, workspace.Repository.GetUpload(backup.Id)!.State);

        using (var connection = workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE uploads SET next_attempt_utc = NULL WHERE backup_id = $id;";
            command.Parameters.AddWithValue("$id", backup.Id);
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, await worker.RunAsync(CancellationToken.None));

        // One file, byte-identical, and no second Drive id was ever reserved.
        Assert.Equal(File.ReadAllBytes(workspace.ArchivePathOf(backup)), Assert.Single(drive.Files).Value);
        Assert.Equal(1, drive.GeneratedIds);
    }
}
