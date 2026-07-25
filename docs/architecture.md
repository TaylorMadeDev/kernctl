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
are in-memory only. Metrics are deliberately labelled sample values. No privileged,
destructive, registry, service, file-deletion, or power-plan code exists.
