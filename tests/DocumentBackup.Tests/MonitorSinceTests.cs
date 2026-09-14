using DocumentBackup.Backup;
using DocumentBackup.Devices;
using DocumentBackup.GoogleDrive;
using DocumentBackup.Storage;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// PowerPoint's recent-file list can hold months of material, so installing the app must not sweep
/// all of it into Drive. Monitoring starts at first run; older material is opt-in.
/// </summary>
public sealed class MonitorSinceTests
{
    private sealed class StubOpenedDocuments : IOpenedDocumentSource
    {
        public List<OpenedDocument> Documents { get; } = [];

        public int Reads { get; private set; }

        public IReadOnlyList<OpenedDocument> GetRecentlyOpened()
        {
            Reads++;
            return Documents;
        }
    }

    private static readonly DateTimeOffset InstalledAt = new(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

    private static (BackupCoordinator Coordinator, StubOpenedDocuments Source, AppSettings Settings)
        Arrange(TestWorkspace workspace, params (string Name, DateTimeOffset Opened)[] documents)
    {
        var source = new StubOpenedDocuments();
        foreach (var (name, opened) in documents)
        {
            var full = workspace.WritePptx(name, name);
            source.Documents.Add(new OpenedDocument(full, opened));
        }

        var settings = new AppSettings { MonitorSinceUtc = InstalledAt, UploadBytesPerSecond = 0 };
        var settingsStore = new SettingsStore(Path.Combine(workspace.Root, "settings.json"));

        var volumes = new FakeVolumeProvider();
        volumes.Volumes.Add(new VolumeInfo(
            workspace.VolumeRoot,
            VolumeId: "\\\\?\\Volume{stub}\\",
            Fingerprint: "stub",
            DisplayName: "테스트 볼륨",
            DriveType: DriveType.Removable,
            BusKind: BusKind.Usb,
            IsReady: true));

        var uploadWorker = new UploadWorker(
            workspace.Paths, workspace.Repository, settings, settingsStore, () => null, workspace.Log);

        var coordinator = new BackupCoordinator(
            volumes,
            workspace.Repository,
            workspace.CreateBackupService(),
            workspace.CreateSweepService(0),
            source,
            uploadWorker,
            settings,
            workspace.Log,
            watchVolumes: false);

        return (coordinator, source, settings);
    }

    private static async Task SweepAsync(BackupCoordinator coordinator)
    {
        coordinator.Start();

        // The worker is a background loop; give it a moment to drain the queued request.
        for (var i = 0; i < 60 && coordinator.GetStatus().LastScanUtc is null; i++)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_presentation_opened_before_installation_is_left_alone()
    {
        using var workspace = new TestWorkspace();
        var (coordinator, _, _) = Arrange(
            workspace,
            ("작년 발표.pptx", InstalledAt.AddDays(-200)),
            ("어제 발표.pptx", InstalledAt.AddDays(-1)));

        await using (coordinator)
        {
            await SweepAsync(coordinator);
        }

        // Neither predates-install document should have been copied at the retained tier.
        Assert.DoesNotContain(workspace.Repository.Search(null), r => r.Tier == BackupTier.Retained);
        Assert.Equal(0, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task A_presentation_opened_after_installation_is_backed_up()
    {
        using var workspace = new TestWorkspace();
        var (coordinator, _, _) = Arrange(
            workspace,
            ("오늘 발표.pptx", InstalledAt.AddMinutes(30)));

        await using (coordinator)
        {
            await SweepAsync(coordinator);
        }

        var retained = workspace.Repository.Search(null).Where(r => r.Tier == BackupTier.Retained).ToList();
        Assert.Single(retained);
        Assert.Equal("오늘 발표.pptx", retained[0].FileName);
        Assert.Equal(1, workspace.Repository.CountUploads(UploadState.Waiting));
    }

    [Fact]
    public async Task Asking_for_older_material_picks_it_up_exactly_once()
    {
        using var workspace = new TestWorkspace();
        var (coordinator, _, _) = Arrange(
            workspace,
            ("작년 발표.pptx", InstalledAt.AddDays(-200)));

        await using (coordinator)
        {
            await SweepAsync(coordinator);
            Assert.DoesNotContain(workspace.Repository.Search(null), r => r.Tier == BackupTier.Retained);

            Assert.Equal(1, coordinator.CountHistoricalCandidates());

            coordinator.RequestHistoricalScan();
            for (var i = 0; i < 60 && !workspace.Repository.Search(null).Any(r => r.Tier == BackupTier.Retained); i++)
            {
                await Task.Delay(50);
            }
        }

        var retained = workspace.Repository.Search(null).Where(r => r.Tier == BackupTier.Retained).ToList();
        Assert.Single(retained);
        Assert.Equal("작년 발표.pptx", retained[0].FileName);
    }

    [Fact]
    public async Task Counting_older_material_ignores_files_that_are_no_longer_there()
    {
        using var workspace = new TestWorkspace();
        var (coordinator, source, _) = Arrange(workspace);

        await using (coordinator)
        {
            source.Documents.Add(new OpenedDocument(
                Path.Combine(workspace.Root, "사라진 발표.pptx"),
                InstalledAt.AddDays(-30)));

            Assert.Equal(0, coordinator.CountHistoricalCandidates());
        }
    }

    [Fact]
    public async Task With_no_cutoff_nothing_counts_as_historical()
    {
        using var workspace = new TestWorkspace();
        var (coordinator, _, settings) = Arrange(workspace, ("옛날 발표.pptx", InstalledAt.AddDays(-100)));

        await using (coordinator)
        {
            settings.MonitorSinceUtc = null;
            Assert.Equal(0, coordinator.CountHistoricalCandidates());
        }
    }
}
