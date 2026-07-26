# System profiles

## Safety boundary

kernctl profiles are ordered data, not scripts. A profile may select only actions
registered in the fixed `ISystemAction` registry. The builder never accepts commands,
registry paths, service names, power-setting indexes, process arguments, or arbitrary
operation identifiers.

Applying a profile always reuses `IActionTransactionEngine`:

```text
SystemProfile
  → schema and conflict validation
  → typed action-ID resolution
  → detect and plan every action
  → explicit review and confirmation
  → durable per-action snapshot
  → deterministic apply and verification
  → commit, or reverse-order rollback
  → sanitized profile activation history
```

`IProfileEngine` adds profile validation, active-state semantics, and profile history
around the existing action engine. It does not provide an alternate mutation path.
The profile is persisted as active only after the underlying transaction commits.
Unsupported required actions make the plan unavailable. Two profile applications
cannot run at the same time, and the action engine retains its own process-wide
mutation lock as a second guard.

## Built-in profiles

Battery Saver, Balanced, Gaming, and Competitive are immutable. They can be duplicated
into a custom profile but cannot be overwritten or deleted. Balanced is the default.
Descriptions state intent only and never promise FPS, latency, battery-life, or other
performance gains.

Built-ins use only:

- selection of an existing Windows power scheme;
- kernctl's FPS-monitoring state;
- kernctl's performance-mode preference.

Battery Saver requests the existing Power saver scheme. Balanced and Gaming request
the existing Balanced scheme. Competitive requests the existing High performance
scheme and is shown as unsupported when that scheme is absent. kernctl never creates,
imports, or edits a Windows power scheme.

## Supported action contracts

Power-scheme selection uses `PowerGetActiveScheme`, `PowerReadFriendlyName`, and
`PowerSetActiveScheme` from `PowrProf.dll`; it never shells out to `powercfg`. Detection
confirms that the known scheme exists. The exact prior scheme GUID is captured and
persisted before apply, verification reads the active scheme independently, and undo
restores and verifies the captured GUID. The operation targets the current user's
active scheme and does not request elevation.

The two kernctl-only action families use typed enums and Boolean values. They capture
the previous Boolean, apply one bounded preference change, re-read it, and restore it
on rollback.

Intentionally unsupported operations are listed under [Limitations](#limitations).

## Profile schema and persistence

Custom profiles use schema version `1` and are stored under:

```text
%LocalAppData%/kernctl/
├── profile-settings.json
├── profile-history.json
└── profiles/
    └── <sanitized-profile-id>.json
```

Writes use a uniquely named sibling temporary file followed by an atomic replace.
Profile IDs become filenames only after strict sanitization. Files are limited to
256 KB, validated on read, and malformed documents are skipped with a safe fallback
to Balanced. Imports receive a new local ID and all trigger assignments and prior
automatic approval are removed, so an imported absolute path is never trusted as a
user selection. Exports are data-only JSON.

Executable paths appear only when a user selects a game through the native file
picker. History stores a safe trigger label rather than that path and never records
usernames, process arguments, credentials, cookies, tokens, or rollback snapshots.
History is capped at 200 profile activations and can be cleared.

## Builder validation

The profile builder supports create, duplicate, rename, description, supplied icon,
accent, typed action values, required/optional status, reorder, removal, validation,
save, cancel, delete, import, and export.

Each action has a stable target key. More than one action targeting the same setting
is rejected. Type-specific fields are checked so, for example, a power-scheme action
cannot carry a monitoring Boolean. Names, descriptions, IDs, priorities, cooldowns,
and trigger paths are bounded and validated.

## Automatic switching

The portable `IAutomaticProfileSwitcher` prepares decisions for game start, game
exit, battery, AC, and kernctl-start events. Decisions:

- consider only profiles whose automatic behaviour is enabled and explicitly
  approved;
- select the highest trigger priority with a deterministic profile-ID tie break;
- enforce a per-profile cooldown;
- remember a previous profile for an approved temporary game activation;
- restore that profile when the selected game exits, when configured.

The current desktop milestone persists configuration and supplies the decision engine.
It does not install a service or a permanently elevated background process. Operating-
system event watchers are intentionally not started yet; future watchers must remain
unelevated and feed typed events into this engine.

## UI workflow

`Change Profile` opens a responsive workspace with three profile cards per row when
space permits and fewer at narrower widths. Each card exposes details, support,
automatic state, preview, and apply. Preview presents current/proposed values,
explanation, reversibility, privilege, restart, confirmation, and unsupported states.
Apply remains disabled until the user checks the explicit confirmation.

Results list every action and offer Keep profile, Restore previous state, and history.
Restore delegates to the journaled transaction and runs rollback in reverse order.

## Limitations

The following are deliberately unsupported:

- Realtime process priority;
- changing priority for an arbitrary existing process;
- terminating arbitrary processes;
- automatic application launch or close;
- Windows services, registry gaming tweaks, Defender, firewall, Windows Update,
  timers, memory management, boot configuration, or driver settings;
- custom/imported power schemes or editing scheme values;
- privileged profile actions beyond the existing diagnostic broker allowlist;
- silent background switching before a future unelevated event-source milestone.

Tests use fake power schemes, in-memory feature state, and temporary directories.
They never change the host's real power scheme, process priorities, registry, services,
network, or user files.
