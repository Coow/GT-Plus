using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GtPlus.Controls;
using GtPlus.Models;
using GtPlus.Services;
using Logger = GtPlus.Services.Logger;

namespace GtPlus.Views;

public partial class MainWindow : Window
{
    private readonly GtZipReader _reader = new();
    private readonly GtZipWriter _writer = new();
    private readonly RecentFilesService _recent = new();
    private readonly PreferencesService _prefs = new();
    private readonly HistoryService _history = new();

    private string? _currentPath;
    private GtAssetLibrary _currentAssets = new();

    private double _zoom = 1.0;
    private bool _suppressSliderEvent;

    private int _savedCursor = -1;
    private bool _forceClose;
    private bool IsDirty => _history.Cursor != _savedCursor;
    private void MarkClean() => _savedCursor = _history.Cursor;

    // pan state
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;

    // preferences window
    private PreferencesWindow? _prefsWindow;
    private AboutWindow? _aboutWindow;

    // data source verifier window
    private DataSourceVerifierWindow? _dataVerifierWindow;

    public MainWindow()
    {
        InitializeComponent();

        _prefs.Load();
        GtCanvas.OutsideCanvasOpacity = _prefs.OutsideCanvasOpacity;
        GtCanvas.OutsideLayerOpacity  = _prefs.OutsideLayerOpacity;
        DebugPanel.IsVisible = _prefs.ShowDebugPanel;

        LayersPanel.Canvas  = GtCanvas;
        LayersPanel.History = _history;
        LayersPanel.DocumentChanged += (_, _) =>
        {
            TimelinePanel.Populate(GtCanvas.Document);
            RefreshPropertiesPanel();
            UpdateDebugPanel();
        };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        GtCanvas.SelectionChanged += OnSelectionChanged;
        GtCanvas.AutoSizeApplied  += (_, _) => PropertiesPanel.RefreshDimensions();

        PropertiesPanel.ElementChanged += (_, _) => { GtCanvas.InvalidateVisual(); UpdateDebugPanel(); };
        PropertiesPanel.ImageSourceBrowseRequested += async (_, img) => await ReplaceImageSourceAsync(img);

        // the ticker transport drives the canvas's own ticker clock, independent of the timeline: a storyboard preview takes the clock over while it is applied
        PropertiesPanel.TickerPlayPauseRequested += (_, _) =>
        {
            GtCanvas.ToggleTickerPlayback();
            PropertiesPanel.SetTickerPlaying(GtCanvas.TickerPlaying);
        };
        PropertiesPanel.TickerStopRequested += (_, _) =>
        {
            GtCanvas.StopTicker();
            PropertiesPanel.SetTickerPlaying(false);
        };

        GtCanvas.History = _history;
        GtCanvas.DrawCompleted += OnCanvasDrawCompleted;

        TimelinePanel.History = _history;
        TimelinePanel.Canvas  = GtCanvas;
        TimelinePanel.SelectedElementProvider = () => GtCanvas.SelectedElements.FirstOrDefault();
        // a selected layer is the animation target in its own right, GT animates layers and elements through the same Object attribute
        TimelinePanel.SelectedObjectNameProvider = () =>
            GtCanvas.SelectedLayer?.Name ?? GtCanvas.SelectedElements.FirstOrDefault()?.Name;
        TimelinePanel.PreviewTimeChanged    += OnTimelinePreviewChanged;
        TimelinePanel.PreviewEnabledChanged += OnTimelinePreviewEnabledChanged;
        TimelinePanel.CloseRequested     += () => SetTimelineVisible(false);

        // Delete acts on whichever surface was last clicked, so the key means "delete what I am working on" rather than always the canvas; tunnelled since the timeline's own handlers mark pointer presses handled and a bubbling handler would never see them
        TimelinePanel.AddHandler(PointerPressedEvent, (_, _) => _timelineIsActiveSurface = true,
                                 RoutingStrategies.Tunnel);
        CanvasScrollViewer.AddHandler(PointerPressedEvent, (_, _) => _timelineIsActiveSurface = false,
                                      RoutingStrategies.Tunnel);
        TimelinePanel.StoryboardEdited   += () => GtCanvas.InvalidateVisual();

        HistoryPanel.History = _history;
        PropertiesPanel.History = _history;
        _history.Changed += OnHistoryChanged;

        RebuildFileMenu();

        // rulers drive guide creation, so they need the canvas they draw against
        TopRuler.Canvas  = GtCanvas;
        LeftRuler.Canvas = GtCanvas;
        GtCanvas.GuidesChanged += (_, _) => { UpdateRulers(); UpdateDebugPanel(); };
        PropertiesPanel.AlignRequested += (_, args) => DoAlign(args.Align, args.Target);
        GtCanvas.GuideEditRequested    += (_, guide) => _ = EditGuide(guide);

        // ruler offsets follow the viewport, which moves on scroll, zoom and relayout
        CanvasScrollViewer.ScrollChanged += (_, _) => UpdateRulers();
        CanvasScrollViewer.LayoutUpdated += (_, _) => UpdateRulers();
        GtCanvas.PointerMoved  += OnCanvasPointerMovedForRulers;
        GtCanvas.PointerExited += (_, _) => SetRulerPointer(null);

        ApplyRulerPreferences();

        // tunnel so we intercept before ScrollViewer's own handlers run
        CanvasScrollViewer.AddHandler(PointerWheelChangedEvent,  OnScrollViewerWheel,    RoutingStrategies.Tunnel);
        CanvasScrollViewer.AddHandler(PointerPressedEvent,        OnScrollViewerPressed,  RoutingStrategies.Tunnel);
        CanvasScrollViewer.AddHandler(PointerMovedEvent,          OnScrollViewerMoved,    RoutingStrategies.Tunnel);
        CanvasScrollViewer.AddHandler(PointerReleasedEvent,       OnScrollViewerReleased, RoutingStrategies.Tunnel);

        // clicking anywhere outside a focused text box drops its focus, so keystrokes (tool shortcuts, delete, arrows) don't land in the box by accident
        AddHandler(PointerPressedEvent, OnWindowPressedForFocus, RoutingStrategies.Tunnel);
    }

