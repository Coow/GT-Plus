using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GtPlus.Controls;
using GtPlus.Models;
using GtPlus.Services;
using Logger = GtPlus.Services.Logger;

namespace GtPlus.Views;

/// <summary>export dialog for rendering a storyboard, or the TransitionIn/TransitionOut pair with a hold between them the way vMix actually plays a title, out to an MP4</summary>
public partial class ExportVideoWindow : Window
{
    private readonly GtCanvasControl? _canvas;
    private readonly PreferencesService _prefs;
    private readonly string? _documentPath;
    private readonly List<ExportTarget> _targets = new();

    private string? _ffmpegPath;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _suppressEvents;

    /// <summary>path written by a completed export, or null when nothing was exported</summary>
    public string? ExportedPath { get; private set; }

    // parameterless ctor for the XAML runtime loader, the app always uses the one below
    public ExportVideoWindow() : this(null, new PreferencesService(), null) { }

    public ExportVideoWindow(GtCanvasControl? canvas, PreferencesService prefs, string? documentPath)
    {
        InitializeComponent();

        _canvas       = canvas;
        _prefs        = prefs;
        _documentPath = documentPath;

        _suppressEvents = true;

        BuildTargets();
        BuildOptionLists();

        HoldBox.Value      = (decimal)Math.Max(0, _prefs.ExportHoldSeconds);
        OutputBox.Text     = SuggestedOutputPath();

        _suppressEvents = false;

        RefreshFfmpeg();
        RefreshSummary();
    }

    /// <summary>one entry per storyboard, plus the combined TransitionIn + TransitionOut pair and each DataChangeIn + DataChangeOut pair when the document has both halves, the same list the timeline panel offers</summary>
    private void BuildTargets()
    {
        var doc = _canvas?.Document;
        if (doc is null) return;

        foreach (var storyboard in doc.Storyboards
                                      .OrderBy(s => GtStoryboard.SortIndex(s.DisplayName))
                                      .ThenBy(s => s.DataName, StringComparer.Ordinal))
            _targets.Add(new ExportTarget(storyboard.EventLabel, storyboard));

        var transitionIn  = doc.Storyboards.FirstOrDefault(s => s.IsTransitionIn);
        var transitionOut = doc.Storyboards.FirstOrDefault(s => s.IsTransitionOut);
        if (transitionIn is not null && transitionOut is not null)
            _targets.Add(new ExportTarget(
                $"{GtStoryboard.TransitionIn} + {GtStoryboard.TransitionOut}", transitionIn, transitionOut));

        // vMix runs the DataChange halves back-to-back, so the pair exports as one clip
        foreach (var scope in doc.Storyboards.Where(s => s.IsDataChangeEvent)
                                             .Select(s => s.DataName)
                                             .Distinct(StringComparer.Ordinal)
                                             .OrderBy(n => n, StringComparer.Ordinal))
        {
            var dataIn  = doc.Storyboards.FirstOrDefault(s => s.Matches(GtStoryboard.DataChangeIn,  scope));
            var dataOut = doc.Storyboards.FirstOrDefault(s => s.Matches(GtStoryboard.DataChangeOut, scope));
            if (dataIn is null || dataOut is null) continue;

            var suffix = scope.Length > 0 ? $" ({scope})" : "";
            _targets.Add(new ExportTarget(
                $"{GtStoryboard.DataChangeIn} + {GtStoryboard.DataChangeOut}{suffix}",
                dataIn, dataOut, usesHold: false));
        }

        // always on offer, so a title with no storyboards yet (a brand new one) can still be exported, held for the Hold seconds
        _targets.Add(new ExportTarget("Still frame (no animation)", null));

        StoryboardCombo.ItemsSource = _targets;

        // default to the combined view, on its own a TransitionIn ends mid-title which is rarely what someone wants out of a video file
        int combined = _targets.FindIndex(t => t.IsCombined);
        StoryboardCombo.SelectedIndex = combined >= 0 ? combined : 0;
    }

