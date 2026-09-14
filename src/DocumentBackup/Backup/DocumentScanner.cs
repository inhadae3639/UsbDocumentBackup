namespace DocumentBackup.Backup;

/// <summary>A source document found on a volume.</summary>
public sealed record SourceDocument(
    string FullPath,
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteUtc);

/// <summary>Something the scan could not read, kept so the user sees it instead of a silent gap.</summary>
public sealed record ScanSkip(string Path, string Kind, string Detail);

/// <param name="LockFiles">
/// Relative paths of Office owner files that were present during the scan. Each one means the
/// document beside it is open right now, which is what promotes a presentation to the retained tier.
/// </param>
public sealed record ScanResult(
    IReadOnlyList<SourceDocument> Documents,
    IReadOnlyList<ScanSkip> Skips,
    IReadOnlyList<string> LockFiles);

/// <summary>
/// Walks a volume looking for presentation documents. The walk is read-only, iterative, and never
/// follows a reparse point onto another volume.
/// </summary>
public sealed class DocumentScanner
{
    /// <summary>
    /// Presentations only. PDFs were dropped after a real USB produced 7.2 GB of backups from a
    /// single scan, 511 of 526 files being PDFs that could never reach the retained tier anyway:
    /// nothing writes an Office owner file for a PDF, and PowerPoint's recent list never lists one.
    /// </summary>
    private static readonly string[] TargetExtensions = [".ppt", ".pptx"];

    /// <summary>Windows-managed folders that only ever hold shadow copies or deleted shells.</summary>
    private static readonly string[] ExcludedFolderNames = ["$RECYCLE.BIN", "System Volume Information"];

    public static bool IsTargetFile(string fileName)
    {
        // An Office owner file is evidence about a document, never a document itself.
        if (OfficeLockFile.IsLockFileName(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return TargetExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the file is a presentation. Since PDFs are no longer copied at all, this is the same
    /// set as <see cref="IsTargetFile"/> minus the owner-file check; it is kept separate because it
    /// answers a different question -- "could this ever be retained and uploaded?".
    /// </summary>
    public static bool IsPresentation(string fileName) =>
        TargetExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public ScanResult Scan(string rootPath, CancellationToken cancellationToken = default)
    {
        var documents = new List<SourceDocument>();
        var skips = new List<ScanSkip>();
        var lockFiles = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skips.Add(new ScanSkip(directory, "DirectoryUnreadable", ex.Message));
                continue;
            }

            IEnumerator<FileSystemInfo> enumerator;
            try
            {
                enumerator = entries.GetEnumerator();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skips.Add(new ScanSkip(directory, "DirectoryUnreadable", ex.Message));
                continue;
            }

            using (enumerator)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileSystemInfo entry;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        entry = enumerator.Current;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        skips.Add(new ScanSkip(directory, "DirectoryUnreadable", ex.Message));
                        break;
                    }

                    // A junction or directory symlink can point at another volume or back at an
                    // ancestor. Skipping them keeps the walk on this device and free of cycles.
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        skips.Add(new ScanSkip(entry.FullName, "ReparsePointSkipped", "Not followed."));
                        continue;
                    }

                    if (entry is DirectoryInfo subdirectory)
                    {
                        if (ExcludedFolderNames.Contains(subdirectory.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        pending.Push(subdirectory.FullName);
                        continue;
                    }

                    if (entry is not FileInfo file)
                    {
                        continue;
                    }

                    // An owner file that is already there means the document is open right now,
                    // for instance because the app started while a deck was on screen.
                    if (OfficeLockFile.IsLockFileName(file.Name))
                    {
                        lockFiles.Add(Path.GetRelativePath(rootPath, file.FullName));
                        continue;
                    }

                    if (!IsTargetFile(file.Name))
                    {
                        continue;
                    }

                    try
                    {
                        documents.Add(new SourceDocument(
                            file.FullName,
                            Path.GetRelativePath(rootPath, file.FullName),
                            file.Length,
                            file.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        skips.Add(new ScanSkip(file.FullName, "FileUnreadable", ex.Message));
                    }
                }
            }
        }

        return new ScanResult(documents, skips, lockFiles);
    }
}
