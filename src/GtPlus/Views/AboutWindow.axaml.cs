using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GtPlus.Services;
using Logger = GtPlus.Services.Logger;

namespace GtPlus.Views;

public partial class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/Coow/gt-plus";

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version: " + AppVersion.Current;
    }

    private void Github_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"AboutWindow: could not open {RepoUrl} - {ex.Message}");
        }
    }
}
