# Architecture

## Projects

`Kernctl.App` owns presentation and composes dependencies. `Kernctl.Core` contains
portable state and contracts. `Kernctl.Broker.Protocol` contains strict DTOs and
framing rules, `Kernctl.Broker.Client` owns UAC launch and the unelevated pipe client,
and the separate `Kernctl.Broker` executable owns verification and dispatch.
`Kernctl.Platform.Windows` implements read-only Windows metrics and process
inspection plus the narrow reversible current-user power-scheme and configured-game
priority adapters.

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

## Transactional action architecture

The original proof-of-concept action abstraction has been replaced by one safety
pipeline in `Kernctl.Core.Actions`:

```text
Avalonia review/progress/recovery view models
                    ↓ contracts only
IActionTransactionEngine
    plan + dry run + execute + cancel + rollback + recover
                    ↓
ISystemAction registry                  IActionJournalStore
    stable metadata                       active journals
    detect / plan / validate              atomic snapshots
    capture / apply / verify               archived history
    rollback
                    ↓ administrator contract
Kernctl.Broker.Client → UAC → restricted diagnostic broker
```

Plans use immutable collections and are checked against the registered action ID and
schema version before execution. Detection, planning, and validation run for the full
ordered group before mutation. The process-wide engine lock permits only one mutating
transaction, while dry runs never enter capture or apply.

Every action captures an explicit JSON payload. The engine rejects secret-like fields
and unsafe polymorphic metadata, enforces a 256 KB payload limit, adds transaction and
action ownership, computes SHA-256 integrity metadata, writes the journal atomically,
and only then calls `ApplyAsync`.

Action and transaction states are persisted as typed enums after every transition.
Verification failure, apply failure that may have partially changed state, or
cancellation after mutation triggers reverse-order rollback with an independent
recovery cancellation path. One rollback failure does not stop attempts for earlier
actions; the result becomes `PartiallyRolledBack`.

Production journals use
`%LocalAppData%/kernctl/transactions/{active,archive}`. Startup recovery scans active
journals and exposes a decision through `ActionRecoveryViewModel`; it never silently
resumes apply. Completed journals are archived and reduced to sanitized read-only
history entries. See [action-engine.md](action-engine.md) for the complete contract.

Administrator-required execution now uses `IActionPrivilegeBroker` after confirmation
and before snapshot/apply. Standard-user actions and dry runs bypass it. The client
launches a separate broker with UAC; the broker accepts one verified local pipe client
and only four non-mutating diagnostics. See
[elevated-broker.md](elevated-broker.md).

## Elevation boundary

```text
Avalonia UI (unelevated)
    ↓
Core profile/action engine
    ↓
Windows platform services
    ↓
Restricted elevated broker for approved privileged operations
```

The UI never relaunches as administrator. The broker uses a versioned, length-framed
JSON protocol, a secured local-only named pipe, OS-backed client process verification,
bounded lifetimes and request counts, and exact operation registration. It does not
accept arbitrary commands, scripts, registry paths, service names, or file paths.

## System profile architecture

```text
Profile workspace view models
          ↓
IProfileCatalogService ── ProfileStore (versioned atomic JSON)
          ↓
IProfileEngine ───────── ProfileHistoryStore (sanitized outcomes)
          ↓
IActionTransactionEngine
          ↓
fixed typed actions
    ├── kernctl feature state
    └── IPowerSchemeService → PowrProf.dll
```

Profiles contain ordered typed definitions and fixed target keys. Validation rejects
duplicate targets before planning. `IProfileEngine` resolves definitions only to
pre-registered action IDs and delegates all snapshot, mutation, verification,
cancellation, and rollback work to the existing action engine. See
[system-profiles.md](system-profiles.md).

## Current milestone

Profiles, profile history, active selection, and theme preferences are persisted in
the current user's local application-data directory. The Gaming library adds
versioned local metadata, read-only Steam/Epic providers, direct validated executable
launch, process-tree monitoring, redacted session history, and fixed overlay
inspection. Windows CPU and memory metrics are read through documented APIs.
Production actions are limited to existing known Windows power-scheme selection,
two kernctl-local Boolean settings, and the exact configured game process selected
by an active session. The elevated broker still contains only non-mutating
information, capability, ping, and shutdown diagnostics. No registry, service,
power-scheme editing, arbitrary process target, network, or unrelated user-file
mutation code exists. See [gaming.md](gaming.md).
