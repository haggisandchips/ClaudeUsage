using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ClaudeUsage;

public partial class App : Application
{
    /// <summary>
    /// The single tray icon declared in App.axaml, resolved once at startup so other
    /// parts of the app (e.g. MainWindow, to update the tooltip/icon) can reach it -
    /// TrayIcon isn't a normal named control, so x:Name doesn't work on it in XAML.
    /// </summary>
    internal static TrayIcon? TrayIconInstance { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            TrayIconInstance = TrayIcon.GetIcons(this)?.FirstOrDefault();
            desktop.MainWindow = new MainWindow();
            // The panel is a background utility window; only the tray "Quit" action
            // (or closing it explicitly) should end the process.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayIconClicked(object? sender, System.EventArgs e) => ShowMainWindow();

    private void OnTrayShowClick(object? sender, System.EventArgs e) => ShowMainWindow();

    private void OnTrayQuitClick(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow window)
        {
            window.ResumeFromTray();
        }
    }
}
