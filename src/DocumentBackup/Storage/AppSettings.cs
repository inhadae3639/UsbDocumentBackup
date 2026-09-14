using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentBackup.Storage;

/// <summary>
/// User settings, stored as UTF-8 JSON next to the state database. Nothing secret goes in here:
/// Google tokens are kept separately, encrypted for the current Windows user.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Empty means "use the default archive folder under local app data".</summary>
    public string? ArchiveRoot { get; set; }

    /// <summary>
    /// Throttle applied to every read of a source document. Starting values from the design;
    /// they are expected to be re-tuned against a real USB stick.
    /// </summary>
    public int LocalReadBytesPerSecond { get; set; } = 5 * 1024 * 1024;

    public int UploadBytesPerSecond { get; set; } = 2 * 1024 * 1024;

    /// <summary>Refuse to start a new copy when the archive disk would drop below this.</summary>
    public long MinimumFreeBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// How long a backup is held locally. Temporary backups are deleted this many days after they
    /// were made; retained presentations only after Drive has confirmed the upload as well.
    /// Zero or less disables the sweep entirely.
    /// </summary>
    public int TemporaryRetentionDays { get; set; } = 7;

    /// <summary>
    /// Only presentations opened at or after this moment are picked up automatically. Set once, on
    /// first run, so installing the app does not sweep up months of history from PowerPoint's
    /// recent-file list. Earlier material is backed up only when the user asks for it explicitly.
    /// </summary>
    public DateTimeOffset? MonitorSinceUtc { get; set; }

    public bool RunAtLogin { get; set; }

    public bool Paused { get; set; }

    /// <summary>Identifies the connected Google account so a re-connect cannot silently switch accounts.</summary>
    public string? GoogleAccountKey { get; set; }

    /// <summary>
    /// The account this installation is meant to upload to, set ahead of time. When present, a
    /// connection to any other account is refused instead of quietly uploading somewhere else.
    /// </summary>
    public string? ExpectedGoogleAccount { get; set; }

    /// <summary>Top-level Drive folder name, used only when the folder is first created.</summary>
    public string DriveFolderName { get; set; } = "Document Backups";

    /// <summary>Resolved once the folder exists; the id is what uploads actually use.</summary>
    public string? DriveFolderId { get; set; }

    [JsonIgnore]
    public bool HasCustomArchiveRoot => !string.IsNullOrWhiteSpace(ArchiveRoot);
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _file;
    private readonly object _gate = new();

    public SettingsStore(string file) => _file = file;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_file))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(_file, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A corrupt settings file must not stop backups; fall back to defaults.
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
            File.Move(temp, _file, overwrite: true);
        }
    }
}
