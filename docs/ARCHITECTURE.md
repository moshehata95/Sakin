# How Sakin works

Sakin is one Windows process with three parts:

```
 ┌──────────── UI thread ────────────┐        ┌─────────── engine thread ───────────┐
 │ TrayApp     tray icon, menu       │ Post() │ Engine.Tick()  every 1 s,            │
 │             foreground WinEvent ──┼───────▶│                and at once on a      │
 │ MainWindow  WebView2 + messages   │◀───────┤                foreground change     │
 └───────────────────────────────────┘ JSON   └──────────────────────────────────────┘
```

Everything that touches other processes happens on the engine thread. The UI only sends commands
(`Engine.Post`) and renders the JSON snapshot the engine publishes while the window is open.

## One tick

1. **Read the machine.** The whole process table comes from a single `NtQuerySystemInformation(SystemProcessInformation)`
   call: pid, parent, name, create time, CPU time and private working set for every process, including elevated
   ones, without opening a handle per process. Free memory comes from `GlobalMemoryStatusEx`. Every 2 s, GPU use
   per process from the `\GPU Engine(*)\Utilization Percentage` counters (the same source as Task Manager) and the list of
   processes with visible top-level windows. Every 3 s, the processes with an active audio session (WASAPI).

2. **Group processes into apps.** Each process walks up its parent chain to the app that owns it, so Chrome's
   renderers belong to Chrome and Photoshop's helpers to Photoshop. The walk stops at shell hosts (Explorer,
   svchost and so on), at a parent that was started *after* the child (a reused pid), and at any process that has
   shown its own window. That last rule means a GUI app started from a terminal or a launcher is an app of its own.
   It's sticky: a hung window that Windows replaces with a ghost window doesn't change which app it is.
   An app whose processes vanish for a moment keeps its history for a minute.

3. **Decide a state** for each app, in this order:

   | Check | State |
   |-------|-------|
   | Owns the foreground window | `fg` |
   | Windows, security, audio, input drivers, or a *never touch* rule | `guard` |
   | Never had a window and is smaller than 300 MB | unmanaged (ignored) |
   | Sakin is paused | `off` |
   | Runs as administrator and Sakin doesn't | `locked` |
   | *Always full speed* rule | `work` |
   | Playing audio (if enabled), or busy: CPU or GPU over the threshold averaged over 10 s, released when the 30 s average falls under half of it | `work` |
   | Left the front less than the grace period ago | `wait` |
   | Idle longer than the asleep time | `sleep` |
   | Otherwise | `calm` |

   An *Always calm it* rule skips the audio and busy checks.

4. **Apply** the state to every process of the app:

   | Level | Light | Balanced | Strong |
   |-------|-------|----------|--------|
   | calm  | EcoQoS | EcoQoS, below-normal priority, low memory priority | EcoQoS, idle priority, very-low memory priority |
   | sleep | EcoQoS, below-normal, low memory | EcoQoS, idle, very-low memory | same |

   - EcoQoS is `SetProcessInformation(ProcessPowerThrottling, EXECUTION_SPEED)`, the same thing Task Manager's
     Efficiency mode does. On hybrid CPUs Windows schedules those threads on the efficiency cores at lower clocks.
   - Priority is only ever lowered. A process whose own priority is above normal keeps it.
   - Memory priority (`ProcessMemoryPriority`) makes Windows repurpose that app's pages first when RAM is needed.
   - Before the first change to a process, its original priority class, memory priority and power-throttling state
     are recorded. Leaving the calm/sleep states restores exactly those values.

5. **Reclaim memory** when free RAM is below the threshold: the largest quiet (<1 % CPU over 30 s) asleep app has
   its working set emptied (`SetProcessWorkingSetSizeEx(-1, -1)`), at most once per app every 10 minutes.
   Below 8 % free, quiet calmed apps idle for two minutes qualify too, at most once every 5 minutes. The pages go
   to Windows' standby or modified lists. If the app touches them again they fault back in, from RAM if they're
   still on the standby list, otherwise from the pagefile.

6. **Journal** every recorded original to `%LOCALAPPDATA%\Sakin\journal.json`, keyed by pid and create time.
   On a clean exit or pause everything is restored and the file deleted. If Sakin is killed, the next start
   restores every process that still exists with the same create time.

