using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GtPlus.Services;
using Logger = GtPlus.Services.Logger;

namespace GtPlus.Views;

public partial class UpdateWindow : Window
{
    private readonly PreferencesService _prefs;
    private readonly CancellationTokenSource _cts = new();
    private UpdateInfo? _latest;
    private bool _suppressStartupToggle;

    public UpdateWindow(PreferencesService prefs, UpdateCheckResult? result = null)
    {
        InitializeComponent();
        _prefs = prefs;

        _suppressStartupToggle = true;
        StartupCheck.IsChecked = prefs.CheckUpdatesOnStartup;
        _suppressStartupToggle = false;

        Closed += (_, _) => { _cts.Cancel(); _cts.Dispose(); };

        if (result is not null) Apply(result);
        else                    _ = RunCheck();
    }

    private async System.Threading.Tasks.Task RunCheck()
    {
        try
        {
            var result = await new UpdateService().CheckAsync(_cts.Token);
            if (!_cts.IsCancellationRequested) Apply(result);
        }
        catch (OperationCanceledException) { }
    }

    private void Apply(UpdateCheckResult result)
    {
        if (!result.Success)
        {
            HeadlineText.Text = "Could not check for updates";
            VersionsText.Text = $"{result.Error}\n\nYou are running version {AppVersion.Current}.";
            return;
        }

        _latest = result.Latest;

        if (result.IsNewer && _latest is not null)
        {
            var published = _latest.PublishedAt is { } when ? $"  ·  released {when.ToLocalTime():d MMM yyyy}" : "";

            Title                    = "Update Available";
            HeadlineText.Text        = $"Version {_latest.Version} is available";
            VersionsText.Text        = $"You are running {AppVersion.Current}{published}";
            DownloadButton.IsVisible = true;
            SkipButton.IsVisible     = true;

            if (_latest.Notes.Length > 0)
            {
                NotesText.Text       = _latest.Notes;
                NotesBorder.IsVisible = true;
            }
        }
        else
        {
            HeadlineText.Text = "You are up to date";
            VersionsText.Text = _latest is not null
                ? $"Version {AppVersion.Current} is the latest release."
                : $"You are running version {AppVersion.Current}.";
        }
    }

    private void Download_Click(object? sender, RoutedEventArgs e)
    {
        var url = _latest?.HtmlUrl ?? UpdateService.ReleasesUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"UpdateWindow: could not open {url} - {ex.Message}");
        }
    }

    private void Skip_Click(object? sender, RoutedEventArgs e)
    {
        if (_latest is not null)
        {
            _prefs.SkippedUpdateVersion = _latest.Version;
            _prefs.Save();
        }
        Close();
    }

    private void StartupCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle) return;

        _prefs.CheckUpdatesOnStartup = StartupCheck.IsChecked == true;
        _prefs.Save();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
