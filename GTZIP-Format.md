# GTZIP File Format

## Overview

GTZIP is the file format used by **vMix GT Title Designer** for graphics/title templates.

- File extension: `.gtzip`
- Container: standard **ZIP archive** (rename to `.zip` to open with any archive tool)
- Encoding: OpenXML-style package (uses `[Content_Types].xml`)

---

## Container Structure

```
MyTemplate.gtzip (ZIP)
├── [Content_Types].xml     ← OpenXML content type manifest
├── document.xml            ← main template (UTF-16 XML)
├── resources.xml           ← asset manifest
├── thumbnail.png           ← preview image shown in vMix
├── {guid}                  ← embedded asset (binary blob, no extension)
├── {guid}                  ← embedded asset
└── ...
```

Assets are stored as **flat GUID-named files** with no extension in the ZIP root. The `resources.xml` maps logical paths to those GUIDs.

---

## `[Content_Types].xml`

Standard OpenXML content types file. Lists MIME types for each entry:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="xml" ContentType="text/xml" />
  <Default Extension="png" ContentType="image/png" />
  <Override PartName="/55c33889-aba1-4059-91a2-7777c599530d" ContentType="application/octet-stream" />
  ...
</Types>
```

---

## `resources.xml`

Maps logical asset paths (used in `document.xml`) to the stored GUID filenames:

```xml
<resources>
  <resource filename="9823b0f8-188a-44f3-989a-940c478ca76a\ANOTAND.png">
    <source guid="55c33889-aba1-4059-91a2-7777c599530d">
      9823b0f8-188a-44f3-989a-940c478ca76a\ANOTAND.png
    </source>
  </resource>
</resources>
```

- `filename` - the path used in `document.xml` `Source` attributes
- `guid` - the actual filename in the ZIP (binary blob)
- Path separator is `\` (Windows backslash)
- Structure: `{folder-guid}\{original-filename}`

If the template has no assets, `resources.xml` is `<resources />`.

### A resource may hold many sources (image sequences)

**A `<resource>` is not necessarily a single file.** When the asset is an *image sequence*, the
resource carries **one `<source>` per frame, in playback order**:

```xml
<resources>
  <resource filename="92fad923-93c5-4919-ac5e-616c26862357\GameOpener00095.png">
    <source guid="c541cc93-76b1-49bf-8f9f-db3868a1e53a">92fad923-...\GameOpener00095.png</source>
    <source guid="1c55134e-3da6-4cbe-87c0-ee374334022f">92fad923-...\GameOpener00096.png</source>
    <source guid="b702e748-1cc2-4f0c-9421-943541cd5230">92fad923-...\GameOpener00097.png</source>
    <!-- ... 625 sources in this resource ... -->
  </resource>
</resources>
```

| Part                        | Meaning                                                                  |
| --------------------------- | ------------------------------------------------------------------------ |
| `resource/@filename`        | The **anchor** - the only path `document.xml` ever references             |
| `source/@guid`              | ZIP entry name of that frame's blob                                       |
| `source` text content       | That frame's own logical path                                             |
| `source` **order**          | Playback order. Index 0 is the anchor frame                               |

The anchor's own `<source>` repeats the resource `filename`. Frame filenames are usually
zero-padded and sequential (`Name00000.png`, `Name00001.png`, …), but **order comes from
document order, not from parsing the digits**.

> **Trap:** reading only the first `<source>` per resource silently discards every later
> frame. `EMS GameOpener.gtzip` has 2 resources holding 720 and 625 frames respectively -
> 1345 blobs total, ~276 MB. Writing such a file back with one source per resource reduces
> it to two still images and destroys the animation.

A sequence is played by an [`ImageSequence`](#animation-types) animation, and is scrubbed in
the designer by the [`Position`](#image) attribute on the `<Bitmap>` element.

---

## `document.xml`

The main template descriptor. Encoding: **UTF-16**. Root element: `<Composition>`.

### Root

```xml
<?xml version="1.0" encoding="utf-16"?>
<Composition Width="1920" Height="1080">
  <Layer ... />
  <Layer ... />
  <Storyboard> ... </Storyboard>   <!-- zero or more, always after the layers -->
</Composition>
```

### Coordinate System

- Origin: **top-left** corner
- Units: **pixels**
- `Dimensions` attribute: `"width,height,depth"` - depth (Z) is always `0`
- `Location` attribute: `"x,y,z"` - z is always `0`; x/y can be negative (off-screen)
- `Location` measures the element's **anchor point**, not always its top-left corner - see below

### `Anchor` - which point `Location` measures

Every element carries an optional `Anchor`, and `Location` is the position of *that* point of
the box rather than the corner:

```
top-left corner = Location - (fx * Width, fy * Height)
```

where `fx` is `0` / `0.5` / `1` for a Left / Center / Right anchor and `fy` is `0` / `0.5` / `1`
for Top / Middle / Bottom.

| Value                                        | Meaning                        |
| -------------------------------------------- | ------------------------------ |
| *(attribute absent)* / `TopLeft`             | Location is the top-left corner |
| `TopCenter`, `TopRight`                      | top edge, centred / right       |
| `MiddleLeft`, `MiddleCenter`, `MiddleRight`  | vertical middle                 |
| `BottomLeft`, `BottomCenter`, `BottomRight`  | bottom edge                     |

GT omits the attribute entirely at the `TopLeft` default. `gtzip-examples/AnchorExample.gtzip`
carries one rectangle per value; a centred rectangle there reads
`Dimensions="426,175,0" Location="960,515.5,0" Anchor="MiddleCenter"` - `960` is the canvas
centre line, and the `.5` on Y is half of the odd `175` height, which is exactly what a
half-box offset produces.

The anchor is also what the box grows away from: an auto-sizing text object keeps its anchor
point still and expands the other way, so the default top-left anchor grows right and down
while a `MiddleCenter` one grows both ways.

---

## Element Hierarchy

```
<Composition>               ← root canvas
  ├── <Layer>               ← named group/layer
  │     └── <Layer.Composition>
  │           └── <Composition>   ← layer's inner canvas
  │                 ├── <TextBlock>
  │                 ├── <Image>
  │                 └── <Rectangle>
  └── <Storyboard>          ← animation set (see Storyboards below)
        └── <Storyboard.Animations>
              ├── <Fade Object="..." />
              └── <Fly  Object="..." />
