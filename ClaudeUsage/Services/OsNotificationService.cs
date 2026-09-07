using System;
using System.Diagnostics;

namespace ClaudeUsage.Services;

/// <summary>
/// Shows a real OS-level notification (Windows Action Center toast / macOS Notification
/// Center banner) rather than an in-app overlay, so it's readable and - critically -
/// still appears while the panel is hidden, which an Avalonia WindowNotificationManager
/// cannot do (it renders inside the window's own visual tree).
///
/// The Windows toast implementation lives in OsNotificationService.Windows.cs, compiled
/// only under the net10.0-windows10.0.19041.0 TFM (see ClaudeUsage.csproj) - that's the
/// only build with WinRT toast projections. ShowWindowsToast is a no-op on every other
/// TFM/platform since no partial implementation exists for it there.
/// </summary>
internal static partial class OsNotificationService
{
    public static void Show(string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            ShowWindowsToast(title, message);
        }
        else if (OperatingSystem.IsMacOS())
        {
            ShowMacOS(title, message);
        }
    }

    static partial void ShowWindowsToast(string title, string message);

    private static void ShowMacOS(string title, string message)
    {
        // Note: notifications sent this way are attributed to "Script Editor" /
        // "System Events" in Notification Center, not to the app itself - there's no
        // signed Apple Developer identity here to register a proper NSUserNotification
        // sender, same tradeoff as the login-item registration in LoginItemService.
        var script = $"display notification \"{EscapeForAppleScript(message)}\" " +
                     $"with title \"{EscapeForAppleScript(title)}\"";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("osascript", new[] { "-e", script })
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static string EscapeForAppleScript(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
