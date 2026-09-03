using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GtPlus.Models;
using GtPlus.Services;
using GtPlus.Views;

namespace GtPlus.Controls;

public partial class PropertiesPanelControl : UserControl
{
    private GtElement? _currentElement;                                          // primary (first selected)
    private IReadOnlyList<GtElement> _allElements = Array.Empty<GtElement>();
    private GtLayer? _currentLayer;                                              // layer owning the selection
    private bool _updating;

    private readonly ObservableCollection<string> _fontNames = new();
    private TextBox? _fontInnerBox;
    private bool _fontJustSelected;

    private string _textBefore = "";

    // color picker popup
    private Popup?             _colorPopup;
    private ColorPickerControl? _colorPicker;
    private bool               _editingFill;
    private List<(GtElement el, GtBrush? before)>? _pickerBrushSnapshot;
    private bool               _openingGradientEditor;
    private bool               _suppressPickerColor;

    private GtTextBlock? CurrentText => _currentElement as GtTextBlock;
    private IReadOnlyList<GtTextBlock> AllTexts =>
        _allElements.OfType<GtTextBlock>().ToList();

    /// <summary>the selected text objects that own an AutoSize property; a Ticker is a text object too but GT hangs auto-size off TextObject rather than the shared base, so a ticker overrides it per scrolling clone and never exposes it</summary>
    private IReadOnlyList<GtTextBlock> AllAutoSizableTexts =>
        _allElements.OfType<GtTextBlock>().Where(t => t is not GtTickerElement).ToList();

    /// <summary>fires when the user changes any property, caller should InvalidateVisual on canvas</summary>
    public event EventHandler? ElementChanged;

    /// <summary>fires when the user clicks Browse on an image; the host owns the asset library and the file picker, so it does the swap (and the history entry) for the element passed here</summary>
    public event EventHandler<GtImageElement>? ImageSourceBrowseRequested;

    /// <summary>fires when an align button is clicked; the host owns the document so it runs the alignment, only it knows the canvas bounds and the layer offsets involved</summary>
    public event EventHandler<AlignRequestedEventArgs>? AlignRequested;

    /// <summary>modifiers of the click that is about to raise <see cref="AlignRequested"/></summary>
    private KeyModifiers _alignModifiers;

    /// <summary>set to enable undo/redo recording of property changes</summary>
    public HistoryService? History { get; set; }

    /// <summary>fires when the ticker transport's play/pause button is clicked; the host owns the canvas and with it the ticker clock, so it runs the transport and reports the new state back through <see cref="SetTickerPlaying"/></summary>
    public event EventHandler? TickerPlayPauseRequested;

    /// <summary>fires when the ticker transport's stop button is clicked</summary>
    public event EventHandler? TickerStopRequested;

    /// <summary>swaps the transport button between its play and pause glyphs</summary>
    public void SetTickerPlaying(bool playing)
    {
        TickerPlayGlyph.IsVisible  = !playing;
        TickerPauseGlyph.IsVisible = playing;
    }

    private void AlignLeftButton_Click(object? sender, RoutedEventArgs e)       => RaiseAlign(GtAlign.Left);
    private void AlignCenterHButton_Click(object? sender, RoutedEventArgs e)    => RaiseAlign(GtAlign.CenterHorizontal);
    private void AlignRightButton_Click(object? sender, RoutedEventArgs e)      => RaiseAlign(GtAlign.Right);
    private void AlignTopButton_Click(object? sender, RoutedEventArgs e)        => RaiseAlign(GtAlign.Top);
    private void AlignMiddleButton_Click(object? sender, RoutedEventArgs e)     => RaiseAlign(GtAlign.Middle);
    private void AlignBottomButton_Click(object? sender, RoutedEventArgs e)     => RaiseAlign(GtAlign.Bottom);
    private void AlignCenterBothButton_Click(object? sender, RoutedEventArgs e) => RaiseAlign(GtAlign.CenterBoth);

    /// <summary>buttons report no modifiers on Click so the preceding press records them: Shift means "align to the canvas" even when several elements would otherwise align to each other</summary>
    private void AlignButton_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        _alignModifiers = e.KeyModifiers;

    private void RaiseAlign(GtAlign align)
    {
        var target = _alignModifiers.HasFlag(KeyModifiers.Shift)
            ? GtAlignTarget.Canvas
            : GtAlignTarget.Auto;
        _alignModifiers = KeyModifiers.None;
        AlignRequested?.Invoke(this, new AlignRequestedEventArgs(align, target));
    }

    private void HookAlignButtons()
    {
        var buttons = new[]
        {
            AlignLeftButton, AlignCenterHButton, AlignRightButton,
            AlignTopButton, AlignMiddleButton, AlignBottomButton, AlignCenterBothButton
        };
        foreach (var button in buttons)
            button.AddHandler(PointerPressedEvent, AlignButton_PointerPressed, RoutingStrategies.Tunnel);
    }

    public PropertiesPanelControl()
    {
        InitializeComponent();
        HookAlignButtons();
        PopulateFontWeights();
        PopulateStrokeCombos();
        MaskObjectBox.ItemsSource = _maskEntries;
        BoundingObjectBox.ItemsSource = _boundingEntries;
        WireBoundingPadding();

        // drag-to-scrub on labels plus modifier increment plus history (left/right drag changes value, Alt = 0.1 step)
        // X/Y/W/H read and write through the element's anchor, so every one of these is the anchor point's coordinate rather than the top-left corner the file stores
        AttachNumericBehavior(XLabel, XBox,
            (el, v) => el.SetAnchorX((double)v),
            (el)    => (decimal)el.AnchorX(),       "X position");
        AttachNumericBehavior(YLabel, YBox,
            (el, v) => el.SetAnchorY((double)v),
            (el)    => (decimal)el.AnchorY(),       "Y position");
        AttachNumericBehavior(ZLabel, ZBox,
            (el, v) => el.Z          = (double)v,
            (el)    => (decimal)el.Z,               "Z position");
        AttachNumericBehavior(WLabel, WBox,
            (el, v) => el.SetWidthAboutAnchor((double)v),
            (el)    => (decimal)el.Dimensions.Width, "Width");
        AttachNumericBehavior(HLabel, HBox,
            (el, v) => el.SetHeightAboutAnchor((double)v),
            (el)    => (decimal)el.Dimensions.Height, "Height");
        AttachNumericBehavior(DLabel, DBox,
            (el, v) => el.Depth      = (double)v,
            (el)    => (decimal)el.Depth,           "Depth");
        AttachNumericBehavior(RxLabel, RxBox,
            (el, v) => el.RotateX    = (double)v * (Math.PI / 180.0),
            (el)    => (decimal)(el.RotateX * (180.0 / Math.PI)), "Rotate X");
        AttachNumericBehavior(RyLabel, RyBox,
            (el, v) => el.RotateY    = (double)v * (Math.PI / 180.0),
            (el)    => (decimal)(el.RotateY * (180.0 / Math.PI)), "Rotate Y");
        AttachNumericBehavior(RzLabel, RzBox,
            (el, v) => el.RotateZ    = (double)v * (Math.PI / 180.0),
            (el)    => (decimal)(el.RotateZ * (180.0 / Math.PI)), "Rotate Z");
        AttachNumericBehavior(null, FontSizeBox,
            (el, v) => { if (el is GtTextBlock tb) tb.FontSize = (double)v; },
            (el)    => el is GtTextBlock tb2 ? (decimal)tb2.FontSize : 0m,
            "Font size", deltaMode: false);
        AttachNumericBehavior(TickerSpeedLabel, TickerSpeedBox,
            (el, v) => { if (el is GtTickerElement tk) tk.Speed = (double)v; },
            (el)    => el is GtTickerElement tk2 ? (decimal)tk2.Speed : 0m,
            "Ticker speed", deltaMode: false, defaultIncrement: 0.5m);
        AttachNumericBehavior(SpacingLabel, SpacingBox,
            (el, v) => { if (el is GtTextBlock tb) tb.LineSpacing = (double)v / 100.0; },
            (el)    => el is GtTextBlock tb2 ? SpacingPercent(tb2) : 100m,
            "Line spacing", deltaMode: false);

        // FontFamilyBox: grab inner PART_TextBox when template is applied
        FontFamilyBox.TemplateApplied += (_, e) =>
            _fontInnerBox = e.NameScope.Find<TextBox>("PART_TextBox");
        FontFamilyBox.GotFocus       += FontFamilyBox_GotFocus;
        FontFamilyBox.DropDownClosed += (_, _) =>
        {
            if (CurrentText is null) return;

            var name = FontFamilyBox.SelectedItem as string;
            if (!string.IsNullOrEmpty(name) && name != CurrentText.FontFamily)
            {
                var texts   = AllTexts;
                var befores = texts.Select(t => t.FontFamily).ToList();
                var n       = name;
                foreach (var t in texts) t.FontFamily = n;
                PushHistory("Font family",
                    () => { for (int i = 0; i < texts.Count; i++) texts[i].FontFamily = befores[i]; },
                    () => { foreach (var t in texts) t.FontFamily = n; });
                Apply();
            }

            _fontJustSelected = !string.IsNullOrEmpty(name);

            _updating = true;
            FontFamilyBox.Text = CurrentText.FontFamily;
            _updating = false;
        };

        // TextInputBox: track value for history on LostFocus
        TextInputBox.GotFocus  += TextInputBox_GotFocus;
        TextInputBox.LostFocus += TextInputBox_LostFocus;
        // tunnel so we beat the TextBox's own Enter-inserts-newline handling
        TextInputBox.AddHandler(KeyDownEvent, TextInputBox_KeyDown, RoutingStrategies.Tunnel);

        WireCrop(CropX0Slider, CropX0Box, "Crop X0",
            (el, v) => { var c = EnsureCrop(el); c.X0 = Math.Min(v / 100.0, c.X1); },
            (el)    => (el.Crop?.X0 ?? 0) * 100.0);
        WireCrop(CropX1Slider, CropX1Box, "Crop X1",
            (el, v) => { var c = EnsureCrop(el); c.X1 = Math.Max(v / 100.0, c.X0); },
            (el)    => (el.Crop?.X1 ?? 1) * 100.0);
        WireCrop(CropY0Slider, CropY0Box, "Crop Y0",
            (el, v) => { var c = EnsureCrop(el); c.Y0 = Math.Min(v / 100.0, c.Y1); },
            (el)    => (el.Crop?.Y0 ?? 0) * 100.0);
        WireCrop(CropY1Slider, CropY1Box, "Crop Y1",
            (el, v) => { var c = EnsureCrop(el); c.Y1 = Math.Max(v / 100.0, c.Y0); },
            (el)    => (el.Crop?.Y1 ?? 1) * 100.0);
        // four independent edges; GT Designer offers one slider but the file stores "Feather=l,t,r,b" so the editor drives each edge on its own, and values are pixels of the element box (GT's own range is 0-100) not percentages
        WireCrop(FeatherLeftSlider, FeatherLeftBox, "Feather left",
            (el, v) => EnsureCrop(el).FeatherLeft = v,
            (el)    => el.Crop?.FeatherLeft ?? GtCrop.DefaultFeather);
        WireCrop(FeatherTopSlider, FeatherTopBox, "Feather top",
            (el, v) => EnsureCrop(el).FeatherTop = v,
            (el)    => el.Crop?.FeatherTop ?? GtCrop.DefaultFeather);
        WireCrop(FeatherRightSlider, FeatherRightBox, "Feather right",
            (el, v) => EnsureCrop(el).FeatherRight = v,
            (el)    => el.Crop?.FeatherRight ?? GtCrop.DefaultFeather);
        WireCrop(FeatherBottomSlider, FeatherBottomBox, "Feather bottom",
            (el, v) => EnsureCrop(el).FeatherBottom = v,
            (el)    => el.Crop?.FeatherBottom ?? GtCrop.DefaultFeather);

        // collapsed view, crop is one uniform inset: X0/Y0 measure in from the near edge but X1/Y1 run the other way (1 = uncropped) so the far edges take 1 - inset, and the inset therefore tops out at 50% where the two edges meet
        WireCrop(CropAllSlider, CropAllBox, "Crop",
            (el, v) =>
            {
                var c = EnsureCrop(el);
                var f = Math.Clamp(v, 0, 50) / 100.0;
                c.X0 = f; c.Y0 = f;
                c.X1 = 1 - f; c.Y1 = 1 - f;
            },
            (el)    => (el.Crop?.X0 ?? 0) * 100.0);
        WireCrop(FeatherAllSlider, FeatherAllBox, "Feather",
            (el, v) =>
            {
                var c = EnsureCrop(el);
                c.FeatherLeft = c.FeatherTop = c.FeatherRight = c.FeatherBottom = v;
            },
            (el)    => el.Crop?.FeatherLeft ?? GtCrop.DefaultFeather);

        // opacity uses the same slider and box plumbing: the bar works in whole percent, the model keeps GT's 0-1 float
        WireCrop(OpacitySlider, OpacityBox, "Opacity",
            (el, v) => el.Opacity = Math.Clamp(v / 100.0, 0.0, 1.0),
            (el)    => el.Opacity * 100.0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_fontNames.Count == 0)
            LoadFonts();
    }

