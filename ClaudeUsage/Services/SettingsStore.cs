using System;
using System.IO;
using System.Text.Json;

namespace ClaudeUsage.Services;

internal sealed class AppSettings
{
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public int? PollIntervalSeconds { get; set; }
    public bool? LaunchAtLogin { get; set; }

    /// <summary>
    /// Name of the <see cref="ClaudeUsage.ViewMode"/> to restore on startup. Stored as a
    /// string (rather than the enum) so an unrecognized value from a future/older version
    /// of the app just falls back to the default instead of failing deserialization.
    /// </summary>
    public string? SelectedView { get; set; }
}

internal static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            var path = AppPaths.SettingsFile;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch
        {
            // Best-effort only.
        }
    }
}