    /// <summary>clears keyboard focus when the press lands outside the focused text box</summary>
    private void OnWindowPressedForFocus(object? sender, PointerPressedEventArgs e)
    {
        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
        if (focusManager?.GetFocusedElement() is not TextBox focused) return;

        // treat a templated host (NumericUpDown, AutoCompleteBox, ...) as part of the box
        var owner = focused.TemplatedParent as Visual ?? focused;

        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, owner) || ReferenceEquals(v, focused)) return;

        focusManager.Focus(null);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestoreWindowGeometry();
        FitToWindow();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        SaveWindowGeometry();

        if (_forceClose || !IsDirty) return;
        e.Cancel = true;
        _ = ConfirmAndClose();
    }

    private async System.Threading.Tasks.Task ConfirmAndClose()
    {
        if (await ConfirmDiscardChanges())
        {
            _forceClose = true;
            Close();
        }
    }

    private async System.Threading.Tasks.Task<bool> ConfirmDiscardChanges()
    {
        if (!IsDirty) return true;

        var dialog = new Window
        {
            Title = "Unsaved Changes",
            Width = 380,
            Height = 155,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var discardButton = new Button { Content = "Discard Changes", Margin = new Thickness(8, 0, 0, 0) };
        var cancelButton  = new Button { Content = "Cancel" };

        discardButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click  += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = "You have unsaved changes. Discard them and continue?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20)
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancelButton, discardButton }
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private void RestoreWindowGeometry()
    {
        // apply saved size/position only if we have valid saved values
        if (_prefs.WindowX < 0 && _prefs.WindowY < 0) return;

        if (_prefs.WindowWidth >= 640 && _prefs.WindowHeight >= 400)
        {
            ClientSize = new Size(_prefs.WindowWidth, _prefs.WindowHeight);
            Position   = new PixelPoint(_prefs.WindowX, _prefs.WindowY);
        }

        if (Enum.TryParse<WindowState>(_prefs.WindowState, out var ws) && ws != WindowState.Minimized)
            WindowState = ws;
    }

    private void SaveWindowGeometry()
    {
        // always persist the state, only persist size/pos when Normal so we keep sensible restore values even if closing while Maximized
        _prefs.WindowState = WindowState.ToString();
        if (WindowState == WindowState.Normal)
        {
            _prefs.WindowX      = Position.X;
            _prefs.WindowY      = Position.Y;
            _prefs.WindowWidth  = ClientSize.Width;
            _prefs.WindowHeight = ClientSize.Height;
        }
        _prefs.Save();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        UpdateDebugPanel();
        PropertiesPanel.Populate(GtCanvas.SelectedElements, SelectionLayer());
        UpdateRulerSelection();
        // GT has no layerless composition, so the last layer can never be deleted
        DeleteLayerMenuItem.IsEnabled = (GtCanvas.Document?.Layers.Count ?? 0) > 1;

        bool hasElements = GtCanvas.SelectedElements.Count > 0;
        DuplicateElementMenuItem.IsEnabled = hasElements;
        DeleteElementMenuItem.IsEnabled    = hasElements;
    }

    private bool _draggingRulerOrigin;

    /// <summary>pushes the saved ruler/snap preferences into the canvas and the View menu</summary>
    private void ApplyRulerPreferences()
    {
        GtCanvas.ShowGuides     = _prefs.ShowGuides;
        GtCanvas.SnapEnabled    = _prefs.SnapEnabled;
        GtCanvas.SnapToGuides   = _prefs.SnapToGuides;
        GtCanvas.SnapToElements = _prefs.SnapToElements;
        GtCanvas.SnapToCanvas   = _prefs.SnapToCanvas;
        GtCanvas.SnapDistance   = _prefs.SnapDistance;

        RulersMenuItem.IsChecked        = _prefs.ShowRulers;
        ShowGuidesMenuItem.IsChecked    = _prefs.ShowGuides;
        SnapMenuItem.IsChecked          = _prefs.SnapEnabled;
        SnapToggleButton.IsChecked      = _prefs.SnapEnabled;
        SnapGuidesMenuItem.IsChecked    = _prefs.SnapToGuides;
        SnapElementsMenuItem.IsChecked  = _prefs.SnapToElements;
        SnapCanvasMenuItem.IsChecked    = _prefs.SnapToCanvas;

        SetRulersVisible(_prefs.ShowRulers);
    }

    private void SetRulersVisible(bool visible)
    {
        _prefs.ShowRulers        = visible;
        RulersMenuItem.IsChecked = visible;
        TopRuler.IsVisible       = visible;
        LeftRuler.IsVisible      = visible;
        RulerCorner.IsVisible    = visible;
        _prefs.Save();
        UpdateRulers();
    }

    /// <summary>re-aligns both rulers with the canvas; the offset is the canvas origin expressed in each ruler's own coordinates, which is what makes the ticks track scrolling and zoom</summary>
    private void UpdateRulers()
    {
        if (!TopRuler.IsVisible && !LeftRuler.IsVisible) return;

        var doc = GtCanvas.Document;
        TopRuler.Zoom       = _zoom;
        LeftRuler.Zoom      = _zoom;
        TopRuler.DocLength  = doc?.Width  ?? 1920;
        LeftRuler.DocLength = doc?.Height ?? 1080;
        TopRuler.RulerOrigin  = doc?.RulerOrigin.X ?? 0;
        LeftRuler.RulerOrigin = doc?.RulerOrigin.Y ?? 0;

        if (GtCanvas.TranslatePoint(new Point(0, 0), TopRuler) is { } topOrigin)
            TopRuler.ContentOffset = topOrigin.X;
        if (GtCanvas.TranslatePoint(new Point(0, 0), LeftRuler) is { } leftOrigin)
            LeftRuler.ContentOffset = leftOrigin.Y;

        LockGuidesMenuItem.IsChecked = doc?.GuidesLocked ?? false;
    }

    private void UpdateRulerSelection()
    {
        var bounds = GtCanvas.SelectionBounds;
        if (GtCanvas.SelectedElements.Count == 0 || bounds.Width <= 0 && bounds.Height <= 0)
        {
            TopRuler.SelectionStart  = null;
            TopRuler.SelectionEnd    = null;
            LeftRuler.SelectionStart = null;
            LeftRuler.SelectionEnd   = null;
            return;
        }

        TopRuler.SelectionStart  = bounds.Left;
        TopRuler.SelectionEnd    = bounds.Right;
        LeftRuler.SelectionStart = bounds.Top;
        LeftRuler.SelectionEnd   = bounds.Bottom;
    }

    private void OnCanvasPointerMovedForRulers(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(GtCanvas);
        SetRulerPointer(new Point(p.X / _zoom, p.Y / _zoom));
    }

    private void SetRulerPointer(Point? docPoint)
    {
        TopRuler.PointerPosition  = docPoint?.X;
        LeftRuler.PointerPosition = docPoint?.Y;

        // readout is measured from the ruler origin, so it matches the tick labels
        var origin = GtCanvas.Document?.RulerOrigin ?? GtPoint.Zero;
        CursorPosLabel.Text = docPoint is { } p
            ? $"X {p.X - origin.X,8:F1}   Y {p.Y - origin.Y,8:F1}"
            : "";
    }

    /// <summary>opens the precise-position editor for a double-clicked guide</summary>
    private async System.Threading.Tasks.Task EditGuide(GtGuide guide)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var dialog = new GuideEditWindow(guide, doc.Width, doc.Height);
        var result = await dialog.ShowDialog<GuideEditResult>(this);

        if (result == GuideEditResult.Apply)       GtCanvas.SetGuidePosition(guide, dialog.GuidePosition);
        else if (result == GuideEditResult.Delete) GtCanvas.RemoveGuide(guide);
    }

    private async System.Threading.Tasks.Task DoExportGuides()
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        if (doc.Guides.Count == 0)
        {
            StatusText.Text = "No guides to export";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Guides",
            SuggestedFileName = (_currentPath is not null
                ? Path.GetFileNameWithoutExtension(_currentPath)
                : "guides") + "." + GuidesPart.FileExtension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("GT++ Guides") { Patterns = new[] { "*." + GuidesPart.FileExtension } }
            },
            DefaultExtension = GuidesPart.FileExtension
        });

        var path = file?.TryGetLocalPath();
        if (path is null) return;

        try
        {
            GuidesPart.WriteFile(path, doc);
            StatusText.Text = $"Exported {doc.Guides.Count} guides  ·  {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to export guides", ex);
            StatusText.Text = $"Guide export error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task DoImportGuides()
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Guides",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GT++ Guides") { Patterns = new[] { "*." + GuidesPart.FileExtension } },
                new FilePickerFileType("All Files")   { Patterns = new[] { "*.*" } }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null) return;

        GuideSet set;
        try
        {
            set = GuidesPart.ReadFile(path);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to import guides", ex);
            StatusText.Text = $"Guide import error: {ex.Message}";
            return;
        }

        // guides authored on a different canvas can be kept at their pixel positions or rescaled proportionally, only the user knows which they meant
        var origin = set.RulerOrigin;
        bool differentCanvas = set.SourceWidth  > 0 && set.SourceHeight > 0 &&
                               (Math.Abs(set.SourceWidth  - doc.Width)  > 0.5 ||
                                Math.Abs(set.SourceHeight - doc.Height) > 0.5);

        if (differentCanvas && await ConfirmScaleGuides(set, doc))
        {
            var sx = doc.Width  / set.SourceWidth;
            var sy = doc.Height / set.SourceHeight;
            foreach (var guide in set.Guides)
                guide.Position *= guide.Orientation == GtGuideOrientation.Vertical ? sx : sy;
            origin = new GtPoint(origin.X * sx, origin.Y * sy);
        }

        GtCanvas.ReplaceGuides(set.Guides, origin);
        ShowGuidesMenuItem.IsChecked = true;
        UpdateRulers();
        StatusText.Text = $"Imported {set.Guides.Count} guides  ·  {Path.GetFileName(path)}";
    }

    private async System.Threading.Tasks.Task<bool> ConfirmScaleGuides(GuideSet set, GtDocument doc)
    {
        var dialog = new Window
        {
            Title = "Import Guides",
            Width = 400,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var scaleButton = new Button { Content = "Scale to Fit", Margin = new Thickness(8, 0, 0, 0) };
        var keepButton  = new Button { Content = "Keep Pixel Positions" };

        scaleButton.Click += (_, _) => dialog.Close(true);
        keepButton.Click  += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = $"These guides were made for a {set.SourceWidth:0.##} × {set.SourceHeight:0.##} " +
                           $"canvas. This document is {doc.Width:0.##} × {doc.Height:0.##}.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20)
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { keepButton, scaleButton }
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private void RulerCorner_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (GtCanvas.Document is null) return;
        if (!e.GetCurrentPoint(RulerCorner).Properties.IsLeftButtonPressed) return;

        _draggingRulerOrigin = true;
        e.Pointer.Capture(RulerCorner);
        e.Handled = true;
    }

    private void RulerCorner_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingRulerOrigin) return;
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var p = e.GetPosition(GtCanvas);
        doc.RulerOrigin = new GtPoint(p.X / _zoom, p.Y / _zoom);
        UpdateRulers();
        e.Handled = true;
    }

    private void RulerCorner_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_draggingRulerOrigin) return;
        _draggingRulerOrigin = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void RulerCorner_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;
        doc.RulerOrigin = GtPoint.Zero;
        _draggingRulerOrigin = false;
        UpdateRulers();
        e.Handled = true;
    }

    private void ToggleRulers_Click(object? sender, RoutedEventArgs e) =>
        SetRulersVisible(RulersMenuItem.IsChecked);

    private void ToggleShowGuides_Click(object? sender, RoutedEventArgs e)
    {
        GtCanvas.ShowGuides = ShowGuidesMenuItem.IsChecked;
        _prefs.ShowGuides   = ShowGuidesMenuItem.IsChecked;
        _prefs.Save();
    }

    private void ToggleSnap_Click(object? sender, RoutedEventArgs e) =>
        SetSnapEnabled(SnapMenuItem.IsChecked);

    private void SnapToggleButton_Click(object? sender, RoutedEventArgs e) =>
        SetSnapEnabled(SnapToggleButton.IsChecked == true);

    /// <summary>single owner of the snap state: canvas, both toggles and the saved preference</summary>
    private void SetSnapEnabled(bool enabled)
    {
        GtCanvas.SnapEnabled       = enabled;
        SnapMenuItem.IsChecked     = enabled;
        SnapToggleButton.IsChecked = enabled;
        _prefs.SnapEnabled         = enabled;
        _prefs.Save();
    }

    private void SnapTarget_Click(object? sender, RoutedEventArgs e)
    {
        GtCanvas.SnapToGuides   = SnapGuidesMenuItem.IsChecked;
        GtCanvas.SnapToElements = SnapElementsMenuItem.IsChecked;
        GtCanvas.SnapToCanvas   = SnapCanvasMenuItem.IsChecked;
        _prefs.SnapToGuides     = SnapGuidesMenuItem.IsChecked;
        _prefs.SnapToElements   = SnapElementsMenuItem.IsChecked;
        _prefs.SnapToCanvas     = SnapCanvasMenuItem.IsChecked;
        _prefs.Save();
    }

    private void ToggleLockGuides_Click(object? sender, RoutedEventArgs e)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var before = doc.GuidesLocked;
        var after  = LockGuidesMenuItem.IsChecked;
        if (before == after) return;

        doc.GuidesLocked = after;
        _history.Push(new PropertyChangeAction(
            after ? "Lock guides" : "Unlock guides",
            () => { doc.GuidesLocked = before; LockGuidesMenuItem.IsChecked = before; },
            () => { doc.GuidesLocked = after;  LockGuidesMenuItem.IsChecked = after;  }));
        GtCanvas.InvalidateVisual();
    }

    private void NewHorizontalGuide_Click(object? sender, RoutedEventArgs e) =>
        AddCentreGuide(GtGuideOrientation.Horizontal);

    private void NewVerticalGuide_Click(object? sender, RoutedEventArgs e) =>
        AddCentreGuide(GtGuideOrientation.Vertical);

    private void AddCentreGuide(GtGuideOrientation orientation)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        GtCanvas.ShowGuides          = true;
        ShowGuidesMenuItem.IsChecked = true;
        GtCanvas.AddGuide(orientation,
            orientation == GtGuideOrientation.Vertical ? doc.Width / 2 : doc.Height / 2);
    }

    private void ClearGuides_Click(object? sender, RoutedEventArgs e) => GtCanvas.ClearGuides();

    private void AlignLeft_Click(object? sender, RoutedEventArgs e)       => DoAlign(GtAlign.Left);
    private void AlignCenterH_Click(object? sender, RoutedEventArgs e)    => DoAlign(GtAlign.CenterHorizontal);
    private void AlignRight_Click(object? sender, RoutedEventArgs e)      => DoAlign(GtAlign.Right);
    private void AlignTop_Click(object? sender, RoutedEventArgs e)        => DoAlign(GtAlign.Top);
    private void AlignMiddle_Click(object? sender, RoutedEventArgs e)     => DoAlign(GtAlign.Middle);
    private void AlignBottom_Click(object? sender, RoutedEventArgs e)     => DoAlign(GtAlign.Bottom);
    private void AlignCenterBoth_Click(object? sender, RoutedEventArgs e) => DoAlign(GtAlign.CenterBoth);

    private void DoAlign(GtAlign align, GtAlignTarget target = GtAlignTarget.Auto)
    {
        var doc = GtCanvas.Document;
        if (doc is null || GtCanvas.SelectedElements.Count == 0) return;

        var action = AlignmentService.Align(doc, GtCanvas.SelectedElements.ToList(), align, target);
        if (action is null) return;

        _history.Push(action);
        GtCanvas.InvalidateVisual();
        RefreshPropertiesPanel();
        UpdateDebugPanel();
        UpdateRulerSelection();
    }

    private void SelectToolButton_Click(object? sender, RoutedEventArgs e)   => SetTool(CanvasTool.Select);
    private void EditToolButton_Click(object? sender, RoutedEventArgs e)      => SetTool(CanvasTool.Edit);
    private void TextBoxToolButton_Click(object? sender, RoutedEventArgs e)   => SetTool(CanvasTool.TextBox);
    private void RectangleToolButton_Click(object? sender, RoutedEventArgs e) => SetTool(CanvasTool.Rectangle);
    private void TickerToolButton_Click(object? sender, RoutedEventArgs e)    => SetTool(CanvasTool.Ticker);
    private void ImageToolButton_Click(object? sender, RoutedEventArgs e)     => _ = InsertImageAsync();
    private void PixelGridLockButton_Click(object? sender, RoutedEventArgs e) =>
        GtCanvas.SnapToPixelGrid = PixelGridLockButton.IsChecked == true;

    private void SetTool(CanvasTool tool)
    {
        GtCanvas.ActiveTool = tool;
        SelectToolButton.IsChecked    = tool == CanvasTool.Select;
        EditToolButton.IsChecked      = tool == CanvasTool.Edit;
        TextBoxToolButton.IsChecked   = tool == CanvasTool.TextBox;
        RectangleToolButton.IsChecked = tool == CanvasTool.Rectangle;
        TickerToolButton.IsChecked    = tool == CanvasTool.Ticker;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.S when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                SetTool(CanvasTool.Select);
                e.Handled = true;
                break;
            case Key.E when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                SetTool(CanvasTool.Edit);
                e.Handled = true;
                break;
            case Key.T when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                SetTool(CanvasTool.TextBox);
                e.Handled = true;
                break;
            case Key.R when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                SetTool(CanvasTool.Rectangle);
                e.Handled = true;
                break;
            case Key.K when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                SetTool(CanvasTool.Ticker);
                e.Handled = true;
                break;
            case Key.I when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                _ = InsertImageAsync();
                e.Handled = true;
                break;
            case Key.F2 when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                if (LayersPanel.BeginRenameFocused()) e.Handled = true;
                break;
            case Key.Z when e.KeyModifiers == KeyModifiers.Control:
                DoUndo();
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control && e.Source is not TextBox:
                DoCopy();
                e.Handled = true;
                break;
            case Key.V when e.KeyModifiers == KeyModifiers.Control && e.Source is not TextBox:
                DoPaste();
                e.Handled = true;
                break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control && e.Source is not TextBox:
                DoDuplicateElements();
                e.Handled = true;
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox:
                // falls through to the canvas when the timeline has no clips selected
                if (!_timelineIsActiveSurface || !TimelinePanel.DeleteSelectedClips())
                    DoDeleteElements();
                e.Handled = true;
                break;
            case Key.Y when e.KeyModifiers == KeyModifiers.Control:
                DoRedo();
                e.Handled = true;
                break;
            case Key.S when e.KeyModifiers == KeyModifiers.Control:
                DoSave();
                e.Handled = true;
                break;
            case Key.S when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                _ = DoSaveAs();
                e.Handled = true;
                break;
            case Key.N when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                DoNewLayer();
                e.Handled = true;
                break;
            case Key.D when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                DoDuplicateLayer();
                e.Handled = true;
                break;
            case Key.T when e.KeyModifiers == KeyModifiers.Control:
                SetTimelineVisible(!TimelinePanel.IsVisible);
                e.Handled = true;
                break;
            case Key.R when e.KeyModifiers == KeyModifiers.Control:
                SetRulersVisible(!TopRuler.IsVisible);
                e.Handled = true;
                break;
            case Key.OemSemicolon when e.KeyModifiers == KeyModifiers.Control:
                ShowGuidesMenuItem.IsChecked = !ShowGuidesMenuItem.IsChecked;
                ToggleShowGuides_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.OemSemicolon when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                SetSnapEnabled(!GtCanvas.SnapEnabled);
                e.Handled = true;
                break;
            case Key.OemSemicolon when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt):
                LockGuidesMenuItem.IsChecked = !LockGuidesMenuItem.IsChecked;
                ToggleLockGuides_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            // Enter in a single-line box commits by dropping focus (boxes commit on LostFocus)
            case Key.Enter when e.Source is TextBox { AcceptsReturn: false }:
            case Key.Escape when e.Source is TextBox:
                TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
                e.Handled = true;
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                GtCanvas.ClearSelection();
                e.Handled = true;
                break;
            case Key.Space when e.KeyModifiers == KeyModifiers.None
                             && e.Source is not TextBox && TimelinePanel.IsVisible:
                TimelinePanel.TogglePlay();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // releasing the arrow key closes the coalesced nudge run
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            GtCanvas.CommitNudge();
    }

    /// <summary>edit-mode arrow nudge: 1px, or the preference step with Shift; handled on the tunnel so the canvas scroll viewer cannot scroll the arrow key away first, key repeat gives the "repeats when held" part, and the run lands in history as one move on key up</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        if (GtCanvas.ActiveTool != CanvasTool.Edit) return;
        if (e.KeyModifiers != KeyModifiers.None && e.KeyModifiers != KeyModifiers.Shift) return;
        if (IsTextInputSource(e.Source)) return;

        if (DoNudge(e.Key, e.KeyModifiers == KeyModifiers.Shift)) e.Handled = true;
    }

    /// <summary>true when the key went to a text entry, which owns the arrow keys itself</summary>
    private static bool IsTextInputSource(object? source) =>
        source is TextBox or NumericUpDown or AutoCompleteBox or ComboBox;

    private bool DoNudge(Key key, bool large)
    {
        double step = large ? Math.Max(1, _prefs.NudgeLargeStep) : 1;
        (double dx, double dy) = key switch
        {
            Key.Left  => (-step, 0d),
            Key.Right => ( step, 0d),
            Key.Up    => (0d, -step),
            Key.Down  => (0d,  step),
            _         => (0d, 0d),
        };

        if (!GtCanvas.NudgeSelection(dx, dy)) return false;
        StatusText.Text = $"Moved {GtCanvas.SelectedElements.Count} element(s) by {dx:0.##}, {dy:0.##}";
        return true;
    }

    private void NewLayer_Click(object? sender, RoutedEventArgs e)       => DoNewLayer();
    private void DuplicateElement_Click(object? sender, RoutedEventArgs e) => DoDuplicateElements();
    private void DeleteElement_Click(object? sender, RoutedEventArgs e)    => DoDeleteElements();

    private void DoDuplicateElements()
    {
        if (!LayersPanel.DuplicateElements()) { StatusText.Text = "No element selected"; return; }
        StatusText.Text = $"Duplicated {GtCanvas.SelectedElements.Count} element(s)";
    }

    private void DoDeleteElements()
    {
        // counted before the delete, the selection is cleared by it
        int count = GtCanvas.SelectedElements.Count;
        StatusText.Text = LayersPanel.DeleteElements()
            ? $"Deleted {count} element(s)"
            : "No element to delete";
    }

    private void DuplicateLayer_Click(object? sender, RoutedEventArgs e) => DoDuplicateLayer();
    private void DeleteLayer_Click(object? sender, RoutedEventArgs e)    => DoDeleteLayer();

    private void DoNewLayer()
    {
        if (!LayersPanel.AddLayer()) { StatusText.Text = "No document open"; return; }
        StatusText.Text = $"Added layer {GtCanvas.SelectedLayer?.Name}";
    }

    private void DoDuplicateLayer()
    {
        if (!LayersPanel.DuplicateLayer()) { StatusText.Text = "No layer to duplicate"; return; }
        StatusText.Text = $"Duplicated layer as {GtCanvas.SelectedLayer?.Name}";
    }

    private void DoDeleteLayer()
    {
        StatusText.Text = LayersPanel.DeleteLayer()
            ? "Layer deleted"
            : "Cannot delete the only layer";
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => DoUndo();
    private void Redo_Click(object? sender, RoutedEventArgs e) => DoRedo();

    private void DoUndo()
    {
        _history.Undo();
        GtCanvas.InvalidateVisual();
        RefreshPropertiesPanel();
        UpdateDebugPanel();
    }

    private void DoRedo()
    {
        _history.Redo();
        GtCanvas.InvalidateVisual();
        RefreshPropertiesPanel();
        UpdateDebugPanel();
    }

    private void RefreshPropertiesPanel()
    {
        PropertiesPanel.Populate(GtCanvas.SelectedElements, SelectionLayer());
        // the transport reflects the canvas clock, which keeps running across selection changes
        PropertiesPanel.SetTickerPlaying(GtCanvas.TickerPlaying);
    }

    /// <summary>layer owning the primary selected element; the properties bar needs it for the mask dropdown since vMix can only mask an element against a sibling in its own layer</summary>
    private GtLayer? SelectionLayer()
    {
        var doc   = GtCanvas.Document;
        var first = GtCanvas.SelectedElements.FirstOrDefault();
        return doc is null || first is null ? null : FindLayer(doc, first);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        UndoMenuItem.IsEnabled = _history.CanUndo;
        RedoMenuItem.IsEnabled = _history.CanRedo;
    }

    private void LayersTab_Click(object? sender, RoutedEventArgs e)
    {
        LayersPanel.IsVisible  = true;
        HistoryPanel.IsVisible = false;
        LayersTabButton.IsChecked  = true;
        HistoryTabButton.IsChecked = false;
    }

    private void HistoryTab_Click(object? sender, RoutedEventArgs e)
    {
        LayersPanel.IsVisible  = false;
        HistoryPanel.IsVisible = true;
        LayersTabButton.IsChecked  = false;
        HistoryTabButton.IsChecked = true;
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.10, 4.0);
        GtCanvas.Zoom = _zoom;

        _suppressSliderEvent = true;
        ZoomSlider.Value = _zoom * 100;
        _suppressSliderEvent = false;

        ZoomLabel.Text = $"{_zoom * 100:F0}%";
        UpdateRulers();
    }

    private void FitToWindow()
    {
        var viewport = CanvasScrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;

        var docW = GtCanvas.Document?.Width  ?? 1920;
        var docH = GtCanvas.Document?.Height ?? 1080;

        // subtract the fixed 24px Margin on each side of the canvas
        const double margin = 48;
        var zoom = Math.Min(
            (viewport.Width  - margin) / docW,
            (viewport.Height - margin) / docH);

        SetZoom(zoom);
    }

    private void ZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvent) return;
        SetZoom(e.NewValue / 100.0);
    }

    private void Fit_Click(object? sender, RoutedEventArgs e) => FitToWindow();

    private void OnScrollViewerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;

        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        var oldZoom = _zoom;
        var mousePos = e.GetPosition(CanvasScrollViewer);
        var oldOffset = CanvasScrollViewer.Offset;

        SetZoom(_zoom * factor);

        // adjust scroll so the doc point under the mouse stays fixed; canvas has a fixed 24px Margin inside the ScrollViewer content
        const double margin = 24;
        var zoomRatio = _zoom / oldZoom;
        CanvasScrollViewer.Offset = new Vector(
            (mousePos.X + oldOffset.X - margin) * zoomRatio + margin - mousePos.X,
            (mousePos.Y + oldOffset.Y - margin) * zoomRatio + margin - mousePos.Y);
    }

    private void OnScrollViewerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(CanvasScrollViewer).Properties;

        // left-click outside the canvas deselects everything
        if (props.IsLeftButtonPressed && e.Source is not Controls.GtCanvasControl)
            GtCanvas.ClearSelection();

        if (!props.IsMiddleButtonPressed) return;

        _isPanning = true;
        _panStartPointer = e.GetPosition(CanvasScrollViewer);
        _panStartOffset  = CanvasScrollViewer.Offset;
        CanvasScrollViewer.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(CanvasScrollViewer);
        e.Handled = true;
    }

    private void OnScrollViewerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;

        var delta = e.GetPosition(CanvasScrollViewer) - _panStartPointer;
        CanvasScrollViewer.Offset = new Vector(
            _panStartOffset.X - delta.X,
            _panStartOffset.Y - delta.Y);
        e.Handled = true;
    }

    private void OnScrollViewerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning || e.InitialPressMouseButton != MouseButton.Middle) return;

        _isPanning = false;
        CanvasScrollViewer.Cursor = Cursor.Default;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Preferences_Click(object? sender, RoutedEventArgs e)
    {
        if (_prefsWindow is not null)
        {
            _prefsWindow.Activate();
            return;
        }

        _prefsWindow = new PreferencesWindow(_prefs, GtCanvas, isOn =>
        {
            DebugPanel.IsVisible = isOn;
            UpdateDebugPanel();
        });
        _prefsWindow.Closed += (_, _) => _prefsWindow = null;
        _prefsWindow.Show(this);
    }

    private void About_Click(object? sender, RoutedEventArgs e)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show(this);
    }

    private void DataVerifier_Click(object? sender, RoutedEventArgs e)
    {
        if (_dataVerifierWindow is not null)
        {
            _dataVerifierWindow.Activate();
            return;
        }

        _dataVerifierWindow = new DataSourceVerifierWindow(GtCanvas);
        _dataVerifierWindow.Closed += (_, _) => _dataVerifierWindow = null;
        _dataVerifierWindow.Show(this);
    }

    private void UpdateDebugPanel()
    {
        if (!_prefs.ShowDebugPanel) return;

        var selected = GtCanvas.SelectedElements;

        if (selected.Count == 0)
        {
            DebugText.Text = "(nothing selected)";
            return;
        }

        if (selected.Count > 1)
        {
            DebugText.Text = $"Multiple elements selected ({selected.Count})";
            return;
        }

        var el    = selected.First();
        var layer = GtCanvas.Document?.Layers.FirstOrDefault(l => l.Elements.Contains(el));
        DebugText.Text = BuildDebugText(el, layer);
    }

    private static string BuildDebugText(GtElement el, GtLayer? layer)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Type:       {el.GetType().Name}");
        sb.AppendLine($"Layer:      {layer?.Name ?? "?"}");
        sb.AppendLine();
        sb.AppendLine($"Name:       {el.Name}");
        sb.AppendLine($"Location:   {el.Location.X:F1}, {el.Location.Y:F1}");
        sb.AppendLine($"Dimensions: {el.Dimensions.Width:F1} x {el.Dimensions.Height:F1}");
        if (el.RotateX != 0 || el.RotateY != 0 || el.RotateZ != 0)
        {
            double r2d = 180.0 / Math.PI;
            sb.AppendLine($"RotateX:    {el.RotateX * r2d:F2}°");
            sb.AppendLine($"RotateY:    {el.RotateY * r2d:F2}°");
            sb.AppendLine($"RotateZ:    {el.RotateZ * r2d:F2}°");
        }
        sb.AppendLine($"Visible:    {el.Visible}");
        sb.AppendLine($"Opacity:    {el.Opacity:F2}");
        sb.AppendLine($"Locked:     {el.Locked}");
        sb.AppendLine($"DataFlags:  {el.DataFlags}");
        if (el.MaskObject is not null)
            sb.AppendLine($"MaskObj:    {el.MaskObject}");
        if (el.Bounding is { IsDefault: false } bounding)
        {
            sb.AppendLine($"Bounding:   {bounding.Object ?? "(none)"}");
            if (bounding.HasPadding)
                sb.AppendLine($"Padding:    {bounding.PaddingLeft:F0},{bounding.PaddingTop:F0}," +
                              $"{bounding.PaddingRight:F0},{bounding.PaddingBottom:F0}");
        }
        if (el.Crop is { IsDefault: false } crop)
        {
            sb.AppendLine($"Crop:       {crop.X0:F3},{crop.Y0:F3} → {crop.X1:F3},{crop.Y1:F3}");
            sb.AppendLine($"Feather:    {crop.FeatherLeft:F0},{crop.FeatherTop:F0}," +
                          $"{crop.FeatherRight:F0},{crop.FeatherBottom:F0}");
        }

        switch (el)
        {
            case GtRectangleElement rect:
                sb.AppendLine();
                AppendBrush(sb, "Fill",   rect.Fill);
                AppendBrush(sb, "Stroke", rect.Stroke);
                sb.AppendLine($"StrokePx:   {rect.StrokeThickness:F1}");
                break;

            case GtEllipseElement ellipse:
                sb.AppendLine();
                AppendBrush(sb, "Fill",   ellipse.Fill);
                AppendBrush(sb, "Stroke", ellipse.Stroke);
                sb.AppendLine($"StrokePx:   {ellipse.StrokeThickness:F1}");
                break;

            case GtTickerElement ticker:
                sb.AppendLine();
                var tickerText = ticker.Text.Length > 40 ? ticker.Text[..40] + "…" : ticker.Text;
                sb.AppendLine($"Template:   {tickerText}");
                sb.AppendLine($"Speed:      {ticker.Speed:F2} px/frame");
                sb.AppendLine($"Direction:  {ticker.Direction}");
                sb.AppendLine($"Type:       {ticker.TickerType}");
                sb.AppendLine($"FontFamily: {ticker.FontFamily}");
                sb.AppendLine($"FontSize:   {ticker.FontSize:F1}");
                AppendBrush(sb, "Fill",   ticker.Fill);
                AppendBrush(sb, "Stroke", ticker.Stroke);
                break;

            case GtTextBlock tb:
                sb.AppendLine();
                var text = tb.Text.Length > 40 ? tb.Text[..40] + "…" : tb.Text;
                sb.AppendLine($"Text:       {text}");
                sb.AppendLine($"FontFamily: {tb.FontFamily}");
                sb.AppendLine($"FontSize:   {tb.FontSize:F1}");
                sb.AppendLine($"FontWeight: {tb.FontWeight}");
                sb.AppendLine($"TextAlign:  {tb.TextAlign}");
                sb.AppendLine($"VAlign:     {tb.VerticalAlign}");
                sb.AppendLine($"LineSpacing:{tb.LineSpacing:F3}");
                AppendBrush(sb, "Fill",   tb.Fill);
                AppendBrush(sb, "Stroke", tb.Stroke);
                sb.AppendLine($"StrokePx:   {tb.StrokeThickness:F1}");
                break;

            case GtImageElement img:
                sb.AppendLine();
                sb.AppendLine($"Source:     {img.BitmapSource ?? "(none)"}");
                sb.AppendLine($"SizeMode:   {img.SizeMode}");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendBrush(StringBuilder sb, string label, GtBrush? brush)
    {
        if (brush is null)
        {
            sb.AppendLine($"{label,-10}  null");
            return;
        }

        switch (brush.Type)
        {
            case GtBrushType.Solid:
                sb.AppendLine($"{label,-10}  Solid #{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}");
                break;

            case GtBrushType.LinearGradient:
            case GtBrushType.RadialGradient:
                var gtype = brush.Type == GtBrushType.LinearGradient ? "LinearGradient" : "RadialGradient";
                sb.AppendLine($"{label,-10}  {gtype}");
                sb.AppendLine($"  Start:    {brush.StartPoint.X:F4}, {brush.StartPoint.Y:F4}");
                sb.AppendLine($"  End:      {brush.EndPoint.X:F4}, {brush.EndPoint.Y:F4}");
                if (brush.Type == GtBrushType.RadialGradient)
                    sb.AppendLine($"  WrapMode: {brush.WrapMode}");
                sb.AppendLine($"  Stops:    {brush.Stops.Count}");
                foreach (var stop in brush.Stops)
                    sb.AppendLine($"    @{stop.Position:F3}  #{stop.Color.A:X2}{stop.Color.R:X2}{stop.Color.G:X2}{stop.Color.B:X2}");
                break;

            case GtBrushType.Bitmap:
                sb.AppendLine($"{label,-10}  Bitmap {brush.BitmapSource ?? "(none)"}");
                break;

            default:
                sb.AppendLine($"{label,-10}  Unknown");
                break;
        }
    }

    private void RebuildFileMenu()
    {
        FileMenu.Items.Clear();

        var newItem = new MenuItem { Header = "_New" };
        var presets = new[]
        {
            ("HD720  (1280×720)",          1280.0, 720.0),
            ("HD1080  (1920×1080)",         1920.0, 1080.0),
            ("VerticalHD1920  (1080×1920)", 1080.0, 1920.0),
            ("UHD2160  (3840×2160)",        3840.0, 2160.0),
            ("UHD8K4320  (7680×4320)",      7680.0, 4320.0),
        };
        foreach (var (label, w, h) in presets)
        {
            var pw = w; var ph = h;
            var sub = new MenuItem { Header = label };
            sub.Click += (_, _) => NewDocument(pw, ph);
            newItem.Items.Add(sub);
        }
        FileMenu.Items.Add(newItem);

        FileMenu.Items.Add(new Separator());

        var open = new MenuItem
        {
            Header = "_Open...",
            InputGesture = new KeyGesture(Key.O, KeyModifiers.Control)
        };
        open.Click += OpenFile_Click;
        FileMenu.Items.Add(open);

        var hasDoc = GtCanvas.Document is not null;

        var save = new MenuItem
        {
            Header = "_Save",
            IsEnabled = _currentPath is not null,
            InputGesture = new KeyGesture(Key.S, KeyModifiers.Control)
        };
        save.Click += Save_Click;
        FileMenu.Items.Add(save);

        var saveAs = new MenuItem
        {
            Header = "Save _As...",
            IsEnabled = hasDoc,
            InputGesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)
        };
        saveAs.Click += SaveAs_Click;
        FileMenu.Items.Add(saveAs);

        var exportPng = new MenuItem { Header = "Export as _PNG...", IsEnabled = hasDoc };
        exportPng.Click += async (_, _) => await DoExportPng();
        FileMenu.Items.Add(exportPng);

        var exportVideo = new MenuItem { Header = "Export as _Video (MP4)...", IsEnabled = hasDoc };
        ToolTip.SetTip(exportVideo, "Render a storyboard to an MP4 file");
        exportVideo.Click += async (_, _) => await DoExportVideo();
        FileMenu.Items.Add(exportVideo);

        var exportNames = new MenuItem { Header = "Copy Element _Names", IsEnabled = hasDoc };
        exportNames.Click += async (_, _) => await DoCopyElementNames();
        FileMenu.Items.Add(exportNames);

        FileMenu.Items.Add(new Separator());

        var exportGuides = new MenuItem { Header = "Export _Guides...", IsEnabled = hasDoc };
        ToolTip.SetTip(exportGuides, "Save this document's guides as a .gtguides file");
        exportGuides.Click += async (_, _) => await DoExportGuides();
        FileMenu.Items.Add(exportGuides);

        var importGuides = new MenuItem { Header = "_Import Guides...", IsEnabled = hasDoc };
        ToolTip.SetTip(importGuides, "Replace this document's guides with a shared .gtguides file");
        importGuides.Click += async (_, _) => await DoImportGuides();
        FileMenu.Items.Add(importGuides);

        var paths = _recent.Paths;
        if (paths.Count > 0)
        {
            FileMenu.Items.Add(new Separator());

            for (int i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var item = new MenuItem { Header = $"{i + 1}  {Path.GetFileName(path)}" };
                ToolTip.SetTip(item, path);
                item.Click += async (_, _) => await OpenPath(path);
                FileMenu.Items.Add(item);
            }

            FileMenu.Items.Add(new Separator());

            var clear = new MenuItem { Header = "Clear Recent Files" };
            clear.Click += (_, _) => { _recent.Clear(); RebuildFileMenu(); };
            FileMenu.Items.Add(clear);
        }

        FileMenu.Items.Add(new Separator());

        var about = new MenuItem { Header = "_About" };
        about.Click += About_Click;
        FileMenu.Items.Add(about);

        var exit = new MenuItem { Header = "E_xit" };
        exit.Click += Exit_Click;
        FileMenu.Items.Add(exit);
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open vMix GT Template",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("vMix GT Template") { Patterns = new[] { "*.gtzip" } },
                new FilePickerFileType("All Files")        { Patterns = new[] { "*.*"     } }
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        await OpenPath(path);
    }

    private async System.Threading.Tasks.Task OpenPath(string path)
    {
        if (!await ConfirmDiscardChanges()) return;

        try
        {
            var (document, assets) = _reader.Read(path);
            _currentAssets = assets;
            _currentPath   = path;

            GtCanvas.SetAssets(assets);
            GtCanvas.Document = document;

            LayersPanel.Populate(document);
            DeleteLayerMenuItem.IsEnabled = document.Layers.Count > 1;
            PropertiesPanel.Populate(Array.Empty<GtElement>());
            TimelinePanel.Populate(document);
            _history.Clear();
            MarkClean();

            FitToWindow();
            UpdateRulers();
            UpdateRulerSelection();

            var filename = Path.GetFileName(path);
            var storyboards = document.Storyboards.Count > 0
                ? $"  ·  {document.Storyboards.Count} storyboard(s)" : "";
            StatusText.Text = $"{filename}  ·  {document.Layers.Count} layers  ·  {document.Width}×{document.Height}{storyboards}";
            Title = $"vMix GT++ -  {filename}";

            _recent.Add(path);
            Avalonia.Threading.Dispatcher.UIThread.Post(RebuildFileMenu, Avalonia.Threading.DispatcherPriority.Background);

            Logger.Info($"File loaded: {filename}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load file", ex);
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void TimelineToggleButton_Click(object? sender, RoutedEventArgs e) =>
        SetTimelineVisible(TimelineToggleButton.IsChecked == true);

    /// <summary>status-bar twin of the timeline's own preview toggle, both drive the same gate</summary>
    private void PreviewToggleButton_Click(object? sender, RoutedEventArgs e) =>
        TimelinePanel.PreviewEnabled = PreviewToggleButton.IsChecked == true;

    private void OnTimelinePreviewEnabledChanged(bool enabled) =>
        PreviewToggleButton.IsChecked = enabled;

    private void SetTimelineVisible(bool visible)
    {
        TimelinePanel.IsVisible = visible;
        TimelineToggleButton.IsChecked = visible;

        if (visible)
        {
            TimelinePanel.Populate(GtCanvas.Document);

            // the panel steals height from the canvas view, so what fitted a moment ago may not any more; posted so the check runs against the post-layout viewport
            if (_prefs.AutoFitOnTimelineOpen)
                Dispatcher.UIThread.Post(FitIfCanvasOverflows, DispatcherPriority.Loaded);
        }
        else
        {
            // leaving a scrubbed frame applied would misrepresent the document at rest
            GtCanvas.AnimationFrame = null;
            UpdatePreviewStatus();
        }
    }

    /// <summary>drops the zoom to Fit when the canvas no longer fits the viewport unscrolled</summary>
    private void FitIfCanvasOverflows()
    {
        var viewport = CanvasScrollViewer.Viewport;
        var extent   = CanvasScrollViewer.Extent;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;

        // half a pixel of slack keeps layout rounding from tripping a needless refit
        if (extent.Width  > viewport.Width  + 0.5 ||
            extent.Height > viewport.Height + 0.5)
        {
            FitToWindow();
        }
    }

    /// <summary>true while the timeline was the last surface clicked, Delete follows it there</summary>
    private bool _timelineIsActiveSurface;

    private void OnTimelinePreviewChanged(IReadOnlyList<GtTimelineSegment> segments, double time)
    {
        var doc = GtCanvas.Document;

        // no segments means the timeline's preview toggle is off: the canvas goes back to the document at rest, where the objects sit where the model says and can be edited
        GtCanvas.AnimationFrame = doc is null || segments.Count == 0
            ? null
            : GtAnimationEvaluator.Evaluate(doc, segments, time);

        UpdatePreviewStatus();
    }

    /// <summary>status text the preview notice replaced, restored when the preview ends</summary>
    private string? _statusBeforePreview;

    private void UpdatePreviewStatus()
    {
        bool previewing = GtCanvas.IsPreviewing;

        if (previewing)
        {
            if (_statusBeforePreview is not null) return;
            _statusBeforePreview = StatusText.Text;
            StatusText.Text = "Animation preview  ·  editing suspended - turn the timeline's 👁 preview off to edit";
        }
        else if (_statusBeforePreview is not null)
        {
            StatusText.Text      = _statusBeforePreview;
            _statusBeforePreview = null;
        }
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private void Save_Click(object? sender, RoutedEventArgs e) => DoSave();
    private async void SaveAs_Click(object? sender, RoutedEventArgs e) => await DoSaveAs();

    private void DoSave()
    {
        if (_currentPath is null || GtCanvas.Document is null) return;
        SaveToPath(_currentPath);
    }

    private async System.Threading.Tasks.Task DoSaveAs()
    {
        if (GtCanvas.Document is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save vMix GT Template",
            SuggestedFileName = _currentPath is not null
                ? Path.GetFileName(_currentPath)
                : "template.gtzip",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("vMix GT Template") { Patterns = new[] { "*.gtzip" } }
            },
            DefaultExtension = "gtzip"
        });

        var path = file?.TryGetLocalPath();
        if (path is null) return;

        if (SaveToPath(path))
        {
            _currentPath = path;
            Title = $"vMix GT++  -  {Path.GetFileName(path)}";
            _recent.Add(path);
            RebuildFileMenu();
        }
    }

    private bool SaveToPath(string path)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return false;
        try
        {
            _writer.Write(path, doc, _currentAssets);
            MarkClean();
            StatusText.Text = $"Saved  ·  {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save file", ex);
            StatusText.Text = $"Save error: {ex.Message}";
            return false;
        }
    }

    private async System.Threading.Tasks.Task DoExportPng()
    {
        if (GtCanvas.Document is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export as PNG",
            SuggestedFileName = _currentPath is not null
                ? Path.GetFileNameWithoutExtension(_currentPath) + ".png"
                : "export.png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
            },
            DefaultExtension = "png"
        });

        var path = file?.TryGetLocalPath();
        if (path is null) return;

        try
        {
            var bmp = GtCanvas.ExportToBitmap();
            if (bmp is null) return;
            bmp.Save(path);
            StatusText.Text = $"Exported  ·  {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to export PNG", ex);
            StatusText.Text = $"Export error: {ex.Message}";
        }
    }

    /// <summary>opens the MP4 export dialog; the dialog renders through this window's canvas, so the frames it writes are the same ones the preview draws</summary>
    private async System.Threading.Tasks.Task DoExportVideo()
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        if (doc.Storyboards.Count == 0)
        {
            StatusText.Text = "This title has no storyboards to export";
            return;
        }

        var dialog = new ExportVideoWindow(GtCanvas, _prefs, _currentPath);
        await dialog.ShowDialog(this);

        if (dialog.ExportedPath is not null)
            StatusText.Text = $"Exported  ·  {Path.GetFileName(dialog.ExportedPath)}";
    }

    private async System.Threading.Tasks.Task DoCopyElementNames()
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var names = doc.Layers
            .SelectMany(l => l.Elements)
            .Select(el => el.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var clipboard = Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard unavailable";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, names));
            StatusText.Text = $"Copied {names.Count} element name(s) to clipboard";
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to copy element names", ex);
            StatusText.Text = $"Clipboard error: {ex.Message}";
        }
    }

    private async void NewDocument(double width, double height)
    {
        if (!await ConfirmDiscardChanges()) return;

        var doc = new GtDocument { Width = width, Height = height };
        var layer = new GtLayer
        {
            Name = "Layer 1",
            Location = GtPoint.Zero,
            Dimensions = new GtSize(width, height),
            InnerWidth = width,
            InnerHeight = height,
        };
        doc.Layers.Add(layer);

        _currentAssets = new GtAssetLibrary();
        _currentPath   = null;

        GtCanvas.SetAssets(_currentAssets);
        GtCanvas.Document = doc;
        LayersPanel.Populate(doc);
        DeleteLayerMenuItem.IsEnabled = doc.Layers.Count > 1;
        PropertiesPanel.Populate(Array.Empty<GtElement>());
        TimelinePanel.Populate(doc);
        _history.Clear();
        MarkClean();
        FitToWindow();
        UpdateRulers();
        UpdateRulerSelection();

        StatusText.Text = $"New document  ·  {width}×{height}";
        Title = "vMix GT++  -  (new)";
        Avalonia.Threading.Dispatcher.UIThread.Post(RebuildFileMenu, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnCanvasDrawCompleted(Rect docRect)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var layer = GetOrCreateDefaultLayer(doc);

        GtElement element;
        if (GtCanvas.ActiveTool == CanvasTool.TextBox)
        {
            element = new GtTextBlock
            {
                Name       = GenerateElementName(doc, "TextBox"),
                Location   = new GtPoint(docRect.X - layer.Location.X, docRect.Y - layer.Location.Y),
                Dimensions = new GtSize(docRect.Width, docRect.Height),
                Text       = "Lorem ipsum",
                FontFamily = "Arial",
                FontSize   = 36,
                Fill       = new GtBrush { Type = GtBrushType.Solid, Color = Avalonia.Media.Colors.White },
            };
        }
        else if (GtCanvas.ActiveTool == CanvasTool.Ticker)
        {
            element = new GtTickerElement
            {
                Name       = GenerateElementName(doc, "Ticker"),
                Location   = new GtPoint(docRect.X - layer.Location.X, docRect.Y - layer.Location.Y),
                Dimensions = new GtSize(docRect.Width, docRect.Height),
                Text       = "Lorem ipsum",
                FontFamily = "Arial",
                FontSize   = 36,
                Fill       = new GtBrush { Type = GtBrushType.Solid, Color = Avalonia.Media.Colors.White },
            };
        }
        else
        {
            element = new GtRectangleElement
            {
                Name       = GenerateElementName(doc, "Rectangle"),
                Location   = new GtPoint(docRect.X - layer.Location.X, docRect.Y - layer.Location.Y),
                Dimensions = new GtSize(docRect.Width, docRect.Height),
                Fill       = new GtBrush { Type = GtBrushType.Solid, Color = Avalonia.Media.Colors.Red },
                // GT Title draws a shape with DataFlags "None", which vMix reads as hidden
                DataFlags  = GtDataFlags.Hidden,
            };
        }

        layer.Elements.Add(element);

        _history.Push(new PropertyChangeAction(
            $"Add {element.Name}",
            undo: () =>
            {
                layer.Elements.Remove(element);
                GtCanvas.ClearSelection();
                LayersPanel.Populate(doc);
            },
            redo: () =>
            {
                layer.Elements.Add(element);
                GtCanvas.SetSelection(element);
                LayersPanel.Populate(doc);
            }
        ));

        LayersPanel.Populate(doc);
        GtCanvas.SetSelection(element);
        SetTool(CanvasTool.Select);
    }

    private GtLayer GetOrCreateDefaultLayer(GtDocument doc)
    {
        foreach (var l in doc.Layers)
            if (!l.Locked && l.Visible) return l;

        var layer = new GtLayer
        {
            Name = "Layer 1",
            Location = GtPoint.Zero,
            Dimensions = new GtSize(doc.Width, doc.Height),
            InnerWidth = doc.Width,
            InnerHeight = doc.Height,
        };
        doc.Layers.Add(layer);
        LayersPanel.Populate(doc);
        return layer;
    }

    private static string GenerateElementName(GtDocument doc, string prefix) =>
        UniqueName(CollectElementNames(doc), prefix, 1);

    private static HashSet<string> CollectElementNames(GtDocument doc) => NameService.CollectElementNames(doc);

    private static string UniqueName(HashSet<string> taken, string baseName, int start) =>
        NameService.UniqueName(taken, baseName, start);

    private static string NextCopyName(HashSet<string> taken, string name) =>
        NameService.NextCopyName(taken, name);

    private static GtLayer? FindLayer(GtDocument doc, GtElement element)
    {
        foreach (var l in doc.Layers)
            if (l.Elements.Contains(element)) return l;
        return null;
    }

    /// <summary>one copied element: a detached clone plus the animations that targeted it</summary>
    private sealed class ClipboardEntry
    {
        public GtLayer Layer = null!;
        public GtElement Element = null!;
        public List<(string? StoryboardType, string DataName, GtAnimation Animation)> Animations = new();
    }

    private readonly List<ClipboardEntry> _clipboard = new();

    private void Copy_Click(object? sender, RoutedEventArgs e) => DoCopy();
    private void Paste_Click(object? sender, RoutedEventArgs e) => DoPaste();

    private void DoCopy()
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var selected = GtCanvas.SelectedElements;
        if (selected.Count == 0) return;

        _clipboard.Clear();

        // keep document order so a multi-element paste stacks the same way as the original
        foreach (var layer in doc.Layers)
            foreach (var el in layer.Elements)
            {
                if (!selected.Contains(el)) continue;

                var entry = new ClipboardEntry { Layer = layer, Element = el.Clone() };
                foreach (var sb in doc.Storyboards)
                    foreach (var anim in sb.Animations)
                        if (string.Equals(anim.Object, el.Name, StringComparison.OrdinalIgnoreCase))
                            entry.Animations.Add((sb.Type, sb.DataName, anim.Clone()));

                _clipboard.Add(entry);
            }

        PasteMenuItem.IsEnabled = _clipboard.Count > 0;
        StatusText.Text = $"Copied {_clipboard.Count} element(s)";
    }

    private void DoPaste()
    {
        var doc = GtCanvas.Document;
        if (doc is null || _clipboard.Count == 0) return;

        var names = CollectElementNames(doc);
        var pasted     = new List<(GtLayer Layer, GtElement Element)>();
        var animations = new List<(GtStoryboard Storyboard, GtAnimation Animation)>();
        var newBoards  = new List<GtStoryboard>();
        var renames    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _clipboard)
        {
            var layer = doc.Layers.Contains(entry.Layer) ? entry.Layer : GetOrCreateDefaultLayer(doc);
            var element = entry.Element.Clone();

            element.Name = NextCopyName(names, entry.Element.Name);
            names.Add(element.Name);
            renames[entry.Element.Name] = element.Name;

            pasted.Add((layer, element));

            foreach (var (type, dataName, anim) in entry.Animations)
            {
                var sb = FindOrCreateStoryboard(doc, newBoards, type, dataName);
                var copy = anim.Clone();
                copy.Object = element.Name;
                animations.Add((sb, copy));
            }
        }

        // a mask pointing at another element in the same paste follows the copy, not the original
        foreach (var (_, element) in pasted)
            if (element.MaskObject is not null && renames.TryGetValue(element.MaskObject, out var masked))
                element.MaskObject = masked;

        // a bounding resolves by name the same way, so it follows the copy too
        foreach (var (_, element) in pasted)
            if (element.Bounding is { } bounding && bounding.Object is { } source &&
                renames.TryGetValue(source, out var bound))
                bounding.Object = bound;

        void Apply()
        {
            foreach (var sb in newBoards)
                if (!doc.Storyboards.Contains(sb)) doc.Storyboards.Add(sb);
            foreach (var (layer, element) in pasted)
                layer.Elements.Add(element);
            foreach (var (sb, anim) in animations)
                sb.Animations.Add(anim);

            LayersPanel.Populate(doc);
            GtCanvas.ClearSelection();
            foreach (var (_, element) in pasted)
                GtCanvas.SetSelection(element, addToSelection: true);
            GtCanvas.InvalidateVisual();
            TimelinePanel.Populate(doc);
        }

        void Revert()
        {
            foreach (var (sb, anim) in animations)
                sb.Animations.Remove(anim);
            foreach (var (layer, element) in pasted)
                layer.Elements.Remove(element);
            foreach (var sb in newBoards)
                doc.Storyboards.Remove(sb);

            LayersPanel.Populate(doc);
            GtCanvas.ClearSelection();
            GtCanvas.InvalidateVisual();
            TimelinePanel.Populate(doc);
        }

        Apply();

        _history.Push(new PropertyChangeAction(
            pasted.Count == 1 ? $"Paste {pasted[0].Element.Name}" : $"Paste {pasted.Count} elements",
            undo: Revert,
            redo: Apply));

        StatusText.Text = $"Pasted {pasted.Count} element(s)";
    }

    /// <summary>the storyboard a pasted animation belongs in; GT identifies one by (Type, DataName), so an animation copied out of a DataChange storyboard scoped to a field lands back in that same scope rather than in the unscoped one</summary>
    private static GtStoryboard FindOrCreateStoryboard(
        GtDocument doc, List<GtStoryboard> pending, string? type, string dataName)
    {
        bool Matches(GtStoryboard sb) =>
            sb.Matches(type ?? GtStoryboard.TransitionIn, dataName);

        foreach (var sb in doc.Storyboards)
            if (Matches(sb)) return sb;
        foreach (var sb in pending)
            if (Matches(sb)) return sb;

        var created = new GtStoryboard { Type = type, DataName = dataName };
        pending.Add(created);
        return created;
    }

    private void ReplaceWithText_Click(object? sender, RoutedEventArgs e)
        => ReplaceSelection(GtElementKind.Text);

    private void ReplaceWithRectangle_Click(object? sender, RoutedEventArgs e)
        => ReplaceSelection(GtElementKind.Rectangle);

    private void ReplaceWithEllipse_Click(object? sender, RoutedEventArgs e)
        => ReplaceSelection(GtElementKind.Ellipse);

    private async void ReplaceWithImage_Click(object? sender, RoutedEventArgs e)
        => await ReplaceSelectionWithImageAsync();

    private void ReplaceWithTicker_Click(object? sender, RoutedEventArgs e)
        => ReplaceSelection(GtElementKind.Ticker);

    /// <summary>swaps every selected element for one of <paramref name="kind"/> in the same layer slot so z-order is untouched; names carry over which keeps animations and mask references pointing at the replacement, and elements already of that type and locked ones are skipped</summary>
    private void ReplaceSelection(GtElementKind kind, string? bitmapSource = null,
                                  Action? applyAssets = null, Action? revertAssets = null)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var selected = GtCanvas.SelectedElements.ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Replace with: nothing selected";
            return;
        }

        var swaps = new List<(GtLayer Layer, int Index, GtElement Before, GtElement After)>();
        foreach (var layer in doc.Layers)
        {
            if (layer.Locked) continue;
            for (int i = 0; i < layer.Elements.Count; i++)
            {
                var el = layer.Elements[i];
                if (el.Locked || !selected.Contains(el)) continue;

                var replacement = ElementConversionService.Convert(el, kind, bitmapSource);
                if (replacement is null) continue;   // already that type

                swaps.Add((layer, i, el, replacement));
            }
        }

        if (swaps.Count == 0)
        {
            StatusText.Text = $"Replace with {ElementConversionService.DisplayName(kind)}: nothing to convert";
            return;
        }

        void Select(IEnumerable<GtElement> elements)
        {
            LayersPanel.Populate(doc);
            TimelinePanel.Populate(doc);
            GtCanvas.ClearSelection();
            foreach (var el in elements)
                GtCanvas.SetSelection(el, addToSelection: true);
            GtCanvas.InvalidateVisual();
        }

        void Apply()
        {
            applyAssets?.Invoke();
            foreach (var (layer, index, _, after) in swaps)
                layer.Elements[index] = after;
            Select(swaps.Select(s => s.After));
        }

        void Revert()
        {
            foreach (var (layer, index, before, _) in swaps)
                layer.Elements[index] = before;
            revertAssets?.Invoke();
            Select(swaps.Select(s => s.Before));
        }

        Apply();

        var typeName = ElementConversionService.DisplayName(kind);
        _history.Push(new PropertyChangeAction(
            swaps.Count == 1
                ? $"Replace {swaps[0].Before.Name} with {typeName}"
                : $"Replace {swaps.Count} elements with {typeName}",
            undo: Revert,
            redo: Apply));

        StatusText.Text = $"Replaced {swaps.Count} element(s) with {typeName}";
    }

    /// <summary>picks one bitmap and converts the whole selection to image elements sharing it; the blob is added to the asset library on apply and pulled back out on undo, and unlike a source swap nothing else can reference it yet so removing it again leaves no dangling reference</summary>
    private async System.Threading.Tasks.Task ReplaceSelectionWithImageAsync()
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        if (GtCanvas.SelectedElements.Count == 0)
        {
            StatusText.Text = "Replace with: nothing selected";
            return;
        }

        var path = await PickImageFileAsync("Replace With Image");
        if (path is null) return;

        try
        {
            var bytes = File.ReadAllBytes(path);

            // validates the file is a decodable image before it lands in the document
            using (var ms = new MemoryStream(bytes))
                _ = new Avalonia.Media.Imaging.Bitmap(ms);

            var ext         = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var logicalPath = $"images/{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}.{ext}";

            ReplaceSelection(
                GtElementKind.Image,
                logicalPath,
                applyAssets: () =>
                {
                    _currentAssets[logicalPath] = bytes;
                    GtCanvas.SetAssets(_currentAssets);
                },
                revertAssets: () =>
                {
                    _currentAssets.Remove(logicalPath);
                    GtCanvas.SetAssets(_currentAssets);
                });
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to replace selection with image", ex);
            StatusText.Text = $"Image error: {ex.Message}";
        }
    }

    /// <summary>shows the image file picker, returns the chosen local path or null</summary>
    private async System.Threading.Tasks.Task<string?> PickImageFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image Files")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.dds" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async System.Threading.Tasks.Task InsertImageAsync()
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var path = await PickImageFileAsync("Insert Image");
        if (path is null) return;

        try
        {
            var bytes = File.ReadAllBytes(path);

            double imgWidth, imgHeight;
            using (var ms = new MemoryStream(bytes))
            {
                var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                imgWidth  = bmp.PixelSize.Width;
                imgHeight = bmp.PixelSize.Height;
            }

            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var logicalPath = $"images/{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}.{ext}";
            _currentAssets[logicalPath] = bytes;
            GtCanvas.SetAssets(_currentAssets);

            var layer  = GetOrCreateDefaultLayer(doc);
            var centerX = (doc.Width  - imgWidth)  / 2 - layer.Location.X;
            var centerY = (doc.Height - imgHeight) / 2 - layer.Location.Y;

            var element = new GtImageElement
            {
                Name         = GenerateElementName(doc, "Image"),
                Location     = new GtPoint(Math.Max(0, centerX), Math.Max(0, centerY)),
                Dimensions   = new GtSize(imgWidth, imgHeight),
                BitmapSource = logicalPath,
            };

            layer.Elements.Add(element);

            _history.Push(new PropertyChangeAction(
                $"Add {element.Name}",
                undo: () =>
                {
                    layer.Elements.Remove(element);
                    _currentAssets.Remove(logicalPath);
                    GtCanvas.SetAssets(_currentAssets);
                    GtCanvas.ClearSelection();
                    LayersPanel.Populate(doc);
                },
                redo: () =>
                {
                    _currentAssets[logicalPath] = bytes;
                    GtCanvas.SetAssets(_currentAssets);
                    layer.Elements.Add(element);
                    GtCanvas.SetSelection(element);
                    LayersPanel.Populate(doc);
                }
            ));

            LayersPanel.Populate(doc);
            GtCanvas.SetSelection(element);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to insert image", ex);
            StatusText.Text = $"Image error: {ex.Message}";
        }
    }

    /// <summary>replaces an existing image element's bitmap with a file from disk, adding the file to the asset library so it is written into the gtzip on save; the element's box is left alone since GT never resizes an image object after creation so the size mode decides how the new bitmap fills the old box; the previous blob stays in the library, other elements (and brushes) may still reference it and undo needs it back</summary>
    private async System.Threading.Tasks.Task ReplaceImageSourceAsync(GtImageElement img)
    {
        var doc = GtCanvas.Document;
        if (doc is null) return;

        var path = await PickImageFileAsync("Replace Image Source");
        if (path is null) return;

        try
        {
            var bytes = File.ReadAllBytes(path);

            // validates the file is a decodable image before it lands in the document
            using (var ms = new MemoryStream(bytes))
                _ = new Avalonia.Media.Imaging.Bitmap(ms);

            var ext         = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var logicalPath = $"images/{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}.{ext}";

            var beforeSource   = img.BitmapSource;
            var beforePosition = img.SequencePosition;

            void Apply()
            {
                _currentAssets[logicalPath] = bytes;
                GtCanvas.SetAssets(_currentAssets);
                img.BitmapSource     = logicalPath;
                img.SequencePosition = null;   // a plain file is not a sequence frame
                GtCanvas.InvalidateVisual();
                RefreshPropertiesPanel();
                UpdateDebugPanel();
            }

            void Revert()
            {
                img.BitmapSource     = beforeSource;
                img.SequencePosition = beforePosition;
                GtCanvas.InvalidateVisual();
                RefreshPropertiesPanel();
                UpdateDebugPanel();
            }

            Apply();

            _history.Push(new PropertyChangeAction($"Image source {img.Name}", undo: Revert, redo: Apply));

            StatusText.Text = $"Image source: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to replace image source", ex);
            StatusText.Text = $"Image error: {ex.Message}";
        }
    }
}
