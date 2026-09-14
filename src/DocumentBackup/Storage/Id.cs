using System.Security.Cryptography;

namespace DocumentBackup.Storage;

/// <summary>
/// Short random identifiers. Kept to 16 hex characters so archive paths stay well clear of
/// path-length limits while still being collision-safe for this workload.
/// </summary>
public static class Id
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
