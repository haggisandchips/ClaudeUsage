using ClaudeUsage.Services;
using ClaudeUsage.Tests.TestSupport;

namespace ClaudeUsage.Tests;

[Collection("AppData")]
public class SettingsStoreTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenNoFileExists()
    {
        using var isolated = new IsolatedAppData();

        var settings = SettingsStore.Load();

        Assert.Null(settings.WindowX);
        Assert.Null(settings.WindowY);
        Assert.Null(settings.PollIntervalSeconds);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsWindowPosition()
    {
        using var isolated = new IsolatedAppData();

        SettingsStore.Save(new AppSettings { WindowX = 123.5, WindowY = -40 });
        var loaded = SettingsStore.Load();

        Assert.Equal(123.5, loaded.WindowX);
        Assert.Equal(-40, loaded.WindowY);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsPollIntervalSeconds_AlongsideWindowPosition()
    {
        using var isolated = new IsolatedAppData();

        SettingsStore.Save(new AppSettings { WindowX = 1, WindowY = 2, PollIntervalSeconds = 300 });
        var loaded = SettingsStore.Load();

        Assert.Equal(1, loaded.WindowX);
        Assert.Equal(2, loaded.WindowY);
        Assert.Equal(300, loaded.PollIntervalSeconds);
    }
}
