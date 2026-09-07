# Changelog

## [Unreleased]

## [1.0.0] - 2026-09-07

### Added

- Small always-on-top panel showing Claude session (5h) and weekly usage as progress
  bars, polling every minute (configurable) even while hidden.
- Sign-in via OAuth (browser + paste-code flow), matching Claude Code's own login.
- Tray icon that recolors (green/amber/red) to reflect usage status, with live numbers
  in its tooltip.
- Native OS notifications when usage crosses into amber/red, or when the session limit
  resets.
- Draggable, edge-snapping panel that remembers its position across monitors and
  restarts.
- Right-click context menu (Sign out / Quit) and a Settings dialog (poll interval,
  launch at login).
- Windows and macOS (Apple Silicon) installers via Velopack, built and published
  automatically from a GitHub Actions release pipeline.
