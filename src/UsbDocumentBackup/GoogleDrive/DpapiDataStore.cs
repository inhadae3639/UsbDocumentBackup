using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace UsbDocumentBackup.GoogleDrive;

/// <summary>
/// Token storage encrypted with DPAPI for the current Windows user.
///
/// The default file store writes the refresh token as plain JSON. Here the bytes are protected with
/// <see cref="DataProtectionScope.CurrentUser"/>, so copying the file to another account or another
/// machine yields nothing. Tokens never reach settings.json, the log or the database.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiDataStore : IDataStore
{
    /// <summary>Ties the ciphertext to this app, so another program's blob cannot be swapped in.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UsbDocumentBackup.GoogleTokens.v1");

    private readonly string _directory;

    public DpapiDataStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string FileFor(string key) =>
        Path.Combine(_directory, Convert.ToHexString(Encoding.UTF8.GetBytes(key)).ToLowerInvariant() + ".bin");

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            Entropy,
            DataProtectionScope.CurrentUser);

        var path = FileFor(key);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = FileFor(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<T>(default!);
        }

        try
        {
            var json = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
            return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            // Written by a different user, or corrupt. Treat as "not connected" rather than
            // crashing; the user can reconnect, and local backups are unaffected either way.
            return Task.FromResult<T>(default!);
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = FileFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.bin"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }
}