## Instant restore

`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` runs on the UI thread and wakes the engine. A foreground-only tick
re-reads the process table, makes the front window's process an app of its own straight away, and restores it.
Measured: 32 ms from switch to normal priority (`tools/test-switch.ps1`).

## Inherited priority

Windows gives a child process its parent's priority class when the parent is idle or below normal. So anything an app
starts while Sakin has it calmed would begin slowed, and Sakin never changed it, so there'd be nothing to restore.
New processes whose parent is calmed (or itself inherited) are tracked. When they come to the front they get
normal values, and if they're later calmed their "original" is normal rather than the inherited value.

## Claude mode

While it's on, `Decide` hands every app to `DecideClaude`:

1. **Who is Claude.** Every `claude.exe` in the session is a root (the desktop app, each Claude Code session, the
   Chrome extension's native host), and every process descended from one joins the set. Parent links are only
   followed when the parent is older than the child, so a reused pid can't pull a stranger in. An app whose processes
   are all in the set is *For Claude*. Claude processes that live inside another app's tree (a session in Windows
   Terminal, the extension host under Chrome) always get level 0, whatever their app's state.
2. **Who Claude is using.** Every 3 s `TcpProbe` reads the TCP tables (`GetExtendedTcpTable`, IPv4 and IPv6) and
   pairs the two ends of each established loopback connection. A pair with Claude on one side marks the other
   side's app as used. Apps on the *Claude uses this app* list, and apps holding a Claude process, are also marked
   used when their 10-second CPU average is above 2 %, which is how scripting through COM or the browser
   extension shows up. A used app is *For Claude* until `ClaudeLingerMin` minutes after the last sign.
3. **Everyone else sleeps**, without the grace period, busy or not, except *Always full speed* rules, protected
   processes and (if that setting is on) apps playing sound. The window in front only counts as yours when
   `GetLastInputInfo` shows input in the last 2 minutes.

On entry a first pass trims every quiet sleeping app (4 per tick, during the first minute); after that the normal
low-memory trimming applies.

**Power.** `PowerPlan` duplicates Windows' Balanced plan as *Sakin - Claude mode* and sets, for AC and battery:
`STANDBYIDLE` and `HIBERNATEIDLE` 0, `LIDACTION` 0, `VIDEOIDLE` from the setting, and with *Keep the CPU cool*
`PERFBOOSTMODE` 0 (no turbo), `PROCTHROTTLEMAX` 99 and `PERFEPP` 60. The previous active plan's GUID is stored in
the settings before switching. Turning Claude mode off, quitting, the next start after a crash and uninstalling all
switch back to that plan and delete the copy. `powercfg` does all of this without administrator rights. The engine
thread also holds `ES_SYSTEM_REQUIRED`.

**Do not disturb.** `QuietHours` uses the same object as the Settings app (`QuietHoursSettings`,
`IQuietHoursSettings::put_UserSelectedProfile`) on a short-lived STA thread. Entering sets
`Microsoft.QuietHoursProfile.PriorityOnly` unless the user already has PriorityOnly or AlarmsOnly; the previous
profile and the one Sakin set are stored in the settings. Leaving restores the previous profile only if the current
one is still the one Sakin set, so a change the user made meanwhile is kept. (Writing the WNF state
`WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED` directly changes the value but the shell ignores it, so it isn't used.)

## Starting with Windows

- Default: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, refreshed on every start so it follows the exe if moved.
- *Full permissions*: a scheduled task with *Run with highest privileges* and a logon trigger. Creating it asks
  UAC once. After that, launching `Sakin.exe` normally hands off to the task (`schtasks /Run`), so there's
  never another prompt.

## The window

The interface is static HTML/CSS/JS embedded in the exe and served to WebView2 from `https://sakin.app/`
(intercepted, never on the network). The engine sends a full snapshot about once a second
(`type: "state"`), app icons once (`type: "icons"`), and toasts. The page sends commands back:
`setMode`, `pause`, `resume`, `setRule`, `trimNow`, `boost`, `unboost`, `claude`, `claudeApp`, `screenOff` and `setSetting`. All words live in `web/i18n.js`. The engine
only sends codes and numbers, so English and Arabic come from the same data.

The WebView2 control is created when the window opens and disposed when it closes, which takes Sakin back to
about 35 MB.
