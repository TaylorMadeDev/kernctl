# Design system

kernctl uses a quiet, technical dark interface derived from the supplied mockup
without depending on Avalonia's default Fluent appearance.

## Foundations

- Background: `#090B0E`
- Sidebar: `#0C0F13`
- Primary surface: `#11151A`
- Secondary surface: `#151A20`
- Border: `#202630`
- Primary text: `#F3F4F6`
- Secondary text: `#A0A7B2`
- Muted text: `#727B88`
- Accent: `#8B7CFF`
- Accent hover: `#9B8FFF`
- Danger: `#F06464`
- Success: `#68C78A`
- Warning: `#E5B567`

All reusable colour, spacing, radius, size, and duration values live in
`Styles/DesignTokens.axaml`. Views consume dynamic resources rather than repeating
constants.

At runtime, `AvaloniaThemeResourceSink` replaces these resources from a validated
`ThemeDefinition`. New controls must use dynamic resources; a static brush or
reusable dimension in a control prevents live theming and is not permitted.

## Built-in themes

- **kernctl Dark** — the default violet-accented technical palette.
- **OLED** — pure-black foundation with quiet raised surfaces and visible borders.
- **Graphite** — neutral charcoal surfaces with a restrained steel-blue accent.
- **Ember** — warm near-black surfaces with a controlled amber/orange accent.

Built-in definitions are immutable. Editing one creates a custom copy whose
`BaseThemeId` points to the built-in source.

## Theme tokens

Every theme supplies the following colour concepts:

```text
WindowBackground       SidebarBackground
SurfacePrimary         SurfaceSecondary         SurfaceElevated
BorderSubtle           BorderStrong
TextPrimary            TextSecondary            TextMuted
AccentPrimary          AccentHover               AccentPressed
Success                Warning                   Danger
FocusRing              SelectionBackground
```

The runtime mapper derives matching brushes, translucent accent/status backgrounds,
SimpleTheme accent primitives, and overlay colours. It also updates:

```text
Font.Family            Font.Size.*               Line.Height.*
Radius.Small           Radius.Medium             Radius.Large
Control.Height         Card.Padding              Dialog.Padding
NavigationItem.Height
Page.Spacing           Grid.Gap                  Animation.Duration
```

## Typography

The promised font was not present in the initial repository. The typography token
currently uses `Segoe UI Variable, Segoe UI` as a clearly documented fallback.
When the font is supplied, inspect its internal family name, add the files under
`Assets/Fonts`, and change the single `Font.Family` token.

Display, heading, body, label, and caption classes define size, weight, and line
height. Text containers avoid fixed heights to preserve layout at higher scaling.

## Components and interaction

Buttons, navigation items, cards, toggles, search, metric displays, dialogs, toast
foundations, loading states, and empty states share normal, pointer-over, pressed,
focused, selected, and disabled treatments as relevant. The kernctl toggle uses its
own track and thumb template instead of inheriting the operating-system accent.
Focus uses a visible violet outline. State is always reinforced by text, shape, or
position rather than colour alone.

The desktop window uses native operating-system decorations. The content shell starts
at a compact 1220×760 and supports a 900×600 minimum. Tool cards use an adaptive panel:
three columns when space permits, two at medium widths, and one on narrow layouts.

Animations are limited to short transitions. The duration token is isolated so a
reduced-motion theme can set it to zero.

## Theme JSON schema

Theme documents use schema version `1`. Unknown JSON properties are ignored for
forward compatibility; missing, malformed, out-of-range, or unsupported values are
rejected.

```json
{
  "schemaVersion": 1,
  "id": "custom-<local-id>",
  "name": "My Theme",
  "isBuiltIn": false,
  "baseThemeId": "kernctl-dark",
  "colors": {
    "windowBackground": "#090B0E",
    "sidebarBackground": "#0C0F13",
    "surfacePrimary": "#11151A",
    "surfaceSecondary": "#151A20",
    "surfaceElevated": "#181E25",
    "borderSubtle": "#202630",
    "borderStrong": "#343C48",
    "textPrimary": "#F3F4F6",
    "textSecondary": "#A0A7B2",
    "textMuted": "#727B88",
    "accentPrimary": "#8B7CFF",
    "accentHover": "#9B8FFF",
    "accentPressed": "#6F60E8",
    "success": "#68C78A",
    "warning": "#E5B567",
    "danger": "#F06464",
    "focusRing": "#B8AFFF",
    "selectionBackground": "#2D294F"
  },
  "typography": {
    "fontFamily": "Segoe UI Variable, Segoe UI",
    "scale": 1.0
  },
  "spacing": {
    "controlHeight": 38,
    "cardPadding": 17,
    "navigationItemHeight": 52,
    "pageSpacing": 22,
    "gridGap": 14
  },
  "cornerStyle": "subtle",
  "density": "comfortable",
  "motion": {
    "enableAnimations": true,
    "intensity": "standard",
    "followSystemPreference": false
  }
}
```

Colours accept six-digit RGB (`#RRGGBB`) and eight-digit ARGB (`#AARRGGBB`).
Font scale is restricted to 0.90–1.20. Enum values use camel-case strings.

Imports use the native file picker, accept at most 256 KB, validate every property,
assign a collision-safe local ID/name, and never execute content. Exports are
human-readable JSON and contain no application settings or machine-specific paths.

## Appearance editor behaviour

Opening Appearance starts an explicit preview session. Preset, colour, density,
corner, typography, and motion changes update the entire shell immediately.
`Save changes` validates and commits; `Cancel` restores the prior committed theme.
Navigation and Escape show an unsaved-changes confirmation. Ctrl+S saves.

Contrast checks cover primary text on the window and primary surface, secondary text
on the primary surface, accent content on a surface, and primary button text on the
accent. Warnings include explanatory text and require acknowledgement before save;
the selected colours are never silently modified.

## SVG icons

`SvgIcon` loads selected, linked Font Awesome SVG resources and parses their vector
path data into Avalonia geometry at runtime. It never rasterizes the source. Icon
URIs are centralized in `IconCatalog`; missing or invalid assets render a neutral
fallback and emit a diagnostic trace.
