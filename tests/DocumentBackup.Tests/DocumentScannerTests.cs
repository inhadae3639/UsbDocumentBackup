using DocumentBackup.Backup;
using Xunit;

namespace DocumentBackup.Tests;

public sealed class DocumentScannerTests
{
    [Theory]
    [InlineData("발표자료.pptx", true)]
    [InlineData("발표 자료 최종.PPTX", true)]
    [InlineData("보고서.PDF", false)]
    [InlineData("legacy deck.ppt", true)]
    [InlineData("~$발표자료.pptx", false)]
    [InlineData("메모.docx", false)]
    [InlineData("사진.png", false)]
    [InlineData("noextension", false)]
    public void IsTargetFile_matches_only_presentation_documents(string name, bool expected) =>
        Assert.Equal(expected, DocumentScanner.IsTargetFile(name));

    [Fact]
    public void Scan_finds_korean_and_nested_documents_and_ignores_everything_else()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("발표자료.pptx", "제목");
        workspace.WritePptx("2026 학회/발표 자료 최종.PPTX", "abstract");
        workspace.WritePptx("2026 학회/자료/깊은 폴더/부록.PPTX", "부록");
        workspace.WriteText("2026 학회/메모.docx", "not a target");
        // PDFs are out of scope entirely: they can never be retained, and copying them filled
        // gigabytes on a real stick.
        workspace.WritePdf("2026 학회/배포 자료.pdf", "handout");
        workspace.WriteText("~$발표자료.pptx", "office lock file");
        workspace.WriteText("readme.txt", "ignored");

        var result = new DocumentScanner().Scan(workspace.VolumeRoot);

        Assert.Equal(
            ["2026 학회\\발표 자료 최종.PPTX", "2026 학회\\자료\\깊은 폴더\\부록.PPTX", "발표자료.pptx"],
            result.Documents.Select(d => d.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Scan_records_but_survives_an_unreadable_subfolder()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("좋은 폴더/발표.pptx", "ok");

        // A directory that disappears between enumeration and descent is the realistic version of
        // "cannot read this folder": the scan must record it and carry on with the rest.
        var missing = Path.Combine(workspace.VolumeRoot, "사라진 폴더");
        Directory.CreateDirectory(missing);
        var scanner = new DocumentScanner();
        Directory.Delete(missing);

        var result = scanner.Scan(workspace.VolumeRoot);
        Assert.Single(result.Documents);
    }

    [Fact]
    public void Scan_does_not_follow_a_directory_junction()
    {
        using var workspace = new TestWorkspace();
        workspace.WritePptx("실제/발표.pptx", "ok");

        var linkPath = Path.Combine(workspace.VolumeRoot, "링크");
        try
        {
            Directory.CreateSymbolicLink(linkPath, Path.Combine(workspace.VolumeRoot, "실제"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a symlink needs Developer Mode or elevation; nothing to assert without one.
            return;
        }

        var result = new DocumentScanner().Scan(workspace.VolumeRoot);

        Assert.Single(result.Documents);
        Assert.Contains(result.Skips, s => s.Kind == "ReparsePointSkipped");
    }
}
