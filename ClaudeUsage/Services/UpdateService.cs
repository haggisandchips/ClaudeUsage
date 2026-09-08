using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace ClaudeUsage.Services;

/// <summary>
/// Checks GitHub Releases for a newer version and, if one exists, downloads it
/// immediately in the background - the "download immediately, notify for restart"
/// update flow. Applying/restarting is a separate, explicit user action (see
/// <see cref="ApplyAndRestart"/>); this class never restarts the app on its own.
///
/// Never throws: any failure (offline, GitHub rate-limited, not an installed build,
/// etc.) is swallowed and just means no update this launch - the check runs again
/// next launch, so a transient failure isn't user-visible or fatal.
/// </summary>
internal static class UpdateService
{
    private const string RepoUrl = "https://github.com/haggisandchips/ClaudeUsage";

    /// <summary>
    /// Checks for and downloads a pending update. Returns the new version string
    /// (e.g. "1.3.0") once it's downloaded and ready to apply, or null if there's
    /// nothing to install.
    /// </summary>
    public static async Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, string.Empty, false));

            // Skip entirely for local/dev runs (a plain `dotnet run`/`dotnet build`
            // output isn't a Velopack install, so there's nothing to check against).
            if (!manager.IsInstalled)
            {
                return null;
            }

            var updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                return null;
            }

            await manager.DownloadUpdatesAsync(updateInfo, cancelToken: cancellationToken);
            return updateInfo.TargetFullRelease.Version.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Exits the app immediately, applies the update downloaded by CheckAndDownloadAsync,
    /// and relaunches it - only ever called from an explicit user action (the "Restart to
    /// update" menu item), never automatically.
    /// </summary>
    public static void ApplyAndRestart()
    {
        var manager = new UpdateManager(new GithubSource(RepoUrl, string.Empty, false));
        manager.ApplyUpdatesAndRestart(null);
    }
}
