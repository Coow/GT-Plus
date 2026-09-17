This is a standalone third party editor for vMix GT Title Designer, that adds extended functionality to the editor itself.

The file format is explained in the GTZIP-Format.md file.

Don't automatically commit to git

---

## Project structure

**Framework:** Avalonia 12.1.2, .NET 10, C# - target `net10.0`.

**Project file:** `src/GtPlus/GtPlus.csproj`

---

## Source layout

### Models - `src/GtPlus/Models/`
- **`GtModels.cs`** - all data model classes
  - `GtDocument` - root; holds `Width`, `Height`, `List<GtLayer>`
  - `GtLayer` - named layer; `Location` (GtPoint), `Dimensions` (GtSize), `InnerWidth/Height`, `List<GtElement>`, `Locked`, `Visible`
  - `GtElement` (abstract) - base: `Name`, `Location`, `Dimensions`, `Visible`, `Opacity`, `Locked`, `DataFlags`
  - `GtTextBlock`, `GtImageElement`, `GtRectangleElement` - concrete elements
  - `GtTickerElement : GtTextBlock` - scrolling ticker; adds `Speed` (px/frame), `Direction`,
    `TickerType`, and the template text (kept in the inherited `Text`). Subclassing means every
    `is GtTextBlock` test also catches a ticker - order type switches ticker-first
  - `GtWebElement` - live web page drawn as a design reference; `Url`, `Interactive`,
    `TransparentBackground`. Saved as a carrier object (`WebPagePart`) so it survives a
    round-trip through this editor, but never drawn into a video export - vMix only ever sees
    the empty rectangle it is stored as
  - `GtEffect` - one entry of GT's generic per-object effect list (`GtElement.Effects`,
    `GtLayer.Effects`); `GtEffectType` × `GtEffectMode`, where Mode is the pipeline *stage*
    not the effect kind, so a shadow is `Type=Shadow, Mode=Shadow`. Defaults are omitted on
    write and re-seeded on read, so an absent `Color` means opaque black
  - `GtBrush` - solid/gradient/bitmap; `GtBrushType`, `GtGradientStop`
  - `GtStoryboard` - animations for one vMix event. Identified by the pair `(Type, DataName)`:
    `DataName` scopes a DataChangeIn/Out storyboard to one data field, empty means any field
  - `GtPoint(X,Y)`, `GtSize(Width,Height)` - immutable records; `Location`/`Dimensions` properties have setters so assign `new GtPoint(x,y)` to mutate
- **`GtShadow.cs`** - the only effect type that renders: GT's 12-preset gallery, `Apply` (GT's
  destructive set-shadow semantics), `Resolve` (collapses the whole shadow stage into one
  `GtResolvedShadow` - chained Gaussians compose exactly), and `BlurRadiusForSigma`, the single
  place the Avalonia-radius ↔ GT-sigma conversion lives

### Services - `src/GtPlus/Services/`
- **`GtZipReader.cs`** - reads `.gtzip` → `(GtDocument, Dictionary<string,byte[]> assets)`. Assets keyed by logical path (forward-slash normalised).
- **`GtZipWriter.cs`** - writes `(GtDocument, assets)` → `.gtzip`. Generates fresh GUIDs for asset blobs. Writes `document.xml` as UTF-8 (no BOM). Atomic write via temp file + rename.
- **`HistoryService.cs`** - undo/redo stack. `IHistoryAction` interface. Concrete actions: `MoveElementsAction`, `ResizeElementsAction`. `HistoryService.Push/Undo/Redo/Clear`, fires `Changed` event.
- **`RecentFilesService.cs`** - persists recent file paths
- **`PreferencesService.cs`** - persists user preferences (outside-canvas opacity, debug panel visibility)
- **`GtDataFieldService.cs`** - the composition's data fields (`Name.Text`, `Name.Source`,
  `Name.Fill.Color`/`.Fill.Bitmap`, ticker template children). Only direct children of a
  top-level layer register one, and the list is reversed, matching GT. These are the scopes a
  DataChangeIn/Out storyboard can be keyed to; `Hidden`/`NoEvents` fields are not offered
