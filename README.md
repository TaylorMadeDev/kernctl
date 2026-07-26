# kernctl

**Your PC, under control.**

kernctl is an early-stage Windows control centre for storage, gaming, performance,
network, application, and safe system-management workflows. This milestone provides
the repository architecture, a polished non-destructive Avalonia UI, a complete
runtime theme customizer under **Settings → Appearance**, and the transactional
safety engine that all future system actions must use.

> [!IMPORTANT]
> kernctl is not production-ready. All profile and tool interactions in this
> milestone are in-memory only and do not modify Windows.

## Design reference

The current shell follows the supplied concept while establishing a kernctl-specific
design system.

![Supplied kernctl design reference](docs/images/gaming-dashboard-reference.png)

## Technology

- C# 14 and .NET 10
- Avalonia UI 12.1 with Avalonia XAML
- CommunityToolkit.Mvvm
- Microsoft.Extensions.DependencyInjection
- xUnit v3
- Avalonia ColorPicker

## Prerequisites

- Windows 10 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build and run

```powershell
dotnet restore Kernctl.sln
dotnet build Kernctl.sln --configuration Release --no-restore
dotnet test Kernctl.sln --configuration Release --no-build
dotnet run --project src/Kernctl.App/Kernctl.App.csproj
```

## Solution structure

- `src/Kernctl.App` — Avalonia presentation, controls, views, and view models.
- `src/Kernctl.Core` — platform-independent contracts, models, and state logic.
- `src/Kernctl.Platform.Windows` — harmless Windows-specific service implementations.
- `tests/Kernctl.Core.Tests` — unit tests for core and view-model behaviour.
- `docs` — architecture, safety, and design-system documentation.
- `svgs` — supplied Font Awesome SVG source library, kept in its original categories.

## Design principles

- A restrained, high-contrast dark interface with token-driven styling.
- Reusable controls, local vector assets, and clear keyboard focus.
- MVVM separation with cached page state and dependency injection.
- Honest UI: sample values and unavailable modules are identified explicitly.
- Runtime themes update shared Avalonia resources without recreating pages or restarting.
- Future changes run through versioned plans, durable rollback snapshots, verification,
  reverse-order recovery, and structured history.

## Transactional action safety

kernctl now has a platform-independent transaction engine for future optimizations.
It supports ordered action groups, dry runs, cooperative cancellation, one mutating
transaction at a time, persistent journals, reverse rollback, crash discovery, and
read-only history. Action review, progress, and startup recovery UI foundations are
present, but no production system actions are registered.

Journals are stored without elevation under:

```text
%LocalAppData%/kernctl/transactions/
├── active/
│   └── <transaction-id>.json
└── archive/
    └── <transaction-id>.json
```

See [Action engine and rollback safety](docs/action-engine.md) for the lifecycle,
state machines, journal format, and future action requirements.

## Theme customization

Appearance settings provide four immutable built-in themes—kernctl Dark, OLED,
Graphite, and Ember—plus named custom themes. Users can edit validated colour tokens,
density, corner style, font scale, and motion, with immediate application-wide preview.
Changes are committed explicitly; cancelling or closing with a preview restores the
last saved theme.

Custom themes can be imported and exported as safe, versioned JSON data. Preferences
are stored without elevation under:

```text
%LocalAppData%/kernctl/
├── settings.json
└── themes/
    └── <sanitized-theme-id>.json
```

Theme files contain only appearance data—never paths, system settings, credentials,
or personal information.

## Safety principles

- The UI process does not run permanently elevated.
- No registry, service, power-plan, driver, network, process, or user-file changes are
  implemented by the action engine milestone.
- Future actions must detect, plan, validate, snapshot, apply, verify, and report an
  honest committed or rollback state.
- Sensitive credentials, cookies, and tokens are never collected or logged.

## Roadmap

1. Add automated visual regression coverage for the shell and theme editor.
2. Add read-only Windows hardware and storage inventory.
3. Add the first read-only detections and reviewed mock-backed action workflow.
4. Design a restricted broker for narrowly approved administrator operations.

## Assets and attribution

The repository's `svgs/brands`, `svgs/regular`, and `svgs/solid` directories were
supplied by the project owner and contain Font Awesome artwork. Font Awesome Free
SVG icons are licensed under CC BY 4.0; see
[Font Awesome Free License](https://fontawesome.com/license/free). Only selected
icons are linked into the application resources. No font file was present in the
initial repository, so this milestone uses the system `Segoe UI Variable`/`Segoe UI`
fallback until the promised local font is added.

No software licence has been added to kernctl; the project owner must choose one.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), and
[AGENTS.md](AGENTS.md) before making changes. Keep changes focused, run formatting,
build, and tests, and update documentation when behaviour or architecture changes.