```

Layers can be positioned (`Location`) and sized (`Dimensions`) independently of the root canvas - allowing sub-compositions to be placed anywhere on the canvas.

---

## `<Layer>`

```xml
<Layer Name="LayerName" Dimensions="1920,1080,0" Location="451,885,0" Locked="False">
  <Layer.Composition>
    <Composition Width="1017" Height="160">
      <!-- elements -->
    </Composition>
  </Layer.Composition>
</Layer>
```

| Attribute      | Type        | Description                                    |
| -------------- | ----------- | ---------------------------------------------- |
| `Name`       | string      | Identifier for the layer                       |
| `Dimensions` | `"w,h,0"` | Size of the layer on the canvas                |
| `Location`   | `"x,y,0"` | Position on the canvas (top-left of the layer) |
| `Locked`     | bool        | Whether the layer is locked in the designer    |

The inner `<Composition>` has its own `Width`/`Height` - this is the coordinate space for elements inside the layer.

---

## `<TextBlock>`

```xml
<TextBlock
  Name="Title"
  Dimensions="1073,68,0"
  Location="423.5,506,0"
  Text="Default Text"
  FontFamily="Brixton"
  FontSize="72"
  FontWeight="Bold"
  TextAlign="Center"
  VerticalAlign="Center"
  TextEffect="Uppercase"
  LineSpacing="1"
  Visible="True"
  Opacity="1"
  DataFlags="Hidden"
  IgnoreOverhang="True"
  TextWordWrapping="NoWrap"
>
  <TextBlock.Fill>
    <Brush ... />
  </TextBlock.Fill>
  <TextBlock.Stroke>
    <Brush ... />
  </TextBlock.Stroke>
</TextBlock>
```

| Attribute            | Values                          | Description                                                |
| -------------------- | ------------------------------- | ---------------------------------------------------------- |
| `Name`             | string                          | Element identifier (used in vMix API, masks, data binding) |
| `Dimensions`       | `"w,h,0"`                     | Bounding box size                                          |
| `Location`         | `"x,y,0"`                     | Position of the anchor point within the layer's composition |
| `Anchor`           | `TopLeft` … `BottomRight`   | Which point of the box `Location` measures - see Coordinate System. Omitted at the `TopLeft` default |
| `Text`             | string                          | Default/static text content; newlines encoded as `&#xD;&#xA;` (CRLF) in the XML attribute |
| `FontFamily`       | string                          | Font name                                                  |
| `FontSize`         | float                           | Font size in points                                        |
| `FontWeight`       | `Bold`, `Regular`, ...       | Font weight (vMix uses `Regular`, not `Normal`, for 400)   |
| `TextAlign`        | `Left`, `Center`, `Right` | Horizontal alignment                                       |
| `VerticalAlign`    | `Top`, `Center`, `Bottom` | Vertical alignment                                         |
| `TextEffect`       | `Uppercase`, ...              | Text transform effects                                     |
| `LineSpacing`      | float                           | Line spacing, three meanings by magnitude - see below      |
| `Visible`          | bool                            | Initial visibility                                         |
| `Opacity`          | `0`–`1`                    | Transparency                                               |
| `DataFlags`        | see below                       | Controls vMix data binding behavior                        |
| `IgnoreOverhang`   | bool                            | Whether to ignore font overhang metrics                    |
| `TextWordWrapping` | `NoWrap`, ...                 | Word wrap mode                                             |
| `AutoSize`         | `Fixed`, `Width`, `Height`, `Shrink`, `WidthAndHeight` | Automatic box sizing - see below. Omitted at the `Fixed` default |

#### LineSpacing

One float, three interpretations (DWrite `SetLineSpacing` is called with the value as *both* the
line height and the baseline offset):

| Stored value | Method | Meaning |
| ------------ | ------ | ------- |
| `0` (default for a new object) or negative | `SetLineSpacing` not called - DWrite `Default` | line height and baseline straight from the font metrics (ascent + descent + lineGap) |
| `0 < v <= 2` | `Proportional` | multiplier of the font-computed line height **and** baseline |
| `v > 2` | `Uniform` | absolute line height in DIPs, baseline at the **bottom** of the line box |

Consequences to keep in mind when writing files:

- Because the baseline is scaled too, the extra leading lands above **every** line including the
  first, so raising the value also pushes the whole block down inside its box.
- `2.0` is 200% spacing, `2.001` is a 2 DIP uniform line height - every line collapses onto the
  previous one. There is no clamp or ramp; the cliff is in the original engine.
- `0` and `1.0` render identically but are distinct persisted values. The GT font dialog
  normalises `1.0` back to `0`; the GT toolbar slider writes `1.0`. Keep the raw float to
  round-trip a file unchanged.

#### AutoSize

A `<TextBlock>`-only property (a `<Ticker>` never carries one - it forces its own mode on the
clones it scrolls). Stored as the enum name, and read as the ordinal by the engine, so the values
are fixed:

| Value | Ordinal | Layout box | Effect |
| ----- | ------- | ---------- | ------ |
| `Fixed`          | 0 | `Width` x `Height` | nothing - text overflows and is clipped by the object surface |
| `Width`          | 1 | infinite x `Height` | `Dimensions` width is rewritten to the measured text width |
| `Height`         | 2 | `Width` x infinite | `Dimensions` height is rewritten to the measured text height |
| `Shrink`         | 3 | `Width` x `Height` | font size steps down 1pt at a time (floor 2pt) until the text fits; `Dimensions` is untouched and the reduced size is never persisted |
| `WidthAndHeight` | 4 | infinite x infinite | both `Dimensions` axes are rewritten |

Consequences to keep in mind:

- The modes that grow the box coerce alignment on the render snapshot only - `Height` and
  `WidthAndHeight` force `VerticalAlign="Top"`, `Width` and `WidthAndHeight` force
  `TextAlign="Left"`, and `WidthAndHeight` forces `NoWrap`. The stored attributes are left
  alone, so switching back to `Fixed` restores the original look.
- The box grows away from the element's `Anchor`, which is the point `Location` measures. On the
  default `TopLeft` anchor that means right and down, so a centred auto-width box keeps its left
  edge rather than its centre; a `MiddleCenter` anchor grows both ways instead.
- Measured sizes are truncated and bumped by one pixel, then clamped to 8192 (the max texture
  size). Trailing spaces count towards the width, so `Text="Name "` gives a wider box than
  `Text="Name"`.
