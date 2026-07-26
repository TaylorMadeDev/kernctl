# Gaming library and session safety

Milestone 6 turns the Gaming dashboard into a local library and per-game control
centre. The feature is deliberately not a launcher replacement: kernctl discovers
read-only local metadata, stores the user's own safe configuration, starts an exact
validated executable, observes its process tree, and restores temporary changes.

## Supported discovery

Discovery providers implement `IGameDiscoveryProvider` and return stable local
metadata only:

- **Manual** — the user chooses an existing `.exe` through the native Windows file
  picker. Scripts and non-executable targets are rejected. Network, Windows,
  temporary, and Downloads locations require an additional trust confirmation.
- **Steam** — kernctl reads `libraryfolders.vdf` and local `appmanifest_*.acf`
  files. Steam manifests provide identity, display name, and install directory but
  not a sufficiently trustworthy executable, so the entry remains
  `NeedsExecutable` until the user chooses one.
- **Epic Games Launcher** — kernctl reads local `.item` manifests in
  `%ProgramData%/Epic/EpicGamesLauncher/Data/Manifests`. Epic's own Unreal Engine
  deployment documentation identifies this manifest location. `LaunchExecutable`
  is accepted only when its normalized `.exe` path remains inside `InstallLocation`.

Providers do not sign in, call launcher web APIs, collect passwords, tokens,
cookies, or account data, impersonate a launcher, or modify launcher files. Xbox app
discovery is not claimed or implemented. Parsers are best-effort because local
launcher formats can change; malformed entries become visible discovery errors.

Entries deduplicate by normalized executable path, then by source and external ID.
Saved launch/profile settings take precedence over rescanned metadata. Missing or
moved executables are reclassified at startup and cannot be launched until repaired.

## Persistence and privacy

`GameLibraryStore` writes versioned JSON atomically to:

```text
%LocalAppData%/kernctl/gaming-library.json
```

It stores the library, manual entries, explicit executable and working-directory
paths, literal argument arrays, profile assignments, safe priority choice, automatic
profile preferences, overlay preferences, and the latest 100 session outcomes.

Session history retains game identity, timestamps, duration, outcome, profile,
priority, peak working set, and average CPU. Process IDs are removed on load and
summaries containing paths or environment-like data are replaced with a redacted
message. Environment blocks, raw command lines, access tokens, and launcher account
data are never stored.

Schema version 0 migrates to version 1 defaults. Unknown future schemas fail closed
with a visible library error.

## Direct launch boundary

`GameValidation` requires:

- a fully qualified existing `.exe`;
- an existing fully qualified working directory;
- no NUL, CR, or LF in paths or arguments;
- at most 64 literal arguments of at most 512 characters each; and
- one of `Normal`, `AboveNormal`, or `High` priority.

`WindowsGameProcessService` sets `UseShellExecute = false` and appends each argument
to `ProcessStartInfo.ArgumentList`. It never constructs a command string, invokes a
shell, interprets a URI, accepts a script, or includes credentials. Microsoft
documents `UseShellExecute = false` as direct executable creation and exposes
`ArgumentList` for distinct arguments:

- <https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.useshellexecute>
- <https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.argumentlist>

The launch review is explicit. Search results can open game details or open the
launch confirmation, but search never launches a process silently.

## Session lifecycle

`IGameSessionCoordinator` serializes session setup to prevent competing temporary
profile changes:

1. validate the saved executable and look for that exact running image;
2. apply the assigned profile through `IProfileEngine`, if both global and per-game
   automatic profile handling are enabled;
3. directly launch or attach;
4. plan, snapshot, apply, and verify priority through
   `IActionTransactionEngine`;
5. monitor the root and locally enumerated descendants at a one-second interval;
6. collect genuine duration, CPU, working set, process count, priority, and active
   profile state;
7. independently roll back priority; restore the profile on normal exit when its
   per-game restore option is enabled, and always attempt profile restoration after
   launch failure, cancellation, or monitoring failure; and
8. append a redacted session result.

The Windows process-tree provider uses the read-only Tool Help process snapshot and
parent-process ID documented by Microsoft:

- <https://learn.microsoft.com/windows/win32/toolhelp/process-walking>
- <https://learn.microsoft.com/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot>

A five-second launcher grace period allows a bootstrap process to create a child
before monitoring concludes. It does not inject into a process, read process memory,
or poll in a tight loop.

## Priority safety

The UI offers only:

- `Normal` — recommended;
- `AboveNormal` — a modest CPU scheduling hint; and
- `High` — can reduce responsiveness for other applications and is not an FPS
  guarantee.

`Realtime` is absent and rejected by validation. Microsoft's `SetPriorityClass`
documentation warns that High can consume nearly all CPU time and that Realtime can
interfere with mouse input and disk flushing:

<https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass>

Priority acts only on the exact process ID/start-time identity selected by an active
configured game session. The original supported class is snapshotted before change,
verification reads it back, and rollback restores it while the process remains
alive. Access-denied and exited-process results are reported once without escalation
or retry loops.

## Overlay inspection

The overlay page checks a fixed identity list for Steam, Discord, Xbox Game Bar,
NVIDIA App, and AMD Software. It displays running state, capabilities, local path,
and unverified file-version company metadata when readable. `Open app` uses only an
already observed exact `.exe` path.

No overlay is killed automatically. `Exit` requires explicit confirmation and sends
only `CloseMainWindow`; kernctl deliberately does not force-terminate an
unresponsive process. The Included/Ignored dashboard preference is persisted per
known overlay identity and changes only kernctl's display/counting behaviour.

## FPS provider boundary

`IFpsProvider` is the only frame-rate integration point. The shipping
`UnavailableFpsProvider` returns no value and the UI displays exactly:

```text
FPS provider unavailable.
```

kernctl does not estimate FPS, inject code, read game memory, bypass anti-cheat, or
silently run an external capture tool. A future provider may evaluate PresentMon's
ETW/API approach, but it must be reviewed for permissions, packaging, user consent,
resource cost, security boundaries, and redistribution/license obligations before
being shipped. PresentMon's upstream repository describes the API/service and its
MIT license:

<https://github.com/GameTechDev/PresentMon>

No PresentMon code or binary is included in this milestone.

## Tests

Tests use temporary metadata trees and in-memory process/profile implementations.
They never launch a real game. Coverage includes Steam and Epic parsing, unsafe Epic
path rejection, manual validation and confirmation, deduplication, moved files,
literal argument validation, schema migration, session redaction, priority
apply/verify/rollback, denied priority, child-monitor abstraction, profile
restoration after monitoring failure, missing executables, overlay confirmation, and
the unavailable FPS provider. A Windows-only smoke test directly launches the
repository's own harmless broker fixture with an intentionally invalid diagnostic
argument, confirms a real process identity was returned, and waits for its
non-mutating early exit.
