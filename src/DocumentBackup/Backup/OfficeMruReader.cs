using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DocumentBackup.Backup;

/// <summary>A presentation PowerPoint recorded as opened, wherever it happens to live.</summary>
public sealed record OpenedDocument(string FullPath, DateTimeOffset OpenedUtc);

public interface IOpenedDocumentSource
{
    IReadOnlyList<OpenedDocument> GetRecentlyOpened();
}

/// <summary>
/// Reads PowerPoint's own most-recently-used list.
///
/// This is the authoritative answer to "was this deck opened on this PC": PowerPoint writes the
/// absolute path and a timestamp when a document is opened, whatever drive it came from. Two
/// properties matter more than the Office lock file we also watch:
/// it is <b>location independent</b>, and it is <b>retroactive</b> -- a deck opened while this
/// program was not running is still listed afterwards.
///
/// Layout (Office 2016 and later):
///   HKCU\Software\Microsoft\Office\{ver}\PowerPoint\File MRU
///   HKCU\Software\Microsoft\Office\{ver}\PowerPoint\User MRU\{identity}\File MRU
/// A signed-in Office keeps one list per identity (work and personal accounts get separate keys),
/// so every subkey has to be read, not just the top-level one.
///
/// Values look like:  [F00000000][T01DD3F9A2662DE30][O00000000]*C:\folder\deck.pptx
/// where T is a hex FILETIME.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeMruReader : IOpenedDocumentSource
{
    private const string OfficeRoot = @"Software\Microsoft\Office";

    private readonly Action<string> _onProblem;

    public OfficeMruReader(Action<string>? onProblem = null) => _onProblem = onProblem ?? (_ => { });

    public IReadOnlyList<OpenedDocument> GetRecentlyOpened()
    {
        var results = new Dictionary<string, OpenedDocument>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var office = Registry.CurrentUser.OpenSubKey(OfficeRoot);
            if (office is null)
            {
                _onProblem("Office is not registered for this user; falling back to lock-file detection only.");
                return [];
            }

            foreach (var versionName in office.GetSubKeyNames())
            {
                // Version keys are numeric ("16.0"); skip "Common", "ClickToRun" and friends.
                if (!double.TryParse(versionName, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                using var powerPoint = office.OpenSubKey($@"{versionName}\PowerPoint");
                if (powerPoint is null)
                {
                    continue;
                }

                ReadList(powerPoint, "File MRU", results);

                using var userMru = powerPoint.OpenSubKey("User MRU");
                if (userMru is null)
                {
                    continue;
                }

                foreach (var identity in userMru.GetSubKeyNames())
                {
                    using var identityKey = userMru.OpenSubKey(identity);
                    if (identityKey is not null)
                    {
                        ReadList(identityKey, "File MRU", results);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _onProblem($"Could not read PowerPoint's recent file list: {ex.Message}");
            return [];
        }

        return results.Values.OrderByDescending(d => d.OpenedUtc).ToList();
    }

    private void ReadList(RegistryKey parent, string name, Dictionary<string, OpenedDocument> results)
    {
        using var key = parent.OpenSubKey(name);
        if (key is null)
        {
            return;
        }

        foreach (var valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is not string raw)
            {
                continue;
            }

            var entry = Parse(raw);
            if (entry is null)
            {
                continue;
            }

            // The same deck can appear under several identities; keep the most recent sighting.
            if (!results.TryGetValue(entry.FullPath, out var existing) || existing.OpenedUtc < entry.OpenedUtc)
            {
                results[entry.FullPath] = entry;
            }
        }
    }

    /// <summary>
    /// Parses one MRU value. Returns null for anything we cannot back up from disk: a
    /// SharePoint or OneDrive web URL, a non-presentation, or a malformed entry.
    /// </summary>
    internal static OpenedDocument? Parse(string raw)
    {
        var separator = raw.IndexOf('*');
        if (separator < 0 || separator + 1 >= raw.Length)
        {
            return null;
        }

        var path = raw[(separator + 1)..].Trim();
        if (path.Length == 0 || !DocumentScanner.IsPresentation(path))
        {
            return null;
        }

        // Documents opened straight from a web location have no local file to copy.
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var openedUtc = ParseTimestamp(raw);
        return new OpenedDocument(path, openedUtc);
    }

    private static DateTimeOffset ParseTimestamp(string raw)
    {
        var start = raw.IndexOf("[T", StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = raw.IndexOf(']', start);
            if (end > start + 2
                && long.TryParse(raw[(start + 2)..end], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime)
                && fileTime > 0)
            {
                try
                {
                    return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Fall through to the default below.
                }
            }
        }

        // A missing or unreadable timestamp must not drop the entry: the path is the useful part.
        return DateTimeOffset.MinValue;
    }
}