- Because `Dimensions` is itself what the layout is built from, an auto-sized axis snaps straight
  back when it is edited by hand - that snap-back is how the designer stops you resizing it.

### Multiline Text

Newlines in the `Text` attribute are stored as `&#xD;&#xA;` (XML character references for CR+LF):

```xml
<TextBlock Text="Line one&#xD;&#xA;Line two&#xD;&#xA;Line three" ... />
```

The XML parser decodes these automatically - the model stores plain `\r\n` strings. The writer re-encodes them when saving.

---

## `<Image>`

```xml
<Image
  Name="PlayerLeft"
  Dimensions="1055,835,0"
  Location="-298,245,0"
  Locked="True"
  DataFlags="ShowVisible"
>
  <Image.Bitmap>
    <Bitmap Source="9823b0f8-188a-44f3-989a-940c478ca76a\ANOTAND.png" />
  </Image.Bitmap>
</Image>
```

`Source` uses the logical path from `resources.xml` (`{folder-guid}\{filename}`), not the GUID blob name.

### `SizeMode` - bitmap layout inside the box

```xml
<Image Name="Headshot" Dimensions="400,200,0" SizeMode="Stretch">
  <Image.Bitmap>
    <Bitmap Source="9823b0f8-...\ANOTAND.png" />
  </Image.Bitmap>
</Image>
```

Optional attribute on `<Image>`. **Absent means `Centered`**, which is GT's default for a new image -
so an omitted `SizeMode` is *not* `Normal`, despite `Normal` being ordinal 0. Unknown values fall
back to `Stretch`.

| Value      | Ordinal | Layout of the bitmap inside the `Dimensions` box                            |
| ---------- | ------- | --------------------------------------------------------------------------- |
| `Normal`   | 0       | Native pixel size, anchored top-left; overflow clips right/bottom            |
| `Stretch`  | 1       | Scaled to fill the box exactly, aspect ratio ignored                        |
| `Centered` | 2       | Uniform scale-to-fit (up or down), centred - letterbox/pillarbox, never crops |
| `TopRight` | 3       | Native pixel size, anchored top-right; overflow clips left/bottom            |

The source rect is always the whole bitmap - `SizeMode` never crops. The draw is clipped to the
element box, and the box is never resized by a size-mode or source change: GT sizes an image object
to its bitmap only at insert time.

### `Bitmap Position` - image sequence scrub

