using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using ClaudeUsage.Views;

namespace ClaudeUsage;

/// <summary>Which panel inside the signed-in state is shown. Persisted via <see cref="AppSettings.SelectedView"/>.</summary>
internal enum ViewMode
{
    Detailed,
    TrafficLight
}

public partial class MainWindow : Window
{
    private const int DefaultPollIntervalSeconds = 60;

    // Kept in sync with the Border widths in MainWindow.axaml - used to compute the
    // fallback startup position (see RestorePosition) before layout has necessarily run,
    // so it can't rely on Bounds.Width being settled yet.
    private const double NormalPanelWidth = 300;
    private const double TrafficLightPanelWidth = 92;

    // U+21BB (clockwise open circle arrow) at rest; U+25B6 (play triangle) while a
    // fetch is in flight, to read as "executing" rather than "idle, click to refresh".
    private const string RefreshIdleGlyph = "↻";
    private const string RefreshBusyGlyph = "▶";

    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFB300"));
    private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#E53935"));
    private static readonly IBrush IdleIconBrush = new SolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly IBrush FetchingGlowBrush = new SolidColorBrush(Color.Parse("#4CAF50"));

    /// <summary>
    /// Traffic-light lens colors per band: a highlight/mid/shadow triple for the lens's
    /// radial gradient - deliberately more saturated/luminous than the equivalent
    /// progress-bar brushes above, so the lens reads as a lit LED rather than a flat
    /// fill - plus the percent-text color that contrasts best against that band's lens.
    /// </summary>
    private static readonly (Color Highlight, Color Mid, Color Shadow, IBrush Text) GreenLight =
        (Color.Parse("#B9F6CA"), Color.Parse("#00E676"), Color.Parse("#00B248"), Brushes.Black);
    private static readonly (Color Highlight, Color Mid, Color Shadow, IBrush Text) AmberLight =
        (Color.Parse("#FFECB3"), Color.Parse("#FFC400"), Color.Parse("#FF8F00"), Brushes.Black);
    private static readonly (Color Highlight, Color Mid, Color Shadow, IBrush Text) RedLight =
        (Color.Parse("#FF8A80"), Color.Parse("#FF1744"), Color.Parse("#C4001D"), Brushes.White);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly UsageClient _usageClient;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _positionSaveTimer;
    private readonly AppSettings _settings;
    private ViewMode _viewMode;
    private CancellationTokenSource? _pollCts;
    private Win32TitleBarDragHelper? _win32DragHelper;

    // True between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE. While true, OnPositionChanged
    // must not re-arm the quiet-period debounce - see DragEnded's doc comment.
    private bool _isNativeDragging;

    // Null until the first successful fetch reports a band, so that fetch itself never
    // fires a "crossed" notification - only a genuine increase after that does.
    private int? _lastSessionBand;
    private int? _lastWeeklyBand;
    private double? _lastSessionUtilization;

    public MainWindow()
    {
        InitializeComponent();

        TrafficLightPanel.Background = CreateNoiseBrush();
        PopulateHexOverlay();

        _settings = SettingsStore.Load();
        _viewMode = _settings.SelectedView == nameof(ViewMode.TrafficLight) ? ViewMode.TrafficLight : ViewMode.Detailed;
        ApplyViewMode();

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
            SnapToNearestEdgeIfClose();
            SavePosition();
        };

