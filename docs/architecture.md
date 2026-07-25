# Architecture

## Projects

`Kernctl.App` owns presentation and composes dependencies. `Kernctl.Core` contains
portable state and contracts. `Kernctl.Platform.Windows` implements only harmless,
read-only Windows services during this milestone. Tests target platform-independent
behaviour.

Dependencies point inward:

```text
Kernctl.App ──────────────► Kernctl.Core
      │
      └──► Kernctl.Platform.Windows ───► Kernctl.Core

Kernctl.Core.Tests ───────► Kernctl.Core + Kernctl.App
```

There are no circular references. Views bind to view models, and view models depend
on contracts rather than Avalonia controls. The shell caches page view models so
navigation does not discard state.

## Runtime theme architecture

Theme responsibilities are deliberately split across the project boundary:

```text
Kernctl.Core.Themes
    ThemeDefinition + nested value objects
    built-in palettes
    validation, colour parsing, contrast calculations
    JSON schema and atomic ThemeStore
                 ↓ validated definitions only
Kernctl.App.Services.ThemeService
    preview session and committed state
    custom-theme lifecycle
    import/export orchestration
                 ↓
IThemeResourceSink
    AvaloniaThemeResourceSink maps values to dynamic resources
                 ↓
existing views and reusable controls
```

Views never bind to JSON documents. `ThemeStore` accepts its root directory in the
constructor, so tests use isolated temporary directories and never touch the real
application-data directory. The production composition root uses
`%LocalAppData%/kernctl`.

`ThemeService` is registered through dependency injection. It loads custom themes
and the active selection before constructing the main window. A preview is applied
to the same resource keys as a committed theme, but it is not written to disk.
Cancel, unsaved-navigation discard, or application shutdown reapplies the committed
definition.

Persistence writes UTF-8 JSON to a uniquely named temporary file in the destination
directory and atomically replaces the target. Invalid files are skipped with a useful
log warning; unsupported or missing active themes fall back to kernctl Dark.

The Avalonia storage provider supplies native open/save pickers. Imports are limited
to 256 KB, parsed only as JSON data, fully validated, and assigned a new local ID.
Exports contain only the selected `ThemeDefinition`.

## Future safety boundary

```text
Avalonia UI (unelevated)
    ↓
Core profile/action engine
    ↓
Windows platform services
    ↓
Restricted elevated broker for approved privileged operations
```

The UI must not permanently run as administrator. A future broker will expose a
small, versioned allow-list of operations, validate every input, authenticate the
calling process, and return structured results. It will not accept arbitrary
commands, registry paths, service names, or file paths.

## Safe action lifecycle

Every future system action implements a lifecycle equivalent to:

1. Detect current state.
2. Explain the proposed change and evidence.
3. Declare privilege and restart requirements.
4. Capture rollback state.
5. Apply the narrow change.
6. Verify the observed result.
7. Undo from captured state.
8. Report a structured outcome without sensitive data.

Profiles are transactional. Actions apply in order; if one fails, already-applied
actions undo in reverse order. Rollback failure must be surfaced prominently and
must never be swallowed.

## Current milestone

Profile selection, toggles, search, navigation, modal state, and tool interactions
remain in-memory only. Theme preferences are the sole persisted application state and
stay inside the current user's local application-data directory. Metrics are deliberately
labelled sample values. No privileged, registry, service, power-plan, or unrelated
user-file code exists.