    private void BuildOptionLists()
    {
        var formats = new[]
        {
            new FormatOption("MP4 - H.264",                     VideoExportFormat.Mp4),
            new FormatOption("MOV - ProRes 4444 (transparent)", VideoExportFormat.ProRes4444),
            new FormatOption("WebM - VP9 (transparent)",        VideoExportFormat.WebM),
        };
        FormatCombo.ItemsSource = formats;
        int formatIndex = Array.FindIndex(formats,
            f => string.Equals(f.Format.ToString(), _prefs.ExportFormat, StringComparison.OrdinalIgnoreCase));
        FormatCombo.SelectedIndex = formatIndex >= 0 ? formatIndex : 0;

        var rates = new[] { 24, 25, 30, 50, 60 };
        FpsCombo.ItemsSource = rates;
        int fpsIndex = Array.IndexOf(rates, _prefs.ExportFps);
        FpsCombo.SelectedIndex = fpsIndex >= 0 ? fpsIndex : Array.IndexOf(rates, 30);

        var qualities = new[]
        {
            new QualityOption("High",   16, "slow"),
            new QualityOption("Normal", 20, "medium"),
            new QualityOption("Small",  26, "medium"),
        };
        QualityCombo.ItemsSource = qualities;
        int qualityIndex = Array.FindIndex(qualities, q => q.Crf == _prefs.ExportCrf);
        QualityCombo.SelectedIndex = qualityIndex >= 0 ? qualityIndex : 1;

        var backgrounds = new[]
        {
            new BackgroundOption("Black",   Colors.Black),
            new BackgroundOption("White",   Colors.White),
            new BackgroundOption("Green",   Color.FromRgb(0x00, 0xb1, 0x40)),
            new BackgroundOption("Magenta", Color.FromRgb(0xff, 0x00, 0xff)),
        };
        BackgroundCombo.ItemsSource = backgrounds;
        int bgIndex = Array.FindIndex(backgrounds,
            b => string.Equals(b.Label, _prefs.ExportBackground, StringComparison.OrdinalIgnoreCase));
        BackgroundCombo.SelectedIndex = bgIndex >= 0 ? bgIndex : 0;

        RefreshFormat();
    }

    private ExportTarget? SelectedTarget => StoryboardCombo.SelectedItem as ExportTarget;

    private double Hold => Math.Max(0, (double)(HoldBox.Value ?? 0m));

    private int Fps => FpsCombo.SelectedItem is int fps ? fps : 30;

    private QualityOption Quality =>
        QualityCombo.SelectedItem as QualityOption ?? new QualityOption("Normal", 20, "medium");

    private BackgroundOption BackgroundChoice =>
        BackgroundCombo.SelectedItem as BackgroundOption ?? new BackgroundOption("Black", Colors.Black);

    private VideoExportFormat Format =>
        (FormatCombo.SelectedItem as FormatOption)?.Format ?? VideoExportFormat.Mp4;

    private void Storyboard_Changed(object? sender, SelectionChangedEventArgs e) => RefreshSummary();
    private void Fps_Changed(object? sender, SelectionChangedEventArgs e) => RefreshSummary();
    private void Hold_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => RefreshSummary();

    private void Format_Changed(object? sender, SelectionChangedEventArgs e)
    {
        RefreshFormat();
        if (_suppressEvents) return;

        // keep the output name, swap its extension to match the container
        var current = OutputBox.Text?.Trim();
        if (!string.IsNullOrEmpty(current))
            OutputBox.Text = Path.ChangeExtension(current, Format.Extension());
    }

    /// <summary>an alpha format keeps the title's own transparency, so there is no background to pick</summary>
    private void RefreshFormat()
    {
        bool alpha = Format.HasAlpha();
        BackgroundLabel.Opacity = alpha ? 0.4 : 1;
        BackgroundCombo.IsEnabled = !_busy && !alpha;
        FormatHint.Text = alpha ? "keeps the alpha channel" : "";
    }

    private void RefreshSummary()
    {
        if (_suppressEvents) return;

        var target = SelectedTarget;
        var doc    = _canvas?.Document;

        HoldHint.Text = target is null || target.IsCombined
            ? "seconds the title stays up between the in and out halves"
            : target.IsStill
                ? "seconds of video"
                : "extra seconds held on the final frame";

        if (target is null || doc is null)
        {
            SummaryText.Text = "";
            return;
        }

        double duration = target.DurationFor(Hold);
        int frames = Math.Max(1, (int)Math.Round(duration * Fps));

        SummaryText.Text = string.Format(CultureInfo.InvariantCulture,
            "{0:0.##} s  ·  {1} frames  ·  {2}×{3}",
            duration, frames, (int)Math.Round(doc.Width), (int)Math.Round(doc.Height));
    }

