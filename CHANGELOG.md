# Changelog

## Unreleased

## 1.3.0 — 2026-11-21

- **Claude mode**, for leaving the PC running while Claude Code works. Claude and everything it starts run normally;
  an app Claude is using wakes up (a live local connection from Claude, or activity in an app on the
  *Claude uses this app* list) and stays awake for 5 minutes after; everything else sleeps at once and hands its RAM
  back, including the window in front when nobody is at the keyboard.
- Claude mode switches to a power plan of its own: never sleeps, ignores the lid, turns the screen off after
  2 minutes, and runs the CPU without turbo (*Keep the CPU cool*). Your plan is put back when Claude mode ends,
  when Sakin quits, after a crash, and on uninstall.
- `Sakin.exe --claude-mode on|off` switches it on the running copy, so a script or Claude itself can turn it on.
- Each app's ⋯ menu has *Claude uses this app*; Chrome, the Adobe apps and Office are on the list by default.

## 1.2.0 — 2026-11-21

- **Boost**: give one app the machine. Ctrl+Alt+B (works even when the app has stopped responding), the tray menu,
  or the app's ⋯ menu. Every other background app is calmed and its RAM handed back; the boosted app's memory is
  held in RAM with a hard minimum working set (capped at half of RAM), and it runs above normal priority, never
  throttled, even in the background. Stops on the same shortcut, the Stop button, or when the app closes, and
  restores everything exactly.
- Measured under a 5 GB memory flood: the boosted app kept 756 MB resident (10 MB without Boost) and re-read its
  data in 55 ms (372 ms without).

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
