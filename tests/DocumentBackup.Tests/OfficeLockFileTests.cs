using DocumentBackup.Backup;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// Attributing an owner file to the right document is what decides whether a presentation is kept
/// and uploaded, so both naming shapes Office uses are pinned down here.
/// </summary>
public sealed class OfficeLockFileTests
{
    [Fact]
    public void A_short_name_keeps_the_whole_document_name_after_the_prefix()
    {
        var matches = OfficeLockFile.ResolveCandidates("~$a.pptx", ["a.pptx", "b.pptx"]);
        Assert.Equal(["a.pptx"], matches);
    }

    [Fact]
    public void A_longer_name_has_its_first_two_characters_replaced_by_the_prefix()
    {
        // Office keeps the owner file the same length as the document, dropping two characters.
        var matches = OfficeLockFile.ResolveCandidates(
            "~$26 학회 발표.pptx",
            ["2026 학회 발표.pptx", "다른 자료.pptx", "메모.txt"]);

        Assert.Equal(["2026 학회 발표.pptx"], matches);
    }

    [Fact]
    public void An_owner_file_for_a_document_that_is_not_there_matches_nothing()
    {
        var matches = OfficeLockFile.ResolveCandidates("~$없는 자료.pptx", ["다른 자료.pptx"]);
        Assert.Empty(matches);
    }

    [Fact]
    public void The_owner_file_never_matches_itself()
    {
        var matches = OfficeLockFile.ResolveCandidates("~$a.pptx", ["~$a.pptx"]);
        Assert.Empty(matches);
    }

    [Fact]
    public void Every_document_that_could_own_the_file_is_returned()
    {
        // Two candidates fit the truncated shape. Retaining both is the safe failure: the
        // alternative is dropping the presentation the user actually opened.
        var matches = OfficeLockFile.ResolveCandidates("~$표자료.pptx", ["가나표자료.pptx", "다라표자료.pptx"]);
        Assert.Equal(2, matches.Count);
    }

    [Theory]
    [InlineData("~$a.pptx", true)]
    [InlineData("a.pptx", false)]
    [InlineData("~a.pptx", false)]
    public void IsLockFileName_only_matches_the_office_prefix(string name, bool expected) =>
        Assert.Equal(expected, OfficeLockFile.IsLockFileName(name));

    [Fact]
    public void A_name_that_is_only_the_prefix_matches_nothing() =>
        Assert.Empty(OfficeLockFile.ResolveCandidates("~$", ["a.pptx"]));
}
