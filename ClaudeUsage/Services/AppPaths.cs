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
                IsRunningInstalled() ? "ClaudeUsage" : "ClaudeUsage-Dev");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Velopack installs land at &lt;LocalAppData&gt;\ClaudeUsage\current\ClaudeUsage.exe;
    /// a local `dotnet run`/`dotnet build` output never sits in a "current" folder under
    /// that path. Used to keep local dev/test runs out of the real app's credentials and
    /// settings entirely, rather than sharing (and risking clobbering) production data.
    /// </summary>
    private static bool IsRunningInstalled()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        return exeDir is not null &&
               string.Equals(Path.GetFileName(exeDir), "current", StringComparison.OrdinalIgnoreCase);
    }

    public static string CredentialsFile => Path.Combine(DataDirectory, "credentials.dat");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}
