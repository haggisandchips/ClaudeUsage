using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClaudeUsage.Services;

namespace ClaudeUsage.Views;

public partial class AuthCodeDialog : Window
{
    private readonly UsageClient? _usageClient;

    public bool Succeeded { get; private set; }

    // Parameterless constructor required by the XAML loader / previewer.
    public AuthCodeDialog()
    {
        InitializeComponent();
    }

    internal AuthCodeDialog(UsageClient usageClient) : this()
    {
        _usageClient = usageClient;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnSubmit(object? sender, RoutedEventArgs e)
    {
        if (_usageClient is null)
        {
            return;
        }

        var code = CodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            ShowError("Paste the code from the browser first.");
            return;
        }

        SubmitButton.IsEnabled = false;
        try
        {
            await _usageClient.CompleteLoginAsync(code);
            Succeeded = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Sign-in failed: {ex.Message}");
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
