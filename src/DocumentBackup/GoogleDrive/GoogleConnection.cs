using System.Runtime.Versioning;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using DocumentBackup.Storage;
using DocumentBackup.Windows;

namespace DocumentBackup.GoogleDrive;

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

    /// <summary>Resource name of the OAuth client compiled into the binary, when there is one.</summary>
    private const string EmbeddedSecretsResource = "DocumentBackup.client_secret.json";

    /// <summary>The OAuth desktop client the user imported from Google Cloud.</summary>
    public string ClientSecretsFile => Path.Combine(_paths.CredentialsDirectory, "client_secret.json");

    /// <summary>
    /// Whether the app can start an authorisation at all. True when a client configuration was
    /// imported, or when one was compiled in -- which is what lets a single executable work on a
    /// machine that has never seen the JSON file.
    /// </summary>
    public bool IsConfigured => File.Exists(ClientSecretsFile) || HasEmbeddedClientSecrets;

    internal static bool HasEmbeddedClientSecrets =>
        typeof(GoogleConnection).Assembly.GetManifestResourceInfo(EmbeddedSecretsResource) is not null;

    /// <summary>
    /// Opens the OAuth client configuration, preferring one the user imported so a build-time
    /// default can always be overridden without rebuilding.
    /// </summary>
    private Stream OpenClientSecrets()
    {
        if (File.Exists(ClientSecretsFile))
        {
            return File.OpenRead(ClientSecretsFile);
        }

        return typeof(GoogleConnection).Assembly.GetManifestResourceStream(EmbeddedSecretsResource)
            ?? throw new InvalidOperationException("No OAuth client configuration is available.");
    }

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
    /// Picks up an OAuth client configuration the user dropped next to the executable, so setting
    /// this up on another PC is "copy two files and click Connect" instead of hunting through a
    /// file picker.
    ///
    /// The file is deliberately read from disk rather than compiled in: this repository is public,
    /// and a client secret committed to it would be readable by anyone.
    /// </summary>
    /// <returns>The file that was imported, or null when there was nothing to import.</returns>
    public string? TryAutoImportClientSecrets()
    {
        // Only the imported file short-circuits this. A build-time default is still overridable by
        // dropping a different JSON next to the executable.
        if (File.Exists(ClientSecretsFile))
        {
            return null;
        }

        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(exeDirectory))
        {
            return null;
        }

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(exeDirectory, "client_secret*.json").OrderBy(f => f, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                ImportClientSecrets(candidate);
                _log.Info($"Imported the OAuth client configuration from {Path.GetFileName(candidate)}.");
                return candidate;
            }
            catch (ClientSecretsRejectedException ex)
            {
                // A service-account key sitting in the folder should be reported, not retried.
                _log.Warn($"Ignored {Path.GetFileName(candidate)}: {ex.Message.Split(Environment.NewLine)[0]}");
            }
        }

        return null;
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
    /// <exception cref="ClientSecretsRejectedException">
    /// Google refused the authorisation for a reason the user can act on.
    /// </exception>
    public async Task<string> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Import the OAuth client configuration file first.");
        }

        UserCredential credential;
        try
        {
            await using var stream = OpenClientSecrets();
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                Scopes,
                UserKey,
                cancellationToken,
                _store).ConfigureAwait(false);
        }
        catch (TokenResponseException ex)
        {
            throw new ClientSecretsRejectedException(ExplainAuthorizationFailure(ex.Error?.Error, ex.Message));
        }

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
    /// Turns Google's OAuth error code into something the user can act on. These are almost always
    /// Cloud console configuration, not a bug here, and the raw code says nothing about the fix.
    /// </summary>
    internal static string ExplainAuthorizationFailure(string? errorCode, string fallback)
    {
        var nl = Environment.NewLine;
        return errorCode switch
        {
            "access_denied" =>
                "Google이 이 계정의 접근을 거부했습니다 (access_denied)." + nl + nl
                + "가장 흔한 원인은 OAuth 동의 화면이 '테스트 중'인데 이 계정이 테스트 사용자로 "
                + "등록되지 않은 것입니다." + nl + nl
                + "Google Cloud Console → Google 인증 플랫폼 → 대상 → 맨 아래 '테스트 사용자'에서 "
                + "로그인하려는 계정을 추가한 뒤 다시 연결하세요." + nl + nl
                + "참고: 테스트 상태에서는 갱신 토큰이 7일 뒤 만료됩니다. 계속 쓰시려면 같은 페이지에서 "
                + "'앱 게시'로 프로덕션 전환을 권장합니다." + nl + nl
                + "동의 화면에서 직접 '취소'를 누른 경우에도 이 오류가 납니다.",

            "admin_policy_enforced" =>
                "조직 관리자가 이 앱의 접근을 막고 있습니다 (admin_policy_enforced)." + nl + nl
                + "학교나 회사 계정이면 관리자가 서드파티 앱을 제한해 둔 것입니다. "
                + "개인 Google 계정으로 연결하거나 관리자에게 허용을 요청하세요.",

            "invalid_client" =>
                "클라이언트 정보가 Google에 등록된 것과 맞지 않습니다 (invalid_client)." + nl + nl
                + "클라이언트를 삭제하고 다시 만들었다면 새 JSON을 다시 가져오세요.",

            "invalid_grant" =>
                "인증이 더 이상 유효하지 않습니다 (invalid_grant)." + nl + nl
                + "권한을 철회했거나, 테스트 상태의 7일 만료에 걸렸을 수 있습니다. 다시 연결하세요.",

            _ => "Google 인증에 실패했습니다." + nl + nl + fallback,
        };
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

            await using var stream = OpenClientSecrets();
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
