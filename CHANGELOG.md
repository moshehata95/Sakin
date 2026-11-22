# Changelog

## Unreleased

## 1.5.3 — 2026-11-22

- **Fix:** *Open in Claude Code* didn't appear: the Claude app registers `claude://` as a packaged (Store-style)
  protocol, with no command in the registry, so Sakin thought it wasn't installed. It now finds the app through its
  package and takes the icon from there. The item is in the classic menu (Shift + right-click on Windows 11).

## 1.5.2 — 2026-11-22

- Right-click a folder › **Open in Claude Code** starts a new Claude Code session on it in the Claude app. Added
  on install when the Claude app is there (and at each start, in case it's installed later); removed on uninstall.

## 1.5.1 — 2026-11-22

- The **Session Call** folder now comes with Sakin: it is made on the first start (`%USERPROFILE%\Session Call`)
  with its `CLAUDE.md`, the instructions for the session that never sleeps and passes Sakin's questions on.
  Settings › Claude mode shows it with *Open folder*. Each new version updates the file, unless you have edited it.

## 1.5.0 — 2026-11-22

- **Claude mode asks Claude.** When an app Claude opened (Photoshop, Premiere, a browser…) has gone unused for
  30 minutes and still holds 500 MB or more, Sakin doesn't guess: it writes a question to `claude-asks.json`. A
  Claude session in a *Session Call* folder passes it to the session that opened the app, which either keeps it
  (`Sakin.exe --answer <id> keep "why"`, asked again an hour later) or saves its work, closes the app and answers
  `done`. Sakin itself never closes anything.
- What Claude used is remembered across restarts of Sakin (for a day), so a question can still be asked.
- Tested on a real case: Photoshop holding 7.9 GB with an unsaved document. The question reached the session that
  opened it, which answered *keep* on its own (an export you had stopped, so the decision is yours).

## 1.4.0 — 2026-11-22

- **Claude mode: idle sessions rest.** Each open Claude Code session (its `claude.exe`, MCP servers and tools) is
  watched on its own. After 15 minutes with nothing to do (*Put a Claude session with nothing to do to sleep after*
  in Settings, 0 turns it off) it sleeps and hands its RAM back, and it wakes the moment it works again. The Claude
  app itself, and so Remote Control, is never touched. Tested on 13 open sessions: 11 rested within a minute and
  handed back about 1.2 GB; each woke on its own when it got work.
- Sessions in a folder named *Session Call* never rest, so one session can always be reached (from the phone, for
  example) to wake the others.

## 1.3.7 — 2026-11-22

- The heart in the top bar now reads *Donate*.

## 1.3.6 — 2026-11-22

- A small heart next to Settings in the top bar opens *Support Sakin*. Header icons now show their name on hover.

## 1.3.5 — 2026-11-22

- The Claude mode button shows whether it's on: a small green light and *On*, or a grey light and *Off*.

## 1.3.4 — 2026-11-22

- **Fix:** Windows no longer turns the display off in Claude mode. On Modern Standby laptops that is standby itself
  (the event log shows it), which freezes desktop apps. Sakin now blanks the screens instead: black on every monitor
  and the laptop panel at its lowest brightness, back on the first touch. The PC stays fully awake.
- Apps that Windows starts on Claude's behalf (Notepad and Edge through the shell, Office through COM) now wake up:
  while nobody is at the PC, an app that opens or starts something new counts as Claude's.
- Edge is on the *Claude uses this app* list by default.

## 1.3.3 — 2026-11-22

- **Fix:** removed Claude mode's *Screen off* button and tray item. On Modern Standby laptops turning the display off
  that way puts the PC into standby, which suspends Claude and cuts its sessions. The screen now only turns off on
  the plan's timeout, which doesn't sleep the PC. To turn a screen off yourself, close the lid or switch the
  monitor off.

## 1.3.2 — 2026-11-22

- Claude mode turns on Windows' **Do not disturb** (on by default, a switch in Settings) and puts back what you had
  when it ends, on quit, after a crash and on uninstall. If you already had it on nothing changes, and a change you
  make yourself while Claude mode is on is kept.

## 1.3.1 — 2026-11-21

- *Support Sakin*: a quiet button in Settings › About and an item in the tray menu, opening PayPal. Sakin never
  asks on its own. The repository's Sponsor button points to the same page.
- Claude mode's "For Claude" count now includes Claude itself, not only the apps it is using.

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