    private void LoadFonts()
    {
        _fontNames.Clear();
        try
        {
            var names = FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                _fontNames.Add(name);
        }
        catch
        {
            foreach (var name in new[] { "Arial", "Calibri", "Consolas", "Georgia",
                                          "Segoe UI", "Tahoma", "Times New Roman", "Verdana" })
                _fontNames.Add(name);
        }
        FontFamilyBox.ItemsSource = _fontNames;
    }

    private void PopulateFontWeights()
    {
        foreach (var w in _weightEntries)
            FontWeightBox.Items.Add(w);
    }

    private static readonly WeightEntry[] _weightEntries = new[]
    {
        new WeightEntry("Thin (100)",       FontWeight.Thin),
        new WeightEntry("ExtraLight (200)", FontWeight.ExtraLight),
        new WeightEntry("Light (300)",      FontWeight.Light),
        new WeightEntry("Regular (400)",    FontWeight.Normal),
        new WeightEntry("Medium (500)",     FontWeight.Medium),
        new WeightEntry("SemiBold (600)",   FontWeight.SemiBold),
        new WeightEntry("Bold (700)",       FontWeight.Bold),
        new WeightEntry("ExtraBold (800)",  FontWeight.ExtraBold),
        new WeightEntry("Black (900)",      FontWeight.Black),
    };

