namespace DocumentBackup.Storage;

/// <summary>
/// Resolves every location the app writes to. The state database, credentials and logs always
/// stay under the current user's local app data even when the archive is moved to another disk.
/// </summary>
public sealed class AppPaths
{
    public const string FolderName = "DocumentBackup";

    /// <summary>What the data folder was called before the application was renamed.</summary>
    private const string FormerFolderName = "UsbDocumentBackup";

    public AppPaths(string stateRoot, string archiveRoot)
    {
        StateRoot = stateRoot;
        ArchiveRoot = archiveRoot;
    }

    public static AppPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var stateRoot = Path.Combine(localAppData, FolderName);
        AdoptFormerFolder(Path.Combine(localAppData, FormerFolderName), stateRoot);
        return new AppPaths(stateRoot, Path.Combine(stateRoot, "archive"));
    }

    /// <summary>
    /// Moves the data folder from the name the application used to have.
    ///
    /// Everything lives in there: the archive, the state database and the encrypted Google token.
    /// Simply pointing at a new name would present an upgraded installation with no backups, no
    /// history and no account. Renaming the directory keeps all of it, and the archive paths in the
    /// database are relative to the root so they stay correct.
    ///
    /// Anything that goes wrong here is left alone rather than guessed at: a failed move leaves the
    /// old folder untouched, which is recoverable, whereas a half-copied archive would not be.
    /// </summary>
    /// <returns>True when a move actually happened.</returns>
    internal static bool AdoptFormerFolder(string formerRoot, string stateRoot)
    {
        try
        {
            if (Directory.Exists(stateRoot) || !Directory.Exists(formerRoot))
            {
                return false;
            }

            Directory.Move(formerRoot, stateRoot);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Most likely the old folder is still open in another process. Starting fresh is worse
            // than trying again next launch, so the new root is simply created empty this time.
            return false;
        }
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
