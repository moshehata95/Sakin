# Contributing to Sakin

Thanks for helping. Sakin changes how other people's apps run, so changes to the engine are held to a simple rule:
**measure before and after.**

## Setup

- Windows 11 (or 10 21H2+), x64
- [.NET 11 SDK](https://dotnet.microsoft.com/download)

```
powershell -File build.ps1        # builds Sakin.exe in the repo root
Sakin.exe --selftest out.txt      # all checks should pass
```

For quick iteration: `dotnet build src/Sakin -c Debug`, then run the exe from `src/Sakin/bin/Debug/...` with
`--portable` (without it, any copy that isn't the installed one installs itself over your installed Sakin).
`Sakin.exe --quit` stops a running copy cleanly and restores every app.

## Working on the interface

The UI is plain HTML/CSS/JS in `src/Sakin/web`. Serve that folder with any static server and open `index.html`.
`mock.js` stands in for the engine with realistic data (add `?lang=ar` for Arabic, right to left).

- Every visible word goes in `web/i18n.js`, in both `en` and `ar`. The engine only sends codes and numbers.
- Use logical CSS properties (`inset-inline-start`, `padding-inline-end`) so both directions work.
- Check light and dark (it follows the Windows app theme).

## Working on the engine

- Everything that touches other processes runs on the engine thread. The UI talks to it through `Engine.Post`.
- Any new change to a process must be recorded in a `Tweak` *before* it's made, so it's restored on quit, pause
  and crash recovery.
- Only ever lower priority. Never touch Windows, security software, audio or input drivers (see `Guarded` in `Engine.cs`).
- Run the measurements that apply to your change and put the before/after numbers in the pull request:
  - `tools/ram-test.ps1`: memory (about 9 minutes)
  - `tools/cpu-test.ps1`: foreground speed under load (about 4 minutes)
  - `tools/test-switch.ps1`: calm-then-restore latency

  The test apps are built automatically on first run (`tools/bench/build.ps1`), one exe name per role so each is
  a separate app to Sakin.

## Pull requests

- One change per pull request, with what it fixes and the numbers.
- Keep the style of the surrounding code; comments explain *why*, not *what*.
- Add an entry under *Unreleased* in `CHANGELOG.md`.

## Reporting a bug

Please include your Windows version, CPU and RAM, the intensity you use, what you expected and what happened.
A screenshot of the Sakin window and the *Activity* list helps a lot.
