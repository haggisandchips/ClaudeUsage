using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClaudeUsage.Views;

public partial class SettingsDialog : Window
{
    public bool Succeeded { get; private set; }
    public int ResultIntervalSeconds { get; private set; }

    // Parameterless constructor required by the XAML loader / previewer.
    public SettingsDialog()
    {
        InitializeComponent();
    }

    internal SettingsDialog(int currentIntervalSeconds) : this()
    {
        IntervalBox.Value = Math.Max(1, currentIntervalSeconds / 60);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var minutes = (int)(IntervalBox.Value ?? 1);
        ResultIntervalSeconds = Math.Clamp(minutes, 1, 60) * 60;
        Succeeded = true;
        Close();
    }
}