    /// <summary>pass the full selection; position/size shows the primary element's values with the delta applied to all on change, and the text panel is enabled only when all selected are GtTextBlock (changes apply to all text blocks in the selection); pass an empty collection to clear/disable everything</summary>
    /// <param name="layer">layer owning the selection, used for the mask-object list; pass null to disable it</param>
    public void Populate(IReadOnlyCollection<GtElement> elements, GtLayer? layer = null)
    {
        _allElements    = elements.ToList();
        _currentLayer   = layer;
        _currentElement = _allElements.Count > 0 ? _allElements[0] : null;
        var el          = _currentElement;
        var allText     = _allElements.Count > 0 && _allElements.All(e => e is GtTextBlock);

        var allImages   = _allElements.Count > 0 && _allElements.All(e => e is GtImageElement);

        // sections are hidden rather than greyed out, the bar is too crowded to carry controls the current selection cannot use; each section owns its leading separator so hiding it takes the divider with it
        // location / rotation / dimensions never leave the bar, they grey out instead so the toolbar keeps a fixed anchor on the left whatever is selected
        PositionSizePanel.IsEnabled = el is not null;
        TextSpecificPanel.IsVisible = allText;
        CropPanel.IsVisible         = el is not null;

        // values the single slider cannot describe force their own group open, so a hand-authored file is never misreported; the two groups decide separately
        if (_allElements.Any(e => !CropRangeIsUniform(e)))
            SetCropExpanded(true);
        if (_allElements.Any(e => !FeatherIsUniform(e)))
            SetFeatherExpanded(true);
        OpacityPanel.IsVisible      = el is not null;
        DataFlagsPanel.IsVisible    = el is not null;
        ImagePanel.IsVisible        = allImages;

        var allTickers = _allElements.Count > 0 && _allElements.All(e => e is GtTickerElement);
        TickerPanel.IsVisible = allTickers;
        AutoSizePanel.IsVisible = allText && !_allElements.Any(e => e is GtTickerElement);

        // source swap targets the primary element, so only offer it on a single selection
        ImageBrowseButton.IsVisible = allImages && _allElements.Count == 1;

        // text content editing: primary only, single-selection only
        TextInputGroup.IsVisible = _allElements.Count <= 1;

        _updating = true;
        try
        {
            if (el is not null)
            {
                XBox.Value = (decimal)el.AnchorX();
                YBox.Value = (decimal)el.AnchorY();
                ZBox.Value = (decimal)el.Z;
                AnchorBox.SelectedIndex = (int)el.Anchor;
                RxBox.Value = (decimal)(el.RotateX * (180.0 / Math.PI));
                RyBox.Value = (decimal)(el.RotateY * (180.0 / Math.PI));
                RzBox.Value = (decimal)(el.RotateZ * (180.0 / Math.PI));
                WBox.Value = (decimal)el.Dimensions.Width;
                HBox.Value = (decimal)el.Dimensions.Height;
                DBox.Value = (decimal)el.Depth;

                RefreshCropControls();
            }

            RefreshDataFlagChecks();
            RefreshMaskControls();
            RefreshBoundingControls();

            if (el is GtTextBlock tb)
            {
                TextInputBox.Text = tb.Text;
                _textBefore = tb.Text;

                if (!_fontNames.Contains(tb.FontFamily))
                    _fontNames.Insert(0, tb.FontFamily);
                FontFamilyBox.Text = tb.FontFamily;

                FontSizeBox.Value = (decimal)tb.FontSize;

                ItalicButton.IsChecked    = tb.FontStyle == FontStyle.Italic;
                UnderlineButton.IsChecked = tb.Underline;
                StrikeButton.IsChecked    = tb.Strikethrough;
                UppercaseButton.IsChecked = tb.Uppercase;

                FontWeightBox.SelectedItem = _weightEntries.FirstOrDefault(w => w.Weight == tb.FontWeight)
                    ?? _weightEntries[3];

                HAlignLeftButton.IsChecked   = tb.TextAlign == GtTextAlign.Left;
                HAlignCenterButton.IsChecked = tb.TextAlign == GtTextAlign.Center;
                HAlignRightButton.IsChecked  = tb.TextAlign == GtTextAlign.Right;

                VAlignTopButton.IsChecked    = tb.VerticalAlign == GtVerticalAlign.Top;
                VAlignCenterButton.IsChecked = tb.VerticalAlign == GtVerticalAlign.Center;
                VAlignBottomButton.IsChecked = tb.VerticalAlign == GtVerticalAlign.Bottom;

                WordWrapButton.IsChecked = !tb.NoWrap;

                SpacingBox.Value = SpacingPercent(tb);

                AutoSizeBox.SelectedIndex = (int)tb.AutoSize;
            }

            if (el is GtImageElement img)
                ImageSizeModeBox.SelectedIndex = (int)img.SizeMode;

            if (el is GtTickerElement ticker)
            {
                TickerTypeBox.SelectedIndex      = (int)ticker.TickerType;
                TickerDirectionBox.SelectedIndex = (int)ticker.Direction;
                TickerSpeedBox.Value             = (decimal)ticker.Speed;
            }

            // fill / stroke section: stays visible for elements without fill/stroke (images), just greyed out
            var anyFillStroke = _allElements.Count > 0 && _allElements.All(HasFillStroke);
            FillStrokePanel.IsVisible = anyFillStroke;

            if (!anyFillStroke)
            {
                UpdateSwatch(FillColorSwatch,   null);
                UpdateSwatch(StrokeColorSwatch, null);
            }
            else if (el is not null)
            {
                UpdateSwatch(FillColorSwatch,   GetFill(el));
                UpdateSwatch(StrokeColorSwatch, GetStroke(el));

                StrokeThicknessBox.Value = (decimal)GetStrokeThickness(el);

                // dash style, only rect/ellipse support it
                var hasDash = _allElements.All(e => e is GtRectangleElement or GtEllipseElement);
                StrokeStyleBox.IsVisible = hasDash;
                StrokeStyleLabel.IsVisible = hasDash;
                StrokeStyleBox.SelectedIndex = hasDash ? (int)GetStrokeDashStyle(el) : 0;

                // corners, rectangle only
                var hasCorners = _allElements.All(e => e is GtRectangleElement);
                CornersBox.IsVisible = hasCorners;
                CornersLabel.IsVisible = hasCorners;
                CornersBox.SelectedIndex = hasCorners
                    ? (int)((el as GtRectangleElement)!.Style)
                    : 0;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void FlagHiddenCheck_Click(object? sender, RoutedEventArgs e)
        => ToggleDataFlag(GtDataFlags.Hidden, "Hidden flag");

    private void FlagNoEventsCheck_Click(object? sender, RoutedEventArgs e)
        => ToggleDataFlag(GtDataFlags.NoEvents, "Disable Events flag");

    private void FlagShowVisibleCheck_Click(object? sender, RoutedEventArgs e)
        => ToggleDataFlag(GtDataFlags.ShowVisible, "Show Visible Toggle flag");

    /// <summary>aggregate state of one flag over the selection: null when the elements disagree, which the three-state checkbox shows as an indeterminate mark</summary>
    private bool? DataFlagState(GtDataFlags flag)
    {
        if (_allElements.Count == 0) return false;
        var first = _allElements[0].DataFlags.HasFlag(flag);
        return _allElements.All(el => el.DataFlags.HasFlag(flag) == first) ? first : null;
    }

    private void RefreshDataFlagChecks()
    {
        FlagHiddenCheck.IsChecked      = DataFlagState(GtDataFlags.Hidden);
        FlagNoEventsCheck.IsChecked    = DataFlagState(GtDataFlags.NoEvents);
        FlagShowVisibleCheck.IsChecked = DataFlagState(GtDataFlags.ShowVisible);
    }

    /// <summary>sets or clears one flag across the whole selection; a mixed selection turns the flag on for everything so the user never has to click twice to reach a shared state, and the indeterminate mark is display-only and can't be clicked back into</summary>
    private void ToggleDataFlag(GtDataFlags flag, string description)
    {
        if (_updating || _currentElement is null) return;

        // the model still holds the pre-click state, so this is the state the box showed
        var after    = DataFlagState(flag) != true;
        var elements = _allElements.ToList();
        var befores  = elements.Select(el => el.DataFlags).ToList();

        foreach (var el in elements)
            el.DataFlags = after ? el.DataFlags | flag : el.DataFlags & ~flag;

        if (elements.Where((el, i) => el.DataFlags != befores[i]).Any())
            PushHistory(description,
                () => { for (int i = 0; i < elements.Count; i++) elements[i].DataFlags = befores[i]; },
                () => { foreach (var el in elements) el.DataFlags = after ? el.DataFlags | flag : el.DataFlags & ~flag; });

        _updating = true;
        try { RefreshDataFlagChecks(); }
        finally { _updating = false; }

        Apply();
    }

    /// <summary>one row of an element-reference dropdown, shared by the mask and bounding lists; a null <see cref="Name"/> is the "(None)" row</summary>
    private sealed class ObjectEntry
    {
        public string? Name;
        public string Display = "";
        public override string ToString() => Display;
    }

    private readonly ObservableCollection<ObjectEntry> _maskEntries = new();

    /// <summary>rebuilds the candidate list; vMix resolves a mask by name within the element's own layer so the dropdown offers the siblings of the selection and nothing else, and it is disabled outright when the selection spans layers</summary>
    private void RefreshMaskControls()
    {
        var sameLayer = _currentLayer is not null && _allElements.Count > 0 &&
                        _allElements.All(e => _currentLayer.Elements.Contains(e));

        ObjectRefPanel.IsVisible = sameLayer;
        MaskPanel.IsVisible      = sameLayer;

        _maskEntries.Clear();
        _maskEntries.Add(new ObjectEntry { Name = null, Display = "(None)" });

        if (!sameLayer)
        {
            MaskObjectBox.PlaceholderText = "";
            MaskObjectBox.SelectedIndex   = -1;
            return;
        }

        foreach (var candidate in _currentLayer!.Elements)
        {
            if (_allElements.Contains(candidate)) continue;      // an element cannot mask itself
            if (string.IsNullOrEmpty(candidate.Name)) continue;  // masks are referenced by name
            _maskEntries.Add(new ObjectEntry { Name = candidate.Name, Display = candidate.Name });
        }

        var first  = _allElements[0].MaskObject;
        var agreed = _allElements.All(e => string.Equals(e.MaskObject, first, StringComparison.Ordinal));

        MaskObjectBox.PlaceholderText = agreed ? "" : "Mixed";
        if (!agreed)
        {
            MaskObjectBox.SelectedIndex = -1;
            return;
        }

        var match = _maskEntries.FirstOrDefault(
            m => string.Equals(m.Name, first, StringComparison.OrdinalIgnoreCase));

        // a reference whose target was deleted still has to be shown rather than silently reset
        if (match is null && first is not null)
        {
            match = new ObjectEntry { Name = first, Display = first + "  (missing)" };
            _maskEntries.Add(match);
        }

        MaskObjectBox.SelectedItem = match ?? _maskEntries[0];
    }

    private void MaskObjectBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        if (MaskObjectBox.SelectedItem is not ObjectEntry entry) return;

        var after    = entry.Name;
        var elements = _allElements.ToList();
        var befores  = elements.Select(el => el.MaskObject).ToList();

        if (befores.All(b => string.Equals(b, after, StringComparison.Ordinal))) return;

        foreach (var el in elements) el.MaskObject = after;

        PushHistory("Mask object",
            () => { for (int i = 0; i < elements.Count; i++) elements[i].MaskObject = befores[i]; },
            () => { foreach (var el in elements) el.MaskObject = after; });

        Apply();
    }

    private readonly ObservableCollection<ObjectEntry> _boundingEntries = new();

    private static GtBounding EnsureBounding(GtElement el) => el.Bounding ??= new GtBounding();

    /// <summary>drops a binding back to null once it carries neither a source nor any padding</summary>
    private void DropEmptyBoundings()
    {
        foreach (var el in _allElements)
            if (el.Bounding is { IsDefault: true }) el.Bounding = null;
    }

    /// <summary>label scrub, keyboard entry and the spinner buttons all come from the shared numeric behavior which pushes one history entry per gesture, and the boxes themselves write the model through <see cref="PaddingBox_ValueChanged"/>; padding is an absolute per-edge value not a delta, so the whole selection takes the number shown rather than being nudged by the difference</summary>
    private void WireBoundingPadding()
    {
        AttachNumericBehavior(PadLeftLabel, PadLeftBox,
            (el, v) => EnsureBounding(el).PaddingLeft = (double)v,
            (el)    => (decimal)(el.Bounding?.PaddingLeft ?? 0),
            "Bounding padding left", deltaMode: false);
        AttachNumericBehavior(PadTopLabel, PadTopBox,
            (el, v) => EnsureBounding(el).PaddingTop = (double)v,
            (el)    => (decimal)(el.Bounding?.PaddingTop ?? 0),
            "Bounding padding top", deltaMode: false);
        AttachNumericBehavior(PadRightLabel, PadRightBox,
            (el, v) => EnsureBounding(el).PaddingRight = (double)v,
            (el)    => (decimal)(el.Bounding?.PaddingRight ?? 0),
            "Bounding padding right", deltaMode: false);
        AttachNumericBehavior(PadBottomLabel, PadBottomBox,
            (el, v) => EnsureBounding(el).PaddingBottom = (double)v,
            (el)    => (decimal)(el.Bounding?.PaddingBottom ?? 0),
            "Bounding padding bottom", deltaMode: false);
    }

    /// <summary>rebuilds the candidate list; a binding copies the source's layer-local coordinates as they stand so the dropdown offers the siblings of the selection and nothing else, and it is disabled outright when the selection spans layers exactly like the mask list</summary>
    private void RefreshBoundingControls()
    {
        var sameLayer = _currentLayer is not null && _allElements.Count > 0 &&
                        _allElements.All(e => _currentLayer.Elements.Contains(e));

        BoundingPanel.IsVisible        = sameLayer;
        BoundingPaddingPanel.IsVisible = sameLayer;

        _boundingEntries.Clear();
        _boundingEntries.Add(new ObjectEntry { Name = null, Display = "(None)" });

        if (!sameLayer)
        {
            BoundingObjectBox.PlaceholderText = "";
            BoundingObjectBox.SelectedIndex   = -1;
            return;
        }

        foreach (var candidate in _currentLayer!.Elements)
        {
            if (_allElements.Contains(candidate)) continue;      // an element cannot bind to itself
            if (string.IsNullOrEmpty(candidate.Name)) continue;  // bindings are resolved by name
            // vMix allows one edge of dependency only: binding to an element that is itself bound does nothing at all silently, so those are left out of the list
            if (candidate.Bounding is { HasSource: true }) continue;
            _boundingEntries.Add(new ObjectEntry { Name = candidate.Name, Display = candidate.Name });
        }

        var first  = _allElements[0].Bounding?.Object;
        var agreed = _allElements.All(
            e => string.Equals(e.Bounding?.Object, first, StringComparison.Ordinal));

        RefreshPaddingBoxes();

        BoundingObjectBox.PlaceholderText = agreed ? "" : "Mixed";
        if (!agreed)
        {
            BoundingObjectBox.SelectedIndex = -1;
            return;
        }

        var match = _boundingEntries.FirstOrDefault(
            b => string.Equals(b.Name, first, StringComparison.OrdinalIgnoreCase));

        // a source that was deleted, or that has since been bound itself, still has to be shown rather than silently reset, the name is what the file holds
        if (match is null && !string.IsNullOrEmpty(first))
        {
            match = new ObjectEntry { Name = first, Display = first + "  (unavailable)" };
            _boundingEntries.Add(match);
        }

        BoundingObjectBox.SelectedItem = match ?? _boundingEntries[0];
    }

    /// <summary>pushes the primary element's padding back into the four boxes</summary>
    private void RefreshPaddingBoxes()
    {
        var bounding = _currentElement?.Bounding;

        var wasUpdating = _updating;
        _updating = true;
        try
        {
            PadLeftBox.Value   = (decimal)(bounding?.PaddingLeft   ?? 0);
            PadTopBox.Value    = (decimal)(bounding?.PaddingTop    ?? 0);
            PadRightBox.Value  = (decimal)(bounding?.PaddingRight  ?? 0);
            PadBottomBox.Value = (decimal)(bounding?.PaddingBottom ?? 0);
        }
        finally { _updating = wasUpdating; }
    }

    private void BoundingObjectBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        if (BoundingObjectBox.SelectedItem is not ObjectEntry entry) return;

        var after    = entry.Name;
        var elements = _allElements.ToList();
        var befores  = elements.Select(el => el.Bounding?.Clone()).ToList();

        if (befores.All(b => string.Equals(b?.Object, after, StringComparison.Ordinal))) return;

        // a binding overwrites its owner's box on the next render, so the boxes go into the undo entry alongside it; the write-back happens outside any user gesture and would otherwise leave the followed box behind after an undo
        var boxes = elements.Select(el => (el.Location, el.Dimensions)).ToList();

        void Redo()
        {
            foreach (var el in elements) EnsureBounding(el).Object = after;
            DropEmptyBoundings();
        }

        Redo();

        PushHistory("Bounding object",
            () =>
            {
                for (int i = 0; i < elements.Count; i++)
                {
                    elements[i].Bounding   = befores[i]?.Clone();
                    elements[i].Location   = boxes[i].Location;
                    elements[i].Dimensions = boxes[i].Dimensions;
                }
            },
            Redo);

        Apply();
    }

    /// <summary>one handler for all four padding boxes: the sender says which edge it drives, and the value goes to every selected element</summary>
    private void PaddingBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;

        var v = (double)(e.NewValue ?? 0m);
        foreach (var el in _allElements)
        {
            var bounding = EnsureBounding(el);
            if      (ReferenceEquals(sender, PadLeftBox))  bounding.PaddingLeft   = v;
            else if (ReferenceEquals(sender, PadTopBox))   bounding.PaddingTop    = v;
            else if (ReferenceEquals(sender, PadRightBox)) bounding.PaddingRight  = v;
            else                                           bounding.PaddingBottom = v;
        }

        DropEmptyBoundings();
        Apply();
    }