- **`ImageSequenceBuilder.cs`** - turns files/folders on disk into a GTZIP image sequence: natural
  (digit-aware) frame ordering, one logical asset folder per sequence, frame 0 as the anchor
- **`TickerLayout.cs`** - ticker text splitting + GT's per-frame scroll walk, replayed as a pure
  function of the frame number (`Simulate`) or laid out flush at rest (`Rest`)
- **`WebPagePart.cs`** - stores `GtWebElement`s in a .gtzip the way `GuidesPart` stores guides: each one is swapped **in place** for a rectangle that draws nothing (transparent fill, no stroke) carrying the page's real geometry in its own attributes and `name|url|interactive|transparent` base64url-encoded into its `Name`. Swapping in place is what makes layer and z-order round-trip without being encoded. `Replace`/`Restore` bracket the write; `ExtractCarriers` runs on load. Animations aimed at a web page are still dropped on save - the file holds it under the carrier's name, not its own
- **`ChromiumService.cs`** - locates an installed Chrome/Edge/Chromium (prefs path -> PATH -> usual install dirs). Nothing is bundled or downloaded
- **`CdpSession.cs`** - one headless browser driven over the Chrome DevTools Protocol: `Page.startScreencast` streams the page as PNG frames (every frame must be answered with `Page.screencastFrameAck` or the stream stops after three), `Input.dispatch*` sends clicks, wheel and keys back. No Avalonia types - bytes and numbers only
- **`WebPreviewService.cs`** - one `CdpSession` per `GtWebElement`, keyed by element reference. Starts/stops sessions from the document (a deleted or hidden element loses its browser), debounces resize, coalesces frames so a slow decode drops frames instead of queueing them
- **`FfmpegService.cs`** - locates the ffmpeg binary (prefs path → app dir → PATH → usual install dirs); can download the official Windows build into `%APPDATA%/GtPlus/ffmpeg`
- **`VideoExportService.cs`** - MP4 export. Drives `GtCanvasControl.AnimationFrame` + `ExportToBitmap()` one frame at a time on the UI thread and pipes raw BGRA into ffmpeg's stdin via a bounded background writer
- **`UpdateService.cs`** - `AppVersion.Current` (from the linked version.json resource) plus the GitHub `releases/latest` check and dotted-numeric version compare. Never downloads anything - a newer release only opens the release page
- **`Logger.cs`** - static logger

### Controls - `src/GtPlus/Controls/`
- **`GtCanvasControl.cs`** - main canvas; renders document, handles selection + move + resize
  - Enums: `CanvasTool { Select, Edit, TextBox, Rectangle, Ticker }`, `ResizeHandle { None, NW, N, NE, E, SE, S, SW, W }`
  - Styled props: `Zoom`, `OutsideCanvasOpacity`, `ActiveTool`
  - Plain prop: `HistoryService? History` - push actions here
  - Selection: `SelectedElements`, `SetSelection`, `ToggleSelection`, `ClearSelection`, `SelectionChanged` event
  - Select tool: click to select, Shift+click to toggle, Ctrl+drag for rubber-band box
  - Edit tool: click to select, drag to move, handle drag to resize; Alt held = temporary Select
  - Resize handles: 8 per selected element (individual bounds, not group bounds); all selected elements resize by same delta
  - Locked elements / locked layers cannot be moved or resized
  - `ElementAbsBounds(layer, el)` → absolute doc-space Rect (layer.Location + el.Location)
  - Web elements: `WebPreviews` (a `WebPreviewService`) supplies frames; `RenderWeb` skips
    everything in export mode. When an element is `Interactive` the canvas forwards pointer,
    wheel and key events to its page instead of selecting/moving - Alt bypasses, resize handles
    still win, Escape returns the keyboard (`WebInputFocused`)
  - Shadows: `RenderShape` wraps `RenderShapeCore` in `ctx.PushEffect` so crop, mask and opacity
    all act on element-plus-shadow as GT's compose does. Avalonia folds an ambient opacity into
    both the content and the effect, double-fading the shadow, so any opacity that may sit over
    one goes through `PushComposedOpacity` (an opacity *mask*, which forces a real layer) instead
    of `PushOpacity`
  - Ticker clock: `PlayTicker`/`PauseTicker`/`StopTicker`/`ToggleTickerPlayback`, `TickerPlaying`,
    `TickerFrame` - a 60 fps DispatcherTimer counting frames. A storyboard preview takes the
    clock over while `AnimationFrame` is set; frame 0 draws tickers at rest
