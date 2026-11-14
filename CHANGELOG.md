# Changelog

## Unreleased

## 1.1.0 — 2026-11-14

- Installs itself per user (no admin) to `%LOCALAPPDATA%\Programs\Sakin`, with a Start menu shortcut and an
  entry in Installed apps. Running a newer exe updates it.
- Uninstall from Installed apps: restores every app, then removes the program, startup entry, shortcut and settings.
- `--portable` to run without installing.
- Starting with Windows stays on by default and follows the installed location.

## 1.0.0 — 2026-11-14

First release.

- Calms background apps with Efficiency mode (EcoQoS), lower CPU priority and lower memory priority.
- Hands memory back from apps that have been asleep when free RAM drops below a threshold.
- Leaves apps that are rendering, exporting or playing audio at full speed.
- Restores an app within milliseconds of it coming to the front.
- Three intensities (Light, Balanced, Strong), adjustable numbers, stricter on battery.
- Per-app rules: automatic, always full speed, always calm, never touch, free memory now.
- Journal of every change, restored on quit, pause and after a crash.
- Start with Windows, optionally with full permissions through a scheduled task (one UAC prompt).
- English and Arabic interface, light and dark.
- Single self-contained exe; about 35 MB in the tray.
