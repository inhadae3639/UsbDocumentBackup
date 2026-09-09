namespace UsbDocumentBackup.Storage;

/// <summary>
/// Resolves every location the app writes to. The state database, credentials and logs always
/// stay under the current user's local app data even when the archive is moved to another disk.
/// </summary>
public sealed class AppPaths
{
    public const string FolderName = "UsbDocumentBackup";

    public AppPaths(string stateRoot, string archiveRoot)
    {
        StateRoot = stateRoot;
        ArchiveRoot = archiveRoot;
    }

    public static AppPaths CreateDefault()
    {
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName);
        return new AppPaths(stateRoot, Path.Combine(stateRoot, "archive"));
    }

    /// <summary>Holds settings.json, state.db, credentials/ and logs/. Never relocated.</summary>
    public string StateRoot { get; }

    /// <summary>Holds the backup copies. Relocatable to another local disk.</summary>
    public string ArchiveRoot { get; }

    /// <summary>
    /// Presentations that were actually opened. Uploaded to Drive, and held here until Drive has
    /// confirmed the upload and the retention window has passed.
    /// </summary>
    public string RetainedRoot => Path.Combine(ArchiveRoot, RetainedFolder);

    /// <summary>
    /// Everything else. Local-only, never uploaded, swept purely on age.
    /// </summary>
    public string TemporaryRoot => Path.Combine(ArchiveRoot, TemporaryFolder);

    public const string RetainedFolder = "retained";
    public const string TemporaryFolder = "temp";

    public string SettingsFile => Path.Combine(StateRoot, "settings.json");
    public string DatabaseFile => Path.Combine(StateRoot, "state.db");
    public string CredentialsDirectory => Path.Combine(StateRoot, "credentials");
    public string LogDirectory => Path.Combine(StateRoot, "logs");

    public AppPaths WithArchiveRoot(string archiveRoot) => new(StateRoot, archiveRoot);

    public string ResolveArchivePath(string relativePath) => Path.Combine(ArchiveRoot, relativePath);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(StateRoot);
        Directory.CreateDirectory(ArchiveRoot);
        Directory.CreateDirectory(RetainedRoot);
        Directory.CreateDirectory(TemporaryRoot);
        Directory.CreateDirectory(CredentialsDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
