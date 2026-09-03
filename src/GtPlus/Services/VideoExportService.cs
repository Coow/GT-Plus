using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GtPlus.Controls;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>everything one MP4 export needs to know</summary>
public sealed class VideoExportOptions
{
    public string OutputPath { get; set; } = "";

    /// <summary>full path to the ffmpeg binary frames are piped into</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>storyboards laid out on a shared timeline, exactly as the timeline panel does</summary>
    public IReadOnlyList<GtTimelineSegment> Segments { get; set; } = Array.Empty<GtTimelineSegment>();

    /// <summary>seconds of video to render</summary>
    public double Duration { get; set; } = 1;

    public int Fps { get; set; } = 30;

    /// <summary>x264 constant rate factor, lower is better quality and a bigger file</summary>
    public int Crf { get; set; } = 20;

    public string Preset { get; set; } = "medium";

    /// <summary>colour the (transparent) title is composited over; MP4 carries no alpha so something has to be behind it, black matches how vMix shows a title over a black input</summary>
    public Color Background { get; set; } = Colors.Black;
}

public sealed class VideoExportResult
{
    public VideoExportResult(string path, int frameCount, TimeSpan elapsed)
    {
        Path = path;
        FrameCount = frameCount;
        Elapsed = elapsed;
    }

    public string Path { get; }
    public int FrameCount { get; }
    public TimeSpan Elapsed { get; }
}

/// <summary>renders a storyboard to an MP4 by driving the canvas one frame at a time and piping raw BGRA frames into ffmpeg's stdin; the canvas does the drawing rather than a separate renderer so what lands in the file is by construction what the preview shows, which ties the loop to the UI thread where rendering happens while the blocking pipe writes are handed to a background writer through a short bounded queue so the window stays responsive and ffmpeg is never starved</summary>
public static class VideoExportService
{
    /// <summary>frames allowed to sit in flight between the renderer and the pipe writer</summary>
    private const int QueueDepth = 3;

    /// <summary>ffmpeg stderr lines kept for the error message</summary>
    private const int ErrorTailLines = 40;

    public static async Task<VideoExportResult> ExportAsync(GtCanvasControl canvas,
                                                            VideoExportOptions options,
                                                            IProgress<double>? progress = null,
                                                            CancellationToken ct = default)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("Video export must be started on the UI thread.");

        var doc = canvas.Document ?? throw new InvalidOperationException("No document is open.");

        // match ExportToBitmap's own rounding, the scratch buffer has to be pixel-identical to the bitmap it copies out of
        int width  = Math.Max(1, (int)Math.Round(doc.Width));
        int height = Math.Max(1, (int)Math.Round(doc.Height));
        int fps    = Math.Clamp(options.Fps, 1, 240);
        int frameCount = Math.Max(1, (int)Math.Round(options.Duration * fps));

        var startedAt = Stopwatch.StartNew();
        var previousFrame = canvas.AnimationFrame;

        using var process = StartFfmpeg(options, width, height, fps, out var stderrTail);

        var queue   = new BlockingCollection<byte[]>(QueueDepth);
        var pool    = new ConcurrentBag<byte[]>();
        var stdin   = process.StandardInput.BaseStream;
        Exception? writeFailure = null;

        var writer = Task.Run(() =>
        {
            foreach (var buffer in queue.GetConsumingEnumerable())
            {
                if (writeFailure is not null) continue;   // drain, so the producer never blocks

                try
                {
                    stdin.Write(buffer, 0, buffer.Length);
                    pool.Add(buffer);
                }
                catch (Exception ex)
                {
                    // ffmpeg died (bad arguments, unwritable output, ...), record it and keep draining, the producer notices on its next frame
                    writeFailure = ex;
                }
            }

            try { stdin.Flush(); } catch { }
            try { stdin.Close(); } catch { }
        });

        int stride = width * 4;
        bool cancelled = false;

