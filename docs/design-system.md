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
focused, selected, and disabled treatments as relevant. Focus uses a visible violet
outline. State is always reinforced by text, shape, or position rather than colour
alone.

Animations are limited to short transitions. The duration token is isolated so a
future reduced-motion preference can set it to zero.

## SVG icons

`SvgIcon` loads selected, linked Font Awesome SVG resources and parses their vector
path data into Avalonia geometry at runtime. It never rasterizes the source. Icon
URIs are centralized in `IconCatalog`; missing or invalid assets render a neutral
fallback and emit a diagnostic trace.
