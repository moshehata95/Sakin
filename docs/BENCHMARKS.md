# Measurements

All numbers are from one machine: Dell G15, Intel i7-12700H (6 performance + 8 efficiency cores, 20 threads),
16 GB RAM, Windows 11, pagefile on an SSD. The machine was in normal use during the tests (browser, Photoshop,
editor open), so free RAM was already low, which is the situation Sakin is for.

The scripts are in [`tools/`](../tools) and the test apps in [`tools/bench`](../tools/bench). Each test app is a real
window with its own exe name, so Sakin treats it like any app you'd open.

## Memory: `tools/ram-test.ps1`

Three apps each hold 700 MB of touched private memory. Each is brought to the front once, then a fourth window
stays in front for 4 minutes. Free RAM, the three apps' working sets and `\Memory\Pages Input/sec` are sampled
every ~5 s. Then one of the apps is brought back and the time to re-read all its memory is logged.
Phase A runs without Sakin, phase B with Sakin on *Strong* (so "asleep" starts after 3 minutes instead of 10).

| | A, without Sakin | B, with Sakin |
|---|---|---|
| Average free RAM | 1,358 MB | **2,280 MB** |
| Free RAM at the end | 1,566 MB | **3,128 MB** |
| The three apps' memory, start → end | 2,274 → 1,047 MB | 2,263 → **34 MB** |
| Average pages read from disk | 16,129 / s | **8,624 / s** |
| App priority while in the background | Normal | Idle (asleep) |
| App priority after switching back | Normal | Normal |

Timeline of phase B: at ~30 s the apps were calmed (Idle priority). At ~150 s they fell asleep and were trimmed,
and free RAM rose by about 1.3 GB. Without Sakin, Windows trimmed part of the same apps on its own under
pressure, but kept over 1 GB of them resident.

**Switching back costs something.** Re-reading all 700 MB of a trimmed app took 3 ms in one run (pages still on
the standby list, so no disk access) and 379 ms in another (pages written to the pagefile). Without Sakin it took
39–189 ms, depending on how much Windows had trimmed by itself. This is the trade: free RAM now against a short
reload when you come back to an app you hadn't used for minutes.

## Foreground speed: `tools/cpu-test.ps1`

A foreground window runs a fixed single-thread job (600 M floating-point steps). Each case runs it three times
in a fresh window; the median is reported. The background load is one app spinning 20 threads, one per logical CPU.

| Case | Run 1 | Run 2 |
|------|------:|------:|
| Idle machine | 949 ms | 982 ms |
| Heavy background app, no Sakin | 1,070 ms | 986 ms |
| Heavy background app, Sakin default | 1,091 ms | 950 ms |
| Heavy background app, Sakin + *Always calm it* rule | n/a¹ | 1,078 ms |

¹ That run exposed the inherited-priority bug described below. Fixed before run 2.

Run-to-run noise is about ±10 % (turbo and temperature), so these differences aren't significant. By default
Sakin classes a busy background app as *Working* and leaves it alone, which is the point: a render in the
background shouldn't be slowed. Windows already gives the foreground thread a boost. On this CPU Sakin's CPU
effect is mostly *where* background work runs (efficiency cores via EcoQoS), not how fast the foreground runs.

## Switch-back latency: `tools/test-switch.ps1`

A test window is left in the background for 36 s (past the 30 s grace period), then brought to the front.

```
opened:                    priority=Normal       efficiency=off  memPriority=5
after 36 s in background:  priority=BelowNormal  efficiency=on   memPriority=2
switched back:             restored in 32 ms     priority=Normal efficiency=off memPriority=5
```

## Self-test: `Sakin.exe --selftest out.txt`

Checks the process table against `Process.GetProcesses()` (names, session, create time, CPU time, parent), runs
the GPU and audio probes, and does a full calm and exact-restore round trip on a throwaway process. All 16 checks pass.

## Bugs these tests found (all fixed in 1.0)

- **Hung windows reset an app.** A background app pegging every core stopped answering, and Windows swapped its
  window for a ghost window. Sakin lost the window, regrouped the process and restarted its grace timer every
  few seconds, so it never calmed it. Window ownership is now sticky, and apps keep their history for a minute.
- **Inherited low priority.** Processes started by a calmed app inherited its idle priority and stayed slow even
  in front, because Sakin had nothing recorded to restore. They're tracked now.
- **Trimming busy apps.** Emergency trimming of recently used apps made them fault their memory straight back.
  Now only quiet apps are trimmed.
- **New windows recognised late.** A freshly opened app waited up to 2 s for the next window scan before being
  treated as its own app. The front window is now recognised immediately.
