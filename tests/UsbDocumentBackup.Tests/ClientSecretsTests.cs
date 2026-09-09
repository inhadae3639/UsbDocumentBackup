using UsbDocumentBackup.GoogleDrive;
using Xunit;

namespace UsbDocumentBackup.Tests;

/// <summary>
/// Picking the wrong credential file in the Google Cloud console is easy, and the auth library's
/// own message ("At least one client secrets (Installed or Web) should be set") tells a user
/// nothing about what to download instead. These pin the explanations.
/// </summary>
public sealed class ClientSecretsTests
{
    private const string ServiceAccount =
        """
        {
          "type": "service_account",
          "project_id": "example-project",
          "private_key_id": "aaaa",
          "private_key": "-----BEGIN PRIVATE KEY-----\nREDACTED\n-----END PRIVATE KEY-----\n",
          "client_email": "robot@example-project.iam.gserviceaccount.com",
          "token_uri": "https://oauth2.googleapis.com/token"
        }
        """;

    private const string WebClient =
        """
        {"web":{"client_id":"123.apps.googleusercontent.com","client_secret":"x",
         "auth_uri":"https://accounts.google.com/o/oauth2/auth",
         "token_uri":"https://oauth2.googleapis.com/token"}}
        """;

    private const string DesktopClient =
        """
        {"installed":{"client_id":"123.apps.googleusercontent.com","project_id":"example-project",
         "auth_uri":"https://accounts.google.com/o/oauth2/auth",
         "token_uri":"https://oauth2.googleapis.com/token","client_secret":"x",
         "redirect_uris":["http://localhost"]}}
        """;

    [Fact]
    public void A_service_account_key_is_rejected_with_a_reason_and_a_next_step()
    {
        var message = GoogleConnection.ClassifyCredentialFile(ServiceAccount);

        Assert.NotNull(message);
        Assert.Contains("서비스 계정", message!, StringComparison.Ordinal);
        Assert.Contains("데스크톱 앱", message, StringComparison.Ordinal);
        // The user must be told to dispose of a key file they already downloaded.
        Assert.Contains("폐기", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_web_application_client_is_rejected_because_the_desktop_flow_needs_loopback()
    {
        var message = GoogleConnection.ClassifyCredentialFile(WebClient);

        Assert.NotNull(message);
        Assert.Contains("웹 애플리케이션", message!, StringComparison.Ordinal);
        Assert.Contains("데스크톱 앱", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_desktop_client_is_accepted() =>
        Assert.Null(GoogleConnection.ClassifyCredentialFile(DesktopClient));

    [Fact]
    public void Importing_a_service_account_key_throws_rather_than_storing_it()
    {
        using var workspace = new TestWorkspace();
        var connection = new GoogleConnection(workspace.Paths, workspace.Log);

        var file = Path.Combine(workspace.Root, "service-account.json");
        File.WriteAllText(file, ServiceAccount);

        var error = Assert.Throws<ClientSecretsRejectedException>(() => connection.ImportClientSecrets(file));
        Assert.Contains("서비스 계정", error.Message, StringComparison.Ordinal);

        // The rejected key is not stored. (IsConfigured may still be true from a credential
        // compiled into the build, which is a separate mechanism with its own tests.)
        Assert.False(File.Exists(connection.ClientSecretsFile));
    }

    [Fact]
    public void Importing_a_desktop_client_stores_it_and_enables_connecting()
    {
        using var workspace = new TestWorkspace();
        var connection = new GoogleConnection(workspace.Paths, workspace.Log);

        var file = Path.Combine(workspace.Root, "client_secret.json");
        File.WriteAllText(file, DesktopClient);

        connection.ImportClientSecrets(file);

        Assert.True(connection.IsConfigured);
        Assert.Equal(ConnectionState.NotConnected, connection.State);
    }
}
