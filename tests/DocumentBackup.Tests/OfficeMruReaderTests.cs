using DocumentBackup.Backup;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// PowerPoint's recent-file list is the authoritative "was this opened on this PC" signal, so the
/// value format is pinned down here. The samples use the exact shape observed in
/// HKCU\Software\Microsoft\Office\16.0\PowerPoint\User MRU\{identity}\File MRU.
/// </summary>
public sealed class OfficeMruReaderTests
{
    [Fact]
    public void A_local_presentation_entry_yields_its_path_and_open_time()
    {
        var entry = OfficeMruReader.Parse(@"[F00000000][T01DD3F9A2662DE30][O00000000]*C:\발표\학회 최종.pptx");

        Assert.NotNull(entry);
        Assert.Equal(@"C:\발표\학회 최종.pptx", entry!.FullPath);

        // 0x01DD3F9A2662DE30 is a FILETIME; decoding it is what makes the entry orderable.
        Assert.Equal(
            DateTimeOffset.FromFileTime(0x01DD3F9A2662DE30).ToUniversalTime(),
            entry.OpenedUtc);
    }

    [Fact]
    public void The_path_may_live_anywhere_not_just_on_a_usb_stick()
    {
        foreach (var path in new[]
                 {
                     @"E:\발표.pptx",
                     @"C:\Users\me\OneDrive\deck.pptx",
                     @"D:\Desktop\중간 발표.ppt",
                     @"\\server\share\팀 발표.pptx",
                 })
        {
            var entry = OfficeMruReader.Parse($"[F00000000][T01DD3F9A2662DE30][O00000000]*{path}");
            Assert.NotNull(entry);
            Assert.Equal(path, entry!.FullPath);
        }
    }

    [Fact]
    public void A_web_location_is_skipped_because_there_is_no_local_file_to_copy()
    {
        Assert.Null(OfficeMruReader.Parse(
            "[F00000000][T01DD3F9A2662DE30][O00000000]*https://contoso-my.sharepoint.com/personal/x/deck.pptx"));
    }

    [Fact]
    public void Non_presentations_are_skipped()
    {
        Assert.Null(OfficeMruReader.Parse(@"[F00000000][T01DD3F9A2662DE30][O00000000]*C:\보고서.docx"));
        Assert.Null(OfficeMruReader.Parse(@"[F00000000][T01DD3F9A2662DE30][O00000000]*C:\자료.xlsx"));
    }

    [Fact]
    public void A_relative_or_malformed_entry_is_skipped()
    {
        Assert.Null(OfficeMruReader.Parse("no separator here"));
        Assert.Null(OfficeMruReader.Parse(@"[F00000000][T01DD3F9A2662DE30][O00000000]*deck.pptx"));
        Assert.Null(OfficeMruReader.Parse("[F00000000][T01DD3F9A2662DE30][O00000000]*"));
    }

    [Fact]
    public void A_missing_timestamp_keeps_the_entry_because_the_path_is_the_useful_part()
    {
        var entry = OfficeMruReader.Parse(@"[F00000000][O00000000]*C:\발표.pptx");

        Assert.NotNull(entry);
        Assert.Equal(@"C:\발표.pptx", entry!.FullPath);
        Assert.Equal(DateTimeOffset.MinValue, entry.OpenedUtc);
    }
}
