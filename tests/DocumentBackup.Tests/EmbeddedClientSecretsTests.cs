using DocumentBackup.GoogleDrive;
using Xunit;

namespace DocumentBackup.Tests;

/// <summary>
/// The OAuth client is compiled into the binary so one executable works on a PC that has never
/// seen the JSON file. These pin the two properties that matter: the build does not break when the
/// credential is absent, and an imported file always wins over the compiled-in default.
/// </summary>
public sealed class EmbeddedClientSecretsTests
{
    private const string DesktopClient =
        """
        {"installed":{"client_id":"999.apps.googleusercontent.com","project_id":"p",
         "auth_uri":"https://accounts.google.com/o/oauth2/auth",
         "token_uri":"https://oauth2.googleapis.com/token","client_secret":"x",
         "redirect_uris":["http://localhost"]}}
        """;

    [Fact]
    public void An_embedded_credential_makes_the_app_configured_with_no_file_present()
    {
        using var workspace = new TestWorkspace();
        var connection = new GoogleConnection(workspace.Paths, workspace.Log);

        Assert.False(File.Exists(connection.ClientSecretsFile));

        // Whether one was compiled in depends on the build; the two states are both valid, and
        // IsConfigured has to agree with whichever it is rather than assuming a file.
        Assert.Equal(GoogleConnection.HasEmbeddedClientSecrets, connection.IsConfigured);
    }

    [Fact]
    public void An_imported_file_takes_precedence_over_the_compiled_in_default()
    {
        using var workspace = new TestWorkspace();
        var connection = new GoogleConnection(workspace.Paths, workspace.Log);

        var file = Path.Combine(workspace.Root, "client_secret.json");
        File.WriteAllText(file, DesktopClient);
        connection.ImportClientSecrets(file);

        Assert.True(connection.IsConfigured);
        Assert.True(File.Exists(connection.ClientSecretsFile));
        Assert.Equal(ConnectionState.NotConnected, connection.State);

        // Overriding must not require rebuilding, so the imported bytes are what is stored.
        Assert.Contains("999.apps.googleusercontent.com", File.ReadAllText(connection.ClientSecretsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_import_leaves_an_already_imported_file_alone()
    {
        using var workspace = new TestWorkspace();
        var connection = new GoogleConnection(workspace.Paths, workspace.Log);

        var file = Path.Combine(workspace.Root, "client_secret.json");
        File.WriteAllText(file, DesktopClient);
        connection.ImportClientSecrets(file);

        Assert.Null(connection.TryAutoImportClientSecrets());
    }
}