        try
        {
            using var scratch = new WriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

            for (int i = 0; i < frameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (writeFailure is not null) break;

                double time = (double)i / fps;
                canvas.AnimationFrame = GtAnimationEvaluator.Evaluate(doc, options.Segments, time);

                if (!pool.TryTake(out var buffer)) buffer = new byte[stride * height];
                CapturePixels(canvas, scratch, buffer, width, height, stride, options.Background);

                // off the UI thread, Add blocks once the queue is full which is exactly the back-pressure that keeps memory bounded, but it must not block the window
                await Task.Run(() => queue.Add(buffer), CancellationToken.None).ConfigureAwait(true);

                progress?.Report((double)(i + 1) / frameCount);

                // let the dispatcher run input and paint between frames, so the canvas shows the animation as it renders and Cancel stays clickable
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            canvas.AnimationFrame = previousFrame;

            queue.CompleteAdding();
            await writer.ConfigureAwait(true);
            queue.Dispose();
        }

        if (cancelled)
        {
            KillQuietly(process);
            TryDelete(options.OutputPath);
            throw new OperationCanceledException(ct.IsCancellationRequested ? ct : CancellationToken.None);
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);

        if (writeFailure is not null)
            throw new InvalidOperationException(Describe("ffmpeg stopped reading frames", stderrTail), writeFailure);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(Describe($"ffmpeg exited with code {process.ExitCode}", stderrTail));

        return new VideoExportResult(options.OutputPath, frameCount, startedAt.Elapsed);
    }

    /// <summary>renders the current animation frame and packs it into <paramref name="buffer"/> as tightly-packed BGRA, composited over <paramref name="background"/></summary>
    private static void CapturePixels(GtCanvasControl canvas, WriteableBitmap scratch, byte[] buffer,
                                      int width, int height, int stride, Color background)
    {
        using var bitmap = canvas.ExportToBitmap()
            ?? throw new InvalidOperationException("The canvas produced no frame.");

        using (var fb = scratch.Lock())
        {
            bitmap.CopyPixels(fb);

            if (fb.RowBytes == stride)
            {
                Marshal.Copy(fb.Address, buffer, 0, stride * height);
            }
            else
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(IntPtr.Add(fb.Address, y * fb.RowBytes), buffer, y * stride, stride);
            }
        }

        Composite(buffer, background);
    }

    /// <summary>lays the premultiplied frame over a solid colour; premultiplied source needs no division, <c>out = src + bg * (1 - a)</c>, and black is the identity case and is skipped which is the common one</summary>
    private static void Composite(byte[] buffer, Color background)
    {
        if (background.R == 0 && background.G == 0 && background.B == 0) return;

        int b = background.B, g = background.G, r = background.R;

        for (int i = 0; i < buffer.Length; i += 4)
        {
            int inverse = 255 - buffer[i + 3];
            if (inverse == 0) continue;

            buffer[i]     = (byte)Math.Min(255, buffer[i]     + b * inverse / 255);
            buffer[i + 1] = (byte)Math.Min(255, buffer[i + 1] + g * inverse / 255);
            buffer[i + 2] = (byte)Math.Min(255, buffer[i + 2] + r * inverse / 255);
            buffer[i + 3] = 255;
        }
    }

    private static Process StartFfmpeg(VideoExportOptions options, int width, int height, int fps,
                                       out Queue<string> stderrTail)
    {
        var info = new ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardError  = true,
            RedirectStandardOutput = false,
        };

        foreach (var argument in BuildArguments(options, width, height, fps))
            info.ArgumentList.Add(argument);

        Logger.Info($"ffmpeg {string.Join(" ", info.ArgumentList)}");

        var tail = new Queue<string>();
        stderrTail = tail;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > ErrorTailLines) tail.Dequeue();
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        return process;
    }

    private static IEnumerable<string> BuildArguments(VideoExportOptions options, int width, int height, int fps)
    {
        yield return "-hide_banner";
        yield return "-loglevel"; yield return "error";
        yield return "-y";

        // input, tightly packed BGRA straight off the canvas
        yield return "-f";            yield return "rawvideo";
        yield return "-pixel_format"; yield return "bgra";
        yield return "-video_size";   yield return $"{width}x{height}";
        yield return "-framerate";    yield return fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "-i";            yield return "pipe:0";

        yield return "-an";
        yield return "-c:v";     yield return "libx264";
        yield return "-preset";  yield return options.Preset;
        yield return "-crf";     yield return options.Crf.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "-pix_fmt"; yield return "yuv420p";

        // yuv420p needs even dimensions, GT canvases normally are but a hand-edited one need not be, and padding is cheaper than failing the export
        yield return "-vf"; yield return "pad=ceil(iw/2)*2:ceil(ih/2)*2";

        yield return "-movflags"; yield return "+faststart";
        yield return options.OutputPath;
    }

    private static string Describe(string headline, Queue<string> stderrTail)
    {
        string detail;
        lock (stderrTail) detail = string.Join(Environment.NewLine, stderrTail);

        return string.IsNullOrWhiteSpace(detail) ? headline : $"{headline}.{Environment.NewLine}{detail}";
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);

            // wait for the handle on the half-written file to go away before deleting it
            process.WaitForExit(2000);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
