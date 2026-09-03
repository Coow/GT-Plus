using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GtPlus.Services;

/// <summary>finds, and on Windows fetches, the ffmpeg binary the video export pipes frames into; ffmpeg is not redistributed with this editor since the licence terms of the common Windows builds make bundling awkward and every vMix machine that already has one should use it, so <see cref="Locate"/> searches the likely places and <see cref="DownloadAsync"/> is the fallback, a user-initiated download of the official gyan.dev release build into the app's own data folder</summary>
public static class FfmpegService
{
    /// <summary>official Windows release build (essentials, static), stable URL</summary>
    private const string WindowsDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string ExeName => IsWindows ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>where <see cref="DownloadAsync"/> puts the binary it fetches</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GtPlus", "ffmpeg");

    public static string InstalledExePath => Path.Combine(InstallDir, ExeName);

    /// <summary>true when the download button can do anything on this platform</summary>
    public static bool CanDownload => IsWindows;

    /// <summary>first working ffmpeg found, or null; <paramref name="preferred"/>, the remembered path from preferences, always wins so a user who pointed at a specific build keeps it</summary>
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

    private static IEnumerable<string> Candidates(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) yield return preferred!;

        yield return InstalledExePath;

        // next to the app, as a sibling file or in an ffmpeg/ or ffmpeg/bin/ subfolder
        // this is where a portable copy shipped alongside the editor would land
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, ExeName);
        yield return Path.Combine(baseDir, "ffmpeg", ExeName);
        yield return Path.Combine(baseDir, "ffmpeg", "bin", ExeName);

        foreach (var onPath in FromEnvironmentPath()) yield return onPath;

        if (IsWindows)
        {
            yield return @"C:\ffmpeg\bin\ffmpeg.exe";
            yield return @"C:\ffmpeg\ffmpeg.exe";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe");
        }
        else
        {
            yield return "/usr/bin/ffmpeg";
            yield return "/usr/local/bin/ffmpeg";
            yield return "/opt/homebrew/bin/ffmpeg";
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

            string combined;
            try { combined = Path.Combine(dir.Trim(), ExeName); }
            catch { continue; }   // PATH entries can hold characters Path rejects

            yield return combined;
        }
    }

    /// <summary>downloads the official Windows build and extracts just <c>ffmpeg.exe</c> into <see cref="InstallDir"/>, returns the path to the extracted binary</summary>
    /// <param name="progress">0-1 where the archive size is known, never reported otherwise</param>
    public static async Task<string> DownloadAsync(IProgress<double>? progress = null,
                                                   CancellationToken ct = default)
    {
        if (!CanDownload)
            throw new PlatformNotSupportedException(
                "Automatic download is Windows-only. Install ffmpeg with your package manager and point the editor at it.");

        Directory.CreateDirectory(InstallDir);
        var zipPath = Path.Combine(InstallDir, "ffmpeg-download.zip");

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            using (var response = await http.GetAsync(WindowsDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength;
                using var source = await response.Content.ReadAsStreamAsync();
                using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read, ct);
                    copied += read;
                    if (total is > 0) progress?.Report(Math.Min(1.0, (double)copied / total.Value));
                }
            }

            ct.ThrowIfCancellationRequested();

            // the archive nests everything under ffmpeg-<version>/bin/, so the entry is found by name rather than a fixed path
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry? entry = null;
                foreach (var candidate in archive.Entries)
                {
                    if (string.Equals(candidate.Name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        entry = candidate;
                        break;
                    }
                }

                if (entry is null)
                    throw new InvalidDataException("The downloaded archive contains no ffmpeg.exe.");

                entry.ExtractToFile(InstalledExePath, overwrite: true);
            }

            Logger.Info($"ffmpeg downloaded to {InstalledExePath}");
            return InstalledExePath;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }
}
