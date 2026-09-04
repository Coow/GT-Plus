using System;
using System.IO;
using System.Text.Json;

namespace GtPlus.Services;

public class PreferencesService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GtPlus", "preferences.json");

    /// <summary>0.0 = elements fully visible (no dimming), 1.0 = fully hidden; default 0.70 means outside-canvas elements appear at 30% opacity</summary>
    public double OutsideCanvasTransparency { get; set; } = 0.70;

    /// <summary>opacity applied to outside-canvas elements (= 1 - OutsideCanvasTransparency)</summary>
    public double OutsideCanvasOpacity => 1.0 - OutsideCanvasTransparency;

    /// <summary>0.0 = the selected layer's overflow shows in full, 1.0 = hidden, which is what every unselected layer does anyway since the frame masks its contents; default 0.70</summary>
    public double OutsideLayerTransparency { get; set; } = 0.70;

    /// <summary>opacity applied to the selected layer's overflow (= 1 - OutsideLayerTransparency)</summary>
    public double OutsideLayerOpacity => 1.0 - OutsideLayerTransparency;

    /// <summary>when true, shows the debug info panel in the right sidebar</summary>
    public bool ShowDebugPanel { get; set; } = false;

    /// <summary>when true, opening the timeline refits the canvas if the shrunken viewport no longer holds it, since the panel eats height from the bottom and scrollbars would otherwise appear</summary>
    public bool AutoFitOnTimelineOpen { get; set; } = true;

    // ruler and snapping state, remembered across sessions like Photoshop does
    public bool ShowRulers     { get; set; } = true;
    public bool ShowGuides     { get; set; } = true;
    public bool SnapEnabled    { get; set; } = true;
    public bool SnapToGuides   { get; set; } = true;
    public bool SnapToElements { get; set; } = true;
    public bool SnapToCanvas   { get; set; } = true;

    /// <summary>snap pull distance in screen pixels, scaled by zoom when applied</summary>
    public double SnapDistance { get; set; } = 8;

    /// <summary>document pixels an arrow key moves the selection while Shift is held; a bare arrow key always moves by 1</summary>
    public double NudgeLargeStep { get; set; } = 10;

    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string SkippedUpdateVersion { get; set; } = "";

    /// <summary>remembered ffmpeg binary, empty means "search the usual places"</summary>
    public string FfmpegPath { get; set; } = "";

    public int    ExportFps         { get; set; } = 30;
    /// <summary>seconds the title holds between the in and out halves of an export</summary>
    public double ExportHoldSeconds { get; set; } = 3;
    /// <summary>x264 CRF of the last export, lower is better quality</summary>
    public int    ExportCrf         { get; set; } = 20;
    /// <summary>label of the colour an export composites the title over</summary>
    public string ExportBackground  { get; set; } = "Black";

    // window geometry, saved as Normal-state values so Maximized restores correctly
    public int    WindowX      { get; set; } = -1;
    public int    WindowY      { get; set; } = -1;
    public double WindowWidth  { get; set; } = 1280;
    public double WindowHeight { get; set; } = 780;
    public string WindowState  { get; set; } = "Normal";

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<PrefsData>(json);
            if (data is not null)
            {
                OutsideCanvasTransparency = Math.Clamp(data.OutsideCanvasTransparency, 0.0, 1.0);
                OutsideLayerTransparency  = Math.Clamp(data.OutsideLayerTransparency, 0.0, 1.0);
                ShowDebugPanel            = data.ShowDebugPanel;
                AutoFitOnTimelineOpen     = data.AutoFitOnTimelineOpen;
                ShowRulers                = data.ShowRulers;
                ShowGuides                = data.ShowGuides;
                SnapEnabled               = data.SnapEnabled;
                SnapToGuides              = data.SnapToGuides;
                SnapToElements            = data.SnapToElements;
                SnapToCanvas              = data.SnapToCanvas;
                SnapDistance              = Math.Clamp(data.SnapDistance, 1, 64);
                NudgeLargeStep            = Math.Clamp(data.NudgeLargeStep, 1, 1000);
                CheckUpdatesOnStartup = data.CheckUpdatesOnStartup;
                SkippedUpdateVersion  = data.SkippedUpdateVersion ?? "";
                FfmpegPath        = data.FfmpegPath ?? "";
                ExportFps         = data.ExportFps > 0 ? data.ExportFps : 30;
                ExportHoldSeconds = Math.Clamp(data.ExportHoldSeconds, 0, 3600);
                ExportCrf         = Math.Clamp(data.ExportCrf, 0, 51);
                ExportBackground  = data.ExportBackground ?? "Black";
                WindowX      = data.WindowX;
                WindowY      = data.WindowY;
                WindowWidth  = data.WindowWidth  > 0 ? data.WindowWidth  : 1280;
                WindowHeight = data.WindowHeight > 0 ? data.WindowHeight : 780;
                WindowState  = data.WindowState ?? "Normal";
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(new PrefsData
            {
                OutsideCanvasTransparency = OutsideCanvasTransparency,
                OutsideLayerTransparency  = OutsideLayerTransparency,
                ShowDebugPanel            = ShowDebugPanel,
                AutoFitOnTimelineOpen     = AutoFitOnTimelineOpen,
                ShowRulers                = ShowRulers,
                ShowGuides                = ShowGuides,
                SnapEnabled               = SnapEnabled,
                SnapToGuides              = SnapToGuides,
                SnapToElements            = SnapToElements,
                SnapToCanvas              = SnapToCanvas,
                SnapDistance              = SnapDistance,
                NudgeLargeStep            = NudgeLargeStep,
                CheckUpdatesOnStartup = CheckUpdatesOnStartup,
                SkippedUpdateVersion  = SkippedUpdateVersion,
                FfmpegPath        = FfmpegPath,
                ExportFps         = ExportFps,
                ExportHoldSeconds = ExportHoldSeconds,
                ExportCrf         = ExportCrf,
                ExportBackground  = ExportBackground,
                WindowX      = WindowX,
                WindowY      = WindowY,
                WindowWidth  = WindowWidth,
                WindowHeight = WindowHeight,
                WindowState  = WindowState,
            });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    private class PrefsData
    {
        public double OutsideCanvasTransparency { get; set; } = 0.70;
        public double OutsideLayerTransparency  { get; set; } = 0.70;
        public bool   ShowDebugPanel            { get; set; } = false;
        public bool   AutoFitOnTimelineOpen     { get; set; } = true;
        public bool   ShowRulers                { get; set; } = true;
        public bool   ShowGuides                { get; set; } = true;
        public bool   SnapEnabled               { get; set; } = true;
        public bool   SnapToGuides              { get; set; } = true;
        public bool   SnapToElements            { get; set; } = true;
        public bool   SnapToCanvas              { get; set; } = true;
        public double SnapDistance              { get; set; } = 8;
        public double NudgeLargeStep            { get; set; } = 10;
        public bool   CheckUpdatesOnStartup      { get; set; } = true;
        public string SkippedUpdateVersion       { get; set; } = "";
        public string FfmpegPath                { get; set; } = "";
        public int    ExportFps                 { get; set; } = 30;
        public double ExportHoldSeconds         { get; set; } = 3;
        public int    ExportCrf                 { get; set; } = 20;
        public string ExportBackground          { get; set; } = "Black";
        public int    WindowX      { get; set; } = -1;
        public int    WindowY      { get; set; } = -1;
        public double WindowWidth  { get; set; } = 1280;
        public double WindowHeight { get; set; } = 780;
        public string WindowState  { get; set; } = "Normal";
    }
}
