This is a standalone third party editor for vMix GT Title Designer, that adds extended functionality to the editor itself.

The file format is explained in the GTZIP-Format.md file.

Don't automatically commit to git

---

## Project structure

**Framework:** Avalonia 12.1.2, .NET 10, C# - target `net10.0`.

**Project file:** `src/VmixGtPlus/VmixGtPlus.csproj`

---

## Source layout

### Models - `src/VmixGtPlus/Models/`
- **`GtModels.cs`** - all data model classes
  - `GtDocument` - root; holds `Width`, `Height`, `List<GtLayer>`
  - `GtLayer` - named layer; `Location` (GtPoint), `Dimensions` (GtSize), `InnerWidth/Height`, `List<GtElement>`, `Locked`, `Visible`
  - `GtElement` (abstract) - base: `Name`, `Location`, `Dimensions`, `Visible`, `Opacity`, `Locked`, `DataFlags`
  - `GtTextBlock`, `GtImageElement`, `GtRectangleElement` - concrete elements
  - `GtTickerElement : GtTextBlock` - scrolling ticker; adds `Speed` (px/frame), `Direction`,
    `TickerType`, and the template text (kept in the inherited `Text`). Subclassing means every
    `is GtTextBlock` test also catches a ticker - order type switches ticker-first
  - `GtBrush` - solid/gradient/bitmap; `GtBrushType`, `GtGradientStop`
  - `GtStoryboard` - animations for one vMix event. Identified by the pair `(Type, DataName)`:
    `DataName` scopes a DataChangeIn/Out storyboard to one data field, empty means any field
  - `GtPoint(X,Y)`, `GtSize(Width,Height)` - immutable records; `Location`/`Dimensions` properties have setters so assign `new GtPoint(x,y)` to mutate

### Services - `src/VmixGtPlus/Services/`
- **`GtZipReader.cs`** - reads `.gtzip` → `(GtDocument, Dictionary<string,byte[]> assets)`. Assets keyed by logical path (forward-slash normalised).
- **`GtZipWriter.cs`** - writes `(GtDocument, assets)` → `.gtzip`. Generates fresh GUIDs for asset blobs. Writes `document.xml` as UTF-8 (no BOM). Atomic write via temp file + rename.
- **`HistoryService.cs`** - undo/redo stack. `IHistoryAction` interface. Concrete actions: `MoveElementsAction`, `ResizeElementsAction`. `HistoryService.Push/Undo/Redo/Clear`, fires `Changed` event.
- **`RecentFilesService.cs`** - persists recent file paths
- **`PreferencesService.cs`** - persists user preferences (outside-canvas opacity, debug panel visibility)
- **`GtDataFieldService.cs`** - the composition's data fields (`Name.Text`, `Name.Source`,
  `Name.Fill.Color`/`.Fill.Bitmap`, ticker template children). Only direct children of a
  top-level layer register one, and the list is reversed, matching GT. These are the scopes a
  DataChangeIn/Out storyboard can be keyed to; `Hidden`/`NoEvents` fields are not offered
- **`TickerLayout.cs`** - ticker text splitting + GT's per-frame scroll walk, replayed as a pure
  function of the frame number (`Simulate`) or laid out flush at rest (`Rest`)
- **`FfmpegService.cs`** - locates the ffmpeg binary (prefs path → app dir → PATH → usual install dirs); can download the official Windows build into `%APPDATA%/VmixGtPlus/ffmpeg`
- **`VideoExportService.cs`** - MP4 export. Drives `GtCanvasControl.AnimationFrame` + `ExportToBitmap()` one frame at a time on the UI thread and pipes raw BGRA into ffmpeg's stdin via a bounded background writer
- **`Logger.cs`** - static logger

### Controls - `src/VmixGtPlus/Controls/`
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
  - Ticker clock: `PlayTicker`/`PauseTicker`/`StopTicker`/`ToggleTickerPlayback`, `TickerPlaying`,
    `TickerFrame` - a 60 fps DispatcherTimer counting frames. A storyboard preview takes the
    clock over while `AnimationFrame` is set; frame 0 draws tickers at rest
- **`LayersPanelControl.axaml/.cs`** - layers panel; populated via `Populate(GtDocument)`, syncs selection highlight via `Canvas` property
- **`HistoryPanelControl.axaml/.cs`** - history list; set `History` property to wire up; auto-refreshes on `HistoryService.Changed`

### Views - `src/VmixGtPlus/Views/`
- **`MainWindow.axaml/.cs`** - main window
  - Fields: `_currentPath` (open file path), `_currentAssets` (asset dict), `_history`, `_reader`, `_writer`
  - File menu built in code via `RebuildFileMenu()` - includes Open, Save (Ctrl+S), Save As (Ctrl+Shift+S), recent files, Exit
  - Edit menu in AXAML - Undo (Ctrl+Z), Redo (Ctrl+Y), Preferences
  - Toolbar (left): Select (S), Edit (E), Text Box (T), Rectangle (R), Ticker (K), Image (I) - ToggleButtons `SelectToolButton`/`EditToolButton`/`TextBoxToolButton`/`RectangleToolButton`/`TickerToolButton`
  - Right panel: tab strip (Layers / History) → `LayersPanel` / `HistoryPanel`; Debug panel below (toggled in Preferences)
  - Bottom bar: status text, Fit button, zoom slider + label
  - `OnKeyDown`: S=Select, E=Edit, Ctrl+Z=Undo, Ctrl+Y=Redo, Ctrl+S=Save, Ctrl+Shift+S=SaveAs
  - Middle-click drag = pan canvas; Ctrl+scroll = zoom
- **`ExportVideoWindow.axaml/.cs`** - MP4 export dialog: storyboard (single, or the TransitionIn+TransitionOut pair), hold seconds, fps, quality, background, output path
- **`PreferencesWindow.axaml/.cs`** - preferences dialog (outside canvas opacity, debug panel toggle)

---

## Key patterns

- **Coordinate spaces:** Element `Location`/`Dimensions` are layer-local. Canvas renders with `layer.Location` as transform offset. `ElementAbsBounds` converts to absolute doc space. Zoom is a scale transform on the whole canvas control.
- **Mutation:** Model objects mutate in place (properties have setters). Canvas calls `InvalidateVisual()` after mutations. History actions store before/after snapshots.
- **History:** After any user gesture that changes the model, construct the appropriate `IHistoryAction` and call `GtCanvas.History.Push(action)`. `HistoryService.Changed` fires → `MainWindow.OnHistoryChanged` enables/disables menu items.
- **Assets:** `Dictionary<string,byte[]>` with forward-slash logical paths as keys. Stored in `MainWindow._currentAssets`. Writer regenerates GUIDs on save.
- **No XAML bindings for file menu** - built entirely in `RebuildFileMenu()` C# code because recent files are dynamic.
