using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GtPlus.Models;
using GtPlus.Services;
using GtPlus.Views;

namespace GtPlus.Controls;

/// <summary>bottom-docked storyboard timeline: storyboard picker, transport, editable track area, and a property strip for the selected animation; scrubbing never mutates the document, it only pushes a <see cref="GtAnimationFrame"/> onto the canvas so previewing a storyboard cannot dirty the file, while timing and property edits do mutate the model and are pushed onto the history stack</summary>
public partial class TimelinePanelControl : UserControl
{
    private GtDocument? _document;
    private GtAnimation? _selected;

    /// <summary>storyboard the selected animation belongs to, the target for add/delete edits</summary>
    private GtStoryboard? _selectedStoryboard;

    private readonly List<TimelineView> _views = new();
    private TimelineView? _view;

    /// <summary>seconds the template stays live between TransitionIn and TransitionOut</summary>
    private double _hold = 2.0;

    /// <summary>rate the transport asks for, the FPS readout is measured against it</summary>
    private const double TargetFps = 60;

    private readonly DispatcherTimer _playTimer;
    private DateTime _playAnchor;
    private double _playStartTime;
    private bool _playing;

    /// <summary>guards the property strip while it is being repopulated from the model; set through <see cref="SuppressEvents"/> so nested repopulation (loading the strip refreshes the object list) restores the outer guard instead of clearing it early</summary>
    private bool _suppressPropertyEvents;

    /// <summary>sets the guard for the lifetime of the returned scope, then restores it</summary>
    private SuppressScope SuppressEvents() => new(this);

    private readonly struct SuppressScope : IDisposable
    {
        private readonly TimelinePanelControl _owner;
        private readonly bool _previous;

        public SuppressScope(TimelinePanelControl owner)
        {
            _owner = owner;
            _previous = owner._suppressPropertyEvents;
            owner._suppressPropertyEvents = true;
        }

        public void Dispose() => _owner._suppressPropertyEvents = _previous;
    }

    /// <summary>raised when the previewed time changes, the host applies it to the canvas</summary>
    public event Action<IReadOnlyList<GtTimelineSegment>, double>? PreviewTimeChanged;

    /// <summary>raised when the user closes the panel with the ✕ button</summary>
    public event Action? CloseRequested;

    /// <summary>raised when the storyboard's animation list is edited</summary>
    public event Action? StoryboardEdited;

    public HistoryService? History { get; set; }

    /// <summary>canvas whose paints the FPS readout samples, optional; no canvas means no readout</summary>
    public GtCanvasControl? Canvas { get; set; }

    /// <summary>supplies the element currently selected on the canvas, for "+ Animation"</summary>
    public Func<GtElement?>? SelectedElementProvider { get; set; }

    /// <summary>supplies the name a new animation should target, a layer when one is selected or an element otherwise; falls back to <see cref="SelectedElementProvider"/> when unset</summary>
    public Func<string?>? SelectedObjectNameProvider { get; set; }

    /// <summary>supplies every object a new animation should target, so adding one with a group selected on the canvas gives each of them their own clip; falls back to <see cref="SelectedObjectNameProvider"/> when unset or empty</summary>
    public Func<IReadOnlyList<string>>? SelectedObjectNamesProvider { get; set; }

