using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Logger = GtPlus.Services.Logger;

namespace GtPlus.Views;

public partial class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/Coow/gt-plus";

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version: " + ReadVersion();
    }

    /// <summary>version.json is linked into the assembly as an Avalonia resource, so the number shown here is the same one build.ps1 stamped the binary with</summary>
    private static string ReadVersion()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://GtPlus/Assets/version.json"));
            using var doc    = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("version", out var value) &&
                value.GetString() is { Length: > 0 } version)
            {
                return version;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"AboutWindow: could not read version.json - {ex.Message}");
        }

        // fall back to whatever the assembly was stamped with
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational)) return "unknown";

        // strip the "+<commit sha>" source-revision suffix
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational.Substring(0, plus) : informational;
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
