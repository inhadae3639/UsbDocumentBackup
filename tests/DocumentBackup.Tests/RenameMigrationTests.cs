using System.Text;
using DocumentBackup.Storage;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// The application was renamed, which moves the folder holding the archive, the state database and
/// the encrypted Google token. Pointing at a new name without moving anything would present an
/// upgraded installation with no backups, no history and no account, so the move is covered here.
/// </summary>
public sealed class RenameMigrationTests
{
    private static string Root(TestWorkspace workspace, string name) => Path.Combine(workspace.Root, name);

    private static void CreateOldInstallation(string formerRoot)
    {
        Directory.CreateDirectory(Path.Combine(formerRoot, "archive", "retained", "dev1", "b1"));
        Directory.CreateDirectory(Path.Combine(formerRoot, "credentials"));
        File.WriteAllText(Path.Combine(formerRoot, "settings.json"), "{}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(formerRoot, "archive", "retained", "dev1", "b1", "발표.pptx"), "내용");
        File.WriteAllBytes(Path.Combine(formerRoot, "credentials", "75736572.bin"), [1, 2, 3]);
    }

    [Fact]
    public void An_installation_under_the_old_name_is_moved_across_with_everything_in_it()
    {
        using var workspace = new TestWorkspace();
        var former = Root(workspace, "UsbDocumentBackup");
        var current = Root(workspace, "DocumentBackup");
        CreateOldInstallation(former);

        Assert.True(AppPaths.AdoptFormerFolder(former, current));

        Assert.False(Directory.Exists(former));
        Assert.Equal("내용", File.ReadAllText(Path.Combine(current, "archive", "retained", "dev1", "b1", "발표.pptx")));

        // The Google token has to come along, or the user is signed out by an upgrade.
        Assert.Equal<byte[]>([1, 2, 3], File.ReadAllBytes(Path.Combine(current, "credentials", "75736572.bin")));
        Assert.True(File.Exists(Path.Combine(current, "settings.json")));
    }

    [Fact]
    public void A_fresh_installation_has_nothing_to_move()
    {
        using var workspace = new TestWorkspace();
        Assert.False(AppPaths.AdoptFormerFolder(Root(workspace, "UsbDocumentBackup"), Root(workspace, "DocumentBackup")));
    }

    [Fact]
    public void An_already_migrated_installation_is_left_exactly_as_it_is()
    {
        using var workspace = new TestWorkspace();
        var former = Root(workspace, "UsbDocumentBackup");
        var current = Root(workspace, "DocumentBackup");

        CreateOldInstallation(former);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "settings.json"), "{\"current\":true}", new UTF8Encoding(false));

        Assert.False(AppPaths.AdoptFormerFolder(former, current));

        // The newer folder wins; nothing is overwritten and the old one is not touched.
        Assert.Contains("current", File.ReadAllText(Path.Combine(current, "settings.json")), StringComparison.Ordinal);
        Assert.True(Directory.Exists(former));
    }

    [Fact]
    public void Migrating_twice_is_harmless()
    {
        using var workspace = new TestWorkspace();
        var former = Root(workspace, "UsbDocumentBackup");
        var current = Root(workspace, "DocumentBackup");
        CreateOldInstallation(former);

        Assert.True(AppPaths.AdoptFormerFolder(former, current));
        Assert.False(AppPaths.AdoptFormerFolder(former, current));
        Assert.True(File.Exists(Path.Combine(current, "settings.json")));
    }

    [Fact]
    public void Archive_paths_stay_valid_because_they_are_relative_to_the_root()
    {
        using var workspace = new TestWorkspace();
        var former = Root(workspace, "UsbDocumentBackup");
        var current = Root(workspace, "DocumentBackup");
        CreateOldInstallation(former);

        AppPaths.AdoptFormerFolder(former, current);

        // A backup row stores this relative path; resolving it against the new root must find the
        // file that was moved, with no rewriting of the database.
        var paths = new AppPaths(current, Path.Combine(current, "archive"));
        var relative = Path.Combine("retained", "dev1", "b1", "발표.pptx");
        Assert.True(File.Exists(paths.ResolveArchivePath(relative)));
    }
}
