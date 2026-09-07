using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using ClaudeUsage.Views;

namespace ClaudeUsage;

public partial class MainWindow : Window
{
    private const int DefaultPollIntervalSeconds = 60;

    // U+21BB (clockwise open circle arrow) at rest; U+25B6 (play triangle) while a
    // fetch is in flight, to read as "executing" rather than "idle, click to refresh".
    private const string RefreshIdleGlyph = "↻";
    private const string RefreshBusyGlyph = "▶";

    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFB300"));
    private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#E53935"));
    private static readonly IBrush IdleIconBrush = new SolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly IBrush FetchingGlowBrush = new SolidColorBrush(Color.Parse("#4CAF50"));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly UsageClient _usageClient;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _positionSaveTimer;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _pollCts;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();

        _usageClient = new UsageClient(_http);
        _usageClient.LoadPersistedSession();
        _usageClient.AuthChanged += (_, _) => Dispatcher.UIThread.Post(RefreshAuthState);

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds ?? DefaultPollIntervalSeconds)
        };
        _timer.Tick += async (_, _) => await PollUsageAsync();

        // Debounces PositionChanged (which fires continuously while dragging) so a drag
        // writes settings.json once, ~400ms after the mouse stops, rather than on every pixel.
        _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _positionSaveTimer.Tick += (_, _) =>
        {
            _positionSaveTimer.Stop();
            SavePosition();
        };

        Opened += OnOpened;
        Closing += OnClosing;
        PositionChanged += OnPositionChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        RestorePosition();
        RefreshAuthState();
        _timer.Start();
        _ = PollUsageAsync();
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        // Restart the debounce window on every move; only the final settled position gets saved.
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _timer.Stop();
        _positionSaveTimer.Stop();
        SavePosition();
    }

    private void SavePosition()
    {
        _settings.WindowX = Position.X;
        _settings.WindowY = Position.Y;
        SettingsStore.Save(_settings);
    }

    /// <summary>Called by the tray icon's "Show panel"/click actions to un-hide the window.</summary>
    internal void ResumeFromTray()
    {
        Show();
        Activate();
        _timer.Start();
        _ = PollUsageAsync();
    }

    private void RestorePosition()
    {
        if (_settings.WindowX is double x && _settings.WindowY is double y)
        {
            var saved = new PixelPoint((int)x, (int)y);
            if (IsOnAnyScreen(saved))
            {
                Position = saved;
                return;
            }
            // Saved position is off every currently-connected screen (e.g. a monitor was
            // unplugged since last run) - fall through to the default corner instead.
        }

        var area = Screens.Primary?.WorkingArea;
        if (area is { } bounds)
        {
            Position = new PixelPoint(bounds.Right - (int)Width - 16, bounds.Y + 16);
        }
    }

    private bool IsOnAnyScreen(PixelPoint point)
    {
        foreach (var screen in Screens.All)
        {
            if (screen.WorkingArea.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshAuthState()
    {
        SignedOutPanel.IsVisible = !_usageClient.IsSignedIn;
        SignedInPanel.IsVisible = _usageClient.IsSignedIn;

        if (!_usageClient.IsSignedIn && App.TrayIconInstance is { } tray)
        {
            tray.ToolTipText = "Claude Usage — not signed in";
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Pause polling while hidden (saves battery/network); ResumeFromTray restarts it.
        _timer.Stop();
        Hide();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        // Restart the timer so the next automatic tick is a full interval from now,
        // rather than firing shortly after this manual refresh.
        _timer.Stop();
        _timer.Start();
        _ = PollUsageAsync();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var currentSeconds = _settings.PollIntervalSeconds ?? DefaultPollIntervalSeconds;
        var dialog = new SettingsDialog(currentSeconds);
        await dialog.ShowDialog(this);

        if (dialog.Succeeded)
        {
            _settings.PollIntervalSeconds = dialog.ResultIntervalSeconds;
            SettingsStore.Save(_settings);

            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(dialog.ResultIntervalSeconds);
            _timer.Start();
        }
    }

    private async void OnSignInClick(object? sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        try
        {
            var url = _usageClient.BeginLogin();
            BrowserLauncher.Open(url);

            var dialog = new AuthCodeDialog(_usageClient);
            await dialog.ShowDialog(this);

            if (dialog.Succeeded)
            {
                RefreshAuthState();
                await PollUsageAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Sign-in error: {ex.Message}";
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        _usageClient.SignOut();
        RefreshAuthState();
    }

    private async Task PollUsageAsync()
    {
        if (!_usageClient.IsSignedIn)
        {
            RefreshAuthState();
            return;
        }

        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();

        SetFetchingIndicator(true);

        UsageFetchResult result;
        try
        {
            result = await _usageClient.FetchUsageAsync(_pollCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer fetch (e.g. manual refresh while auto-poll was in
            // flight) - that newer fetch owns clearing the indicator, not this one.
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            SetFetchingIndicator(false);
            ApplyResult(result);
        });
    }

    /// <summary>
    /// Swaps the refresh icon to a glowing "in progress" glyph and blocks clicks on it
    /// while a fetch is running, restoring it once the fetch concludes. The button has a
    /// fixed Width in XAML so this glyph swap never shifts the Settings/Close buttons.
    /// </summary>
    private void SetFetchingIndicator(bool isFetching)
    {
        RefreshButton.Content = isFetching ? RefreshBusyGlyph : RefreshIdleGlyph;
        RefreshButton.Foreground = isFetching ? FetchingGlowBrush : IdleIconBrush;
        RefreshButton.Effect = isFetching
            ? new DropShadowEffect { Color = Color.Parse("#4CAF50"), BlurRadius = 8, OffsetX = 0, OffsetY = 0 }
            : null;
        RefreshButton.IsHitTestVisible = !isFetching;
    }

    private void ApplyResult(UsageFetchResult result)
    {
        RefreshAuthState();

        switch (result.Status)
        {
            case UsageFetchStatus.Success:
                Render(result.Usage!);
                StatusText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
                UpdateTrayTooltip(result.Usage!);
                break;

            case UsageFetchStatus.RateLimited:
                var wait = result.RetryAfter is { } ts ? $" (retry in {(int)ts.TotalSeconds}s)" : "";
                StatusText.Text = $"Rate limited{wait}";
                break;

            case UsageFetchStatus.AuthError:
                StatusText.Text = "Signed out";
                break;

            case UsageFetchStatus.NetworkError:
                StatusText.Text = $"Error: {result.Message}";
                break;

            case UsageFetchStatus.NotSignedIn:
                break;
        }
    }

    private void Render(UsageResponse usage)
    {
        RenderWindow(usage.FiveHour, SessionPercentText, SessionBar, SessionResetText);
        RenderWindow(usage.SevenDay, WeeklyPercentText, WeeklyBar, WeeklyResetText);
    }

    /// <summary>Lets the numbers be checked at a glance by hovering the tray icon, without opening the panel.</summary>
    private static void UpdateTrayTooltip(UsageResponse usage)
    {
        if (App.TrayIconInstance is not { } tray)
        {
            return;
        }

        var session = FormatPercentOrPlaceholder(usage.FiveHour?.Utilization);
        var weekly = FormatPercentOrPlaceholder(usage.SevenDay?.Utilization);
        tray.ToolTipText = $"Claude Usage — Session {session} · Weekly {weekly}";
    }

    private static string FormatPercentOrPlaceholder(double? value) =>
        value is { } v ? $"{Math.Clamp(v, 0, 100):0}%" : "--";

    private static void RenderWindow(UsageWindow? window, TextBlock percentText, ProgressBar bar, TextBlock resetText)
    {
        if (window is null)
        {
            percentText.Text = "--%";
            bar.Value = 0;
            resetText.Text = " ";
            return;
        }

        var pct = Math.Clamp(window.Utilization, 0, 100);
        percentText.Text = $"{pct:0}%";
        bar.Value = pct;
        bar.Foreground = pct >= 80 ? BadBrush : pct >= 50 ? WarnBrush : GoodBrush;
        resetText.Text = window.ResetsAt is { } resetsAt ? $"resets {FormatRelative(resetsAt)}" : " ";
    }

    private static string FormatRelative(DateTimeOffset resetsAt)
    {
        var delta = resetsAt - DateTimeOffset.UtcNow;
        if (delta <= TimeSpan.Zero)
        {
            return "soon";
        }

        if (delta.TotalDays >= 1)
        {
            return $"in {(int)delta.TotalDays}d {delta.Hours}h";
        }

        if (delta.TotalHours >= 1)
        {
            return $"in {(int)delta.TotalHours}h {delta.Minutes}m";
        }

        return $"in {delta.Minutes}m";
    }
}
