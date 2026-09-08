using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace GtPlus.Services;

/// <summary>finds a Chromium-family browser already installed on the machine and drives it in headless mode; the page itself is run and streamed by <see cref="CdpSession"/>. Nothing is bundled and nothing is downloaded: Chrome, Chromium and Edge all ship the same engine and one of them is on practically every machine this editor runs on, so <see cref="Locate"/> searches the usual places the way <see cref="FfmpegService"/> does and the user can point at a specific binary instead</summary>
public static class ChromiumService
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMac     => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>first working browser found, or null; <paramref name="preferred"/>, the remembered path from preferences, always wins</summary>
    public static string? Locate(string? preferred = null)
    {
        foreach (var candidate in Candidates(preferred))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { /* an unreadable candidate is simply not a match */ }
        }

        return null;
    }

    /// <summary>executable names to look for on PATH, most preferred first</summary>
    private static IEnumerable<string> ExeNames()
    {
        if (IsWindows)
        {
            yield return "chrome.exe";
            yield return "msedge.exe";
            yield return "chromium.exe";
        }
        else
        {
            yield return "google-chrome";
            yield return "chromium";
            yield return "chromium-browser";
            yield return "microsoft-edge";
        }
    }

    private static IEnumerable<string> Candidates(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) yield return preferred!;

        foreach (var onPath in FromEnvironmentPath()) yield return onPath;

        if (IsWindows)
        {
            var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var lad  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(pf,   "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(lad,  "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(pf,   "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(pf,   "Chromium", "Application", "chrome.exe");
        }
        else if (IsMac)
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        }
        else
        {
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/google-chrome-stable";
            yield return "/usr/bin/chromium";
            yield return "/usr/bin/chromium-browser";
            yield return "/snap/bin/chromium";
            yield return "/usr/bin/microsoft-edge";
        }
    }

    private static IEnumerable<string> FromEnvironmentPath()
    {
        string? path;
        try { path = Environment.GetEnvironmentVariable("PATH"); }
        catch { yield break; }

        if (string.IsNullOrEmpty(path)) yield break;

        foreach (var dir in path!.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            foreach (var exe in ExeNames())
            {
                string combined;
                try { combined = Path.Combine(dir.Trim(), exe); }
                catch { continue; }
                yield return combined;
            }
        }
    }
}
