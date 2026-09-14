using System.IO.Compression;
using System.Text;
using DocumentBackup.Backup;
using DocumentBackup.Devices;
using DocumentBackup.Storage;
using DocumentBackup.Windows;

namespace DocumentBackup.Tests;

/// <summary>
/// An isolated temp folder holding a fake "USB volume" plus a real archive, database and log.
/// Tests never touch a real device, the user's app data, or anything outside this folder.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        // A short root keeps the deep-path tests meaningful without blowing past what the test
        // harness itself can handle.
        Root = Path.Combine(Path.GetTempPath(), "udb-t", Guid.NewGuid().ToString("N")[..8]);
        VolumeRoot = Path.Combine(Root, "usb");
        Directory.CreateDirectory(VolumeRoot);

        Paths = new AppPaths(Path.Combine(Root, "state"), Path.Combine(Root, "archive"));
        Paths.EnsureCreated();

        Database = new BackupDatabase(Paths.DatabaseFile);
        Database.Migrate();
        Repository = new BackupRepository(Database);
        Log = new Log(Paths.LogDirectory);
    }

    public string Root { get; }

    public string VolumeRoot { get; }

    public AppPaths Paths { get; }

    public BackupDatabase Database { get; }

    public BackupRepository Repository { get; }

    public Log Log { get; }

    public BackupService CreateBackupService(FileCopier? copier = null) => new(
        Paths,
        Repository,
        new DocumentScanner(),
        copier ?? CreateCopier(),
        Log);

    public FileCopier CreateCopier(long bytesPerSecond = 0, TimeSpan? stability = null) =>
        new(new RateLimiter(bytesPerSecond), minimumFreeBytes: 0, stability ?? TimeSpan.Zero);

    public RecoveryService CreateRecoveryService() => new(Paths, Repository, Log);

    public ArchiveSweepService CreateSweepService(int retentionDays = 7, TimeProvider? time = null) =>
        new(Paths, Repository, Log, retentionDays, time);

    /// <summary>Writes the Office owner file that Windows shows while a document is open.</summary>
    public string WriteLockFileFor(string documentRelativePath)
    {
        var directory = Path.GetDirectoryName(documentRelativePath) ?? string.Empty;
        var name = Path.GetFileName(documentRelativePath);
        var lockName = name.Length > 6 ? OfficeLockFile.Prefix + name[2..] : OfficeLockFile.Prefix + name;
        return WriteText(Path.Combine(directory, lockName), "office owner file");
    }

    public RestoreService CreateRestoreService() => new(Paths, Repository);

    public DeviceRecord RegisterDevice(string name = "TestStick", string? volumeId = null) =>
        Repository.UpsertDevice(volumeId ?? "\\\\?\\Volume{" + Guid.NewGuid() + "}\\", name + "|fp", name, BusKind.Usb, DateTimeOffset.UtcNow);

    // ---------- source file helpers ----------

    public string WriteFile(string relativePath, byte[] content)
    {
        var full = Path.Combine(VolumeRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string WriteText(string relativePath, string content) =>
        WriteFile(relativePath, new UTF8Encoding(false).GetBytes(content));

    public string WritePdf(string relativePath, string bodyText) =>
        WriteFile(relativePath, SampleFiles.MinimalPdf(bodyText));

    public string WritePptx(string relativePath, string slideText) =>
        WriteFile(relativePath, SampleFiles.MinimalPptx(slideText));

    public string ArchivePathOf(BackupRecord record) => Paths.ResolveArchivePath(record.LocalRelativePath);

    public void Dispose()
    {
        try
        {
            // Release pooled SQLite handles so the temp folder can actually be deleted on Windows.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Serves a fixed set of volumes so coordinator tests never look at real hardware.</summary>
public sealed class FakeVolumeProvider : IVolumeProvider
{
    public List<VolumeInfo> Volumes { get; } = [];

    public IReadOnlyList<VolumeInfo> GetVolumes() => Volumes;
}

/// <summary>
/// Builds real, structurally valid documents rather than renamed text files, so hash and restore
/// checks run against the kind of binary content the app actually handles.
/// </summary>
public static class SampleFiles
{
    public static byte[] MinimalPdf(string text)
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({Escape(text)}) Tj ET\n";
        var objects = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            $"<</Length {Encoding.ASCII.GetByteCount(content)}>>\nstream\n{content}endstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");

        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<</Size ").Append(objects.Length + 1).Append("/Root 1 0 R>>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>
    /// A PresentationML package with the parts and relationships PowerPoint expects. Verified here
    /// as a well-formed OPC package; whether PowerPoint renders it is a manual check.
    /// </summary>
    public static byte[] MinimalPptx(string slideText)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                  <Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>
                  <Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>
                  <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
                </Types>
                """);

            Add(archive, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """);

            Add(archive, "ppt/presentation.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst>
                  <p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst>
                  <p:sldSz cx="9144000" cy="6858000"/>
                  <p:notesSz cx="6858000" cy="9144000"/>
                </p:presentation>
                """);

            Add(archive, "ppt/_rels/presentation.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                </Relationships>
                """);

            Add(archive, "ppt/slideMasters/slideMaster1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>
                  <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
                  <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>
                </p:sldMaster>
                """);

            Add(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
                </Relationships>
                """);

            Add(archive, "ppt/slideLayouts/slideLayout1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="blank">
                  <p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>
                  <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
                </p:sldLayout>
                """);

            Add(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/>
                </Relationships>
                """);

            Add(archive, "ppt/slides/slide1.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <p:cSld><p:spTree>
                    <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>
                    <p:sp>
                      <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr/></p:nvSpPr>
                      <p:spPr><a:xfrm><a:off x="685800" y="2130425"/><a:ext cx="7772400" cy="1470025"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                      <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="ko-KR" dirty="0"/><a:t>{System.Security.SecurityElement.Escape(slideText)}</a:t></a:r></a:p></p:txBody>
                    </p:sp>
                  </p:spTree></p:cSld>
                  <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
                </p:sld>
                """);

            Add(archive, "ppt/slides/_rels/slide1.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
                </Relationships>
                """);
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
}
