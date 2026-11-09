namespace UITheme {
    internal static class ThemeDocs {
        public const string Readme = @"# KoboldKare UI themes

Each theme is a folder here containing a `theme.json` (plus any images it uses):

    BepInEx/themes/MyTheme/theme.json

Themes can also ship inside a plugin: `BepInEx/plugins/<Plugin>/themes/<Theme>/theme.json`.

Pick a theme in **Options > Graphics > UI Theme**, in the **Themes** tab of the mod panel (F8),
or with `/theme <name>` in chat. Edit a theme's files, then press *Reload themes* to see changes live.

## theme.json

```json
{
  ""name"": ""My Theme"",
  ""author"": ""You"",
  ""version"": ""1.0"",
  ""description"": ""One line shown in the theme browser."",

  ""colors"": {
    ""surface"": ""#384451"",
    ""text"": ""#EDEBFF""
  },

  ""fonts"": {
    ""body"": [""Segoe UI""],
    ""headings"": [""Segoe UI Black""],
    ""headingMinSize"": 34
  },

  ""sprites"": {
    ""UISprite"": { ""file"": ""panel.png"", ""border"": [8, 8, 8, 8] }
  }
}
```

Everything except `name` is optional; anything you leave out keeps the game's look.

### Colour roles

Colours are `#RRGGBB` or `#RRGGBBAA` (alpha last), or `[r, g, b, a]`. Each role is how the game's
own UI uses colour, so one theme restyles every menu, including ones added by updates and other mods.

| Role | What it is in the game |
|---|---|
| `overlay` | full-screen dimmer behind popups |
| `window` | popup / window bodies |
| `panel` | translucent rounded panels, lists, scroll views |
| `panelLight` | faint light panels (stat blocks) |
| `surface` | the main slate-blue menu backgrounds |
| `titleBar` | blue headers and top panels |
| `button` | main buttons |
| `buttonOk` / `buttonAlt` / `buttonCancel` | confirm / secondary / cancel buttons |
| `control` | dropdowns, input fields, toggles, slider tracks |
| `scrollbar` / `scrollbarTrack` | scrollbar handle / track |
| `accent` | slider fills, highlighted boxes, value text in mod panels |
| `text` | text on themed backgrounds |
| `titleText` / `buttonText` / `fieldText` | text on title bars / buttons / inside controls |
| `highlightText` | the yellow values and amounts |
| `icon` | tint for the game's icons (they're never replaced unless you swap their sprite) |
| `hover` / `pressed` / `selected` | button tints when hovered / pressed / controller-selected |
| `danger` | error text |

If you set a role but not its more specific one, the specific one follows it: `titleText`,
`buttonText` and `fieldText` follow `text`; `icon` follows `text`; `selected` follows `hover`;
`window` and `panelLight` follow `panel`; `buttonAlt` follows `buttonOk`; `scrollbar` follows `titleBar`.

Alpha is relative: the game uses the same role at different strengths (a 39% and an 80% panel), and
your alpha scales both, keeping the difference.

Text and icons only follow the theme when they sit on a themed background. HUD text over the game
world keeps its colour, so light themes stay readable.

### Fonts

`body` and `headings` are lists; the first entry that's available is used. An entry is either a
**font file bundled in the theme folder** or the name of an **installed Windows font**:

```json
""fonts"": { ""body"": [""fonts/VT323-Regular.ttf"", ""Courier New""], ""headings"": [""fonts/CarterOne-Regular.ttf"", ""Arial Black""] }
```

Bundling the .ttf/.otf means players don't need to install anything (check the font's licence
allows redistribution; Google Fonts are OFL and do). Text at or above `headingMinSize`, or in objects
named *Title*/*Header*, uses the heading font. Characters a font lacks fall back to the game's font.

### Sprites

Replace any game sprite by its name with a PNG from the theme folder. Useful names:

- `UISprite`: the rounded rectangle behind most panels, popups and dialog buttons
- `Substance_graph_CustomBackground`: the game's main button / menu background
- `Background`, `InputFieldBackground`: controls (dropdowns, fields, toggles)
- `Knob`: slider handles

Images are tinted by the role colour, so draw them in white and greys (shading becomes darker shades
of the role colour). `border` is the 9-slice inset in pixels as `[left, top, right, bottom]`, so
corners stay crisp while the middle stretches. Add `""pixelated"": true` for crisp pixel art.

The game's icons (`ico_*`) are kept as they are and only tinted with `icon`, unless you list an
icon here. A full set of the game's sprites, with names, is in `ModSource/GameIcons` if you have it.

### Role sprites

`sprites` swaps by the game's sprite name, but the game reuses one sprite (`UISprite`) for panels,
title bars and dialog buttons. `roleSprites` gives each *role* its own image instead, so title bars
can have a pattern, windows a frame and cancel buttons a different bevel:

```json
""roleSprites"": {
  ""titleBar"":     { ""file"": ""clouds.png"", ""border"": [2, 2, 2, 2], ""mode"": ""tiled"", ""pixelsPerUnit"": 50, ""pixelated"": true },
  ""window"":       { ""file"": ""frame.png"",  ""border"": [3, 3, 3, 3], ""pixelsPerUnit"": 50, ""pixelated"": true },
  ""buttonCancel"": { ""file"": ""bevel_pink.png"", ""border"": [4, 4, 4, 4], ""pixelsPerUnit"": 50, ""pixelated"": true }
}
```

Allowed roles: `window`, `panel`, `panelLight`, `surface`, `titleBar`, `button`, `buttonOk`, `buttonAlt`,
`buttonCancel`, `control`, `scrollbar`, `scrollbarTrack`, `accent`, `overlay`. Role sprites win over
name swaps. They also style the mod panel (F8).

- `mode`: `sliced` (default with a border: corners fixed, middle stretched), `tiled` (edges and middle
  repeat, good for patterns), or `simple` (stretch the whole image).
- `pixelsPerUnit`: 100 = one image pixel per UI pixel; 50 doubles every pixel (chunky retro art).

Role sprites can be drawn **in colour**: set that role's colour to white (`#FFFFFF`) and the image
shows as drawn; any other colour multiplies it.
";
    }
}
