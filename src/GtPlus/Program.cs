using Avalonia;
using System;
using GtPlus.Services;

namespace GtPlus;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Logger.Init();
        Logger.Info($"GT+ starting - PID {Environment.ProcessId}");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Logger.Info("GT+ exiting");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
