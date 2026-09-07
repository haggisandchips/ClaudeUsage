using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClaudeUsage.Services;

/// <summary>
/// Registers/unregisters the app to launch automatically at login. Windows uses the
/// per-user Run registry key; macOS shells out to `osascript` to add/remove a Login
/// Item via System Events (no signed Apple Developer identity is assumed, so the
/// modern SMAppService API - which needs one - isn't used).
/// </summary>
internal static class LoginItemService
{
    private const string AppName = "ClaudeUsage";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindows(enabled);
        }
        else if (OperatingSystem.IsMacOS())
        {
            SetMacOS(enabled);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                key.SetValue(AppName, $"\"{exePath}\"");
            }
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private static void SetMacOS(bool enabled)
    {
        var appBundlePath = GetMacAppBundlePath();
        if (appBundlePath is null)
        {
            return;
        }

        var script = enabled
            ? $"tell application \"System Events\" to make login item at end with properties " +
              $"{{path:\"{appBundlePath}\", hidden:false, name:\"{AppName}\"}}"
            : $"tell application \"System Events\" to delete login item \"{AppName}\"";

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
            // Best-effort only - e.g. user declines the one-time Automation permission prompt.
        }
    }

    /// <summary>
    /// Environment.ProcessPath for an app bundle points at the unix executable inside
    /// Contents/MacOS/, but a login item needs the .app bundle's own path.
    /// </summary>
    private static string? GetMacAppBundlePath()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return null;
        }

        var dir = new DirectoryInfo(Path.GetDirectoryName(exePath) ?? "");
        // .../<Name>.app/Contents/MacOS/<exe> -> walk up to <Name>.app
        var contents = dir.Parent;
        var appBundle = contents?.Parent;

        return appBundle is { Name: var name } && name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? appBundle.FullName
            : exePath;
    }
}
