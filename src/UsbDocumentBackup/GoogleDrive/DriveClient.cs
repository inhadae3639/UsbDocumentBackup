using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsbDocumentBackup.GoogleDrive;

/// <summary>
/// Drive v3 over plain HTTP.
///
/// The client library is not used for these calls on purpose: a resumable upload has to survive the
/// process exiting, which means owning the session URI and the byte offset ourselves. Everything
/// here works with the <c>drive.file</c> scope, so the app can only ever see files it created.
/// </summary>
public sealed class DriveClient : IDriveClient
{
    private const string ApiRoot = "https://www.googleapis.com/drive/v3";
    private const string UploadRoot = "https://www.googleapis.com/upload/drive/v3";
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _accessToken;

    public DriveClient(HttpClient http, Func<CancellationToken, Task<string>> accessTokenProvider)
    {
        _http = http;
        _accessToken = accessTokenProvider;
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await _accessToken(cancellationToken).ConfigureAwait(false));
        return request;
    }

    public async Task<string> GetAccountKeyAsync(CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Get, $"{ApiRoot}/about?fields=user(emailAddress)", cancellationToken)
            .ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return document.RootElement.TryGetProperty("user", out var user)
            && user.TryGetProperty("emailAddress", out var email)
            && email.GetString() is { Length: > 0 } address
                ? address
                : "unknown";
    }

    public async Task<string> EnsureFolderAsync(string name, string? parentId, CancellationToken cancellationToken)
    {
        // drive.file only ever lists what this app created, so this finds our own folder or nothing.
        var escaped = name.Replace("'", @"\'", StringComparison.Ordinal);
        var query = $"mimeType='{FolderMimeType}' and name='{escaped}' and trashed=false"
            + (parentId is null ? string.Empty : $" and '{parentId}' in parents");

        var url = $"{ApiRoot}/files?q={Uri.EscapeDataString(query)}&fields=files(id)&pageSize=1";
        using (var request = await RequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false))
        using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            if (document.RootElement.TryGetProperty("files", out var files)
                && files.GetArrayLength() > 0
                && files[0].GetProperty("id").GetString() is { Length: > 0 } existing)
            {
                return existing;
            }
        }

        var metadata = new FileMetadata
        {
            Name = name,
            MimeType = FolderMimeType,
            Parents = parentId is null ? null : [parentId],
        };

        using var create = await RequestAsync(HttpMethod.Post, $"{ApiRoot}/files?fields=id", cancellationToken)
            .ConfigureAwait(false);
        create.Content = JsonContent.Create(metadata, options: Json);

        using var created = await _http.SendAsync(create, cancellationToken).ConfigureAwait(false);
        created.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return body.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<string> GenerateFileIdAsync(CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Get, $"{ApiRoot}/files/generateIds?count=1&space=drive", cancellationToken)
            .ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.GetProperty("ids")[0].GetString()!;
    }

    public async Task<DriveFileInfo?> TryGetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Get, $"{ApiRoot}/files/{fileId}?fields=id,name,size", cancellationToken)
            .ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // With drive.file, a 404 means "this app did not create a file with that id" -- which is
        // exactly the question we are asking: did our upload actually land?
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var root = document.RootElement;
        var size = root.TryGetProperty("size", out var sizeElement)
            && long.TryParse(sizeElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1;

        return new DriveFileInfo(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            size);
    }

    public async Task<string> StartResumableUploadAsync(
        string fileId,
        string name,
        string parentId,
        long totalBytes,
        IReadOnlyDictionary<string, string> appProperties,
        CancellationToken cancellationToken)
    {
        var metadata = new FileMetadata
        {
            Id = fileId,
            Name = name,
            Parents = [parentId],
            AppProperties = appProperties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        };

        using var request = await RequestAsync(HttpMethod.Post, $"{UploadRoot}/files?uploadType=resumable", cancellationToken)
            .ConfigureAwait(false);
        request.Content = JsonContent.Create(metadata, options: Json);
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", totalBytes.ToString(CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return response.Headers.Location?.ToString()
            ?? throw new HttpRequestException("Drive did not return an upload session location.");
    }

    public async Task<long?> GetResumeOffsetAsync(string sessionUri, long totalBytes, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Put, sessionUri, cancellationToken).ConfigureAwait(false);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(totalBytes) { Unit = "bytes" };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            // Drive already has the whole thing.
            return totalBytes;
        }

        // Google documents resumable sessions as expiring. A gone session is not an error, it just
        // means starting a new one rather than assuming the URI is permanent.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        if ((int)response.StatusCode != 308)
        {
            return null;
        }

        if (response.Headers.TryGetValues("Range", out var ranges))
        {
            var range = ranges.FirstOrDefault();
            var dash = range?.LastIndexOf('-') ?? -1;
            if (range is not null
                && dash >= 0
                && long.TryParse(range[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastByte))
            {
                return lastByte + 1;
            }
        }

        // A 308 with no Range header means nothing has been stored yet.
        return 0;
    }

    public async Task<DriveResult> UploadChunkAsync(
        string sessionUri,
        Stream content,
        long offset,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = await RequestAsync(HttpMethod.Put, sessionUri, cancellationToken).ConfigureAwait(false);
            request.Content = new StreamContent(content);

            var length = totalBytes - offset;
            if (length > 0)
            {
                request.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(offset, totalBytes - 1, totalBytes) { Unit = "bytes" };
            }
            else
            {
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(totalBytes) { Unit = "bytes" };
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return DriveResult.Completed;
            }

            if ((int)response.StatusCode == 308)
            {
                return new DriveResult(DriveOutcome.Incomplete);
            }

            return await ClassifyAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new DriveResult(DriveOutcome.Cancelled);
        }
        catch (HttpRequestException ex)
        {
            return new DriveResult(DriveOutcome.Transient, ex.Message);
        }
        catch (IOException ex)
        {
            return new DriveResult(DriveOutcome.Transient, ex.Message);
        }
    }

    public async Task<DriveResult> DownloadAsync(string fileId, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await RequestAsync(HttpMethod.Get, $"{ApiRoot}/files/{fileId}?alt=media", cancellationToken)
                .ConfigureAwait(false);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await ClassifyAsync(response, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(destinationPath))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            return DriveResult.Completed;
        }
        catch (OperationCanceledException)
        {
            return new DriveResult(DriveOutcome.Cancelled);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new DriveResult(DriveOutcome.Transient, ex.Message);
        }
    }

    /// <summary>
    /// Splits failures into "wait and try again" and "a person has to do something". Running out of
    /// Drive space or losing authorisation must never turn into an endless retry loop.
    /// </summary>
    private static async Task<DriveResult> ClassifyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        var status = (int)response.StatusCode;

        if (status == 401)
        {
            return new DriveResult(DriveOutcome.NeedsAttention, "Google rejected the credentials; reconnect the account.");
        }

        if (status == 403)
        {
            // 403 covers both "slow down" and "you are out of space"; only the reason separates them.
            var transientReasons = new[] { "rateLimitExceeded", "userRateLimitExceeded", "sharingRateLimitExceeded" };
            if (transientReasons.Any(r => body.Contains(r, StringComparison.OrdinalIgnoreCase)))
            {
                return new DriveResult(DriveOutcome.Transient, "Rate limited.", retryAfter);
            }

            if (body.Contains("storageQuotaExceeded", StringComparison.OrdinalIgnoreCase))
            {
                return new DriveResult(DriveOutcome.NeedsAttention, "The Google account is out of Drive storage.");
            }

            return new DriveResult(DriveOutcome.NeedsAttention, "Drive refused the request: " + Trim(body));
        }

        if (status == 429 || status >= 500)
        {
            return new DriveResult(DriveOutcome.Transient, $"Drive returned {status}.", retryAfter);
        }

        return new DriveResult(DriveOutcome.NeedsAttention, $"Drive returned {status}: " + Trim(body));
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Trim(string body) => body.Length <= 200 ? body : body[..200];

    private sealed class FileMetadata
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? MimeType { get; init; }

        public string[]? Parents { get; init; }

        public Dictionary<string, string>? AppProperties { get; init; }
    }
}
