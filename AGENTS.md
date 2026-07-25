# Instructions for coding agents

- Inspect `git status` before editing and preserve unrelated user changes.
- Use MVVM; keep business logic out of code-behind.
- Use design tokens instead of hardcoded UI colours and reusable dimensions.
- New and updated controls must consume dynamic theme resources for colours, typography,
  reusable spacing, density-sensitive sizes, corner radii, and animation durations.
- Use supplied local fonts and SVG icons. Never substitute emoji, Unicode icon glyphs,
  downloaded icons, or rasterized SVGs.
- Never store passwords, cookies, session tokens, or authentication tokens.
- Never add an optimization without detection, explanation, verification, and rollback.
- Never perform privileged work directly from the UI process.
- Never silently change the registry, services, power configuration, or user files.
- Run formatting, Release build, and tests before committing.
- Keep commits focused; never force-push or rewrite another contributor's history.
- Never commit secrets, build output, coverage, machine-specific files, or certificates.
- Update documentation when architecture or behaviour changes.
