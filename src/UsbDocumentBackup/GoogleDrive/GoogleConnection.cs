using System.Runtime.Versioning;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using UsbDocumentBackup.Storage;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.GoogleDrive;

/// <summary>The chosen file cannot be used, with an explanation aimed at the person choosing it.</summary>
public sealed class ClientSecretsRejectedException : Exception
{
    public ClientSecretsRejectedException(string message) : base(message)
    {
    }
}

public enum ConnectionState
{
    /// <summary>No OAuth client configuration has been imported yet.</summary>
    NotConfigured,

    /// <summary>Client configuration is present but nobody has authorised the app.</summary>
    NotConnected,

    /// <summary>A refresh token is stored and usable.</summary>
    Connected,

    /// <summary>Authorisation was revoked or expired. Uploads pause; local backups continue.</summary>
    ReconnectRequired,
}

/// <summary>
/// Owns the Google account connection.
///
/// The browser is only ever launched from an explicit Connect click. An expired token switches
/// uploads to "reconnect required" and waits, because popping a browser window open on its own
/// during a presentation is exactly what this app must not do.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GoogleConnection
{
    /// <summary>Only files this app creates. Never full Drive access.</summary>
    private static readonly string[] Scopes = ["https://www.googleapis.com/auth/drive.file"];

    private const string UserKey = "user";

    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly DpapiDataStore _store;
    private readonly object _gate = new();

    private UserCredential? _credential;

    public GoogleConnection(AppPaths paths, Log log)
    {
        _paths = paths;
        _log = log;
        _store = new DpapiDataStore(paths.CredentialsDirectory);
    }

    /// <summary>The OAuth desktop client the user imported from Google Cloud.</summary>
    public string ClientSecretsFile => Path.Combine(_paths.CredentialsDirectory, "client_secret.json");

    public bool IsConfigured => File.Exists(ClientSecretsFile);

    public ConnectionState State { get; private set; } = ConnectionState.NotConnected;

    /// <summary>
    /// Copies the downloaded OAuth client configuration in.
    ///
    /// The wrong file is easy to download from the Cloud console, so the two common mistakes are
    /// named explicitly rather than surfaced as the library's generic message.
    /// </summary>
    /// <exception cref="ClientSecretsRejectedException">The file is not a desktop OAuth client.</exception>
    public void ImportClientSecrets(string sourceFile)
    {
        string json;
        try
        {
            json = File.ReadAllText(sourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ClientSecretsRejectedException("파일을 읽지 못했습니다: " + ex.Message);
        }

        var kind = ClassifyCredentialFile(json);
        if (kind is not null)
        {
            throw new ClientSecretsRejectedException(kind);
        }

        try
        {
            using var stream = File.OpenRead(sourceFile);
            if (GoogleClientSecrets.FromStream(stream).Secrets is null)
            {
                throw new ClientSecretsRejectedException("이 JSON에는 OAuth 클라이언트 정보가 없습니다.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Newtonsoft.Json.JsonException or IOException)
        {
            throw new ClientSecretsRejectedException(
                "OAuth 클라이언트 설정으로 읽을 수 없는 파일입니다. (" + ex.Message + ")");
        }

        Directory.CreateDirectory(_paths.CredentialsDirectory);
        File.Copy(sourceFile, ClientSecretsFile, overwrite: true);
        State = ConnectionState.NotConnected;
    }

    /// <summary>
    /// Returns an explanation when the file is a recognisable but unusable credential, or null when
    /// it looks like the right thing.
    /// </summary>
    internal static string? ClassifyCredentialFile(string json)
    {
        if (json.Contains("\"type\"", StringComparison.Ordinal)
            && json.Contains("service_account", StringComparison.Ordinal))
        {
            return "이 파일은 '서비스 계정 키'입니다. 이 앱에는 쓸 수 없습니다.\n\n"
                + "서비스 계정은 사람이 아니라 로봇 계정이라, 업로드한 파일이 회원님의 내 드라이브에 나타나지 않고, "
                + "자체 저장 용량도 없어 업로드가 용량 초과로 실패합니다.\n\n"
                + "필요한 것은 'OAuth 2.0 클라이언트 ID', 애플리케이션 유형 '데스크톱 앱' 입니다.\n"
                + "받은 JSON 안에 \"installed\" 항목이 있으면 맞는 파일입니다.\n\n"
                + "※ 방금 고른 서비스 계정 키 파일은 개인 키가 그대로 들어 있으니 폐기하세요.";
        }

        if (json.Contains("\"web\"", StringComparison.Ordinal)
            && !json.Contains("\"installed\"", StringComparison.Ordinal))
        {
            return "이 파일은 '웹 애플리케이션' 유형 OAuth 클라이언트입니다.\n\n"
                + "데스크톱 앱은 loopback 리디렉션을 쓰므로 웹 유형으로는 인증할 수 없습니다.\n"
                + "클라이언트를 다시 만들 때 애플리케이션 유형을 '데스크톱 앱'으로 선택하세요.";
        }

        return null;
    }

    /// <summary>
    /// Interactive authorisation. Called only from the Connect button: it opens the system browser
    /// on a loopback redirect, which is the flow Google documents for desktop apps.
    /// </summary>
    public async Task<string> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Import the OAuth client configuration file first.");
        }

        await using var stream = File.OpenRead(ClientSecretsFile);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            UserKey,
            cancellationToken,
            _store).ConfigureAwait(false);

        lock (_gate)
        {
            _credential = credential;
            State = ConnectionState.Connected;
        }

        _log.Info("Connected to Google Drive.");
        return await new DriveClient(SharedHttpClient, GetAccessTokenAsync)
            .GetAccountKeyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Restores a stored connection without any user interaction. Returns false when nobody has
    /// authorised yet, or when the stored token is no longer good.
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            State = ConnectionState.NotConfigured;
            return false;
        }

        try
        {
            var token = await _store.GetAsync<TokenResponse>(UserKey).ConfigureAwait(false);
            if (token?.RefreshToken is null)
            {
                State = ConnectionState.NotConnected;
                return false;
            }

            await using var stream = File.OpenRead(ClientSecretsFile);
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = GoogleClientSecrets.FromStream(stream).Secrets,
                Scopes = Scopes,
                DataStore = _store,
            });

            var credential = new UserCredential(flow, UserKey, token);

            // Force a refresh now so a revoked grant is discovered here rather than mid-upload.
            if (!await credential.RefreshTokenAsync(cancellationToken).ConfigureAwait(false))
            {
                State = ConnectionState.ReconnectRequired;
                return false;
            }

            lock (_gate)
            {
                _credential = credential;
                State = ConnectionState.Connected;
            }

            return true;
        }
        catch (TokenResponseException ex)
        {
            // invalid_grant is what a revoked or expired refresh token looks like. Notably, an
            // OAuth project still in Testing expires refresh tokens after seven days.
            _log.Warn($"Stored Google authorisation is no longer valid: {ex.Error?.Error ?? "unknown"}.");
            State = ConnectionState.ReconnectRequired;
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.Warn($"Could not restore the Google connection: {ex.Message}");
            State = ConnectionState.NotConnected;
            return false;
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        UserCredential? credential;
        lock (_gate)
        {
            credential = _credential;
        }

        if (credential is null)
        {
            throw new InvalidOperationException("Not connected to Google Drive.");
        }

        return await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void MarkReconnectRequired()
    {
        lock (_gate)
        {
            _credential = null;
            State = ConnectionState.ReconnectRequired;
        }
    }

    /// <summary>Forgets the stored token. The archive and its database rows are untouched.</summary>
    public async Task DisconnectAsync()
    {
        lock (_gate)
        {
            _credential = null;
            State = ConnectionState.NotConnected;
        }

        await _store.ClearAsync().ConfigureAwait(false);
    }

    public IDriveClient CreateClient() => new DriveClient(SharedHttpClient, GetAccessTokenAsync);

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };
}