    private void RefreshFfmpeg()
    {
        _ffmpegPath = FfmpegService.Locate(_prefs.FfmpegPath);

        if (_ffmpegPath is not null)
        {
            FfmpegText.Text = _ffmpegPath;
            ToolTip.SetTip(FfmpegText, _ffmpegPath);
            DownloadButton.IsVisible = false;
        }
        else
        {
            FfmpegText.Text = FfmpegService.CanDownload
                ? "not found - locate it, or download a copy"
                : "not found - install ffmpeg, then locate it";
            ToolTip.SetTip(FfmpegText, null);
            DownloadButton.IsVisible = FfmpegService.CanDownload;
        }

        UpdateEnabled();
    }

    private async void Locate_Click(object? sender, RoutedEventArgs e)
    {
        var patterns = FfmpegService.IsWindows ? new[] { "ffmpeg.exe" } : new[] { "ffmpeg", "*" };

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate ffmpeg",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ffmpeg") { Patterns = patterns },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        _prefs.FfmpegPath = path;
        _prefs.Save();
        RefreshFfmpeg();
    }

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        UpdateEnabled();
        Progress.Value = 0;
        StatusText.Text = "Downloading ffmpeg...";

        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusText.Text = $"Downloading ffmpeg...  {p * 100:0}%";
            });

            var path = await FfmpegService.DownloadAsync(progress);
            _prefs.FfmpegPath = path;
            _prefs.Save();
            StatusText.Text = "ffmpeg ready.";
        }
        catch (Exception ex)
        {
            Logger.Error("ffmpeg download failed", ex);
            StatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            Progress.Value = 0;
            _busy = false;
            RefreshFfmpeg();
        }
    }

    private string SuggestedOutputPath()
    {
        var name = _documentPath is not null
            ? Path.GetFileNameWithoutExtension(_documentPath)
            : "title";

        var folder = _documentPath is not null
            ? Path.GetDirectoryName(_documentPath)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        var file = name + "." + Format.Extension();
        return string.IsNullOrEmpty(folder) ? file : Path.Combine(folder!, file);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var current = OutputBox.Text;
        var suggested = string.IsNullOrWhiteSpace(current) ? SuggestedOutputPath() : current!;

        IStorageFolder? startIn = null;
        try
        {
            var dir = Path.GetDirectoryName(suggested);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                startIn = await StorageProvider.TryGetFolderFromPathAsync(dir!);
        }
        catch { /* a bad path just means no start folder */ }

        var extension = Format.Extension();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Video",
            SuggestedFileName = Path.GetFileName(Path.ChangeExtension(suggested, extension)),
            SuggestedStartLocation = startIn,
            FileTypeChoices = new[]
            {
                new FilePickerFileType($"{extension.ToUpperInvariant()} Video") { Patterns = new[] { "*." + extension } }
            },
            DefaultExtension = extension
        });

        var path = file?.TryGetLocalPath();
        if (path is not null) OutputBox.Text = path;
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var target = SelectedTarget;
        if (target is null || _canvas is null || _canvas.Document is null)
        {
            StatusText.Text = "Nothing to export.";
            return;
        }

        if (_ffmpegPath is null)
        {
            StatusText.Text = "ffmpeg is required. Locate an existing copy or download one.";
            return;
        }

        var output = OutputBox.Text?.Trim();
        if (string.IsNullOrEmpty(output))
        {
            StatusText.Text = "Choose an output file.";
            return;
        }

        if (!target.IsStill && !target.Main!.HasAnimations && !(target.Tail?.HasAnimations ?? false))
            StatusText.Text = "This storyboard has no animations - exporting a still frame.";

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(output!));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cannot write there: {ex.Message}";
            return;
        }

        double hold = Hold;
        var options = new VideoExportOptions
        {
            OutputPath = Path.GetFullPath(output!),
            FfmpegPath = _ffmpegPath,
            Format     = Format,
            Segments   = target.BuildSegments(hold),
            Duration   = target.DurationFor(hold),
            Fps        = Fps,
            Crf        = Quality.Crf,
            Preset     = Quality.Preset,
            Background = BackgroundChoice.Color,
        };

        _cts  = new CancellationTokenSource();
        _busy = true;
        UpdateEnabled();
        Progress.Value = 0;

        var reporter = new Progress<double>(p =>
        {
            Progress.Value = p;
            StatusText.Text = $"Rendering...  {p * 100:0}%";
        });

        try
        {
            var result = await VideoExportService.ExportAsync(_canvas, options, reporter, _cts.Token);

            ExportedPath = result.Path;
            StatusText.Text = string.Format(CultureInfo.InvariantCulture,
                "Exported {0} frames in {1:0.#} s  ·  {2}",
                result.FrameCount, result.Elapsed.TotalSeconds, Path.GetFileName(result.Path));

            RememberSettings(hold);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Export cancelled.";
            Progress.Value = 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Video export failed", ex);
            StatusText.Text = $"Export failed: {ex.Message}";
            Progress.Value = 0;
        }
        finally
        {
            _cts?.Dispose();
            _cts  = null;
            _busy = false;
            UpdateEnabled();
        }
    }

    private void RememberSettings(double hold)
    {
        _prefs.ExportFps         = Fps;
        _prefs.ExportHoldSeconds = hold;
        _prefs.ExportCrf         = Quality.Crf;
        _prefs.ExportBackground  = BackgroundChoice.Label;
        _prefs.ExportFormat      = Format.ToString();
        if (_ffmpegPath is not null) _prefs.FfmpegPath = _ffmpegPath;
        _prefs.Save();
    }

    private void UpdateEnabled()
    {
        bool canExport = !_busy && _ffmpegPath is not null && _targets.Count > 0;

        ExportButton.IsEnabled    = canExport;
        StoryboardCombo.IsEnabled = !_busy;
        HoldBox.IsEnabled         = !_busy;
        FpsCombo.IsEnabled        = !_busy;
        QualityCombo.IsEnabled    = !_busy;
        FormatCombo.IsEnabled     = !_busy;
        BackgroundCombo.IsEnabled = !_busy && !Format.HasAlpha();
        OutputBox.IsEnabled       = !_busy;
        LocateButton.IsEnabled    = !_busy;
        DownloadButton.IsEnabled  = !_busy;

        // the one button doubles as Cancel while a render is running, there is nothing else to do in this window until it finishes
        CloseButton.Content = _cts is not null ? "Cancel" : "Close";
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            StatusText.Text = "Cancelling...";
            return;
        }

        if (_busy) return;   // a download is in flight and cannot be interrupted
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // closing mid-render would leave the canvas stuck on a preview frame and ffmpeg holding the output file, so the render is cancelled first and the window stays
        if (!_busy) return;

        e.Cancel = true;
        _cts?.Cancel();
    }

    /// <summary>one row of the storyboard picker, a single storyboard, the in/out pair, or no storyboard at all (the title at rest)</summary>
    private sealed class ExportTarget
    {
        public ExportTarget(
            string label, GtStoryboard? main, GtStoryboard? tail = null, bool usesHold = true)
        {
            Label    = label;
            Main     = main;
            Tail     = tail;
            UsesHold = usesHold;
        }

        public string Label { get; }
        public GtStoryboard? Main { get; }
        public GtStoryboard? Tail { get; }

        public bool IsStill => Main is null;
        public bool IsCombined => Tail is not null;

        /// <summary>whether the hold sits between the two halves; it does for a transition pair (the template stays live in between) but not for a DataChange pair which vMix runs back-to-back, where the hold becomes a freeze on the final frame instead</summary>
        public bool UsesHold { get; }

        private double Gap(double hold) => IsCombined && UsesHold ? hold : 0;

        /// <summary>timeline layout for the render; the transition pair puts the out half after the in half plus the hold, anything else runs from zero and the hold becomes a freeze on the last frame which the segment list gets for free by simply running longer</summary>
        public List<GtTimelineSegment> BuildSegments(double hold) =>
            IsStill    ? new List<GtTimelineSegment>() :
            IsCombined ? new List<GtTimelineSegment> { new(Main!), new(Tail!, Main!.Duration + Gap(hold)) }
                       : new List<GtTimelineSegment> { new(Main!) };

        public double DurationFor(double hold) =>
            IsStill    ? hold :
            IsCombined ? Main!.Duration + Gap(hold) + Tail!.Duration + (UsesHold ? 0 : hold)
                       : Main!.Duration + hold;

        public override string ToString() => Label;
    }

    private sealed class QualityOption
    {
        public QualityOption(string label, int crf, string preset)
        {
            Label  = label;
            Crf    = crf;
            Preset = preset;
        }

        public string Label { get; }
        public int Crf { get; }
        public string Preset { get; }

        public override string ToString() => Label;
    }

    private sealed class FormatOption
    {
        public FormatOption(string label, VideoExportFormat format)
        {
            Label  = label;
            Format = format;
        }

        public string Label { get; }
        public VideoExportFormat Format { get; }

        public override string ToString() => Label;
    }

    private sealed class BackgroundOption
    {
        public BackgroundOption(string label, Color color)
        {
            Label = label;
            Color = color;
        }

        public string Label { get; }
        public Color Color { get; }

        public override string ToString() => Label;
    }
}