        Opened += OnOpened;
        Closing += OnClosing;
        PositionChanged += OnPositionChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle()?.Handle;
            if (handle is { } hwnd && hwnd != IntPtr.Zero)
            {
                _win32DragHelper = new Win32TitleBarDragHelper(hwnd, IsDraggableClientPoint);

                // Stop the quiet-period debounce from firing (and reassigning Position)
                // while Windows' own native drag loop is still active - see DragEnded's
                // doc comment. It's the authoritative "drag is truly over" signal instead.
                _win32DragHelper.DragStarted += () =>
                {
                    _isNativeDragging = true;
                    _positionSaveTimer.Stop();
                };
                _win32DragHelper.DragEnded += () =>
                {
                    _isNativeDragging = false;
                    _positionSaveTimer.Stop();
                    SnapToNearestEdgeIfClose();
                    SavePosition();
                };
            }
        }

        // RefreshAuthState must run first: it settles which panel (and therefore which
        // width) is visible, which RestorePosition's fallback-position branch depends on.
        RefreshAuthState();
        RestorePosition();
        _timer.Start();
        _ = PollUsageAsync();
        _ = CheckForUpdateAsync();
    }

    /// <summary>
    /// Checks GitHub Releases once per launch and, if a newer version was downloaded,
    /// reveals the "Restart to update" menu item and shows a toast - see UpdateService
    /// for why this never surfaces an error on failure (it just retries next launch).
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        var version = await UpdateService.CheckAndDownloadAsync(CancellationToken.None);
        if (version is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var header = $"Restart to update (v{version})";
            UpdateMenuItem.Header = header;
            UpdateMenuItem.IsVisible = true;
            UpdateSeparator.IsVisible = true;
            UpdateMenuItem2.Header = header;
            UpdateMenuItem2.IsVisible = true;
            UpdateSeparator2.IsVisible = true;

            OsNotificationService.Show("Update ready", $"Claude Usage v{version} downloaded. Restart to install.");
        });
    }

    /// <summary>
    /// Windows-only: answers Win32TitleBarDragHelper's WM_NCHITTEST query. Point is in
    /// client-area device pixels; true means "drag the window", false means "ordinary
    /// content" (so the refresh/settings/close buttons keep receiving real clicks even
    /// though they sit inside the draggable area). The draggable area is the whole
    /// traffic-light panel (it has no separate header row to grab) but only the header
    /// row on the normal panel (dragging from the progress bars would be surprising).
    /// </summary>
    private bool IsDraggableClientPoint(int clientX, int clientY)
    {
        var scale = RenderScaling;
        var point = new Point(clientX / scale, clientY / scale);

        var (dragArea, buttons) = TrafficLightPanel.IsVisible
            ? ((Control)TrafficLightPanel, new Control[] { TLRefreshButton, TLSettingsButton, TLCloseButton })
            : ((Control)HeaderPanel, new Control[] { RefreshButton, SettingsButton, CloseButton });

        var dragOrigin = dragArea.TranslatePoint(new Point(0, 0), this) ?? default;
        var dragRect = new Rect(dragOrigin, dragArea.Bounds.Size);
        if (!dragRect.Contains(point))
        {
            return false;
        }

        foreach (var button in buttons)
        {
            var buttonOrigin = button.TranslatePoint(new Point(0, 0), this) ?? default;
            var buttonRect = new Rect(buttonOrigin, button.Bounds.Size);
            if (buttonRect.Contains(point))
            {
                return false;
            }
        }

        return true;
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_isNativeDragging)
        {
            // DragEnded (WM_EXITSIZEMOVE) will handle this drag's snap/save authoritatively
            // once it actually finishes - don't let the debounce race it mid-drag.
            return;
        }

        // Restart the debounce window on every move; only the final settled position gets saved.
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _timer.Stop();
        _positionSaveTimer.Stop();
        SavePosition();

        if (OperatingSystem.IsWindows())
        {
            _win32DragHelper?.Dispose();
        }
    }

    private void SavePosition()
    {
        _settings.WindowX = Position.X;
        _settings.WindowY = Position.Y;
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// Called by the tray icon's "Show panel"/click actions to un-hide the window.
    /// Polling never stopped while hidden, but this still fetches immediately so the
    /// panel doesn't show slightly-stale numbers for up to a full poll interval.
    /// </summary>
    internal void ResumeFromTray()
    {
        Show();
        Activate();
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
            Position = new PixelPoint(bounds.Right - (int)CurrentPanelWidth - 16, bounds.Y + 16);
        }
    }

    /// <summary>
    /// The width of whichever panel is (or is about to become) visible. Computed from
    /// state rather than read from Bounds.Width, because RestorePosition's fallback
    /// branch can run before layout has necessarily settled after a panel swap.
    /// </summary>
    private double CurrentPanelWidth =>
        _usageClient.IsSignedIn && _viewMode == ViewMode.TrafficLight ? TrafficLightPanelWidth : NormalPanelWidth;

    private bool IsOnAnyScreen(PixelPoint point) => FindScreenContaining(point) is not null;

    private Screen? FindScreenContaining(PixelPoint point)
    {
        foreach (var screen in Screens.All)
        {
            if (screen.WorkingArea.Contains(point))
            {
                return screen;
            }
        }

        return null;
    }

    // Wider than a typical "snap zone" because most edges have no hard physical stop for
    // the cursor to land precisely against: the top edge is trivial to hit exactly (the
    // cursor is capped at screen Y=0), but the bottom sits above the taskbar (excluded
    // from WorkingArea) and left/right edges between adjacent monitors have nothing
    // stopping the cursor from gliding straight past them onto the next screen.
    private const int EdgeSnapThreshold = 48;

    /// <summary>
    /// Snaps the panel flush to the nearest screen edge once a drag settles within
    /// <see cref="EdgeSnapThreshold"/> pixels of it, so it doesn't have to be
    /// pixel-perfectly placed by hand to sit cleanly in a corner.
    /// </summary>
    private void SnapToNearestEdgeIfClose()
    {
        var scale = RenderScaling;
        var winWidth = (int)(Bounds.Width * scale);
        var winHeight = (int)(Bounds.Height * scale);

        // Resolve the screen from the window's center, not its top-left corner: Position
        // crosses into the adjacent monitor's coordinate space the instant it passes a
        // boundary, which - right when approaching that monitor's edge from within it -
        // flips screen resolution to the wrong monitor and makes every distance check
        // measure against the wrong WorkingArea, silently defeating the snap. The center
        // only crosses once the window is genuinely mostly on the next monitor.
        var center = new PixelPoint(Position.X + winWidth / 2, Position.Y + winHeight / 2);
        var screen = FindScreenContaining(center) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var x = Position.X;
        var y = Position.Y;

        if (Math.Abs(x - area.X) <= EdgeSnapThreshold)
        {
            x = area.X;
        }
        else if (Math.Abs(x + winWidth - area.Right) <= EdgeSnapThreshold)
        {
            x = area.Right - winWidth;
        }

        if (Math.Abs(y - area.Y) <= EdgeSnapThreshold)
        {
            y = area.Y;
        }
        else if (Math.Abs(y + winHeight - area.Bottom) <= EdgeSnapThreshold)
        {
            y = area.Bottom - winHeight;
        }

        var snapped = new PixelPoint(x, y);
        if (snapped != Position)
        {
            Position = snapped;
        }
    }

    private void RefreshAuthState()
    {
        var signedIn = _usageClient.IsSignedIn;
        SignOutMenuItem.IsEnabled = signedIn;
        SignOutMenuItem2.IsEnabled = signedIn;
        UpdateActivePanel();

        if (!signedIn && App.TrayIconInstance is { } tray)
        {
            tray.ToolTipText = "Claude Usage — not signed in";
            tray.Icon = GetTrayStatusIcon(TrayIconNeutral);
        }
    }

    /// <summary>
    /// Single source of truth for which of the two root panels is showing, combining
    /// sign-in state and the selected view mode: the compact traffic-light panel only
    /// ever appears while signed in, so signing out always falls back to the normal one
    /// regardless of the last-selected view.
    /// </summary>
    private void UpdateActivePanel()
    {
        var signedIn = _usageClient.IsSignedIn;
        var showTrafficLight = signedIn && _viewMode == ViewMode.TrafficLight;

        NormalPanel.IsVisible = !showTrafficLight;
        TrafficLightPanel.IsVisible = showTrafficLight;

        SignedOutPanel.IsVisible = !signedIn;
        DetailedView.IsVisible = signedIn;
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
        // Polling deliberately keeps running while hidden: the tray icon color/tooltip
        // and threshold/reset toasts all depend on fresh data even when the panel isn't
        // open, which is the whole point of those features.
        Hide();
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// Only reachable once CheckForUpdateAsync has already downloaded an update, so this
    /// applies and restarts unconditionally - it mirrors OnClosing's save-state steps
    /// first since ApplyAndRestart exits the process immediately, before WindowClosing
    /// would otherwise get a chance to fire.
    /// </summary>
    private void OnUpdateMenuClick(object? sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _positionSaveTimer.Stop();
        SavePosition();
        UpdateService.ApplyAndRestart();
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
        var currentLaunchAtLogin = _settings.LaunchAtLogin ?? false;
        var dialog = new SettingsDialog(currentSeconds, currentLaunchAtLogin);
        await dialog.ShowDialog(this);

        if (dialog.Succeeded)
        {
            _settings.PollIntervalSeconds = dialog.ResultIntervalSeconds;
            _settings.LaunchAtLogin = dialog.ResultLaunchAtLogin;
            SettingsStore.Save(_settings);

            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(dialog.ResultIntervalSeconds);
            _timer.Start();

            if (LoginItemService.IsSupported)
            {
                LoginItemService.SetEnabled(dialog.ResultLaunchAtLogin);
            }
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

    private void OnSelectDetailedView(object? sender, RoutedEventArgs e) => SetViewMode(ViewMode.Detailed);

    private void OnSelectTrafficLightView(object? sender, RoutedEventArgs e) => SetViewMode(ViewMode.TrafficLight);

    private void SetViewMode(ViewMode mode)
    {
        if (_viewMode == mode)
        {
            return;
        }

        _viewMode = mode;
        ApplyViewMode();
        UpdateActivePanel();

        _settings.SelectedView = mode.ToString();
        SettingsStore.Save(_settings);
    }

    /// <summary>Syncs the View submenu's radio checkmarks (both copies - see MainWindow.axaml) to _viewMode.</summary>
    private void ApplyViewMode()
    {
        var trafficLight = _viewMode == ViewMode.TrafficLight;
        DetailedViewMenuItem.IsChecked = !trafficLight;
        TrafficLightViewMenuItem.IsChecked = trafficLight;
        DetailedViewMenuItem2.IsChecked = !trafficLight;
        TrafficLightViewMenuItem2.IsChecked = trafficLight;
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
        var content = isFetching ? RefreshBusyGlyph : RefreshIdleGlyph;
        var foreground = isFetching ? FetchingGlowBrush : IdleIconBrush;

        foreach (var button in new[] { RefreshButton, TLRefreshButton })
        {
            button.Content = content;
            button.Foreground = foreground;
            button.Effect = isFetching
                ? new DropShadowEffect { Color = Color.Parse("#4CAF50"), BlurRadius = 8, OffsetX = 0, OffsetY = 0 }
                : null;
            button.IsHitTestVisible = !isFetching;
        }
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
                UpdateTrayStatusIcon(result.Usage!);
                CheckThresholdNotifications(result.Usage!);
                CheckSessionResetNotification(result.Usage!);
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
        RenderTrafficLight(usage.FiveHour);
    }

    /// <summary>
    /// Drives the single-light traffic-light view from session (5h) usage only - kept in
    /// sync regardless of which view is currently visible, so switching views never shows
    /// stale data while waiting for the next poll.
    /// </summary>
    private void RenderTrafficLight(UsageWindow? window)
    {
        if (window is null)
        {
            TrafficLightPercentText.Text = "--%";
            TrafficLightResetText.Text = " ";
            ApplyLightColors(GreenLight);
            return;
        }

        var pct = Math.Clamp(window.Utilization, 0, 100);
        TrafficLightPercentText.Text = $"{pct:0}%";
        TrafficLightResetText.Text = window.ResetsAt is { } resetsAt ? FormatRemaining(resetsAt) : " ";
        ApplyLightColors(pct >= 80 ? RedLight : pct >= 50 ? AmberLight : GreenLight);
    }

    private void ApplyLightColors((Color Highlight, Color Mid, Color Shadow, IBrush Text) light)
    {
        var stops = ((RadialGradientBrush)TrafficLightLens.Fill!).GradientStops;
        stops[0].Color = light.Highlight;
        stops[1].Color = light.Mid;
        stops[2].Color = light.Shadow;
        TrafficLightPercentText.Foreground = light.Text;
        TrafficLightResetText.Foreground = light.Text;
    }

    /// <summary>
    /// Generates a small tiled grain texture for the traffic-light housing, so its
    /// near-black background reads as painted metal/plastic rather than a flat, printer
    /// -perfect fill. Built once at startup - the tile is small and seeded so every run
    /// looks the same rather than randomly regenerating on each launch.
    /// </summary>
    private static IBrush CreateNoiseBrush()
    {
        const int size = 48;
        const byte baseGray = 22;
        const int amplitude = 9;

        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
        {
            var random = new Random(20240613);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var offset = y * buffer.RowBytes + x * 4;
                    var value = (byte)Math.Clamp(baseGray + random.Next(-amplitude, amplitude + 1), 0, 255);
                    Marshal.WriteByte(buffer.Address, offset, value);
                    Marshal.WriteByte(buffer.Address, offset + 1, value);
                    Marshal.WriteByte(buffer.Address, offset + 2, value);
                    Marshal.WriteByte(buffer.Address, offset + 3, 255);
                }
            }
        }

        return new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            SourceRect = new RelativeRect(0, 0, size, size, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, size, size, RelativeUnit.Absolute)
        };
    }

    /// <summary>
    /// Draws a faint honeycomb grid over the lens, echoing the diffuser texture on a real
    /// LED traffic-light lens. Geometry is static (only the lens's own fill color changes
    /// per band) so this only needs to run once at startup; MainWindow.axaml clips the
    /// host Canvas to the lens circle so hexagons never spill past its edge.
    /// </summary>
    private void PopulateHexOverlay()
    {
        const double diameter = 64;
        const double hexRadius = 2;
        var stroke = new SolidColorBrush(Colors.Black, 0.18);

        var hexWidth = Math.Sqrt(3) * hexRadius;
        var rowSpacing = hexRadius * 1.5;
        var margin = hexRadius * 2;

        var row = 0;
        for (var cy = -margin; cy <= diameter + margin; cy += rowSpacing, row++)
        {
            var xOffset = row % 2 == 0 ? 0 : hexWidth / 2;
            for (var cx = -margin + xOffset; cx <= diameter + margin; cx += hexWidth)
            {
                var hex = new Avalonia.Controls.Shapes.Path
                {
                    Data = new PolylineGeometry(BuildHexPoints(cx, cy, hexRadius), true),
                    Stroke = stroke,
                    StrokeThickness = 0.75
                };
                TrafficLightHexOverlay.Children.Add(hex);
            }
        }
    }

    private static Point[] BuildHexPoints(double cx, double cy, double r)
    {
        var points = new Point[6];
        for (var i = 0; i < 6; i++)
        {
            var angle = Math.PI / 180 * (60 * i - 30);
            points[i] = new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
        }

        return points;
    }

    /// <summary>
    /// Fires an OS-level notification the moment session/weekly usage newly crosses into
    /// the amber or red band (same thresholds as the progress bar colors). Only fires on
    /// an upward crossing, not every poll while already in that band.
    /// </summary>
    private void CheckThresholdNotifications(UsageResponse usage)
    {
        NotifyIfBandIncreased("Session (5h)", usage.FiveHour?.Utilization, ref _lastSessionBand);
        NotifyIfBandIncreased("Weekly", usage.SevenDay?.Utilization, ref _lastWeeklyBand);
    }

    private void NotifyIfBandIncreased(string label, double? utilization, ref int? lastBand)
    {
        if (utilization is not { } pct)
        {
            return;
        }

        var band = GetBand(pct);
        var previous = lastBand;
        lastBand = band;

        if (previous is null || band <= previous)
        {
            return;
        }

        var title = band switch
        {
            2 => $"{label} usage is critical",
            _ => $"{label} usage is high"
        };

        OsNotificationService.Show(title, $"Now at {pct:0}%.");
    }

    private static int GetBand(double pct) => pct switch
    {
        >= 80 => 2,
        >= 50 => 1,
        _ => 0
    };

    /// <summary>
    /// Fires an OS-level notification when session usage is observed to have gone DOWN
    /// between two consecutive polls - the only reliable signal that a genuine reset just
    /// happened. resets_at looked like a fixed deadline but apparently isn't one (it seems
    /// to get recalculated/slide forward server-side as time passes rather than staying
    /// put until an actual reset), so comparing it fired on nearly every poll instead of
    /// only on real resets. Utilization, by contrast, can only ever decrease via an actual
    /// reset - it just accumulates otherwise - so this can't misfire the same way.
    ///
    /// Never fires on the very first observation after launch (no prior value to compare
    /// against - startup usage being low isn't itself a "reset"), and fires at most once
    /// per genuine reset: after firing, the new lower value becomes the baseline, so
    /// ordinary usage climbing back up afterward can't trigger it again until the next
    /// real drop.
    /// </summary>
    private void CheckSessionResetNotification(UsageResponse usage)
    {
        if (usage.FiveHour?.Utilization is not { } current)
        {
            return;
        }

        if (_lastSessionUtilization is { } previous && current < previous)
        {
            OsNotificationService.Show("Session limit reset", "Your 5-hour session usage has reset.");
        }

        _lastSessionUtilization = current;
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

    private const string TrayIconNeutral = "tray-status-neutral.ico";
    private const string TrayIconGood = "tray-status-good.ico";
    private const string TrayIconWarn = "tray-status-warn.ico";
    private const string TrayIconCritical = "tray-status-critical.ico";

    private static readonly Dictionary<string, WindowIcon> TrayStatusIconCache = new();

    /// <summary>
    /// Recolors the tray icon to match the worse of the two progress bars (same
    /// thresholds as their fill color), so a glance at the tray shows whether
    /// you're fine without opening the panel.
    /// </summary>
    private static void UpdateTrayStatusIcon(UsageResponse usage)
    {
        if (App.TrayIconInstance is not { } tray)
        {
            return;
        }

        var worst = Math.Max(usage.FiveHour?.Utilization ?? 0, usage.SevenDay?.Utilization ?? 0);
        var fileName = worst switch
        {
            >= 80 => TrayIconCritical,
            >= 50 => TrayIconWarn,
            _ => TrayIconGood
        };

        tray.Icon = GetTrayStatusIcon(fileName);
    }

    private static WindowIcon GetTrayStatusIcon(string fileName)
    {
        if (!TrayStatusIconCache.TryGetValue(fileName, out var icon))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://ClaudeUsage/Assets/{fileName}"));
            icon = new WindowIcon(stream);
            TrayStatusIconCache[fileName] = icon;
        }

        return icon;
    }

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
        return delta <= TimeSpan.Zero ? "soon" : $"in {FormatDuration(delta)}";
    }

    /// <summary>
    /// Compact "Xh Ym"-style remaining time shown inside the traffic light - there's no
    /// room there for FormatRelative's "resets in"/"in" framing.
    /// </summary>
    private static string FormatRemaining(DateTimeOffset resetsAt)
    {
        var delta = resetsAt - DateTimeOffset.UtcNow;
        return delta <= TimeSpan.Zero ? "now" : FormatDuration(delta);
    }

    private static string FormatDuration(TimeSpan delta)
    {
        if (delta.TotalDays >= 1)
        {
            return $"{(int)delta.TotalDays}d {delta.Hours}h";
        }

        if (delta.TotalHours >= 1)
        {
            return $"{(int)delta.TotalHours}h {delta.Minutes}m";
        }

        return $"{delta.Minutes}m";
    }
}
