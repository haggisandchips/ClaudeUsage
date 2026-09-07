using System.Runtime.Versioning;
using CommunityToolkit.WinUI.Notifications;

namespace ClaudeUsage.Services;

/// <summary>
/// Only compiled under the net10.0-windows10.0.19041.0 TFM (see ClaudeUsage.csproj) -
/// that's the only build where CommunityToolkit.WinUI.Notifications exposes actual toast
/// dispatch (WinRT projections) rather than just XML building.
/// </summary>
internal static partial class OsNotificationService
{
    [SupportedOSPlatform("windows")]
    static partial void ShowWindowsToast(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch
        {
            // Best-effort: toast activation needs an AppUserModelID, which comes from the
            // Start Menu shortcut Velopack creates on install. Running via `dotnet run`
            // in dev (no shortcut) can fail here - that's fine, not worth crashing over.
        }
    }
}
