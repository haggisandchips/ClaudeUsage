using System;
using System.IO;

namespace ClaudeUsage.Services;

internal static class AppPaths
{
    /// <summary>
    /// Test-only override for <see cref="DataDirectory"/>, so tests never read or write the
    /// real user's credentials/settings under %LOCALAPPDATA%. Null in production.
    /// </summary>
    internal static string? DataDirectoryOverride { get; set; }

    public static string DataDirectory
    {
        get
        {
            var dir = DataDirectoryOverride ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolderOption.Create),
                "ClaudeUsage");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string CredentialsFile => Path.Combine(DataDirectory, "credentials.dat");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}