When the referenced resource is an [image sequence](#a-resource-may-hold-many-sources-image-sequences),
`<Bitmap>` may carry a `Position` attribute:

```xml
<Image Name="Animation" Dimensions="1920,1080,0">
  <Image.Bitmap>
    <Bitmap Source="70667c3e-...\GameOpener00000.png" Position="0.6361106" />
  </Image.Bitmap>
</Image>
```

| Attribute  | Type          | Description                                                        |
| ---------- | ------------- | ------------------------------------------------------------------ |
| `Source`   | logical path  | The sequence **anchor** (frame 0), not the frame currently shown    |
| `Position` | `0`–`1`       | Normalised scrub position into the sequence; absent = `0`           |

`Position` is the designer's saved preview position - the frame index is
`round(Position × (frameCount - 1))`. At runtime it is driven by an
[`ImageSequence`](#animation-types) animation instead.

---

## Element Transform (Rotation)

Any element type (`TextBlock`, `Rectangle`, `Ellipse`, `Image`) can carry an optional transform child that applies 3D rotation:

```xml
<Rectangle Name="Rectangle1" Dimensions="226,136,0" Location="274,214,0">
  <Rectangle.Transform>
    <Transform Rotate="1.012291,0,0" />
  </Rectangle.Transform>
  <Rectangle.Fill> ... </Rectangle.Fill>
</Rectangle>
```

The pattern is `<ElementType.Transform>` containing `<Transform Rotate="rx,ry,rz"/>`.

| `Rotate` field | Axis     | Unit    | Description                                      |
| -------------- | -------- | ------- | ------------------------------------------------ |
| `rx`         | X (horiz) | radians | Spin left/right — compresses width (`scaleX = cos(rx)`)  |
| `ry`         | Y (vert)  | radians | Tilt forward/back — compresses height (`scaleY = cos(ry)`) |
| `rz`         | Z (depth) | radians | Standard 2D in-plane rotation (clockwise +)              |

**Notes:**
- All three values are always written when any is non-zero: `Rotate="rx,ry,rz"`
- Zero values may be omitted in vMix files (not all fields always present)
- Angles are in **radians** — convert with `degrees × π/180`
- vMix GT Designer uses WPF 3D `PlaneProjection`; X/Y rotation creates perspective effects
- vMix GT++ editor displays and edits these values in **degrees** and approximates X/Y rotation as axis scaling (`cos(angle)` foreshortening) in the 2D canvas preview

---

## Element Bounding (follow another element's box)

```xml
<Rectangle Name="Box1" Location="0,0,0" Dimensions="10,10,0">
  <Rectangle.Bounding>
    <Bounding Object="Text1" Padding="10,8,10,8" />
  </Rectangle.Bounding>
</Rectangle>
```

The pattern is `<ElementType.Bounding>` containing
`<Bounding Object="Name" Padding="left,top,right,bottom"/>`. Any element type may carry one.

The owner takes the source element's `Location` and `Dimensions` every frame, grown outward by
the padding: the example puts `Box1` 10px left/right and 8px above/below `Text1`, whatever
`Text1` currently measures. The canonical use is a background rectangle behind an
`AutoSize="Width"` text box.

| Attribute | Type   | Default    | Description                                                |
| --------- | ------ | ---------- | ---------------------------------------------------------- |
| `Object`  | string | *(absent)* | `Name` of the source element; absent means the binding is off |
| `Padding` | floats | `0,0,0,0`  | `left,top,right,bottom` in composition pixels, positive = outward |

Rules GT enforces, all reproduced by this editor:

- **One edge of dependency only.** If the source itself carries a non-empty
  `Bounding.Object`, the binding does nothing at all - not even the location copy. This is
  GT's entire cycle guard, so `A -> B -> C` silently drops the `A -> B` link, and an element
  bound to itself is a no-op.
- **1px floor.** Padding more negative than the source's size collapses the owner to 1x1
  rather than inverting it.
- **One-way.** The owner's own `Location` / `Dimensions` are overwritten on every frame, so
  dragging it in the editor moves it only until the next frame.
- **A hidden or fully transparent owner stops tracking**, because GT resolves bounding from
  its compose pass and skips such objects there. It snaps back once visible again.

GT resolves `Object` by name across the whole document but then copies the source's
*layer-local* coordinates raw, so a cross-layer binding lands offset by the layer's own
location. This editor keeps the lookup inside the owner's layer, and its dropdown offers only
that layer's elements.

GT clears a `Mask.Object` naming a deleted element but leaves `Bounding.Object` dangling, which
freezes the owner at its last copied box. This editor clears both.

## Element Crop (crop + feather)

Any element type (`TextBlock`, `Rectangle`, `Ellipse`, `Image`) can carry an optional crop child:

```xml
<Image Name="CountryFlag" Dimensions="203,128,0" Location="425.5,845,0">
  <Image.Crop>
    <Crop Range="0,0,0.36,1" Feather="100,100,100,100" />
  </Image.Crop>
  <Image.Bitmap>
    <Bitmap Source="eef1e7f4-...\ar.png" />
  </Image.Bitmap>
</Image>
```

The pattern is `<ElementType.Crop>` containing `<Crop Range="x0,y0,x1,y1" Feather="l,t,r,b"/>`.

| Attribute | Type                     | Default     | Description                                              |
| --------- | ------------------------ | ----------- | -------------------------------------------------------- |
| `Range`   | 4 × `0`–`1` (normalised) | `0,0,1,1`   | Visible sub-rect as a fraction of the element box         |
| `Feather` | 4 × **pixels**, `0`–`100`| `8,8,8,8`   | Per-edge fade width, in order **left, top, right, bottom** |

The mixed units are deliberate in GT: `Range` is normalised, `Feather` is pixels of the element's
own (unscaled) `Dimensions`. Either attribute may be absent; both then take the defaults above,
so a `<Crop>` with no `Feather` still fades 8px.

**Notes:**
- The crop is purely visual - `Location`/`Dimensions` are unchanged, so animations, masks and
  the designer's selection box still use the full element box
- GT Designer exposes one Feather slider (0-100) and writes it to all four edges, and reads back
  only `x0`, so per-edge values are invisible in its UI - but honoured by the renderer. vMix GT++
  exposes all four separately
- **Feather is added onto the cropped box, not taken out of it.** Content stays solid up to the
  crop line and the ramp lies *outside* it, in the strip the crop removed:
  - right/bottom: alpha 1 up to `cut`, linear ramp `cut → cut + feather`, 0 beyond
  - left/top: 0 before `cut - feather`, linear ramp up to alpha 1 at `cut`
- **Feather is a no-op on an edge that is not cropped**, and the whole crop block is skipped
  when `Range` is exactly `0,0,1,1` - so feather alone never shows on an uncropped element
- **It is a ramp, not a blur.** One texture sample per pixel, linear alpha falloff; hard-edged
  art gets a linear fade, not a soft one
- **Corners multiply.** The X and Y passes each scale alpha, so a pixel inside both ramps gets
  `vx × vy` - a visibly darker quadratic falloff in the corner than either edge alone
- Two quirks of GT's shader, reproduced by vMix GT++ because they are visible:
  - normalised feather is clamped for **right/bottom only** (`if (x1 - feather < 0) feather = x1`),
    and against the crop coordinate itself rather than the distance remaining
  - on **left/top**, a band that would overrun the far edge (`cut + feather > 1`) is pulled in to
    start at `2 × cut - 1`
- Because feather is normalised against the element's own pixel size, it is constant in source
  pixels: scaling or 3D-rotating an element stretches its feather on screen
- The `Reveal` transition (Center direction) animates `Feather` from `0,0,0,0` up to its authored
  value alongside `Range`; no other animation touches it
- GT writes `<Crop Feather="…"/>` once its crop panel is touched even when nothing is cropped.
  vMix GT++ drops a crop that would change nothing rather than writing the empty element

---

## `<Rectangle>`

```xml
<Rectangle Name="BG" Dimensions="1920,1080,0" Locked="True" DataFlags="None"
           StrokeThickness="2" Style="Square">
  <Rectangle.Fill>
    <Brush Type="Bitmap" Color="#FFFF0000" StartPoint="0.5,1" EndPoint="0.5,0">
      <Brush.Stops>
        <GradientStop Color="#BF000000" />
        <GradientStop Position="1" Color="#54000000" />
      </Brush.Stops>
      <Brush.Bitmap>
        <Bitmap Source="d693d181-b790-4d17-926b-c4b54dd2ac61\EGC HEAD TO HEAD.png" />
      </Brush.Bitmap>
    </Brush>
  </Rectangle.Fill>
  <Rectangle.Stroke>
    <Brush Color="#FFFEECC9" />
  </Rectangle.Stroke>
  <Rectangle.StrokeStyle>
    <StrokeStyle DashStyle="Dash" />
  </Rectangle.StrokeStyle>
  <Rectangle.Mask>
    <Mask Object="OtherElementName" />
  </Rectangle.Mask>
</Rectangle>
```

`Rectangle.Mask` clips the rectangle using another element's bounds - referenced by `Name`.

Masks are not rectangle-only: any element type may carry a `<Type>.Mask` child using the same
`<Mask Object="Name" />` payload. The mask element must live in the same layer.

```xml
<TextBlock Name="LeftPlayer" Text="SCARLET DEVIL" ...>
  <TextBlock.Mask>
    <Mask Object="LeftMask1" />
  </TextBlock.Mask>
</TextBlock>
```

When the mask target is itself a `TextBlock`, the clip is the glyph outline, not the box.

### Rectangle stroke attributes

| Attribute         | Type   | Default   | Description                      |
| ----------------- | ------ | --------- | -------------------------------- |
| `StrokeThickness` | float  | `0`       | Stroke width in pixels           |
| `Style`           | string | *(absent)* | `"Square"` for square corners; absent/omitted = rounded |

### `<Rectangle.StrokeStyle>`

Optional child element. Absent when dash style is Solid (default).

```xml
<Rectangle.StrokeStyle>
  <StrokeStyle DashStyle="Dash" />
</Rectangle.StrokeStyle>
```

| `DashStyle` value | Meaning           |
| ----------------- | ----------------- |
| *(absent)*        | Solid (default)   |
| `Dash`            | Dashes            |
| `Dot`             | Dots              |
| `DashDot`         | Dash-dot pattern  |
| `DashDotDot`      | Dash-dot-dot      |

Same `<ElementType.StrokeStyle>` child applies to `<Ellipse>` elements.

---

## `<Ticker>`

A ticker is a text object that is also a container: it never draws its own text. It splits the
template text into chunks, clones each chunk as a text object carrying **the ticker's own** font,
fill and stroke, and scrolls the clones through its bounds one frame at a time. Its box clips the
run.

```xml
<Ticker Name="Ticker1" Dimensions="999,52,0" Location="167,877,0" Speed="5" Direction="Right">
  <Ticker.Fill>
    <Brush Color="#FF000000" />
  </Ticker.Fill>
  <Ticker.Stroke>
    <Brush Color="#FFFFFFFF" />
  </Ticker.Stroke>
  <Ticker.Template>
    <TextBlock Name="TextBlock1" Text="ah yes, a long ticker for lorem ipsum uwu hi vodka stop reading">
      <TextBlock.Fill>
        <Brush Color="#FF000000" />
      </TextBlock.Fill>
      <TextBlock.Stroke>
        <Brush Color="#FFFFFFFF" />
      </TextBlock.Stroke>
    </TextBlock>
  </Ticker.Template>
</Ticker>
```

*(verbatim GT Designer output - see `gtzip-examples/TickerExample.gtzip`)*

| Attribute   | Type   | Default   | Description                                                  |
| ----------- | ------ | --------- | ------------------------------------------------------------ |
| `Speed`     | float  | `1`       | **Pixels per frame** - on-screen speed scales with the composition's frame rate |
| `Direction` | string | `Left`    | `Left`, `Right`, `Top`, `Bottom` - the direction the content travels |
| `Type`      | string | `Replace` | `Replace` loops the content forever; `Add` appends each update and lets it expire |

Every `<TextBlock>` font/fill/stroke attribute applies, with `<Ticker.Fill>` / `<Ticker.Stroke>`
carrying the brushes. `Transform`, `Mask`, `Crop` and `DataFlags` work as on any other element.
GT drops each of `Speed`, `Direction` and `Type` while it still sits on its default.

### The template

`<Ticker.Template>` holds one child object - the prototype that is cloned per chunk. GT's designer
only ever puts a `<TextBlock>` there, and gives it a name and its own `Fill` / `Stroke` brushes -
but every font, fill and stroke property is overwritten from the **ticker** on each clone, so
**only its `Text` matters** to what ends up on screen. A `Layer` template is legal (it arrives from
the vMix runtime, not the designer): its direct children are then bound to
`<TickerName>.<ChildName>.Text` / `.Source` instead. vMix GT++ keeps the template element exactly
as it was read and writes back only the edited text, so the template's name and brushes survive a
save untouched.

The ticker's own `Text` property is never written to the file - GT re-defaults it after every
write, so the persisted text lives in the template.

### Data binding

A text template exposes one field, `<TickerName>.Text`; a layer template exposes one field per
direct child. Feeding a field in `Replace` mode restarts the ticker with the new value; in `Add`
mode it appends behind what is still on screen, dropping the update if active + queued clones have
reached 50.

### How the scroll is laid out

- Each incoming value gets a separator appended before splitting - a space horizontally, a newline
  vertically. That whitespace is the **only** thing separating consecutive items; there is no gap
  property
- Horizontal (`Left`/`Right`): line breaks collapse to spaces, the text is cut into greedy 100
  character chunks at the last space inside each window, and each clone is a single unwrapped line
  keeping the ticker's height
- Vertical (`Top`/`Bottom`): one chunk per line (an empty line becomes a single space), each clone
  wrapping inside the ticker's width and growing its own height
- Clones chain with zero gap along the scroll axis, and their cross-axis coordinate is pinned to 0
  (left edge when scrolling horizontally, top edge when scrolling vertically)
- New clones enter from the far edge whenever the trailing edge of the run has drifted back inside
  the box, so content shorter than the ticker shows a gap once per lap
- `Replace` recycles a finished clone onto the tail of the queue, which is what makes it loop;
  `Add` destroys it, so an unfed ticker empties out

vMix GT++ replays that walk as a function of the frame number (60 fps) rather than running a live
engine: a storyboard preview or a video export drives it from their own time, and otherwise the
play/pause/stop transport in the ticker properties does. At frame 0 the chunks sit flush against
the leading edge, so a stopped ticker can be positioned instead of being empty.

---

## `<Brush>`

Used inside `.Fill` and `.Stroke` child elements.

### Solid color

```xml
<Brush Color="#FFFFFFFF" />
```

### Linear gradient

```xml
<Brush Type="LinearGradient" Color="#FFFFFFFF" StartPoint="0.5000001,0" EndPoint="0.5,1">
  <Brush.Stops>
    <GradientStop Color="#FFFEBD5C" />
    <GradientStop Position="1" Color="#FFEDAD37" />
  </Brush.Stops>
</Brush>
```

`StartPoint`/`EndPoint` are normalized `0`–`1` coordinates. `GradientStop Position` defaults to `0` if omitted.

The gradient angle is implied by the `StartPoint` → `EndPoint` vector, both centred on `0.5,0.5`:
`0°` = left → right, `90°` = top → bottom (clockwise, Y axis down).

### Radial gradient

```xml
<Brush Type="RadialGradient" Color="#FFFFFFFF" StartPoint="0.5,0" EndPoint="0.5,1" WrapX="Clamp" WrapY="Clamp">
  <Brush.Stops>
    <GradientStop Color="#FFFEBD5C" />
    <GradientStop Position="1" Color="#FFEDAD37" />
  </Brush.Stops>
</Brush>
```

Stop `0` sits at the element centre and radiates outward; `StartPoint`/`EndPoint` are unused for rendering.

### Wrap mode (`WrapX` / `WrapY`)

Both attributes are always written with the same value. Applies to gradient brushes.

| Value        | Meaning                                                        |
| ------------ | -------------------------------------------------------------- |
| *(absent)*   | `Mirror` (default)                                             |
| `Mirror`     | Gradient reflects back through the stops outside `0`–`1`        |
| `Clamp`      | Edge stop colours extend outward                                |
| `Wrap`       | Gradient repeats                                                |

### Bitmap (image fill)

```xml
<Brush Type="Bitmap" Color="#FFFF0000" StartPoint="0.5,1" EndPoint="0.5,0">
  <Brush.Stops> ... </Brush.Stops>
  <Brush.Bitmap>
    <Bitmap Source="folder-guid\filename.png" />
  </Brush.Bitmap>
</Brush>
```

### Color format

Colors are **ARGB hex**: `#AARRGGBB`

- `#FFFFFFFF` = fully opaque white
- `#BF000000` = 75% opaque black
- `#54000000` = ~33% opaque black

---

## `DataFlags`

Controls how an element appears in vMix's title data editor. The flags **combine** - the
attribute holds a comma-separated list, in the order below:

| Value           | Behavior                                              |
| --------------- | ----------------------------------------------------- |
| `Hidden`      | Hidden from vMix title editor                         |
| `NoEvents`    | Element raises no click / mouse events in vMix         |
| `ShowVisible` | Exposed in vMix title editor with a visibility toggle |
| *(absent)*    | No flags set - default behavior (exposed as editable field by Name) |

```xml
<TextBlock Name="TextBlock1" DataFlags="Hidden, NoEvents, ShowVisible" ... />
```

When no flag is set the attribute may be left off the element entirely; GT Title itself writes
`DataFlags="None"`, which parses the same way. This editor writes `None` on save.

### Shapes: `None` means hidden

For `Rectangle` and `Ellipse`, "None" is **not** neutral - vMix treats a shape with no flags as
if `Hidden` were set, so it never shows up in the title editor. GT Title's element panel shows
"None" for a freshly drawn shape, which is why a new rectangle is not data-bindable until a flag
is set explicitly (e.g. `ShowVisible`). `TextBlock` and `Image` keep the documented default -
absent flags mean "exposed as an editable field by Name".

This editor does not fold the two spellings together. `DataFlags="None"` (or an absent
attribute) on a `Rectangle` / `Ellipse` reads back with every tick clear, new shapes drawn in
the editor start with no flags, and only a flag the user actually ticks is written - `Hidden`
saves as `DataFlags="Hidden"`. vMix's own "flagless shape behaves as hidden" rule still applies
at playout; tick `ShowVisible` to expose the shape there.

---

## Input Fields

There is **no separate `<Fields>` block**. vMix infers editable fields directly from element `Name` attributes in `document.xml`. Elements with names become data-bindable in vMix's title editor. `DataFlags` controls whether/how they appear.

---

## Storyboards (animations)

Animations live in `<Storyboard>` elements that are **siblings of `<Layer>`, after all layers**,
directly under the root `<Composition>`. Templates with no animation simply have no
`<Storyboard>` element - which is why the earlier example files showed none.

```xml
<Composition Width="1920" Height="1080">
  <Layer Name="Layer1" ...> ... </Layer>

  <Storyboard>
    <Storyboard.Animations>
      <ImageSequence Object="Animation" Duration="12" />
      <Reveal Object="GameID" Delay="0.6" Interpolation="CubicEasingInOut" Direction="Center" CenterAxis="X" />
      <Reveal Object="GameID" Delay="9" Duration="0.4" Reverse="True" Interpolation="CubicEasingInOut" Direction="Center" CenterAxis="X" />
      <Fade Object="Layer1" Delay="11" Reverse="True" Interpolation="CubicEasingInOut" />
      <Fly Object="RightPlayer" Delay="0.5" Interpolation="CubicEasingInOut" />
      <Fly Object="LeftPlayer" Delay="0.5" Interpolation="CubicEasingInOut" Direction="Right" />
    </Storyboard.Animations>
  </Storyboard>
</Composition>
```

### `<Storyboard>`

| Attribute  | Type   | Description                                                                    |
| ---------- | ------ | ------------------------------------------------------------------------------ |
| `Type`     | string | Which vMix event triggers it. **Absent = `TransitionIn`**                       |
| `DataName` | string | `DataChangeIn`/`DataChangeOut` only: the one data field that triggers it. Absent = any field |

`TransitionIn` is written without the attribute; every other storyboard carries it explicitly,
e.g. `<Storyboard Type="TransitionOut">` (see `BD - LowerThird.gtzip`, which has both).

A storyboard is identified by the **pair** `(Type, DataName)`, so a title can hold
`DataChangeIn` and `DataChangeIn` scoped to `Name.Text` at the same time - see below.

Storyboard types (from the GT Designer docs):

| Type                          | Trigger                                                       |
| ----------------------------- | -------------------------------------------------------------- |
| `TransitionIn`                | Template turns on. Used as the stinger's in-half               |
| `TransitionOut`               | Template turns off. Used as the stinger's out-half             |
| `DataChangeIn` / `DataChangeOut` | A bound field's value changes (Advanced edition); optionally scoped to one field by `DataName` |
| `Page1` … `Page10`            | Driven by the previous/next page buttons                        |
| `Continuous`                  | Runs forever while the template is live                         |

While a storyboard plays it **overrides** the resting `Location`/`Opacity`/`Visible` of the
objects it targets; the values in `document.xml` are the at-rest state.

#### `DataChangeIn` / `DataChangeOut` fire on a data change

The DataChange pair is the only event a title raises **itself**: vMix runs it whenever a bound
field's value changes, rather than because the host asked for a transition. A `DataName`
narrows a storyboard to a single field; without one it runs on any field change. Both the
unscoped storyboard and the changed field's own storyboard build into the **same** timeline and
play together, and two fields changing in one batch run both their storyboards.

The two halves run back-to-back with no gap, and the new values are committed **between** them:

1. `DataChangeIn` animates over the **old** values.
2. The new values are applied.
3. `DataChangeOut` animates over the **new** values.

Read the pair as "out phase, then in phase" despite the names - `DataChangeIn` plays rewound
(like `TransitionOut`) and `DataChangeOut` plays forwards (like `TransitionIn`). A `Fade` on
`DataChangeIn` therefore fades the object *down* over the outgoing value, and the same `Fade` on
`DataChangeOut` brings it *up* over the incoming one. When neither half has animations, or the
clock is not running, the values are applied immediately with no animation at all.

##### Field names

`DataName` is `<ObjectName>.<Field>`, matched **case-sensitively**:

| Element            | Field(s)                                       |
| ------------------ | ---------------------------------------------- |
| `TextBlock`        | `Name.Text`                                    |
| `Ticker`           | `Name.Text`, or one field per template child (`Ticker1.Caption.Text`, `Ticker1.Logo.Source`) |
| `Image`            | `Name.Source`                                  |
| `Rectangle`, `Ellipse` | `Name.Fill.Color`, or `Name.Fill.Bitmap` for a bitmap fill |

Only the **direct children of a top-level `<Layer>`** register fields - objects at the
composition root and objects inside a nested layer register none, though any object at any
depth can still be *animated by* a DataChange storyboard. Fields whose element carries
`DataFlags="Hidden"` or `DataFlags="NoEvents"` are not offered as event sources, which is why
shapes (hidden by default) only appear once the flag is cleared. A storyboard whose `DataName`
no longer names a field of the composition is dropped on save.

#### `TransitionOut` plays rewound

A `TransitionOut` storyboard describes how the settled picture comes apart, so each of its
animations runs **from its finished state back towards its start**: a `Reveal` un-wipes, a
`Fly` retraces its entrance, a `Fade` fades down. `Reverse="True"` flips that again, per
animation. This is why the out half always continues from the final frame of `TransitionIn`
rather than from the animation's own start state.

`BD - LowerThird.gtzip` shows it: its `TransitionOut` holds
`<Reveal Object="Mask" Direction="Right" />` with no `Reverse`, and `Mask` is fully visible
for the whole of `TransitionIn` - so that `Reveal` can only be un-revealing it.

### Animation elements

Each child of `<Storyboard.Animations>` is one animation. **The element name is the animation
type**; there is no `Type` attribute.

| Attribute       | Type      | Default when absent | Description                                                     |
| --------------- | --------- | ------------------- | --------------------------------------------------------------- |
| `Object`        | string    | *(required)*        | `Name` of the target **layer or element**                        |
| `Delay`         | float (s) | `0`                 | Seconds before the animation starts, relative to storyboard start |
| `Duration`      | float (s) | `1`                 | Seconds the animation runs, after the delay                      |
| `Reverse`       | bool      | `False`             | Inverts the direction of travel (in → out)                       |
| `Interpolation` | enum      | `Linear`            | Easing curve; see below                                          |
| `Direction`     | enum      | **per type**        | 9-way direction pad, for direction-aware types; see below        |
| `CenterAxis`    | `Both`/`X`/`Y` | `Both`         | Axis a `Reveal` with `Direction="Center"` opens along            |
| `Speed`         | float     | `1`                 | Cycles per second; `Continuous` storyboards only                 |

Attributes at their default are **omitted entirely** rather than written out. Up to **3
animations per object per storyboard** are supported by the designer, and stacking them is
normal: an in/out pair is two entries on the same `Object` with the second `Reverse="True"`.

Storyboard length is implied - it is the largest `Delay + Duration` across its animations.

### Animation types

Verified in `EMS GameOpener.gtzip`:

| Element           | Effect                                                                       |
| ----------------- | ---------------------------------------------------------------------------- |
| `Fade`            | Animates opacity 0 → 1 (reversed: 1 → 0)                                      |
| `Fly`             | Slides the object in from off-canvas to its resting `Location`. **Directional** |
| `Reveal`          | Wipes the object's `Crop.Range` open; **directional**, plus `CenterAxis`      |
| `ImageSequence`   | Plays a numbered PNG sequence, fitting the whole sequence into `Duration`     |
| `ZoomFade`        | Shrinks from **2× size down to 1×** while fading in. **Takes no `Direction`** (`BD - LowerThird.gtzip`) |

`None` is a placeholder GT writes for an object with no animation, e.g. `<None Object="Mask" />`.
It animates nothing and has no timing; it is preserved on save but otherwise ignored - no
timeline row, no preview effect, and it does not count against the per-object limit.

Documented by GT Designer but not present in the sample files:

| Element             | Effect                                                                     |
| ------------------- | -------------------------------------------------------------------------- |
| `Bounce`            | A `Fly` plus a vertical overshoot that settles with a forced bounce curve. **Directional** |
| `Expand`            | Grows the box out of the edge or corner named by `Direction`. **Directional** |
| `Rotate`            | Spins one full turn onto the object's authored rotation. **Directional**     |
| `Scroll`            | Edge-to-edge travel across the composition. **Directional, defaults to `Direction="Bottom"`** |
| `Zoom`              | Scales 0 → 1 about the object's centre. **Takes no `Direction`**             |
| `Hidden`            | Keeps the object invisible for the entire storyboard                        |
| `RotateContinuous`  | Spins forever at `Speed` turns per second. **Directional**                  |
| `ImageSequenceLoop` | Loops an image sequence continuously rather than playing it once            |
| `FillOffset`        | Continuously drifts the object's **fill** (gradient/bitmap). **Directional** |
| `StrokeOffset`      | Same, for the object's **stroke**. **Directional**                          |
| `Blink`             | Toggles visibility on and off. **Takes no `Direction`**                     |

`RotateContinuous`, `FillOffset`, `StrokeOffset`, `Blink` and `ImageSequenceLoop` are
`Continuous`-storyboard animations: they have no end, ignore `Duration`, and advance by a
per-frame delta scaled by `Speed` instead of interpolating between two end states.

The gallery is filtered by **event type only, never by object type** - `FillOffset` is offered
for an image object even though an image has no fill, and simply builds to nothing. Reproduce
that as a silent no-op rather than an error.

> The GT Designer docs do not publish a complete type list, so other element names may exist.
> Treat the element name as an open enum: preserve unknown types and their attributes verbatim
> rather than dropping them.

### `Interpolation` (easing)

| Value              | Motion                                              |
| ------------------ | --------------------------------------------------- |
| *(absent)*         | `Linear`                                            |
| `Linear`           | Constant speed from start to finish                 |
| `CubicEasingIn`    | **Decelerates** from start to finish                |
| `CubicEasingOut`   | **Accelerates** from start to finish                |
| `CubicEasingInOut` | Speeds up, then slows down                          |
| `BounceIn`         | Bounces in, each bounce larger and slower           |
| `BounceOut`        | Bounces out, each bounce smaller and faster         |

> **Note the inverted naming.** GT's `CubicEasingIn` decelerates, which is a conventional
> *ease-out* curve (and vice versa). Mapping these onto a standard easing library requires
> swapping in/out — `GtAnimationEvaluator` does this deliberately.

### `Direction`

GT Designer shows a **3×3 direction pad** - nine buttons, one always selected:

```
TopLeft      Top       TopRight
Left        Center     Right
BottomLeft  Bottom     BottomRight
```

The attribute is persisted **by name**, and those nine names plus `None` are the entire
vocabulary. `Up` and `Down` are *not* GT names.

The pad is only enabled for the nine animations built on GT's direction builder:

`Fly`, `Bounce`, `Expand`, `Reveal`, `Rotate`, `Scroll`, `RotateContinuous`, `FillOffset`,
`StrokeOffset`

Every other type - `Fade`, `Zoom`, `ZoomFade`, `Hidden`, `Blink`, `ImageSequence`,
`ImageSequenceLoop` and the `None` placeholder - carries no `Direction` attribute at all, and
writing one would not match GT Designer output.

#### The naming is not one convention

The same button means a different thing per animation, and this is as-shipped behaviour, not
an inconsistency to iron out:

| Animation                     | What the direction names                                  |
| ----------------------------- | --------------------------------------------------------- |
| `Fly`, `Bounce`               | The edge the object comes **from**                        |
| `Scroll`                      | The edge it travels **toward** (the opposite convention)  |
| `Expand`                      | The edge or corner the box grows **out of**               |
| `Reveal`                      | Where the wipe **starts**                                 |
| `Rotate`, `RotateContinuous`  | Which axis and which sign; corners combine two axes       |
| `FillOffset`, `StrokeOffset`  | Which way the brush texture drifts, in +X/+Y screen terms |

So `Fly` with `Direction="Top"` drops in from above, but `Scroll` with `Direction="Top"`
starts below and travels **up** off the top edge.

The anchor does not change between the in and out halves - what changes is which end of the
animation the object sits at. A `Fly` with `Direction="Right"` comes in *from* the right in
`TransitionIn` and goes out *to* the right in `TransitionOut` (the same animation rewound).
`Reverse="True"` swaps the ends within a single storyboard the same way.

#### Worked tables

`Fly` start position, given composition `comp`, object box `db` and resting `loc`:

| Value         | Start X            | Start Y             |
| ------------- | ------------------ | ------------------- |
| `Top`         | `loc.X`            | `-db.Height`        |
| `Bottom`      | `loc.X`            | `comp.Height`       |
| `Left`        | `-db.Width`        | `loc.Y`             |
| `Right`       | `comp.Width`       | `loc.Y`             |
| `TopLeft`     | `-db.Width`        | `-db.Height`        |
| `TopRight`    | `comp.Width`       | `-db.Height`        |
| `BottomLeft`  | `-db.Width`        | `comp.Height`       |
| `BottomRight` | `comp.Width`       | `comp.Height`       |
| `Center`      | `loc.X`            | `loc.Y`             |
| `None`        | `loc.X`            | `loc.Y`             |

`Center` and `None` still drive and hold `Location`; they just never move.

`Reveal` start `Crop.Range` (`x0,y0,x1,y1`, normalised, settling on the object's own range):

| Value            | Start range           |
| ---------------- | --------------------- |
| `Top`            | `0,0,1,0`             |
| `Bottom`         | `0,1,1,1`             |
| `Left`, `None`   | `0,0,0,1`             |
| `Right`          | `1,0,1,1`             |
| `TopLeft`        | `0,0,0,0`             |
| `TopRight`       | `1,0,1,0`             |
| `BottomLeft`     | `0,1,0,1`             |
| `BottomRight`    | `1,1,1,1`             |
| `Center` + `Both`| `0.5,0.5,0.5,0.5`     |
| `Center` + `X`   | `0.5,0,0.5,1`         |
| `Center` + `Y`   | `0,0.5,1,0.5`         |

`Rotate` (one full turn settling onto the authored angle; `Rotate.X` = yaw, `Rotate.Y` = pitch,
`Rotate.Z` = in-plane roll):

| Value         | Axes spun                      |
| ------------- | ------------------------------ |
| `Top`         | `Rotate.Y` +360                |
| `Bottom`      | `Rotate.Y` −360                |
| `Left`        | `Rotate.X` +360                |
| `Right`       | `Rotate.X` −360                |
| `TopLeft`     | `Rotate.Y` +360, `Rotate.X` +360 |
| `TopRight`    | `Rotate.Y` +360, `Rotate.X` −360 |
| `BottomLeft`  | `Rotate.Y` −360, `Rotate.X` +360 |
| `BottomRight` | `Rotate.Y` −360, `Rotate.X` −360 |
| `Center`      | `Rotate.Z` +360                |
| `None`        | **nothing at all**             |

#### `None`

`None` is in the enum but has **no button** on the pad - it is only reachable from hand-edited
XML or an uninitialised value. It is *not* a synonym for `Left`: each animation handles it
differently (`Fly` holds still, `Rotate` emits nothing, `Reveal` and the offset pair fall in
with `Left`, `Scroll` falls in with `Bottom`). It is preserved rather than normalised away.

Not to be confused with the `None` **animation** (`<None Object="X" />`), the do-nothing
placeholder described above; the two are unrelated.

**Default when the attribute is absent:**

| Animation type  | Implicit `Direction` |
| --------------- | -------------------- |
| `Scroll`        | `Bottom`             |
| everything else | `Left`               |

A missing `Direction` therefore means the **type's own default**, never `None` and never `0`.

`CenterAxis` is GT's only direction-conditional extra option: it appears solely on a `Reveal`
whose `Direction` is `Center`, and its default is `Both`. A `Center` reveal is also the only
one that animates `Crop.Feather` (from `0,0,0,0` up to the authored feather) alongside the
range.

---

## Pages / States

Multi-page behaviour is expressed through storyboards named `Page1`–`Page10` rather than a
separate page structure in the document tree: all pages share one root `<Composition>` and
`<Layer>` set, and the page storyboards reposition the objects. None of the example files use
them, so this is documented from the GT Designer docs rather than verified.

---

## Verification Method

```bash
# Extract and inspect
cp template.gtzip template.zip
unzip template.zip -d template_extracted/

# Main template (UTF-16 encoded)
cat template_extracted/document.xml

# Asset manifest
cat template_extracted/resources.xml
```