- **`LayersPanelControl.axaml/.cs`** - layers panel; populated via `Populate(GtDocument)`, syncs selection highlight via `Canvas` property
- **`HistoryPanelControl.axaml/.cs`** - history list; set `History` property to wire up; auto-refreshes on `HistoryService.Changed`

### Views - `src/GtPlus/Views/`
- **`MainWindow.axaml/.cs`** - main window
  - Fields: `_currentPath` (open file path), `_currentAssets` (asset dict), `_history`, `_reader`, `_writer`
  - File menu built in code via `RebuildFileMenu()` - includes Open, Save (Ctrl+S), Save As (Ctrl+Shift+S), recent files, Exit
  - Edit menu in AXAML - Undo (Ctrl+Z), Redo (Ctrl+Y), Preferences
  - Toolbar (left): Select (S), Edit (E), Text Box (T), Rectangle (R), Ticker (K), Web Page (W), Image (I) - ToggleButtons `SelectToolButton`/`EditToolButton`/`TextBoxToolButton`/`RectangleToolButton`/`TickerToolButton`
  - Right panel: tab strip (Layers / History) → `LayersPanel` / `HistoryPanel`; Debug panel below (toggled in Preferences)
  - Bottom bar: status text, Fit button, zoom slider + label
  - `OnKeyDown`: S=Select, E=Edit, Ctrl+Z=Undo, Ctrl+Y=Redo, Ctrl+S=Save, Ctrl+Shift+S=SaveAs
  - Middle-click drag = pan canvas; Ctrl+scroll = zoom
- **`ExportVideoWindow.axaml/.cs`** - MP4 export dialog: storyboard (single, or the TransitionIn+TransitionOut pair), hold seconds, fps, quality, background, output path
- **`SequenceLengthWindow.axaml/.cs`** - sets an ImageSequence clip's Duration from the sequence's
  frame count and a target frame rate (default 60 fps); opened by the timeline strip's "Frames..." button,
  and straight away by the "+ Sequence" button / layers-panel "Add Image Sequence Animation..." entry, which
  create the clip on the storyboard the timeline is showing (both grey out when no storyboard is selected)
- **`PreferencesWindow.axaml/.cs`** - preferences dialog (outside canvas opacity, debug panel toggle, update check on startup)
- **`UpdateWindow.axaml/.cs`** - update check result: version numbers, release notes, "Open Download Page" / "Skip This Version". Opened from File > Check for Updates, and at startup only when a newer, non-skipped release exists

---

## Key patterns

- **Coordinate spaces:** Element `Location`/`Dimensions` are layer-local. Canvas renders with `layer.Location` as transform offset. `ElementAbsBounds` converts to absolute doc space. Zoom is a scale transform on the whole canvas control.
- **Mutation:** Model objects mutate in place (properties have setters). Canvas calls `InvalidateVisual()` after mutations. History actions store before/after snapshots.
- **History:** After any user gesture that changes the model, construct the appropriate `IHistoryAction` and call `GtCanvas.History.Push(action)`. `HistoryService.Changed` fires → `MainWindow.OnHistoryChanged` enables/disables menu items.
- **Assets:** `Dictionary<string,byte[]>` with forward-slash logical paths as keys. Stored in `MainWindow._currentAssets`. Writer regenerates GUIDs on save.
- **No XAML bindings for file menu** - built entirely in `RebuildFileMenu()` C# code because recent files are dynamic.
