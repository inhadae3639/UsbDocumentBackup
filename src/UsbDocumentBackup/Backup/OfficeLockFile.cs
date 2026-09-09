namespace UsbDocumentBackup.Backup;

/// <summary>
/// Maps an Office owner ("lock") file back to the document it belongs to.
///
/// Office writes a hidden "~$" file next to a document while it is open, so seeing one appear is
/// our evidence that someone actually opened that presentation. The naming is not a simple prefix:
/// Office keeps the owner file name the same length as the document name, dropping the first two
/// characters of longer names. Both shapes are matched, and an owner file we cannot attribute is
/// reported rather than quietly ignored.
/// </summary>
public static class OfficeLockFile
{
    public const string Prefix = "~$";

    public static bool IsLockFileName(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Returns the candidate document names in the same folder that this owner file could belong to.
    /// More than one match is possible for short names, in which case every candidate is treated as
    /// opened: retaining one extra presentation is a far better failure than losing the real one.
    /// </summary>
    public static IReadOnlyList<string> ResolveCandidates(string lockFileName, IEnumerable<string> siblingFileNames)
    {
        if (!IsLockFileName(lockFileName))
        {
            return [];
        }

        var core = lockFileName[Prefix.Length..];
        if (core.Length == 0)
        {
            return [];
        }

        var matches = new List<string>();
        foreach (var candidate in siblingFileNames)
        {
            if (candidate.Equals(lockFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Short name: the owner file is just the document name with the prefix bolted on.
            if (candidate.Equals(core, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(candidate);
                continue;
            }

            // Longer name: the prefix replaced the first two characters, so the lengths line up
            // and the owner file name is a suffix of the document name.
            if (candidate.Length == core.Length + Prefix.Length
                && candidate.EndsWith(core, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(candidate);
            }
        }

        return matches;
    }
}
