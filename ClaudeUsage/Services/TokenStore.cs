using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

/// <summary>
/// Persists the OAuth token set to disk. On Windows the file is encrypted at rest with
/// DPAPI (CurrentUser scope). On macOS/Linux there is no equivalent in-box API without
/// extra native interop (e.g. Keychain), so the file is written user-readable-only.
/// </summary>
internal static class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeUsage.v1");

    public static void Save(TokenSet tokens)
    {
        var json = JsonSerializer.Serialize(tokens);
        var bytes = Encoding.UTF8.GetBytes(json);

        if (OperatingSystem.IsWindows())
        {
            bytes = ProtectWindows(bytes);
        }

        var path = AppPaths.CredentialsFile;
        File.WriteAllBytes(path, bytes);

        if (!OperatingSystem.IsWindows())
        {
            TryRestrictToOwner(path);
        }
    }

    public static TokenSet? Load()
    {
        var path = AppPaths.CredentialsFile;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);

            if (OperatingSystem.IsWindows())
            {
                bytes = UnprotectWindows(bytes);
            }

            var json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<TokenSet>(json);
        }
        catch
        {
            // Corrupt or unreadable (e.g. encrypted by a different user profile) -
            // treat as signed out rather than crashing the app.
            return null;
        }
    }

    public static void Clear()
    {
        var path = AppPaths.CredentialsFile;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plainBytes) =>
        ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] cipherBytes) =>
        ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);

    [UnsupportedOSPlatform("windows")]
    private static void TryRestrictToOwner(string path)
    {
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort only (e.g. unsupported filesystem).
        }
    }
}