    private void Apply()
    {
        if (_updating || _currentElement is null) return;
        ElementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PushHistory(string desc, Action undo, Action redo)
        => History?.Push(new PropertyChangeAction(desc, undo, redo));

    private void XBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        // the box carries the primary's anchor point but the whole selection still moves by the same delta, anchors differ per element and positions must not drift apart
        var delta = (double)(e.NewValue ?? 0m) - _currentElement.AnchorX();
        foreach (var el in _allElements)
            el.Location = new GtPoint(el.Location.X + delta, el.Location.Y);
        Apply();
    }

    private void YBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var delta = (double)(e.NewValue ?? 0m) - _currentElement.AnchorY();
        foreach (var el in _allElements)
            el.Location = new GtPoint(el.Location.X, el.Location.Y + delta);
        Apply();
    }

    private void ZBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var delta = (double)(e.NewValue ?? 0m) - _currentElement.Z;
        foreach (var el in _allElements)
            el.Z += delta;
        Apply();
    }

    private void WBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        // each element keeps its own anchor point fixed while it grows, so a top-left anchor still grows right and down exactly as before
        var delta = (double)(e.NewValue ?? 1m) - _currentElement.Dimensions.Width;
        foreach (var el in _allElements)
            el.SetWidthAboutAnchor(el.Dimensions.Width + delta);
        Apply();
    }

    private void HBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var delta = (double)(e.NewValue ?? 1m) - _currentElement.Dimensions.Height;
        foreach (var el in _allElements)
            el.SetHeightAboutAnchor(el.Dimensions.Height + delta);
        Apply();
    }

    private void DBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var delta = (double)(e.NewValue ?? 0m) - _currentElement.Depth;
        foreach (var el in _allElements)
            el.Depth += delta;
        Apply();
    }

    private void RxBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var deltaRad = ((double)(e.NewValue ?? 0m) - _currentElement.RotateX * (180.0 / Math.PI)) * (Math.PI / 180.0);
        foreach (var el in _allElements)
            el.RotateX += deltaRad;
        Apply();
    }

    private void RyBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var deltaRad = ((double)(e.NewValue ?? 0m) - _currentElement.RotateY * (180.0 / Math.PI)) * (Math.PI / 180.0);
        foreach (var el in _allElements)
            el.RotateY += deltaRad;
        Apply();
    }

    private void RzBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var deltaRad = ((double)(e.NewValue ?? 0m) - _currentElement.RotateZ * (180.0 / Math.PI)) * (Math.PI / 180.0);
        foreach (var el in _allElements)
            el.RotateZ += deltaRad;
        Apply();
    }

    private void TextInputBox_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        _textBefore = TextInputBox.Text ?? "";
    }

    private void TextInputBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (CurrentText is null || History is null) return;
        var after = TextInputBox.Text ?? "";
        if (after == _textBefore) return;
        var tb     = CurrentText;
        var before = _textBefore;
        _textBefore = after;
        PushHistory("Text content", () => tb.Text = before, () => tb.Text = after);
    }

    /// <summary>Enter commits and drops focus, Shift+Enter inserts the newline, Escape acts like Enter</summary>
    private void TextInputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    private void TextInputBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        CurrentText.Text = TextInputBox.Text ?? "";
        Apply();
    }

    private void FontFamilyBox_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        if (_fontJustSelected) { _fontJustSelected = false; return; }
        _fontInnerBox?.Clear();
    }

    private void FontSizeBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var size = (double)(e.NewValue ?? 36m);
        foreach (var tb in AllTexts)
            tb.FontSize = size;
        Apply();
    }

    private void ItalicButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = ItalicButton.IsChecked == true ? FontStyle.Italic : FontStyle.Normal;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.FontStyle).ToList();
        foreach (var t in texts) t.FontStyle = after;
        if (befores.Any(b => b != after))
            PushHistory("Italic",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].FontStyle = befores[i]; },
                () => { foreach (var t in texts) t.FontStyle = after; });
        Apply();
    }

    private void UnderlineButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = UnderlineButton.IsChecked == true;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.Underline).ToList();
        foreach (var t in texts) t.Underline = after;
        if (befores.Any(b => b != after))
            PushHistory("Underline",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].Underline = befores[i]; },
                () => { foreach (var t in texts) t.Underline = after; });
        Apply();
    }

    private void StrikeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = StrikeButton.IsChecked == true;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.Strikethrough).ToList();
        foreach (var t in texts) t.Strikethrough = after;
        if (befores.Any(b => b != after))
            PushHistory("Strikethrough",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].Strikethrough = befores[i]; },
                () => { foreach (var t in texts) t.Strikethrough = after; });
        Apply();
    }

    private void UppercaseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = UppercaseButton.IsChecked == true;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.Uppercase).ToList();
        foreach (var t in texts) t.Uppercase = after;
        if (befores.Any(b => b != after))
            PushHistory("Uppercase",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].Uppercase = befores[i]; },
                () => { foreach (var t in texts) t.Uppercase = after; });
        Apply();
    }

    private void FontWeightBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        if (FontWeightBox.SelectedItem is not WeightEntry we) return;
        var after   = we.Weight;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.FontWeight).ToList();
        foreach (var t in texts) t.FontWeight = after;
        if (befores.Any(b => b != after))
            PushHistory("Font weight",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].FontWeight = befores[i]; },
                () => { foreach (var t in texts) t.FontWeight = after; });
        Apply();
    }

    private void HAlignLeft_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = GtTextAlign.Left;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.TextAlign).ToList();
        foreach (var t in texts) t.TextAlign = after;
        HAlignLeftButton.IsChecked   = true;
        HAlignCenterButton.IsChecked = false;
        HAlignRightButton.IsChecked  = false;
        if (befores.Any(b => b != after))
            PushHistory("H-align left",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].TextAlign = befores[i]; },
                () => { foreach (var t in texts) t.TextAlign = after; });
        Apply();
    }

    private void HAlignCenter_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = GtTextAlign.Center;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.TextAlign).ToList();
        foreach (var t in texts) t.TextAlign = after;
        HAlignLeftButton.IsChecked   = false;
        HAlignCenterButton.IsChecked = true;
        HAlignRightButton.IsChecked  = false;
        if (befores.Any(b => b != after))
            PushHistory("H-align center",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].TextAlign = befores[i]; },
                () => { foreach (var t in texts) t.TextAlign = after; });
        Apply();
    }

    private void HAlignRight_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = GtTextAlign.Right;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.TextAlign).ToList();
        foreach (var t in texts) t.TextAlign = after;
        HAlignLeftButton.IsChecked   = false;
        HAlignCenterButton.IsChecked = false;
        HAlignRightButton.IsChecked  = true;
        if (befores.Any(b => b != after))
            PushHistory("H-align right",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].TextAlign = befores[i]; },
                () => { foreach (var t in texts) t.TextAlign = after; });
        Apply();
    }

    private void VAlignTop_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = GtVerticalAlign.Top;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.VerticalAlign).ToList();
        foreach (var t in texts) t.VerticalAlign = after;
        VAlignTopButton.IsChecked    = true;
        VAlignCenterButton.IsChecked = false;
        VAlignBottomButton.IsChecked = false;
        if (befores.Any(b => b != after))
            PushHistory("V-align top",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].VerticalAlign = befores[i]; },
                () => { foreach (var t in texts) t.VerticalAlign = after; });
        Apply();
    }

    private void VAlignCenter_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = GtVerticalAlign.Center;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.VerticalAlign).ToList();
        foreach (var t in texts) t.VerticalAlign = after;
        VAlignTopButton.IsChecked    = false;
        VAlignCenterButton.IsChecked = true;
        VAlignBottomButton.IsChecked = false;
        if (befores.Any(b => b != after))
            PushHistory("V-align center",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].VerticalAlign = befores[i]; },
                () => { foreach (var t in texts) t.VerticalAlign = after; });
        Apply();
    }

    private void VAlignBottom_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = GtVerticalAlign.Bottom;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.VerticalAlign).ToList();
        foreach (var t in texts) t.VerticalAlign = after;
        VAlignTopButton.IsChecked    = false;
        VAlignCenterButton.IsChecked = false;
        VAlignBottomButton.IsChecked = true;
        if (befores.Any(b => b != after))
            PushHistory("V-align bottom",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].VerticalAlign = befores[i]; },
                () => { foreach (var t in texts) t.VerticalAlign = after; });
        Apply();
    }

    private void WordWrapButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var after   = WordWrapButton.IsChecked != true;
        var texts   = AllTexts;
        var befores = texts.Select(t => t.NoWrap).ToList();
        foreach (var t in texts) t.NoWrap = after;
        if (befores.Any(b => b != after))
            PushHistory("Word wrap",
                () => { for (int i = 0; i < texts.Count; i++) texts[i].NoWrap = befores[i]; },
                () => { foreach (var t in texts) t.NoWrap = after; });
        Apply();
    }

    /// <summary>switching mode resizes the box on the next render, so the undo entry carries the boxes as well as the mode; the auto-size write-back happens outside any user gesture and would otherwise leave a grown box behind after an undo</summary>
    private void AutoSizeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        if (AutoSizeBox.SelectedIndex < 0) return;

        var after = (GtAutoSize)AutoSizeBox.SelectedIndex;
        var texts = AllAutoSizableTexts;
        var befores = texts.Select(t => (t.AutoSize, t.Dimensions)).ToList();
        if (befores.All(b => b.AutoSize == after)) return;

        foreach (var t in texts) t.AutoSize = after;

        PushHistory("Text AutoSize",
            () =>
            {
                for (int i = 0; i < texts.Count; i++)
                {
                    texts[i].AutoSize   = befores[i].AutoSize;
                    texts[i].Dimensions = befores[i].Dimensions;
                }
            },
            () => { foreach (var t in texts) t.AutoSize = after; });

        Apply();
    }

    /// <summary>pushes the model's current box back into the boxes for when something other than the user moved it, an auto-sizing text box resizing itself or a bound element following its source which moves it as well as resizes it</summary>
    /// <summary>re-reads the anchor-relative position boxes and the anchor combo from the model, for when the anchor changed rather than the box</summary>
    private void RefreshPositionBoxes()
    {
        if (_currentElement is null) return;
        _updating = true;
        try
        {
            XBox.Value = (decimal)_currentElement.AnchorX();
            YBox.Value = (decimal)_currentElement.AnchorY();
            AnchorBox.SelectedIndex = (int)_currentElement.Anchor;
        }
        finally { _updating = false; }
    }

    /// <summary>switching the anchor moves nothing: the box stays where it is on the canvas and only the X/Y readout changes to name the new anchor point instead; the file follows suit, its Location is whatever point the anchor names</summary>
    private void AnchorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        if (AnchorBox.SelectedIndex < 0) return;

        var after   = (GtAnchor)AnchorBox.SelectedIndex;
        var targets = _allElements.ToList();
        var befores = targets.Select(t => t.Anchor).ToList();
        if (befores.All(b => b == after)) return;

        foreach (var t in targets) t.Anchor = after;

        PushHistory("Anchor",
            () =>
            {
                for (int i = 0; i < targets.Count; i++) targets[i].Anchor = befores[i];
                RefreshPositionBoxes();
            },
            () =>
            {
                foreach (var t in targets) t.Anchor = after;
                RefreshPositionBoxes();
            });

        RefreshPositionBoxes();
    }

    public void RefreshDimensions()
    {
        if (_currentElement is null) return;
        _updating = true;
        try
        {
            XBox.Value = (decimal)_currentElement.AnchorX();
            YBox.Value = (decimal)_currentElement.AnchorY();
            WBox.Value = (decimal)_currentElement.Dimensions.Width;
            HBox.Value = (decimal)_currentElement.Dimensions.Height;
        }
        finally { _updating = false; }
    }

    /// <summary>line spacing as the percentage the box shows; GT keeps 0 as a sentinel for "use the font metrics" and writes 1.0 for an explicit 100%, both display as 100; GT's own slider stops at 200% (past that the engine reinterprets the value as an absolute line height in pixels) so the box clamps there too, a file carrying a larger value still renders with it the box just cannot show or edit it</summary>
    private static decimal SpacingPercent(GtTextBlock tb)
        => tb.LineSpacing <= 0.0 ? 100m : Math.Min(200m, (decimal)(tb.LineSpacing * 100.0));

    private void SpacingBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || CurrentText is null) return;
        var val = (double)Math.Clamp(e.NewValue ?? 100m, 1m, 200m) / 100.0;
        foreach (var tb in AllTexts)
            tb.LineSpacing = val;
        Apply();
    }

    private readonly List<(Slider Slider, NumericUpDown Box, Func<GtElement, double> Get)> _cropControls = new();

    private static GtCrop EnsureCrop(GtElement el) => el.Crop ??= new GtCrop();

    /// <summary>wires one crop field: slider and box mirror each other, either one writes the value (0-100) to every selected element; values are absolute not deltas, a crop describes a fraction of each element's own box so the whole selection shares the same numbers</summary>
    private void WireCrop(Slider slider, NumericUpDown box, string description,
                          Action<GtElement, double> setter, Func<GtElement, double> getter)
    {
        _cropControls.Add((slider, box, getter));

        // keyboard entry and spinner buttons get history from the shared numeric behavior
        AttachNumericBehavior(null, box,
            (el, v) => setter(el, (double)v),
            (el)    => (decimal)getter(el),
            description, deltaMode: false);

        box.ValueChanged += (_, e) =>
        {
            if (_updating || _currentElement is null) return;
            var v = (double)(e.NewValue ?? 0m);
            foreach (var el in _allElements) setter(el, v);
            // clamping (X0 never past X1, etc.) can move the value, so read the model back
            RefreshCropControls();
            Apply();
        };

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _updating || _currentElement is null) return;
            var v = Math.Round(e.GetNewValue<double>());
            if (box.Value is { } cur && Math.Abs((double)cur - v) < 0.001) return;
            box.Value = (decimal)v;   // the box handler writes the model
        };

        // one history entry per slider drag, snapshotting the whole selection
        List<(GtElement el, double ov)>? dragSnapshot = null;

        slider.AddHandler(PointerPressedEvent, (object? s, PointerPressedEventArgs e) =>
        {
            dragSnapshot = _allElements.Select(el => (el, getter(el))).ToList();
        }, RoutingStrategies.Tunnel);

        slider.AddHandler(PointerReleasedEvent, (object? s, PointerReleasedEventArgs e) =>
        {
            var before = dragSnapshot;
            dragSnapshot = null;
            if (before is null || History is null) return;

            var after = _allElements.Select(el => (el, nv: getter(el))).ToList();
            if (after.Count != before.Count) return;

            bool changed = false;
            for (int i = 0; i < after.Count; i++)
                if (Math.Abs(after[i].nv - before[i].ov) > 1e-9) changed = true;
            if (!changed) return;

            History.Push(new PropertyChangeAction(description,
                () => { foreach (var (el, ov) in before) setter(el, ov); },
                () => { foreach (var (el, nv) in after)  setter(el, nv); }));
        }, RoutingStrategies.Bubble);

        slider.AddHandler(PointerCaptureLostEvent, (_, _) => dragSnapshot = null,
            RoutingStrategies.Bubble);
    }

    /// <summary>pushes the primary element's crop values back into every slider and box</summary>
    private void RefreshCropControls()
    {
        var el = _currentElement;
        if (el is null) return;

        var wasUpdating = _updating;
        _updating = true;
        try
        {
            foreach (var (slider, box, get) in _cropControls)
            {
                var v = get(el);
                box.Value    = (decimal)v;
                slider.Value = v;
            }
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    private const double CropEpsilon = 1e-6;

    /// <summary>true when the crop range is the same inset on all four edges, i.e. it round-trips through the single collapsed slider without loss</summary>
    private static bool CropRangeIsUniform(GtElement el)
    {
        var c = el.Crop;
        if (c is null) return true;

        return Math.Abs(c.X0 - c.Y0) < CropEpsilon
            && Math.Abs(c.X0 - (1 - c.X1)) < CropEpsilon
            && Math.Abs(c.X0 - (1 - c.Y1)) < CropEpsilon;
    }

    /// <summary>true when all four feather edges carry the same width</summary>
    private static bool FeatherIsUniform(GtElement el)
    {
        var c = el.Crop;
        if (c is null) return true;

        return Math.Abs(c.FeatherLeft - c.FeatherTop) < CropEpsilon
            && Math.Abs(c.FeatherLeft - c.FeatherRight) < CropEpsilon
            && Math.Abs(c.FeatherLeft - c.FeatherBottom) < CropEpsilon;
    }

    private void SetCropExpanded(bool expanded)
    {
        CropExpandButton.IsChecked  = expanded;
        CropExpandedGrid.IsVisible  = expanded;
        CropCollapsedGrid.IsVisible = !expanded;
        UpdateCropLayout();
    }

    private void SetFeatherExpanded(bool expanded)
    {
        FeatherExpandButton.IsChecked  = expanded;
        FeatherExpandedGrid.IsVisible  = expanded;
        FeatherCollapsedGrid.IsVisible = !expanded;
        UpdateCropLayout();
    }

    /// <summary>two collapsed one-line groups stack vertically and fit the bar, as soon as either opens its two-row grid they have to sit side by side instead</summary>
    private void UpdateCropLayout()
    {
        var anyExpanded = CropExpandedGrid.IsVisible || FeatherExpandedGrid.IsVisible;

        CropFeatherStack.Orientation   = anyExpanded ? Orientation.Horizontal
                                                     : Orientation.Vertical;
        CropFeatherSeparator.IsVisible = anyExpanded;

        // collapsed rows already name themselves inline, so the caption is dead height
        CropCaption.IsVisible    = CropExpandedGrid.IsVisible;
        FeatherCaption.IsVisible = FeatherExpandedGrid.IsVisible;

        // an expanded group is two rows tall, so its buttons cost less width stacked
        CropButtonStack.Orientation    = CropExpandedGrid.IsVisible    ? Orientation.Vertical
                                                                       : Orientation.Horizontal;
        FeatherButtonStack.Orientation = FeatherExpandedGrid.IsVisible ? Orientation.Vertical
                                                                       : Orientation.Horizontal;
    }

    private void CropExpandButton_Click(object? sender, RoutedEventArgs e)
        => SetCropExpanded(CropExpandButton.IsChecked == true);

    private void FeatherExpandButton_Click(object? sender, RoutedEventArgs e)
        => SetFeatherExpanded(FeatherExpandButton.IsChecked == true);

    private void CropResetButton_Click(object? sender, RoutedEventArgs e)
        => ResetCrop("Reset crop range", c => { c.X0 = 0; c.Y0 = 0; c.X1 = 1; c.Y1 = 1; });

    private void FeatherResetButton_Click(object? sender, RoutedEventArgs e)
        => ResetCrop("Reset feather", c =>
        {
            c.FeatherLeft  = GtCrop.DefaultFeather; c.FeatherTop    = GtCrop.DefaultFeather;
            c.FeatherRight = GtCrop.DefaultFeather; c.FeatherBottom = GtCrop.DefaultFeather;
        });

    /// <summary>applies <paramref name="reset"/> to the crop of every selected element, dropping the whole Crop once nothing is left for it to do, and records one history entry</summary>
    private void ResetCrop(string description, Action<GtCrop> reset)
    {
        if (_updating || _currentElement is null) return;

        var befores = _allElements.Select(el => (el, crop: el.Crop?.Clone())).ToList();

        foreach (var el in _allElements)
        {
            if (el.Crop is null) continue;
            reset(el.Crop);
            if (el.Crop.IsDefault) el.Crop = null;
        }

        var afters = _allElements.Select(el => (el, crop: el.Crop?.Clone())).ToList();

        if (befores.Zip(afters, (b, a) => !GtCrop.AreEqual(b.crop, a.crop)).Any(x => x))
            PushHistory(description,
                () => { foreach (var (el, c) in befores) el.Crop = c?.Clone(); },
                () => { foreach (var (el, c) in afters)  el.Crop = c?.Clone(); });

        RefreshCropControls();
        Apply();
    }

    /// <summary>wires a NumericUpDown with three input modes, each pushing one history entry; when multiple elements are selected history is snapshot-based so undo/redo correctly restores all elements; deltaMode=true means each element gets its original value plus the primary's delta (position/size), deltaMode=false means each element gets the same absolute new value (font size etc.)</summary>
    private void AttachNumericBehavior(
        TextBlock? label, NumericUpDown nud,
        Action<GtElement, decimal> setter,
        Func<GtElement, decimal> getOrigValue,
        string description,
        bool deltaMode = true,
        decimal defaultIncrement = 1m)
    {
        bool IsMulti() => _allElements.Count > 1;

        List<(GtElement el, decimal ov)> TakeSnapshot() =>
            _allElements.Select(e => (e, getOrigValue(e))).ToList();

        Action MakeUndo(List<(GtElement el, decimal ov)> snap) =>
            () => { foreach (var (el, ov) in snap) setter(el, ov); };

        Action MakeRedo(List<(GtElement el, decimal ov)> snap, decimal before, decimal after) =>
            deltaMode
                ? (Action)(() => { var d = after - before; foreach (var (el, ov) in snap) setter(el, ov + d); })
                :          () => {                         foreach (var (el, _)  in snap) setter(el, after);   };

        // keyboard input tracking
        decimal keyboardBefore = 0m;
        List<(GtElement el, decimal ov)>? kbSnapshot = null;

        nud.GotFocus += (_, _) =>
        {
            if (_updating) return;
            keyboardBefore = nud.Value ?? 0m;
            kbSnapshot = IsMulti() ? TakeSnapshot() : null;
        };

        nud.ValueChanged += (_, e) => { if (_updating) keyboardBefore = e.NewValue ?? 0m; };

        nud.LostFocus += (_, _) =>
        {
            var after = nud.Value ?? 0m;
            if (after == keyboardBefore || _currentElement is null || History is null) return;
            var before = keyboardBefore;
            keyboardBefore = after;

            if (kbSnapshot != null)
            {
                var snap = kbSnapshot;
                kbSnapshot = null;
                History.Push(new PropertyChangeAction(description, MakeUndo(snap), MakeRedo(snap, before, after)));
            }
            else
            {
                var el = _currentElement;
                History.Push(new PropertyChangeAction(description,
                    () => setter(el, before), () => setter(el, after)));
            }
        };

        // spinner button: modifier increment plus history
        decimal spinnerBefore = 0m;
        List<(GtElement el, decimal ov)>? spinnerSnapshot = null;

        nud.AddHandler(PointerPressedEvent, (object? s, PointerPressedEventArgs e) =>
        {
            spinnerBefore   = nud.Value ?? 0m;
            spinnerSnapshot = IsMulti() ? TakeSnapshot() : null;
            nud.Increment   = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10m
                            : e.KeyModifiers.HasFlag(KeyModifiers.Alt)   ? 0.1m
                            : defaultIncrement;
        }, RoutingStrategies.Tunnel);

        nud.AddHandler(PointerReleasedEvent, (object? s, PointerReleasedEventArgs e) =>
        {
            nud.Increment = defaultIncrement;
            var after = nud.Value ?? 0m;
            if (after == spinnerBefore || _currentElement is null || History is null) return;
            var before = spinnerBefore;
            keyboardBefore = after;

            if (spinnerSnapshot != null)
            {
                var snap = spinnerSnapshot;
                spinnerSnapshot = null;
                History.Push(new PropertyChangeAction(description, MakeUndo(snap), MakeRedo(snap, before, after)));
            }
            else
            {
                var el = _currentElement;
                History.Push(new PropertyChangeAction(description,
                    () => setter(el, before), () => setter(el, after)));
            }
        }, RoutingStrategies.Bubble);

        nud.AddHandler(PointerCaptureLostEvent, (_, _) => nud.Increment = defaultIncrement,
            RoutingStrategies.Bubble);

        // label scrub
        if (label is null) return;
        label.Cursor = new Cursor(StandardCursorType.SizeWestEast);

        bool dragging = false;
        double startX = 0;
        decimal startValue = 0;
        List<(GtElement el, decimal ov)>? labelSnapshot = null;
        CursorScrub? scrub = null;   // null on platforms that can't warp the cursor

        label.AddHandler(PointerPressedEvent, (object? s, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            dragging      = true;
            startX        = e.GetPosition(label).X;
            startValue    = nud.Value ?? 0m;
            labelSnapshot = IsMulti() ? TakeSnapshot() : null;
            scrub         = CursorScrub.Begin();
            e.Pointer.Capture(label);
            e.Handled = true;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerMovedEvent, (object? s, PointerEventArgs e) =>
        {
            if (!dragging) return;
            // with a scrub active the cursor wraps around the monitor so travel comes from the OS in physical pixels, convert to DIPs to keep the feel identical either way
            var dx = scrub is not null
                ? scrub.UpdateX() / ((VisualRoot as TopLevel)?.RenderScaling ?? 1.0)
                : e.GetPosition(label).X - startX;
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0.1 : 1.0;
            var raw  = startValue + (decimal)(dx * step);
            nud.Value = Math.Clamp(raw, nud.Minimum, nud.Maximum);
            e.Handled = true;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerReleasedEvent, (object? s, PointerReleasedEventArgs e) =>
        {
            if (!dragging) return;
            dragging = false;
            scrub?.Dispose();
            scrub = null;
            e.Pointer.Capture(null);

            var after = nud.Value ?? 0m;
            if (after != startValue && _currentElement is not null && History is not null)
            {
                var before = startValue;
                keyboardBefore = after;

                if (labelSnapshot != null)
                {
                    var snap = labelSnapshot;
                    History.Push(new PropertyChangeAction(description, MakeUndo(snap), MakeRedo(snap, before, after)));
                }
                else
                {
                    var el = _currentElement;
                    History.Push(new PropertyChangeAction(description,
                        () => setter(el, before), () => setter(el, after)));
                }
            }
            labelSnapshot = null;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerCaptureLostEvent, (object? s, PointerCaptureLostEventArgs e) =>
        {
            dragging      = false;
            labelSnapshot = null;
            scrub?.Dispose();   // never leave the cursor hidden
            scrub = null;
        }, RoutingStrategies.Bubble);
    }

    private sealed class WeightEntry
    {
        public FontWeight Weight { get; }
        private readonly string _name;
        public WeightEntry(string name, FontWeight weight) { _name = name; Weight = weight; }
        public override string ToString() => _name;
    }

    private static bool HasFillStroke(GtElement el)
        => el is GtTextBlock or GtRectangleElement or GtEllipseElement;

    private static GtBrush? GetFill(GtElement el) => el switch
    {
        GtTextBlock        tb => tb.Fill,
        GtRectangleElement r  => r.Fill,
        GtEllipseElement   e  => e.Fill,
        _                     => null
    };

    private static void SetFill(GtElement el, GtBrush? brush)
    {
        if      (el is GtTextBlock        tb) tb.Fill = brush;
        else if (el is GtRectangleElement r)  r.Fill  = brush;
        else if (el is GtEllipseElement   e)  e.Fill  = brush;
    }

    private static GtBrush? GetStroke(GtElement el) => el switch
    {
        GtTextBlock        tb => tb.Stroke,
        GtRectangleElement r  => r.Stroke,
        GtEllipseElement   e  => e.Stroke,
        _                     => null
    };

    private static void SetStroke(GtElement el, GtBrush? brush)
    {
        if      (el is GtTextBlock        tb) tb.Stroke = brush;
        else if (el is GtRectangleElement r)  r.Stroke  = brush;
        else if (el is GtEllipseElement   e)  e.Stroke  = brush;
    }

    private static double GetStrokeThickness(GtElement el) => el switch
    {
        GtTextBlock        tb => tb.StrokeThickness,
        GtRectangleElement r  => r.StrokeThickness,
        GtEllipseElement   e  => e.StrokeThickness,
        _                     => 0
    };

    private static void SetStrokeThickness(GtElement el, double v)
    {
        if      (el is GtTextBlock        tb) tb.StrokeThickness = v;
        else if (el is GtRectangleElement r)  r.StrokeThickness  = v;
        else if (el is GtEllipseElement   e)  e.StrokeThickness  = v;
    }

    private static GtStrokeDashStyle GetStrokeDashStyle(GtElement el) => el switch
    {
        GtRectangleElement r => r.StrokeDashStyle,
        GtEllipseElement   e => e.StrokeDashStyle,
        _                    => GtStrokeDashStyle.Solid
    };

    private static void SetStrokeDashStyle(GtElement el, GtStrokeDashStyle s)
    {
        if      (el is GtRectangleElement r) r.StrokeDashStyle = s;
        else if (el is GtEllipseElement   e) e.StrokeDashStyle = s;
    }

    private static void UpdateSwatch(Border swatch, GtBrush? brush)
        => swatch.Background = BrushPreview.ToPreviewBrush(brush);

    private void PopulateStrokeCombos()
    {
        foreach (var s in new[] { "Solid", "Dash", "Dot", "Dash Dot", "Dash Dot Dot" })
            StrokeStyleBox.Items.Add(s);
        StrokeStyleBox.SelectedIndex = 0;

        foreach (var s in new[] { "Rounded", "Square" })
            CornersBox.Items.Add(s);
        CornersBox.SelectedIndex = 0;

        // order matches GtImageSizeMode's ordinals (GT's own combo order)
        foreach (var s in new[] { "Normal", "Stretch", "Centered", "TopRight" })
            ImageSizeModeBox.Items.Add(s);

        // both ticker combos follow their enum's ordinals, which are GT's own combo order
        foreach (var s in new[] { "Replace", "Add" })
            TickerTypeBox.Items.Add(s);
        TickerTypeBox.SelectedIndex = 0;

        foreach (var s in new[] { "Left", "Right", "Top", "Bottom" })
            TickerDirectionBox.Items.Add(s);
        TickerDirectionBox.SelectedIndex = 0;

        // order is GtAutoSize's ordinals, which the file stores and GT's own menu follows
        foreach (var s in new[] { "Fixed", "Width", "Height", "Shrink", "Width and Height" })
            AutoSizeBox.Items.Add(s);
        AutoSizeBox.SelectedIndex = (int)GtAutoSize.Fixed;
        ImageSizeModeBox.SelectedIndex = (int)GtImageSizeMode.Centered;

        // order is GtAnchor's ordinals, the wording is GT's own attribute names spaced out
        foreach (var s in new[] { "Top Left",    "Top Center",    "Top Right",
                                  "Middle Left", "Middle Center", "Middle Right",
                                  "Bottom Left", "Bottom Center", "Bottom Right" })
            AnchorBox.Items.Add(s);
        AnchorBox.SelectedIndex = (int)GtAnchor.TopLeft;
    }

    private void ImageBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentElement is GtImageElement img)
            ImageSourceBrowseRequested?.Invoke(this, img);
    }

    private void ImageSizeModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var mode    = (GtImageSizeMode)ImageSizeModeBox.SelectedIndex;
        var images  = _allElements.OfType<GtImageElement>().ToList();
        var befores = images.Select(i => (i, i.SizeMode)).ToList();
        foreach (var i in images) i.SizeMode = mode;
        if (befores.Any(x => x.Item2 != mode))
            PushHistory("Image size mode",
                () => { foreach (var (i, b) in befores) i.SizeMode = b; },
                () => { foreach (var i in images) i.SizeMode = mode; });
        Apply();
    }

    private void OpenColorPicker(bool fill)
    {
        if (_currentElement is null) return;
        _editingFill = fill;

        if (_colorPicker is null)
        {
            _colorPicker = new ColorPickerControl();
            _colorPicker.ColorChanged     += OnPickerColorChanged;
            _colorPicker.GradientRequested += OnGradientRequested;
        }

        if (_colorPopup is null)
        {
            _colorPopup = new Popup
            {
                Child                = _colorPicker,
                Placement            = PlacementMode.Bottom,
                IsLightDismissEnabled = true,
            };
            _colorPopup.Closed += ColorPopup_Closed;
        }

        // snapshot brushes before editing for history
        _pickerBrushSnapshot = SnapshotBrushes(fill);

        var initColor = PrimaryColor(fill ? GetFill(_currentElement) : GetStroke(_currentElement));

        // seeding the picker must not flatten an existing gradient
        _suppressPickerColor = true;
        _colorPicker.SetColor(initColor);
        _suppressPickerColor = false;
        _colorPicker.RefreshRecentColors();

        _colorPopup.PlacementTarget = fill ? FillColorButton : StrokeColorButton;
        _colorPopup.IsOpen = true;
    }

    private List<(GtElement el, GtBrush? before)> SnapshotBrushes(bool fill)
        => _allElements
            .Where(HasFillStroke)
            .Select(el => (el, (fill ? GetFill(el) : GetStroke(el))?.Clone()))
            .ToList();

    /// <summary>pushes one history entry covering every brush change since the snapshot</summary>
    private void PushBrushHistory(string desc, List<(GtElement el, GtBrush? before)> snap, bool fill)
    {
        if (History is null) return;

        var after = snap
            .Select(x => (x.el, brush: (fill ? GetFill(x.el) : GetStroke(x.el))?.Clone()))
            .ToList();

        var changed = snap.Select((x, i) => GtBrush.AreEqual(x.before, after[i].brush)).Any(same => !same);
        if (!changed) return;

        History.Push(new PropertyChangeAction(desc,
            () => { foreach (var (el, br) in snap)  SetBrush(el, fill, br?.Clone()); },
            () => { foreach (var (el, br) in after) SetBrush(el, fill, br?.Clone()); }));
    }

    private static void SetBrush(GtElement el, bool fill, GtBrush? brush)
    {
        if (fill) SetFill(el, brush); else SetStroke(el, brush);
    }

    private void ColorPopup_Closed(object? sender, EventArgs e)
    {
        if (_colorPicker is null || _pickerBrushSnapshot is null) return;

        // the gradient editor takes over the snapshot, it pushes the history entry itself
        if (_openingGradientEditor) return;

        ColorPickerControl.AddToRecent(_colorPicker.Color);
        PushBrushHistory(_editingFill ? "Fill color" : "Stroke color", _pickerBrushSnapshot, _editingFill);
        _pickerBrushSnapshot = null;
    }

    /// <summary>colour that best represents a brush in the flat picker (first stop for gradients)</summary>
    private static Color PrimaryColor(GtBrush? brush)
    {
        if (brush is null) return Colors.Transparent;
        if (brush.Type is GtBrushType.LinearGradient or GtBrushType.RadialGradient && brush.Stops.Count > 0)
            return brush.Stops[0].Color;
        return brush.Color;
    }

    private void OnPickerColorChanged(Color c)
    {
        if (_currentElement is null || _suppressPickerColor) return;
        foreach (var el in _allElements.Where(HasFillStroke))
        {
            var br = (_editingFill ? GetFill(el) : GetStroke(el)) ?? new GtBrush();
            br.Color = c;
            // picking a flat colour replaces a gradient, bitmap fills keep their image
            if (br.Type is GtBrushType.LinearGradient or GtBrushType.RadialGradient)
            {
                br.Type = GtBrushType.Solid;
                br.Stops.Clear();
            }
            SetBrush(el, _editingFill, br);
        }
        UpdateSwatch(_editingFill ? FillColorSwatch : StrokeColorSwatch,
                     _editingFill ? GetFill(_currentElement) : GetStroke(_currentElement));
        Apply();
    }

    private async void OnGradientRequested(object? sender, EventArgs e)
    {
        if (_currentElement is null) return;

        var fill = _editingFill;
        var snap = _pickerBrushSnapshot ?? SnapshotBrushes(fill);

        _openingGradientEditor = true;
        if (_colorPopup is not null) _colorPopup.IsOpen = false;
        _openingGradientEditor = false;
        _pickerBrushSnapshot   = null;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new GradientEditorWindow(fill ? GetFill(_currentElement) : GetStroke(_currentElement));
        dialog.Applied += brush => ApplyBrushToSelection(brush, fill);

        var ok = await dialog.ShowDialog<bool>(owner);

        if (ok)
        {
            ApplyBrushToSelection(dialog.Brush, fill);
            PushBrushHistory(fill ? "Fill gradient" : "Stroke gradient", snap, fill);
        }
        else
        {
            // revert the live preview
            foreach (var (el, br) in snap) SetBrush(el, fill, br?.Clone());
            UpdateSwatch(fill ? FillColorSwatch : StrokeColorSwatch,
                         fill ? GetFill(_currentElement) : GetStroke(_currentElement));
            ElementChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyBrushToSelection(GtBrush brush, bool fill)
    {
        foreach (var el in _allElements.Where(HasFillStroke))
            SetBrush(el, fill, brush.Clone());

        UpdateSwatch(fill ? FillColorSwatch : StrokeColorSwatch, brush);
        ElementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FillColorButton_Click(object? sender, RoutedEventArgs e)
        => OpenColorPicker(fill: true);

    private void StrokeColorButton_Click(object? sender, RoutedEventArgs e)
        => OpenColorPicker(fill: false);

    private void StrokeThicknessBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var val = (double)(e.NewValue ?? 0m);
        foreach (var el in _allElements.Where(HasFillStroke))
            SetStrokeThickness(el, val);
        Apply();
    }

    private void StrokeStyleBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var style = (GtStrokeDashStyle)StrokeStyleBox.SelectedIndex;
        var befores = _allElements.Where(HasFillStroke)
                                  .Select(el => (el, GetStrokeDashStyle(el))).ToList();
        foreach (var el in _allElements.Where(HasFillStroke))
            SetStrokeDashStyle(el, style);
        if (befores.Any(x => x.Item2 != style))
            PushHistory("Stroke style",
                () => { foreach (var (el, b) in befores) SetStrokeDashStyle(el, b); },
                () => { foreach (var (el, _) in befores) SetStrokeDashStyle(el, style); });
        Apply();
    }

    private void TickerTypeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var type    = (GtTickerType)TickerTypeBox.SelectedIndex;
        var tickers = _allElements.OfType<GtTickerElement>().ToList();
        var befores = tickers.Select(t => (t, t.TickerType)).ToList();
        foreach (var t in tickers) t.TickerType = type;
        if (befores.Any(x => x.Item2 != type))
            PushHistory("Ticker type",
                () => { foreach (var (t, b) in befores) t.TickerType = b; },
                () => { foreach (var t in tickers) t.TickerType = type; });
        Apply();
    }

    private void TickerDirectionBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var direction = (GtTickerDirection)TickerDirectionBox.SelectedIndex;
        var tickers   = _allElements.OfType<GtTickerElement>().ToList();
        var befores   = tickers.Select(t => (t, t.Direction)).ToList();
        foreach (var t in tickers) t.Direction = direction;
        if (befores.Any(x => x.Item2 != direction))
            PushHistory("Ticker direction",
                () => { foreach (var (t, b) in befores) t.Direction = b; },
                () => { foreach (var t in tickers) t.Direction = direction; });
        Apply();
    }

    private void TickerPlayButton_Click(object? sender, RoutedEventArgs e)
        => TickerPlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void TickerStopButton_Click(object? sender, RoutedEventArgs e)
        => TickerStopRequested?.Invoke(this, EventArgs.Empty);

    private void TickerSpeedBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var speed = (double)(e.NewValue ?? 0m);
        foreach (var t in _allElements.OfType<GtTickerElement>())
            t.Speed = speed;
        Apply();
    }

    private void CornersBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _currentElement is null) return;
        var style   = (GtRectangleStyle)CornersBox.SelectedIndex;
        var rects   = _allElements.OfType<GtRectangleElement>().ToList();
        var befores = rects.Select(r => (r, r.Style)).ToList();
        foreach (var r in rects) r.Style = style;
        if (befores.Any(x => x.Item2 != style))
            PushHistory("Corner style",
                () => { foreach (var (r, b) in befores) r.Style = b; },
                () => { foreach (var r in rects) r.Style = style; });
        Apply();
    }
}

/// <summary>which alignment was asked for, and what it should align against</summary>
public sealed class AlignRequestedEventArgs : EventArgs
{
    public AlignRequestedEventArgs(GtAlign align, GtAlignTarget target)
    {
        Align  = align;
        Target = target;
    }

    public GtAlign       Align  { get; }
    public GtAlignTarget Target { get; }
}
