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
        Logger.Info($"vMix GT++ starting - PID {Environment.ProcessId}");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Logger.Info("vMix GT++ exiting");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
