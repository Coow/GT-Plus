using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GtPlus.Views;

namespace GtPlus;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { StartupPath = StartupPathFromArgs(desktop.Args) };
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>the file to open at launch: <c>GtPlus file.gtzip</c> or <c>GtPlus --open file.gtzip</c>; unknown flags are skipped so a launcher's extra switches don't get taken for a path</summary>
    private static string? StartupPathFromArgs(string[]? args)
    {
        if (args is null) return null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--open" or "-o")
                return i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
            if (!arg.StartsWith('-') && arg.Length > 0)
                return Path.GetFullPath(arg);
        }
        return null;
    }
}
