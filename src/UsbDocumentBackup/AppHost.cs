using UsbDocumentBackup.Backup;
using UsbDocumentBackup.Devices;
using UsbDocumentBackup.GoogleDrive;
using UsbDocumentBackup.Storage;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup;

/// <summary>
/// Wires the pieces together once, so the tray context, the windows and the tests all work against
/// the same object graph.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(
        AppPaths paths,
        AppSettings settings,
        SettingsStore settingsStore,
        Log log,
        BackupRepository repository,
        BackupCoordinator coordinator,
        RestoreService restoreService,
        RecoveryService recoveryService,
        ArchiveSweepService sweepService,
        GoogleConnection google,
        UploadWorker uploadWorker)
    {
        Paths = paths;
        Settings = settings;
        SettingsStore = settingsStore;
        Log = log;
        Repository = repository;
        Coordinator = coordinator;
        RestoreService = restoreService;
        RecoveryService = recoveryService;
        SweepService = sweepService;
        Google = google;
        UploadWorker = uploadWorker;
    }

    public AppPaths Paths { get; }

    public AppSettings Settings { get; }

    public SettingsStore SettingsStore { get; }

    public Log Log { get; }

    public BackupRepository Repository { get; }

    public BackupCoordinator Coordinator { get; }

    public RestoreService RestoreService { get; }

    public RecoveryService RecoveryService { get; }

    public ArchiveSweepService SweepService { get; }

    public GoogleConnection Google { get; }

    public UploadWorker UploadWorker { get; }

    public static AppHost Create(AppPaths? overridePaths = null, IVolumeProvider? volumeProvider = null)
    {
        var basePaths = overridePaths ?? AppPaths.CreateDefault();
        var settingsStore = new SettingsStore(basePaths.SettingsFile);
        var settings = settingsStore.Load();

        // First run establishes "from now on". Without this, the first sweep would treat every
        // entry in PowerPoint's recent list -- potentially months of it -- as something to upload.
        if (settings.MonitorSinceUtc is null)
        {
            settings.MonitorSinceUtc = DateTimeOffset.UtcNow;
            settingsStore.Save(settings);
        }

        // The archive can be moved to another disk; state, credentials and logs never move.
        var paths = settings.HasCustomArchiveRoot ? basePaths.WithArchiveRoot(settings.ArchiveRoot!) : basePaths;
        paths.EnsureCreated();

        var log = new Log(paths.LogDirectory);

        var database = new BackupDatabase(paths.DatabaseFile);
        database.Migrate();
        var repository = new BackupRepository(database);

        var copier = new FileCopier(
            new RateLimiter(settings.LocalReadBytesPerSecond),
            settings.MinimumFreeBytes);

        var backupService = new BackupService(paths, repository, new DocumentScanner(), copier, log);

        var provider = volumeProvider ?? new WindowsVolumeProvider(
            (message, ex) => log.Warn(message + (ex is null ? string.Empty : " :: " + ex.Message)));

        var sweepService = new ArchiveSweepService(paths, repository, log, settings.TemporaryRetentionDays);

        var google = new GoogleConnection(paths, log);

        // Null while no account is connected, which is what keeps every upload queued instead of
        // failing, and keeps local backup and restore working on their own.
        IDriveClient? DriveClient() =>
            google.State == ConnectionState.Connected ? google.CreateClient() : null;

        var uploadWorker = new UploadWorker(
            paths,
            repository,
            settings,
            settingsStore,
            DriveClient,
            log,
            google.MarkReconnectRequired);

        var openedDocuments = new OfficeMruReader(message => log.Warn(message));

        var coordinator = new BackupCoordinator(
            provider,
            repository,
            backupService,
            sweepService,
            openedDocuments,
            uploadWorker,
            settings,
            log);
        if (settings.Paused)
        {
            coordinator.SetPaused(true);
        }

        return new AppHost(
            paths,
            settings,
            settingsStore,
            log,
            repository,
            coordinator,
            new RestoreService(paths, repository, DriveClient),
            new RecoveryService(paths, repository, log),
            sweepService,
            google,
            uploadWorker);
    }

    public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
}