    public TimelinePanelControl()
    {
        InitializeComponent();

        Track.TimeScrubbed            += OnTimeScrubbed;
        Track.AnimationSelected       += OnAnimationSelected;
        Track.AnimationsTimingChanged += OnAnimationsTimingChanged;
        Track.MuteToggled             += OnMuteToggled;

        BuildDirectionPad();
        BuildScrubHandles();

        Track.ContentShifted += OnTrackContentShifted;

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps) };
        _playTimer.Tick += OnPlayTick;

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FpsWindowMs) };
        _fpsTimer.Tick += OnFpsTick;

        PopulateStaticCombos();
    }

    /// <summary>smallest useful height: grip, header, ruler, one row and property strip; the strip is as tall as the three-row direction pad so this allows for it</summary>
    private const double MinPanelHeight = 176;

    /// <summary>pixels of the window always left over for the canvas above the timeline</summary>
    private const double MinCanvasHeadroom = 220;

    private bool _resizing;
    private double _resizeStartY;
    private double _resizeStartHeight;

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _resizing          = true;
        _resizeStartY      = RootY(e);
        _resizeStartHeight = Bounds.Height;
        e.Pointer.Capture(ResizeGrip);
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;

        // dragging the grip upwards grows the panel, so the delta is subtracted
        double proposed = _resizeStartHeight - (RootY(e) - _resizeStartY);

        double available = TopLevel.GetTopLevel(this)?.Bounds.Height ?? double.MaxValue;
        double max = Math.Max(MinPanelHeight, available - MinCanvasHeadroom);

        Height = Math.Clamp(proposed, MinPanelHeight, max);
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>pointer Y in window space; measuring against the panel itself would feed the resize back into its own coordinate system since the panel's top edge moves as it grows</summary>
    private double RootY(PointerEventArgs e) =>
        TopLevel.GetTopLevel(this) is Visual root ? e.GetPosition(root).Y : e.GetPosition(this).Y;

    public void Populate(GtDocument? document)
    {
        Stop();
        _document = document;

        // dropped before the rebuild so a same-named view from the previous document cannot be restored, which would leave the track pointing at the old storyboards
        _view = null;
        using (SuppressEvents()) StoryboardCombo.SelectedIndex = -1;

        // with no view to preserve, the rebuild falls back to the combined view (the only way to watch the in and out halves run as one sequence) or to the first storyboard
        RebuildStoryboardList();

        RefreshObjectCombo();
    }

    /// <summary>builds the picker: every storyboard on its own, plus the combined TransitionIn + TransitionOut view when the document has both, and one combined view per DataChange pair since vMix runs those back-to-back on a data change so they read as one sequence</summary>
    private void RebuildStoryboardList()
    {
        var previous = _view?.Label;

        _views.Clear();
        if (_document is not null)
        {
            // GT's own order (None, TransitionIn/Out, Page1-10, DataChangeIn/Out, Continuous) rather than file order, so newly created storyboards land where the user expects to find them; scoped DataChange storyboards sort by field under their own type
            foreach (var storyboard in _document.Storyboards
                                                .OrderBy(s => GtStoryboard.SortIndex(s.DisplayName))
                                                .ThenBy(s => s.DataName, StringComparer.Ordinal))
                _views.Add(new TimelineView(storyboard.EventLabel,
                                            new List<GtTimelineSegment> { new(storyboard) }));

            var transitionIn  = _document.Storyboards.FirstOrDefault(s => s.IsTransitionIn);
            var transitionOut = _document.Storyboards.FirstOrDefault(s => s.IsTransitionOut);
            if (transitionIn is not null && transitionOut is not null)
                _views.Add(new TimelineView($"{GtStoryboard.TransitionIn} + {GtStoryboard.TransitionOut}",
                                            new List<GtTimelineSegment>
                                            {
                                                new(transitionIn),
                                                new(transitionOut, transitionIn.Duration + _hold),
                                            },
                                            outPhaseStart: 1,
                                            usesHold: true));

            AddDataChangeViews();
        }

        using (SuppressEvents())
        {
            StoryboardCombo.ItemsSource = new List<TimelineView>(_views);

            // keep the user on the same view across list rebuilds (add / undo of a storyboard); the view object itself is replaced so the track is re-pointed at the new segments without resetting the playhead or the selected animation
            int index = previous is null ? -1 : _views.FindIndex(v => v.Label == previous);
            if (index >= 0)
            {
                StoryboardCombo.SelectedIndex = index;
                _view = _views[index];
                Track.Segments = _view.Segments;
                SyncSegmentOffsets();
                ApplyLoopLock(_view);
            }
        }

        if (_views.Count == 0)
        {
            using (SuppressEvents()) StoryboardCombo.SelectedIndex = -1;
            SetView(null);
            return;
        }

        // the previous view is gone (first load, a new document, or an undone storyboard): fall back to the transition pair when there is one (the whole run of the title), then to any other combined view, then to the first storyboard
        if (_view is null || !_views.Contains(_view))
        {
            int fallback = _views.FindIndex(v => v.UsesHold);
            if (fallback < 0) fallback = _views.FindIndex(v => v.IsCombined);
            if (fallback < 0) fallback = 0;
            ShowView(fallback);
        }

        // a rebuild that kept the user on the same view skips SetView, so the shortcut is re-checked here too: a document going from no storyboards to one makes it available
        RefreshSelectionState();
    }

    /// <summary>one combined view per DataChange scope, showing what vMix actually plays when a field changes: the unscoped storyboard and the field's own one build into the same timeline so both are laid on top of each other here too; there is no hold between the halves since vMix commits the new values the moment the in phase ends and starts the out phase straight away, so the pair runs as one continuous sequence</summary>
    private void AddDataChangeViews()
    {
        if (_document is null) return;

        var scopes = _document.Storyboards
                              .Where(s => s.IsDataChangeEvent)
                              .Select(s => s.DataName)
                              .Distinct(StringComparer.Ordinal)
                              .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var scope in scopes)
        {
            var inBoards  = DataChangePhase(GtStoryboard.DataChangeIn,  scope);
            var outBoards = DataChangePhase(GtStoryboard.DataChangeOut, scope);
            if (inBoards.Count == 0 || outBoards.Count == 0) continue;

            // every in-phase storyboard starts at zero, the out phase begins once the longest of them has finished
            double inEnd = inBoards.Max(s => s.Duration);

            var segments = new List<GtTimelineSegment>();
            foreach (var storyboard in inBoards)  segments.Add(new GtTimelineSegment(storyboard));
            int outPhaseStart = segments.Count;
            foreach (var storyboard in outBoards) segments.Add(new GtTimelineSegment(storyboard, inEnd));

            var suffix = scope.Length > 0 ? $" ({scope})" : "";
            _views.Add(new TimelineView(
                $"{GtStoryboard.DataChangeIn} + {GtStoryboard.DataChangeOut}{suffix}",
                segments,
                outPhaseStart: outPhaseStart));
        }
    }

    /// <summary>storyboards that run for one phase of a data change on <paramref name="scope"/>: the unscoped one which fires on any field, plus the one scoped to this field</summary>
    private List<GtStoryboard> DataChangePhase(string type, string scope)
    {
        var boards = new List<GtStoryboard>();
        if (_document is null) return boards;

        var unscoped = _document.Storyboards.FirstOrDefault(s => s.Matches(type, ""));
        if (unscoped is not null) boards.Add(unscoped);

        if (scope.Length > 0)
        {
            var scoped = _document.Storyboards.FirstOrDefault(s => s.Matches(type, scope));
            if (scoped is not null) boards.Add(scoped);
        }

        return boards;
    }

    /// <summary>selects the view at <paramref name="index"/>, applying it even when the combo is already sitting there and so would raise no selection change</summary>
    private void ShowView(int index)
    {
        if (index < 0 || index >= _views.Count) return;

        if (StoryboardCombo.SelectedIndex == index) SetView(_views[index]);
        else StoryboardCombo.SelectedIndex = index;
    }

    private void SetView(TimelineView? view)
    {
        _view = view;
        Track.Segments = view?.Segments ?? (IReadOnlyList<GtTimelineSegment>)Array.Empty<GtTimelineSegment>();
        HoldPanel.IsVisible = view?.UsesHold == true;
        HoldBox.Text = _hold.ToString("0.##", CultureInfo.InvariantCulture);
        ApplyLoopLock(view);
        SelectAnimation(null);
        SetTime(0);
        UpdateTimeLabel();
        ReportOverLimitObjects();
        RefreshSelectionState();
    }

    /// <summary>storyboards currently on the timeline, in display order</summary>
    private IEnumerable<GtStoryboard> VisibleStoryboards =>
        _view?.Segments.Select(s => s.Storyboard) ?? Enumerable.Empty<GtStoryboard>();

    private IReadOnlyList<GtTimelineSegment> CurrentSegments =>
        _view?.Segments ?? (IReadOnlyList<GtTimelineSegment>)Array.Empty<GtTimelineSegment>();

    /// <summary>timeline length in seconds: the end of the last segment</summary>
    private double ViewDuration
    {
        get
        {
            double end = 0;
            foreach (var segment in CurrentSegments)
                if (segment.EndTime > end) end = segment.EndTime;
            return end;
        }
    }

    private void HoldBox_Changed(object? sender, RoutedEventArgs e) => CommitHold();

    private void HoldBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHold();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        using (SuppressEvents())
            HoldBox.Text = _hold.ToString("0.##", CultureInfo.InvariantCulture);
        e.Handled = true;
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    /// <summary>re-places the TransitionOut segment; the hold is preview state (how long the template stays live before vMix plays the out half) so it never touches the document</summary>
    private void CommitHold()
    {
        if (_suppressPropertyEvents || _view is null || !_view.UsesHold) return;

        if (!double.TryParse(HoldBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hold))
            hold = _hold;
        _hold = Math.Max(0, hold);

        using (SuppressEvents())
            HoldBox.Text = _hold.ToString("0.##", CultureInfo.InvariantCulture);

        SyncSegmentOffsets();
        Track.Refresh();
        UpdateTimeLabel();
        EmitPreview(Track.CurrentTime);
    }

    /// <summary>standing warning for storyboards that already break the limit; files written by other tools can contain more than three animations per object and are left untouched so the user can delete the extras themselves, but GT will ignore them on playback</summary>
    private void ReportOverLimitObjects()
    {
        var over = VisibleStoryboards
            .SelectMany(s => s.ObjectsOverLimit().Select(o => (Storyboard: s, o.Key, o.Value)))
            .ToList();

        if (over.Count == 0)
        {
            ClearWarning();
            return;
        }

        // the storyboard is named as well, since a combined view shows two of them at once
        var offenders = string.Join(", ",
            over.Select(o => $"{o.Key} ({o.Value} in {o.Storyboard.EventLabel})"));
        ShowWarning($"⚠ Over GT's {GtStoryboard.MaxAnimationsPerObject}-animation limit: {offenders}", transient: false);
    }

    /// <summary>true when <paramref name="objectName"/> can take another animation; otherwise warns and returns false so callers can bail out of the edit</summary>
    private bool EnsureRoomFor(GtStoryboard storyboard, string? objectName, GtAnimation? ignore = null)
    {
        if (storyboard.HasRoomFor(objectName, ignore)) return true;

        var name = string.IsNullOrEmpty(objectName) ? "(no object)" : objectName;
        ShowWarning($"⚠ \"{name}\" already has {GtStoryboard.MaxAnimationsPerObject} animations - " +
                    "GT Title Designer's limit per object.", transient: true);
        return false;
    }

    private void ShowWarning(string message, bool transient)
    {
        _warningTimer?.Stop();
        LimitLabel.Text      = message;
        LimitLabel.IsVisible = true;

        if (!transient) return;

        _warningTimer ??= CreateWarningTimer();
        _warningTimer.Start();
    }

    private void ClearWarning()
    {
        _warningTimer?.Stop();
        LimitLabel.Text      = "";
        LimitLabel.IsVisible = false;
    }

    private DispatcherTimer? _warningTimer;

    private DispatcherTimer CreateWarningTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        // a transient warning falls back to the standing one, so an over-limit storyboard does not lose its notice just because a rejected edit was shown on top of it
        timer.Tick += (_, _) => { timer.Stop(); ReportOverLimitObjects(); };
        return timer;
    }

    private void StoryboardCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropertyEvents || _document is null) return;
        int i = StoryboardCombo.SelectedIndex;
        SetView(i >= 0 && i < _views.Count ? _views[i] : null);
    }

    /// <summary>offers every storyboard GT knows about; types the document already holds stay in the list ticked and disabled so the menu doubles as a view of what the title has, and a second storyboard of the same type would never be triggered by vMix anyway</summary>
    private void NewStoryboard_Click(object? sender, RoutedEventArgs e)
    {
        if (_document is null) return;

        var menu = new MenuFlyout();
        var fields = GtDataFieldService.GetEventFields(_document);

        foreach (var type in GtStoryboard.CreatableTypes)
        {
            // a DataChange storyboard can be scoped to one data field, so it gets a submenu listing the fields alongside the unscoped entry rather than a single item
            if (type is GtStoryboard.DataChangeIn or GtStoryboard.DataChangeOut)
            {
                menu.Items.Add(BuildDataChangeMenu(type, fields));
                continue;
            }

            bool exists = _document.Storyboards.Any(s => s.Matches(type, ""));
            string label = type == GtStoryboard.None ? $"{type} (placeholder)" : type;

            var item = new MenuItem
            {
                Header    = exists ? $"{label}  ✓" : label,
                IsEnabled = !exists,
            };

            if (!exists)
            {
                var created = type;
                item.Click += (_, _) => CreateStoryboard(created);
            }

            menu.Items.Add(item);
        }

        menu.ShowAt(NewStoryboardButton);
    }

    /// <summary>submenu for one half of the DataChange pair: the unscoped storyboard, then one entry per data field the composition offers; both fire on a data change (the unscoped storyboard on any field, a scoped one only on its own) and vMix plays them into the same timeline</summary>
    private MenuItem BuildDataChangeMenu(string type, IReadOnlyList<GtDataField> fields)
    {
        var parent = new MenuItem { Header = type };
        parent.Items.Add(BuildDataChangeItem(type, "", "Any field"));
        parent.Items.Add(new Separator());

        if (fields.Count == 0)
        {
            parent.Items.Add(new MenuItem
            {
                Header    = "No data fields in this title",
                IsEnabled = false,
            });
            return parent;
        }

        foreach (var field in fields)
            parent.Items.Add(BuildDataChangeItem(type, field.Name, field.Name));

        return parent;
    }

    private MenuItem BuildDataChangeItem(string type, string dataName, string label)
    {
        bool exists = _document!.Storyboards.Any(s => s.Matches(type, dataName));

        var item = new MenuItem
        {
            Header    = exists ? $"{label}  ✓" : label,
            IsEnabled = !exists,
        };

        if (!exists) item.Click += (_, _) => CreateStoryboard(type, dataName);
        return item;
    }

    private void CreateStoryboard(string type, string dataName = "")
    {
        if (_document is null) return;

        var storyboard = GtStoryboard.Create(type, dataName);
        _document.Storyboards.Add(storyboard);

        var document = _document;
        History?.Push(new PropertyChangeAction($"Add {storyboard.EventLabel} storyboard",
            undo: () =>
            {
                document.Storyboards.Remove(storyboard);
                RebuildStoryboardList();
                StoryboardEdited?.Invoke();
            },
            redo: () =>
            {
                document.Storyboards.Add(storyboard);
                RebuildStoryboardList();
                ShowStoryboard(storyboard);
                StoryboardEdited?.Invoke();
            }));

        RebuildStoryboardList();
        ShowStoryboard(storyboard);
        StoryboardEdited?.Invoke();
    }

    /// <summary>switches the picker to <paramref name="storyboard"/> on its own, never to a combined view that merely contains it</summary>
    private void ShowStoryboard(GtStoryboard storyboard) =>
        ShowView(_views.FindIndex(v => !v.IsCombined && v.Segments[0].Storyboard == storyboard));

    /// <summary>the user's own loop choice, parked while a Continuous view forces it on</summary>
    private bool _loopPreference;
    private bool _loopLocked;

    /// <summary>a Continuous storyboard has no end (vMix runs it for as long as the template is live) so the transport pins Loop on and disables the button rather than letting playback stop at an arbitrary point; the user's own setting comes back on any other view</summary>
    private void ApplyLoopLock(TimelineView? view)
    {
        bool locked = view?.AlwaysLoops == true;

        if (locked)
        {
            if (!_loopLocked)
            {
                _loopPreference = LoopButton.IsChecked == true;
                _loopLocked     = true;
            }

            LoopButton.IsChecked = true;
            LoopButton.IsEnabled = false;
            ToolTip.SetTip(LoopButton, "Continuous storyboards always loop");
            return;
        }

        if (_loopLocked)
        {
            LoopButton.IsChecked = _loopPreference;
            _loopLocked          = false;
        }

        LoopButton.IsEnabled = true;
        ToolTip.SetTip(LoopButton, "Loop playback");
    }

    public void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (_view is null) return;

        // playing implies wanting to see it, so the preview comes back on by itself
        PreviewEnabled = true;

        // restarting from the end feels like a replay rather than a no-op
        if (Track.CurrentTime >= Track.TimelineLength - 1e-3) SetTime(0);

        _playing       = true;
        _playStartTime = Track.CurrentTime;
        _playAnchor    = DateTime.UtcNow;
        PlayIcon.Source = PauseIconBitmap;
        _playTimer.Start();
        StartFpsSampling();
    }

    private void Pause()
    {
        _playing = false;
        _playTimer.Stop();
        PlayIcon.Source = PlayIconBitmap;
        StopFpsSampling();
    }

    private void Stop()
    {
        Pause();
        SetTime(0);
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        // wall-clock driven rather than tick-counted, so a dropped frame does not slow playback
        double elapsed = (DateTime.UtcNow - _playAnchor).TotalSeconds;
        double t       = _playStartTime + elapsed;
        double length  = Track.TimelineLength;

        if (t >= length)
        {
            if (LoopButton.IsChecked == true)
            {
                _playStartTime = 0;
                _playAnchor    = DateTime.UtcNow;
                t              = 0;
            }
            else
            {
                SetTime(length);
                Pause();
                return;
            }
        }

        SetTime(t);
    }

    /// <summary>sampling window in milliseconds, long enough to be steady short enough to react</summary>
    private const double FpsWindowMs = 400;

    private readonly DispatcherTimer _fpsTimer;
    private DateTime _fpsAnchor;
    private long _fpsRenderMark;
    private double _fpsMsMark;

    private void StartFpsSampling()
    {
        if (Canvas is null) return;

        ResetFpsWindow();
        FpsLabel.Text = "-- fps";
        FpsLabel.Foreground = FpsIdleBrush;
        _fpsTimer.Start();
    }

    /// <summary>leaves the last measurement on screen dimmed: after a pause it is the useful number, and a live counter reading 0 while nothing is animating would be noise</summary>
    private void StopFpsSampling()
    {
        _fpsTimer.Stop();
        FpsLabel.Foreground = FpsIdleBrush;
    }

    private void ResetFpsWindow()
    {
        _fpsAnchor     = DateTime.UtcNow;
        _fpsRenderMark = Canvas?.RenderCount  ?? 0;
        _fpsMsMark     = Canvas?.RenderMsTotal ?? 0;
    }

    private void OnFpsTick(object? sender, EventArgs e)
    {
        var canvas = Canvas;
        if (canvas is null) { _fpsTimer.Stop(); return; }

        double elapsed = (DateTime.UtcNow - _fpsAnchor).TotalSeconds;
        long frames    = canvas.RenderCount - _fpsRenderMark;
        double drawMs  = canvas.RenderMsTotal - _fpsMsMark;
        ResetFpsWindow();

        if (elapsed <= 0) return;

        double fps = frames / elapsed;
        // per-frame draw cost, not the frame budget: the gap between them is idle time
        double perFrameMs = frames > 0 ? drawMs / frames : 0;

        FpsLabel.Text = frames > 0
            ? $"{fps:0.0} fps · {perFrameMs:0.0} ms"
            : "0.0 fps";

        FpsLabel.Foreground = fps >= TargetFps * 0.9 ? FpsGoodBrush
                            : fps >= TargetFps * 0.6 ? FpsFairBrush
                            : FpsPoorBrush;
    }

    /// <summary>transport glyphs the play button swaps between, loaded once and shared</summary>
    private static readonly Bitmap PlayIconBitmap  = LoadIcon("play.png");
    private static readonly Bitmap PauseIconBitmap = LoadIcon("pause.png");

    private static Bitmap LoadIcon(string filename) =>
        new Bitmap(AssetLoader.Open(new Uri($"avares://GtPlus/Assets/Icons/{filename}")));

    private static readonly IBrush FpsIdleBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly IBrush FpsGoodBrush = new SolidColorBrush(Color.FromRgb(0x6f, 0xbf, 0x73));
    private static readonly IBrush FpsFairBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0xa2, 0x4d));
    private static readonly IBrush FpsPoorBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0x5d, 0x5d));

    /// <summary>false when the canvas should show the document at rest; a previewed frame moves, hides and fades objects away from their stored geometry so dragging one there would apply an offset the user cannot see, and switching the preview off puts every object back where the model says it is and makes the title editable again</summary>
    public bool PreviewEnabled
    {
        get => PreviewButton.IsChecked == true;
        set => PreviewButton.IsChecked = value;
    }

    /// <summary>raised whenever the preview gate flips, so mirrored buttons can follow it</summary>
    public event Action<bool>? PreviewEnabledChanged;

    /// <summary>pushes the previewed time to the host, or nothing at all when preview is off</summary>
    private void EmitPreview(double time) =>
        PreviewTimeChanged?.Invoke(
            PreviewEnabled ? CurrentSegments : Array.Empty<GtTimelineSegment>(),
            time);

    private void Preview_Changed(object? sender, RoutedEventArgs e)
    {
        // the XAML default fires this before the rest of the panel is built
        if (Track is null) return;

        // playing with the preview off would advance the clock over a static canvas
        if (!PreviewEnabled && _playing) Pause();
        EmitPreview(Track.CurrentTime);
        PreviewEnabledChanged?.Invoke(PreviewEnabled);
    }

    private void Play_Click(object? sender, RoutedEventArgs e) => TogglePlay();
    private void Stop_Click(object? sender, RoutedEventArgs e) => Stop();
    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void ZoomSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Track is not null) Track.PixelsPerSecond = e.NewValue;
    }

    private void SetTime(double seconds)
    {
        Track.CurrentTime = seconds;
        UpdateTimeLabel();
        EmitPreview(seconds);
    }

    private void OnTimeScrubbed(double seconds)
    {
        // a manual scrub takes over from playback
        if (_playing) Pause();

        // scrubbing is a request to see the animation, so it turns the preview back on
        if (!PreviewEnabled) PreviewEnabled = true;

        UpdateTimeLabel();
        EmitPreview(seconds);
    }

    private void UpdateTimeLabel() =>
        TimeLabel.Text = $"{Track.CurrentTime:0.00} / {ViewDuration:0.00}s";

    private static readonly Cursor ScrubCursor = new(StandardCursorType.SizeWestEast);

    /// <summary>three decimals: as fine as an Alt-modified scrub can reach</summary>
    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>clips a label scrub is driving, with the values they held before it started</summary>
    private List<GtAnimation>? _scrubTargets;
    private List<GtAnimation>? _scrubBefore;

    /// <summary>makes each timing label a horizontal drag handle, the same gesture the properties panel uses; the clip fields mutate the model live so the track and the preview follow the drag, and land on the history stack as a single entry when the drag ends</summary>
    private void BuildScrubHandles()
    {
        // the hold is preview state and never reaches the file, so it takes no history entry
        EnableScrub(HoldLabel, 0.01,
            begin:  () => _view?.UsesHold == true ? _hold : (double?)null,
            apply:  v => { HoldBox.Text = Fmt(Math.Max(0, v)); CommitHold(); },
            commit: () => { });

        EnableScrub(DelayLabel, 0.01,
            begin:  () => BeginClipScrub() ? _selected!.Delay : (double?)null,
            apply:  v => ApplyClipScrub(DelayBox, v, (a, x) => a.Delay = x),
            commit: () => EndClipScrub("Retime"));

        EnableScrub(DurationLabel, 0.01,
            begin:  () => _selected is { IsContinuousType: false } && BeginClipScrub()
                          ? _selected.EffectiveDuration : (double?)null,
            apply:  v => ApplyClipScrub(DurationBox, Math.Max(0.01, v), (a, x) => a.Duration = x),
            commit: () => EndClipScrub("Retime"));

        EnableScrub(SpeedLabel, 0.01,
            begin:  () => _selected is { IsContinuousType: true } && BeginClipScrub()
                          ? _selected.EffectiveSpeed : (double?)null,
            apply:  v => ApplyClipScrub(SpeedBox, Math.Max(0.01, v),
                                        (a, x) => { if (a.IsContinuousType) a.Speed = x; }),
            commit: () => EndClipScrub("Respeed"));
    }

    /// <summary>wires a horizontal drag on <paramref name="label"/>; <paramref name="begin"/> returns the value the drag starts from or null when the field has nothing to edit, and pointer travel is scaled by <paramref name="step"/> per pixel, Alt for fine and Shift for coarse</summary>
    private void EnableScrub(TextBlock label, double step,
                             Func<double?> begin, Action<double> apply, Action commit)
    {
        label.Cursor = ScrubCursor;

        bool dragging = false;
        double startX = 0;
        double startValue = 0;
        CursorScrub? scrub = null;   // null on platforms that cannot warp the cursor

        void EndDrag()
        {
            dragging = false;
            scrub?.Dispose();        // never leave the cursor hidden
            scrub = null;
        }

        label.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            if (begin() is not { } value) return;

            dragging   = true;
            startX     = e.GetPosition(label).X;
            startValue = value;
            scrub      = CursorScrub.Begin();
            e.Pointer.Capture(label);
            e.Handled  = true;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerMovedEvent, (object? _, PointerEventArgs e) =>
        {
            if (!dragging) return;

            // with a scrub active the cursor wraps around the monitor so travel comes from the OS in physical pixels, converting to DIPs keeps the feel identical either way
            double dx = scrub is not null
                ? scrub.UpdateX() / ((VisualRoot as TopLevel)?.RenderScaling ?? 1.0)
                : e.GetPosition(label).X - startX;

            double scale = e.KeyModifiers.HasFlag(KeyModifiers.Alt)   ? 0.1
                         : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10.0
                         : 1.0;

            apply(Math.Round(startValue + dx * step * scale, 3));
            e.Handled = true;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (!dragging) return;
            EndDrag();
            e.Pointer.Capture(null);
            commit();
            e.Handled = true;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerCaptureLostEvent, (object? _, PointerCaptureLostEventArgs e) =>
        {
            if (!dragging) return;
            EndDrag();
            commit();
        }, RoutingStrategies.Bubble);
    }

    /// <summary>snapshots the clips a scrub will drive, false when there is nothing selected</summary>
    private bool BeginClipScrub()
    {
        if (_selected is null || !ClipPropertiesEnabled) return false;

        _scrubTargets = SelectedAnimations.Count > 0
            ? SelectedAnimations.ToList()
            : new List<GtAnimation> { _selected };
        _scrubBefore = _scrubTargets.Select(a => a.Clone()).ToList();
        return true;
    }

    /// <summary>applies one scrub step to every selected clip and redraws off the new values</summary>
    private void ApplyClipScrub(TextBox box, double value, Action<GtAnimation, double> set)
    {
        if (_scrubTargets is null) return;

        using (SuppressEvents()) box.Text = Fmt(value);
        foreach (var target in _scrubTargets) set(target, value);
        AfterModelChange();
    }

    /// <summary>pushes the whole scrub onto the history stack as one entry</summary>
    private void EndClipScrub(string verb)
    {
        var targets = _scrubTargets;
        var befores = _scrubBefore;
        _scrubTargets = null;
        _scrubBefore  = null;
        if (targets is null || befores is null || targets.Count == 0) return;

        bool changed = false;
        for (int i = 0; i < targets.Count; i++)
            if (!Equivalent(targets[i], befores[i])) { changed = true; break; }
        if (!changed) return;

        var afters = targets.Select(a => a.Clone()).ToList();
        var description = targets.Count > 1
            ? $"{verb} {targets.Count} animations"
            : $"{verb} {targets[0].TypeName} animation";

        PushEdit(description, targets, befores, afters);
        if (_selected is not null) LoadPropertyStrip(_selected);
    }

    /// <summary>pre-roll growing or shrinking under a bar drag moves the whole track sideways, the scroll offset absorbs it so the clips stay under the pointer where the user left them</summary>
    private void OnTrackContentShifted(double dx)
    {
        // the track re-measures on the next layout pass, so the offset waits for it
        Dispatcher.UIThread.Post(() =>
        {
            var offset = TrackScroller.Offset;
            TrackScroller.Offset = new Vector(Math.Max(0, offset.X + dx), offset.Y);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>the track already holds the new selection, so this only mirrors it</summary>
    private void OnAnimationSelected(GtAnimation? anim, GtStoryboard? storyboard) =>
        SyncSelection(anim, storyboard);

    /// <summary>replaces the track's selection with a single animation (or nothing)</summary>
    private void SelectAnimation(GtAnimation? anim, GtStoryboard? storyboard = null)
    {
        Track.SelectedAnimation = anim;
        SyncSelection(anim, storyboard);
    }

    /// <summary>points the strip at the primary selection; edits made in the strip are applied to every animation in <see cref="SelectedAnimations"/> so the badge reports how many that is</summary>
    private void SyncSelection(GtAnimation? anim, GtStoryboard? storyboard = null)
    {
        // a combined view holds animations from two storyboards, so the owner is tracked alongside the selection: edits and the per-object limit are storyboard-scoped
        _selectedStoryboard = storyboard
                              ?? (anim is null ? null : StoryboardOf(anim));
        _selected = anim;

        int count = SelectedAnimations.Count;
        DeleteButton.IsEnabled   = count > 0;
        // the strip stays put with nothing selected, greyed out rather than gone so the track below it never jumps as clips are selected and deselected
        SetClipPropertiesEnabled(anim is not null);
        SelectionBadge.IsVisible = count > 1;
        SelectionLabel.Text      = count > 1 ? $"{count} clips selected" : "";

        if (anim is not null) LoadPropertyStrip(anim);
    }

    /// <summary>every clip the strip edits: the whole track selection, primary last</summary>
    private IReadOnlyList<GtAnimation> SelectedAnimations => Track.SelectedAnimations;

    /// <summary>the clip controls are spread over the bar's grid cells so their columns line up down the two rows; they carry one enable state between them so a selection greys or lights the lot</summary>
    private bool ClipPropertiesEnabled => ObjectCombo.IsEnabled;

    private void SetClipPropertiesEnabled(bool enabled)
    {
        foreach (var control in new Control[]
                 {
                     SelectionBadge,
                     ObjectLabel, ObjectCombo, TypeLabel, TypeCombo,
                     DelayLabel, DelayBox, DurationLabel, DurationBox,
                     EasingLabel, EasingCombo, SpeedLabel, SpeedBox,
                     ClipPropertiesDirection,
                 })
            control.IsEnabled = enabled;
    }

    private GtStoryboard? StoryboardOf(GtAnimation anim) =>
        VisibleStoryboards.FirstOrDefault(s => s.Animations.Contains(anim));

    /// <summary>direction the pad is showing, the strip's stand-in for the old combo box</summary>
    private GtAnimDirection _padDirection = GtAnimDirection.None;

    private readonly List<(ToggleButton Button, GtAnimDirection Direction)> _directionPad = new();

    private void BuildDirectionPad()
    {
        // GT's pad has nine buttons; None is in the enum but has no button and is only reachable from hand-edited XML, an animation carrying it simply lights nothing up
        _directionPad.Add((DirTopLeft,     GtAnimDirection.TopLeft));
        _directionPad.Add((DirTop,         GtAnimDirection.Top));
        _directionPad.Add((DirTopRight,    GtAnimDirection.TopRight));
        _directionPad.Add((DirLeft,        GtAnimDirection.Left));
        _directionPad.Add((DirCenter,      GtAnimDirection.Center));
        _directionPad.Add((DirRight,       GtAnimDirection.Right));
        _directionPad.Add((DirBottomLeft,  GtAnimDirection.BottomLeft));
        _directionPad.Add((DirBottom,      GtAnimDirection.Bottom));
        _directionPad.Add((DirBottomRight, GtAnimDirection.BottomRight));
    }

    private void ShowPadDirection(GtAnimDirection direction)
    {
        _padDirection = direction;
        foreach (var (button, value) in _directionPad)
            button.IsChecked = value == direction;
    }

    private void DirectionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag ||
            !Enum.TryParse<GtAnimDirection>(tag, out var direction))
            return;

        // the pad is a radio group: clicking the lit button re-checks it rather than turning the direction off, since GT has no "no direction" state for these types
        ShowPadDirection(direction);

        if (_suppressPropertyEvents || _selected is null) return;
        CommitPropertyStrip();
    }

    private void PopulateStaticCombos()
    {
        // only the types this editor can author are offered; an unrecognised type read from a file still round-trips it just cannot be re-picked from this list, and None is GT's do-nothing placeholder never worth authoring
        TypeCombo.ItemsSource = Enum.GetValues(typeof(GtAnimationType))
            .Cast<GtAnimationType>()
            .Where(t => t != GtAnimationType.Unknown && t != GtAnimationType.None)
            .Select(t => t.ToString())
            .ToList();

        EasingCombo.ItemsSource = Enum.GetValues(typeof(GtInterpolation))
            .Cast<GtInterpolation>().Select(i => i.ToString()).ToList();

        AxisCombo.ItemsSource = Enum.GetValues(typeof(GtCenterAxis))
            .Cast<GtCenterAxis>().Select(a => a.ToString()).ToList();
    }

    private void RefreshObjectCombo()
    {
        var names = new List<string>();
        if (_document is not null)
            foreach (var layer in _document.Layers)
            {
                if (!string.IsNullOrEmpty(layer.Name)) names.Add(layer.Name);
                foreach (var el in layer.Elements)
                    if (!string.IsNullOrEmpty(el.Name)) names.Add(el.Name);
            }

        var previous = ObjectCombo.SelectedItem as string;
        using (SuppressEvents())
        {
            ObjectCombo.ItemsSource = names;
            if (previous is not null && names.Contains(previous)) ObjectCombo.SelectedItem = previous;
        }
    }

    private void LoadPropertyStrip(GtAnimation anim)
    {
        using (SuppressEvents())
        {
            RefreshObjectCombo();

            TypeCombo.SelectedItem   = anim.Type == GtAnimationType.Unknown ? null : anim.Type.ToString();
            ObjectCombo.SelectedItem = anim.Object;
            DelayBox.Text            = Fmt(anim.Delay);
            DurationBox.Text         = Fmt(anim.EffectiveDuration);
            SpeedBox.Text            = Fmt(anim.EffectiveSpeed);
            EasingCombo.SelectedItem = anim.Interpolation.ToString();
            AxisCombo.SelectedItem   = anim.CenterAxis.ToString();
            ReverseCheck.IsChecked   = anim.Reverse;

            ApplyTypeAvailability(anim);
        }
    }

    /// <summary>greys out the controls the selected clip's type cannot use; nothing is hidden, the strip keeps the same shape for every clip so controls never jump sideways as the selection moves and a setting that does not apply reads as unavailable rather than missing</summary>
    private void ApplyTypeAvailability(GtAnimation anim)
    {
        // only the nine direction-builder animations can carry a direction; the rest keep the pad greyed out with the centre dot lit, which is how GT spells "no direction"
        bool directional = GtAnimation.SupportsDirection(anim.Type);
        ShowPadDirection(directional ? anim.Direction : GtAnimDirection.Center);
        foreach (var (button, _) in _directionPad) button.IsEnabled = directional;

        // Reveal's Center axis is the one direction-conditional extra option GT has
        AxisCombo.IsEnabled = GtAnimation.SupportsCenterAxis(anim.Type) &&
                              anim.Direction == GtAnimDirection.Center;

        // continuous animations run forever off a per-second Speed instead of a Duration
        bool continuous = anim.IsContinuousType;
        SpeedLabel.IsEnabled    = continuous;
        SpeedBox.IsEnabled      = continuous;
        DurationLabel.IsEnabled = !continuous;
        DurationBox.IsEnabled   = !continuous;

        // the frame-count helper only means anything when the clip actually drives a sequence of more than one frame
        SequenceLengthButton.IsEnabled = SequenceFrameCount(anim) > 1;
    }

    /// <summary>frame rate the sequence-length helper offers, remembered for the session after the first use</summary>
    private double _sequenceFps = DefaultSequenceFps;

    /// <summary>frame rate a title runs at unless the user says otherwise; vMix renders GT titles at 60</summary>
    private const double DefaultSequenceFps = 60;

    /// <summary>frames behind the sequence the clip's object draws, 0 when the clip is not a sequence animation or its object is not an image element anchored on one</summary>
    private int SequenceFrameCount(GtAnimation? anim)
    {
        if (anim is null) return 0;
        if (anim.Type != GtAnimationType.ImageSequence && anim.Type != GtAnimationType.ImageSequenceLoop)
            return 0;

        return SequenceFrameCountOf(ElementNamed(anim.Object));
    }

    /// <summary>frames behind the sequence an element draws, 0 when it is not an image anchored on a multi-frame sequence</summary>
    private int SequenceFrameCountOf(GtElement? element)
    {
        if (element is not GtImageElement img || img.BitmapSource is null) return 0;

        var assets = Canvas?.Assets;
        if (assets is null) return 0;

        // FrameCount reports 1 for an ordinary still, which is not a sequence to fit
        int count = assets.FrameCount(img.BitmapSource);
        return count > 1 ? count : 0;
    }

    private GtElement? ElementNamed(string name)
    {
        if (_document is null || string.IsNullOrEmpty(name)) return null;

        foreach (var layer in _document.Layers)
            foreach (var el in layer.Elements)
                if (string.Equals(el.Name, name, StringComparison.OrdinalIgnoreCase))
                    return el;

        return null;
    }

    private void SequenceLength_Click(object? sender, RoutedEventArgs e)
    {
        var anim = _selected;
        if (anim is null) return;

        // the whole selection is retimed, matching how every other control in the strip applies to the group
        var targets = SelectedAnimations.Where(a => SequenceFrameCount(a) > 1).ToList();
        if (targets.Count == 0) targets.Add(anim);

        _ = FitSequenceLengthAsync(anim, targets);
    }

    /// <summary>sets clip length from the sequence's frame count and a frame rate the user picks: an ImageSequence fits the whole sequence into Duration (and an ImageSequenceLoop makes one loop that long), so frames / fps is the length that plays it at one frame per title frame; each target is retimed against its own frame count so one fps answers for a whole group</summary>
    private async System.Threading.Tasks.Task FitSequenceLengthAsync(
        GtAnimation anim, IReadOnlyList<GtAnimation> targets)
    {
        int frames = SequenceFrameCount(anim);
        if (frames <= 1) return;

        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new SequenceLengthWindow(anim.Object, frames, _sequenceFps, anim.EffectiveDuration);
        if (!await dialog.ShowDialog<bool>(owner)) return;

        _sequenceFps = dialog.Fps;

        var befores = targets.Select(t => t.Clone()).ToList();
        foreach (var target in targets)
            target.Duration = Math.Max(0.01, Math.Round(SequenceFrameCount(target) / _sequenceFps, 3));
        var afters = targets.Select(t => t.Clone()).ToList();

        var description = targets.Count > 1
            ? $"Fit {targets.Count} sequences to {Fmt(_sequenceFps)} fps"
            : $"Fit {anim.TypeName} to {Fmt(_sequenceFps)} fps";

        PushEdit(description, targets, befores, afters);

        LoadPropertyStrip(anim);
        AfterModelChange();
    }

    /// <summary>true when an ImageSequence clip can be added for <paramref name="element"/>: it draws a multi-frame sequence and the timeline is showing a storyboard to hold the clip; with no storyboard picked there is nothing to add to, so the entry points grey out rather than inventing one</summary>
    public bool CanAddSequenceAnimation(GtElement? element) =>
        SequenceFrameCountOf(element) > 1 && TargetStoryboard is not null;

    /// <summary>the storyboard a new clip lands on: the half of a combined view the selection sits in, else the first one shown</summary>
    private GtStoryboard? TargetStoryboard => _selectedStoryboard ?? VisibleStoryboards.FirstOrDefault();

    /// <summary>adds an ImageSequence clip for the element on the storyboard currently shown and opens the length helper straight away, since the only length worth having is frames / fps</summary>
    public void AddSequenceAnimation(GtElement? element)
    {
        if (element is null) return;

        int frames = SequenceFrameCountOf(element);
        if (frames <= 1) return;

        var storyboard = TargetStoryboard;
        if (storyboard is null) return;

        if (!EnsureRoomFor(storyboard, element.Name)) return;

        // a Continuous storyboard runs only never-ending types, where the looping sequence is the one that belongs
        bool loop = storyboard.IsContinuousEvent;

        var anim = new GtAnimation
        {
            TypeName = loop ? "ImageSequenceLoop" : "ImageSequence",
            Type     = loop ? GtAnimationType.ImageSequenceLoop : GtAnimationType.ImageSequence,
            Object   = element.Name,
            Duration = Math.Max(0.01, Math.Round(frames / _sequenceFps, 3)),
        };

        var owner = storyboard;
        owner.Animations.Add(anim);
        History?.Push(new PropertyChangeAction($"Add {anim.TypeName} animation",
            undo: () => { owner.Animations.Remove(anim); AfterModelChange(); SelectAnimation(null); },
            redo: () => { owner.Animations.Add(anim);    AfterModelChange(); SelectAnimation(anim, owner); }));

        AfterModelChange();
        SelectAnimation(anim, owner);

        // the clip is only useful once it is the right length, so the helper opens on the new clip without a second click
        _ = FitSequenceLengthAsync(anim, new[] { anim });
    }

    private void AddSequenceAnimation_Click(object? sender, RoutedEventArgs e) =>
        AddSequenceAnimation(SelectedElementProvider?.Invoke());

    /// <summary>re-reads what the canvas has selected; the host calls it when the selection moves so the sequence shortcut greys in step with it</summary>
    public void RefreshSelectionState() =>
        AddSequenceButton.IsEnabled = CanAddSequenceAnimation(SelectedElementProvider?.Invoke());

    private void AnimationProperty_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is null) return;
        CommitPropertyStrip();
    }

    private void AnimationTiming_Committed(object? sender, RoutedEventArgs e)
    {
        if (_suppressPropertyEvents || _selected is null) return;
        CommitPropertyStrip();
    }

    private void TimingBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_selected is null) return;

        if (e.Key == Key.Enter)
        {
            CommitPropertyStrip();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        LoadPropertyStrip(_selected);
        e.Handled = true;
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    /// <summary>applies every field of the strip as one undoable edit; reading all fields at once keeps a single history entry per user action instead of one per control, and with several clips selected only the fields the user actually changed are copied across (the untouched ones keep each clip's own value) so editing the easing of a group does not flatten their delays onto one another</summary>
    private void CommitPropertyStrip()
    {
        var anim = _selected;
        if (anim is null) return;

        var before = anim.Clone();
        var after  = anim.Clone();

        if (TypeCombo.SelectedItem is string typeName &&
            Enum.TryParse<GtAnimationType>(typeName, out var type))
        {
            after.Type     = type;
            after.TypeName = typeName;
        }

        if (ObjectCombo.SelectedItem is string objectName)
        {
            // retargeting counts against the new object's budget; the clips being moved are excluded so re-picking their current object is never blocked
            if (!string.Equals(objectName, anim.Object, StringComparison.OrdinalIgnoreCase) &&
                !EnsureRoomForGroup(SelectedAnimations, objectName))
            {
                LoadPropertyStrip(anim);
                return;
            }

            after.Object = objectName;
        }

        // negative delays are legal: vMix computes the animation before the title goes live, so it is already part-way through at time zero
        if (double.TryParse(DelayBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay))
            after.Delay = Math.Round(delay, 3);

        if (double.TryParse(DurationBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            after.Duration = Math.Max(0.01, duration);

        // Speed only means anything to a continuous animation, switching away from one drops it rather than leaving a stale attribute on a fixed animation
        if (!GtAnimation.IsContinuous(after.Type))
            after.Speed = null;
        else if (double.TryParse(SpeedBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            after.Speed = Math.Max(0.01, speed);

        if (EasingCombo.SelectedItem is string easing &&
            Enum.TryParse<GtInterpolation>(easing, out var interpolation))
            after.Interpolation = interpolation;

        if (!GtAnimation.SupportsDirection(after.Type))
        {
            // switching to a non-directional type drops any direction the old type carried
            after.Direction = GtAnimation.DefaultDirectionFor(after.Type);
        }
        else if (!GtAnimation.SupportsDirection(before.Type))
        {
            // the pad was greyed out for the old type so it holds no real choice, the new type starts on its own default instead of inheriting the greyed-out centre
            after.Direction = GtAnimation.DefaultDirectionFor(after.Type);
        }
        else if (_padDirection != GtAnimDirection.None)
        {
            // each type has its own implicit default (Bottom for Scroll, Left otherwise); when the type changes while the direction is still sitting on the old type's default, the direction follows the new type instead of freezing into an explicit override
            after.Direction = after.Type != before.Type &&
                              _padDirection == GtAnimation.DefaultDirectionFor(before.Type)
                ? GtAnimation.DefaultDirectionFor(after.Type)
                : _padDirection;
        }

        if (!GtAnimation.SupportsCenterAxis(after.Type))
            after.CenterAxis = GtCenterAxis.Both;
        else if (AxisCombo.SelectedItem is string axisName &&
                 Enum.TryParse<GtCenterAxis>(axisName, out var axis))
            after.CenterAxis = axis;

        after.Reverse = ReverseCheck.IsChecked == true;

        if (Equivalent(before, after))
        {
            AxisCombo.IsEnabled = GtAnimation.SupportsCenterAxis(after.Type) &&
                                  after.Direction == GtAnimDirection.Center;
            return;
        }

        var targets = SelectedAnimations.ToList();
        if (targets.Count == 0) targets.Add(anim);

        var befores = targets.Select(t => t.Clone()).ToList();
        foreach (var target in targets) ApplyChangedFields(target, before, after);
        var afters = targets.Select(t => t.Clone()).ToList();

        var description = targets.Count > 1
            ? $"Edit {targets.Count} animations"
            : $"Edit {anim.TypeName} animation";

        PushEdit(description, targets, befores, afters);

        // reload rather than just toggling the axis box: a type change can move the direction
        LoadPropertyStrip(anim);
        AfterModelChange();
    }

    /// <summary>copies onto <paramref name="target"/> only the fields the user actually changed in the strip, the difference between <paramref name="before"/> and <paramref name="after"/>; type-conditional fields are skipped for targets whose type cannot carry them so a direction never lands on a Fade</summary>
    private static void ApplyChangedFields(GtAnimation target, GtAnimation before, GtAnimation after)
    {
        bool typeChanged = after.Type != before.Type || after.TypeName != before.TypeName;

        if (typeChanged)              { target.Type = after.Type; target.TypeName = after.TypeName; }
        if (after.Object != before.Object)               target.Object        = after.Object;
        if (after.Delay != before.Delay)                 target.Delay         = after.Delay;
        if (after.Duration != before.Duration)           target.Duration      = after.Duration;
        if (after.Interpolation != before.Interpolation) target.Interpolation = after.Interpolation;
        if (after.Reverse != before.Reverse)             target.Reverse       = after.Reverse;

        if (after.Direction != before.Direction && GtAnimation.SupportsDirection(target.Type))
            target.Direction = after.Direction;
        if (after.CenterAxis != before.CenterAxis && GtAnimation.SupportsCenterAxis(target.Type))
            target.CenterAxis = after.CenterAxis;
        if (after.Speed != before.Speed && GtAnimation.IsContinuous(target.Type))
            target.Speed = after.Speed;

        // a retyped clip drops whatever the old type carried that the new one cannot
        if (!typeChanged) return;
        if (!GtAnimation.SupportsDirection(target.Type))
            target.Direction = GtAnimation.DefaultDirectionFor(target.Type);
        if (!GtAnimation.SupportsCenterAxis(target.Type)) target.CenterAxis = GtCenterAxis.Both;
        if (!GtAnimation.IsContinuous(target.Type))       target.Speed      = null;
    }

    /// <summary>true when every clip in <paramref name="targets"/> can be retargeted to <paramref name="objectName"/> without breaking GT's per-object limit; the clips being moved are discounted so re-picking an object they already sit on is never blocked</summary>
    private bool EnsureRoomForGroup(IReadOnlyList<GtAnimation> targets, string objectName)
    {
        foreach (var storyboard in VisibleStoryboards)
        {
            int moving = targets.Count(a => !a.IsPlaceholder && storyboard.Animations.Contains(a));
            if (moving == 0) continue;

            int existing = storyboard.Animations.Count(
                a => !a.IsPlaceholder && !targets.Contains(a) &&
                     string.Equals(a.Object, objectName, StringComparison.OrdinalIgnoreCase));

            if (existing + moving <= GtStoryboard.MaxAnimationsPerObject) continue;

            var name = string.IsNullOrEmpty(objectName) ? "(no object)" : objectName;
            ShowWarning($"⚠ \"{name}\" cannot hold {existing + moving} animations - " +
                        $"GT Title Designer's limit is {GtStoryboard.MaxAnimationsPerObject} per object.",
                        transient: true);
            return false;
        }

        return true;
    }

    private static bool Equivalent(GtAnimation a, GtAnimation b) =>
        a.TypeName == b.TypeName && a.Type == b.Type && a.Object == b.Object &&
        a.Delay == b.Delay && a.Duration == b.Duration && a.Reverse == b.Reverse &&
        a.Interpolation == b.Interpolation && a.Direction == b.Direction &&
        a.CenterAxis == b.CenterAxis && a.Speed == b.Speed;

    private static void ApplyTo(GtAnimation target, GtAnimation source)
    {
        target.TypeName      = source.TypeName;
        target.Type          = source.Type;
        target.Object        = source.Object;
        target.Delay         = source.Delay;
        target.Duration      = source.Duration;
        target.Reverse       = source.Reverse;
        target.Interpolation = source.Interpolation;
        target.Direction     = source.Direction;
        target.CenterAxis    = source.CenterAxis;
        target.Speed         = source.Speed;
    }

    /// <summary>one history entry for the whole edited group, restoring each clip's own values</summary>
    private void PushEdit(string description, IReadOnlyList<GtAnimation> targets,
                          IReadOnlyList<GtAnimation> befores, IReadOnlyList<GtAnimation> afters)
    {
        void Restore(IReadOnlyList<GtAnimation> snapshots)
        {
            for (int i = 0; i < targets.Count; i++) ApplyTo(targets[i], snapshots[i]);
            AfterModelChange();
            if (_selected is not null) RefreshStripIfSelected(_selected);
        }

        History?.Push(new PropertyChangeAction(description,
            undo: () => Restore(befores),
            redo: () => Restore(afters)));
    }

    private void RefreshStripIfSelected(GtAnimation anim)
    {
        if (ReferenceEquals(anim, _selected)) LoadPropertyStrip(anim);
    }

    /// <summary>a drag can move a whole group of bars at once, so the pre-drag timings arrive together and go onto the history stack as one entry</summary>
    private void OnAnimationsTimingChanged(IReadOnlyList<TimelineTrackControl.AnimationTiming> before)
    {
        var after = before
            .Select(b => new TimelineTrackControl.AnimationTiming(b.Animation, b.Animation.Delay, b.Animation.Duration))
            .ToList();

        void Restore(IReadOnlyList<TimelineTrackControl.AnimationTiming> snapshots)
        {
            foreach (var entry in snapshots)
            {
                entry.Animation.Delay    = entry.Delay;
                entry.Animation.Duration = entry.Duration;
            }

            AfterModelChange();
            if (_selected is not null) RefreshStripIfSelected(_selected);
        }

        var description = before.Count > 1
            ? $"Retime {before.Count} animations"
            : $"Retime {before[0].Animation.TypeName} animation";

        History?.Push(new PropertyChangeAction(description,
            undo: () => Restore(before),
            redo: () => Restore(after)));

        AfterModelChange();
        if (_selected is not null) RefreshStripIfSelected(_selected);
    }

    private void AddAnimation_Click(object? sender, RoutedEventArgs e) => AddAnimations(null);

    /// <summary>right-clicking the button picks the type instead of taking the storyboard's default one; only the types the target storyboard can actually play are offered, since a Continuous storyboard runs the never-ending animations and every other storyboard runs the fixed ones</summary>
    private void AddAnimation_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_document is null) return;

        bool continuous = TargetStoryboard?.IsContinuousEvent == true;

        var menu = new MenuFlyout();
        foreach (var type in Enum.GetValues<GtAnimationType>())
        {
            // Unknown is the reader's catch-all and None is GT's do-nothing placeholder, neither is worth authoring
            if (type is GtAnimationType.Unknown or GtAnimationType.None) continue;
            if (GtAnimation.IsContinuous(type) != continuous) continue;

            var chosen = type;
            var item = new MenuItem { Header = type.ToString() };
            item.Click += (_, _) => AddAnimations(chosen);
            menu.Items.Add(item);
        }

        menu.ShowAt(AddButton);
        e.Handled = true;
    }

    /// <summary>objects a new animation targets: every selected element (or the selected layer), so adding one with a group selected gives each object its own clip; falls back to the single-object providers and finally to the first layer when the canvas has nothing selected</summary>
    private List<string> TargetObjectNames()
    {
        var names = new List<string>();

        foreach (var name in SelectedObjectNamesProvider?.Invoke() ?? Array.Empty<string>())
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);

        if (names.Count > 0) return names;

        var single = SelectedObjectNameProvider?.Invoke();
        if (string.IsNullOrEmpty(single)) single = SelectedElementProvider?.Invoke()?.Name;
        if (string.IsNullOrEmpty(single)) single = _document?.Layers.FirstOrDefault()?.Name;

        names.Add(single ?? "");
        return names;
    }

    /// <summary>adds one clip per selected object to the storyboard on the timeline, of <paramref name="type"/> or the storyboard's own default when null; the whole group is one history entry and comes back selected, so the property strip edits them together</summary>
    private void AddAnimations(GtAnimationType? type)
    {
        if (_document is null) return;

        var names = TargetObjectNames();

        // in a combined view the animation joins whichever half the selection is in, falling back to the first (TransitionIn) when nothing is selected
        var storyboard = _selectedStoryboard ?? VisibleStoryboards.FirstOrDefault();

        // checked before a storyboard is created so a rejected add leaves nothing behind
        if (storyboard is not null && !TrimToObjectsWithRoom(storyboard, names)) return;

        // adding an animation implies a storyboard to hold it
        if (storyboard is null)
        {
            storyboard = new GtStoryboard();
            var created = storyboard;
            _document.Storyboards.Add(created);
            History?.Push(new PropertyChangeAction("Add storyboard",
                undo: () => { _document.Storyboards.Remove(created); RebuildStoryboardList(); },
                redo: () => { _document.Storyboards.Add(created);    RebuildStoryboardList(); }));
            RebuildStoryboardList();
            StoryboardCombo.SelectedIndex = _views.FindIndex(v => !v.IsCombined && v.Segments[0].Storyboard == created);
        }

        // a Continuous storyboard only ever runs the never-ending animation types, so a new row there starts as one instead of a Fade GT would not play
        var chosen = type ?? (storyboard.IsContinuousEvent
            ? GtAnimationType.RotateContinuous
            : GtAnimationType.Fade);

        var added = names.Select(name => NewAnimation(chosen, name)).ToList();
        var owner = storyboard;

        void Add()
        {
            foreach (var anim in added) owner.Animations.Add(anim);
            AfterModelChange();
            Track.SetSelection(added);
            SyncSelection(Track.SelectedAnimation, owner);
        }

        void Remove()
        {
            foreach (var anim in added) owner.Animations.Remove(anim);
            AfterModelChange();
            SelectAnimation(null);
        }

        Add();

        var description = added.Count > 1
            ? $"Add {added.Count} {added[0].TypeName} animations"
            : $"Add {added[0].TypeName} animation";

        History?.Push(new PropertyChangeAction(description, undo: Remove, redo: Add));
    }

    /// <summary>a clip of <paramref name="type"/> aimed at <paramref name="objectName"/>, carrying the defaults GT applies: a continuous type runs off a Speed and never ends, everything else off a Duration</summary>
    private static GtAnimation NewAnimation(GtAnimationType type, string objectName)
    {
        bool continuous = GtAnimation.IsContinuous(type);

        return new GtAnimation
        {
            TypeName      = type.ToString(),
            Type          = type,
            Object        = objectName,
            Direction     = GtAnimation.DefaultDirectionFor(type),
            Duration      = continuous ? null : GtAnimation.DefaultDuration,
            Speed         = continuous ? GtAnimation.DefaultSpeed : null,
            Interpolation = continuous ? GtInterpolation.Linear : GtInterpolation.CubicEasingInOut,
        };
    }

    /// <summary>drops the objects already at GT's per-object limit out of <paramref name="names"/> and names them in a warning, so one full object in a group does not sink the whole add; false when none of them has room and there is nothing to add</summary>
    private bool TrimToObjectsWithRoom(GtStoryboard storyboard, List<string> names)
    {
        var full = names.Where(n => !storyboard.HasRoomFor(n)).ToList();
        if (full.Count == 0) return true;

        names.RemoveAll(n => !storyboard.HasRoomFor(n));

        var listed = string.Join(", ",
            full.Select(n => $"\"{(string.IsNullOrEmpty(n) ? "(no object)" : n)}\""));
        ShowWarning($"⚠ {listed} already at {GtStoryboard.MaxAnimationsPerObject} animations - " +
                    "GT Title Designer's limit per object.", transient: true);

        return names.Count > 0;
    }

    /// <summary>muting is editor state not an edit: the preview is rebuilt without the clip, but nothing is pushed onto the history stack and the file stays clean</summary>
    private void OnMuteToggled(GtAnimation anim) => EmitPreview(Track.CurrentTime);

    private void DeleteAnimation_Click(object? sender, RoutedEventArgs e) => DeleteSelectedClips();

    /// <summary>deletes every selected clip as one undoable edit; false when the timeline has no selection, which lets the window's Delete key fall through to the canvas</summary>
    public bool DeleteSelectedClips()
    {
        // removals are recorded with their original index and replayed in ascending order on undo, so a group delete comes back in the positions it left from
        var removed = new List<(GtStoryboard Storyboard, int Index, GtAnimation Animation)>();

        foreach (var anim in SelectedAnimations)
        {
            var storyboard = StoryboardOf(anim);
            if (storyboard is null) continue;

            int index = storyboard.Animations.IndexOf(anim);
            if (index >= 0) removed.Add((storyboard, index, anim));
        }

        if (removed.Count == 0) return false;

        var ascending  = removed.OrderBy(r => r.Index).ToList();
        var descending = removed.OrderByDescending(r => r.Index).ToList();

        void Remove()
        {
            foreach (var (storyboard, index, _) in descending) storyboard.Animations.RemoveAt(index);
            AfterModelChange();
            SelectAnimation(null);
        }

        void Restore()
        {
            foreach (var (storyboard, index, anim) in ascending) storyboard.Animations.Insert(index, anim);
            AfterModelChange();
            Track.SetSelection(ascending.Select(r => r.Animation));
            SyncSelection(Track.SelectedAnimation);
        }

        Remove();

        var description = removed.Count > 1
            ? $"Delete {removed.Count} animations"
            : $"Delete {removed[0].Animation.TypeName} animation";

        History?.Push(new PropertyChangeAction(description, undo: Restore, redo: Remove));
        return true;
    }

    /// <summary>redraws the track and re-applies the preview so edits show immediately</summary>
    private void AfterModelChange()
    {
        // retiming the in half moves where the out half starts, so offsets are recomputed first
        SyncSegmentOffsets();
        Track.Refresh();
        UpdateTimeLabel();
        ReportOverLimitObjects();
        // the picker's dot indicator tracks the animation counts, so it is refreshed in place rather than by rebuilding the list which would drop the playhead and the selection
        foreach (var view in _views) view.RefreshIndicator();
        EmitPreview(Track.CurrentTime);
        StoryboardEdited?.Invoke();
    }

    /// <summary>re-places the out-phase segments after the in-phase ones; a view can hold more than one storyboard per phase (a DataChange pair overlays the unscoped storyboard on the scoped one) so the out phase starts after the longest of the in-phase storyboards</summary>
    private void SyncSegmentOffsets()
    {
        if (_view is null) return;

        int start = _view.OutPhaseStart;
        if (start <= 0 || start >= _view.Segments.Count) return;

        double inEnd = 0;
        for (int i = 0; i < start; i++)
        {
            double end = _view.Segments[i].Storyboard.Duration;
            if (end > inEnd) inEnd = end;
        }

        double offset = inEnd + (_view.UsesHold ? _hold : 0);
        for (int i = start; i < _view.Segments.Count; i++)
            _view.Segments[i].Offset = offset;
    }
}

/// <summary>one entry in the timeline's storyboard picker: a storyboard on its own, or the combined TransitionIn + TransitionOut pair; the animation-count properties are what the picker's dot indicator binds to, so they raise change notifications when the model is edited</summary>
public sealed class TimelineView : INotifyPropertyChanged
{
    public TimelineView(
        string label,
        List<GtTimelineSegment> segments,
        int outPhaseStart = -1,
        bool usesHold = false)
    {
        Label         = label;
        Segments      = segments;
        OutPhaseStart = outPhaseStart;
        UsesHold      = usesHold;
    }

    public string Label { get; }
    public List<GtTimelineSegment> Segments { get; }

    /// <summary>index of the first segment belonging to the out phase, or -1 for a view that has only one phase; everything before it starts at zero, everything from it on is re-placed after the in phase whenever a storyboard's length changes</summary>
    public int OutPhaseStart { get; }

    /// <summary>true for a view that plays more than one storyboard</summary>
    public bool IsCombined => Segments.Count > 1;

    /// <summary>true when the gap between the two halves is the user's own: the template stays live between TransitionIn and TransitionOut for as long as the operator leaves it up; a DataChange pair has no such gap, the out half starts the moment the in half ends</summary>
    public bool UsesHold { get; }

    /// <summary>animations across every storyboard on this view, placeholders excluded</summary>
    public int AnimationCount
    {
        get
        {
            int count = 0;
            foreach (var segment in Segments) count += segment.Storyboard.AnimationCount;
            return count;
        }
    }

    public bool HasAnimations => AnimationCount > 0;

    /// <summary>count shown beside the label, blank rather than "0" for an empty storyboard</summary>
    public string CountText =>
        AnimationCount > 0 ? AnimationCount.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>continuous storyboards run for as long as the template is live, so the timeline plays them looped and takes the choice away from the transport</summary>
    public bool AlwaysLoops
    {
        get
        {
            if (Segments.Count == 0) return false;
            foreach (var segment in Segments)
                if (!segment.Storyboard.IsContinuousEvent) return false;
            return true;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>re-reads the animation counts after the storyboard has been edited</summary>
    public void RefreshIndicator()
    {
        Raise(nameof(AnimationCount));
        Raise(nameof(HasAnimations));
        Raise(nameof(CountText));
    }

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
