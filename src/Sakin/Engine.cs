using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace Sakin;

/// <summary>
/// The whole policy lives here and runs on one background thread.
///
/// Every app the user opened is grouped by its root process (Chrome and its
/// renderers are one app; Photoshop and its helpers are one app). Each group is in
/// one state:
///   fg     – the window in front of you: untouched, restored instantly on switch.
///   work   – in the background but busy (rendering, exporting, playing audio): untouched.
///   wait   – just left the foreground; untouched for the grace period.
///   calm   – background: Efficiency mode (EcoQoS → E-cores), lower CPU and memory priority.
///   sleep  – unused for a while: lowest priorities; its RAM is handed back when RAM runs short.
///   guard  – Windows, security, drivers, or a "never touch" rule.
///   locked – runs as administrator and ساكن doesn't.
/// Every change is journaled with the original values and undone on exit, on pause,
/// on crash recovery, and the moment the app comes back to the front.
/// </summary>
internal sealed class Engine : IDisposable
{
    // ---------- tuning that is not exposed (kept deliberately conservative) ----------
    const double ManagedWithoutWindowMB = 300;  // windowless processes this big are managed too
    const double MinTrimMB = 80;                // not worth trimming below this
    const int RetrimAfterMin = 10;              // never trim the same app twice inside this window
    const int EmergencyPct = 8;                 // below this, calm apps are trimmed too

    static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "services", "wininit", "winlogon", "sihost", "userinit", "runtimebroker",
        "dllhost", "taskhostw", "smss", "csrss", "applicationframehost", "startmenuexperiencehost",
        "shellexperiencehost", "searchhost", "system", "idle",
    };

    static readonly HashSet<string> Guarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "ctfmon", "taskhostw", "runtimebroker",
        "searchhost", "searchindexer", "searchprotocolhost", "searchfilterhost", "startmenuexperiencehost",
        "shellexperiencehost", "shellhost", "textinputhost", "applicationframehost", "lockapp", "logonui",
        "securityhealthsystray", "securityhealthservice", "msmpeng", "nissrv", "mpdefendercoreservice",
        "smartscreen", "audiodg", "conhost", "dllhost", "wudfhost", "spoolsv", "memory compression",
        "secure system", "ngciso", "useroobebroker", "systemsettingsbroker", "crossdeviceresume",
        "rtkauduservice64", "rtkauduservice", "igfxem", "igfxcuiservice", "igfxext",
        "sakin",
    };
    static readonly string[] GuardedPrefixes = { "avp", "kav", "ksde", "kaspersky", "logi", "nvidia", "nvcontainer", "nvdisplay", "synaptics", "etdctrl", "dellsupport", "waves", "maxxaudio" };

    static readonly Dictionary<string, string> GuardReason = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer"] = "shell", ["dwm"] = "screen", ["msmpeng"] = "security",
        ["audiodg"] = "sound", ["searchhost"] = "search",
    };

    // ---------- types ----------
    sealed class Tweak
    {
        public long Create;
        public uint Prio;
        public uint Mem;
        public uint PowerControl, PowerState;
        public int Level;
    }

    sealed class Group
    {
        public string Key = "";
        public string Name = "";
        public string? Path;
        public int RootPid;
        public readonly List<ProcRow> Procs = new();
        public double PrivateMB;
        public double Cpu, Gpu;
        public bool Audio;
        public bool HadWindow;
        public bool Guard;
        public bool Locked;
        public long LastActive;          // ms: last time it was in front, busy, or playing audio
        public bool Busy;
        public string BusyWhy = "";
        public double BusyPct;
        public double Cpu30 = 100;
        public long EmptySince;       // average CPU over the last 30 s (100 until measured)
        public string State = "";
        public string Reason = "";
        public long LastTrim;
        public double TrimmedMB;
        public bool SeenFg;
        public readonly Queue<(long t, double cpu, double gpu)> Samples = new();
    }

    // ---------- state ----------
    readonly Settings _s;
    readonly int _session = Process.GetCurrentProcess().SessionId;
    readonly int _selfPid = Environment.ProcessId;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly Queue<Action> _cmds = new();
    readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Tweak> _tweaks = new();
    readonly Dictionary<(int, long), long> _prevCpu = new();
    readonly Dictionary<(int, long), string> _keyCache = new();
    readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _iconsSent = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(long t, double mb)> _history = new();
    readonly List<(long t, double mb)> _trims = new();
    readonly List<(int id, long t, string kind, string code, string app, double num, string extra)> _log = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly GpuProbe _gpu = new();
    readonly int _cpus = Environment.ProcessorCount;
    readonly bool _admin;

    volatile bool _stop;
    volatile bool _fgChanged;
    bool _journalDirty;
    bool _statsDirty;
    long _lastTickMs, _lastGpuMs, _lastAudioMs, _lastWinMs, _lastHistMs, _lastStatsSave;
    long _prevSysIdle, _prevSysTotal;
    double _cpuTotal;
    double _availMB, _totalMB;
    Dictionary<int, double> _gpuByPid = new();
    HashSet<int> _audioPids = new();
    HashSet<int> _windowPids = new();
    readonly Dictionary<int, long> _windowEver = new();
    // Processes started by an app while ساكن had it calmed: Windows copies the low priority (and
    // efficiency mode) to the child. They are tracked so the child gets normal values back.
    readonly Dictionary<int, long> _inherited = new();   // pid → create time of every process that has shown its own window
    HashSet<int> _self = new();
    volatile int _sumCalm, _sumSleep;
    Dictionary<int, ProcRow> _byPid = new();
    string _fgKey = "";
    int _logId;
    public bool Paused { get; private set; }
    long _pausedUntil; // unix ms, 0 = until resumed
    public volatile bool UiAttached;

    public event Action<string>? Snapshot;   // JSON for the window
    public event Action<string>? Toast;
    public event Action? ModeChanged;        // so the tray menu can follow

    static long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Engine(Settings s, bool admin)
    {
        _s = s;
        _admin = admin;
        _thread = new Thread(Loop) { IsBackground = true, Name = "sakin-engine", Priority = ThreadPriority.AboveNormal };
    }

    public void Start()
    {
        RestoreFromJournal();
        Log("info", "start", "", _admin ? 1 : 0, _s.Mode);
        _thread.Start();
    }

    /// <summary>Called from the UI thread's WinEvent hook; the engine reacts within milliseconds.</summary>
    public void ForegroundChanged() { _fgChanged = true; _wake.Set(); }

    public void Post(Action a) { lock (_cmds) _cmds.Enqueue(a); _wake.Set(); }

    // ======================================================================
    void Loop()
    {
        while (!_stop)
        {
            try { Tick(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            long wait = 1000 - (_clock.ElapsedMilliseconds - _lastTickMs);
            if (wait > 0) _wake.WaitOne((int)wait);
        }
    }

    void Tick()
    {
        // commands from the UI first, so a click is reflected in this very tick
        while (true)
        {
            Action? a = null;
            lock (_cmds) if (_cmds.Count > 0) a = _cmds.Dequeue();
            if (a == null) break;
            try { a(); } catch (Exception ex) { Debug.WriteLine(ex); }
        }

        bool fgOnly = _fgChanged && _clock.ElapsedMilliseconds - _lastTickMs < 900;
        _fgChanged = false;
        long ms = _clock.ElapsedMilliseconds;
        long now = UnixMs;
        double elapsed = Math.Max(1, ms - _lastTickMs);
        if (!fgOnly) _lastTickMs = ms;

        if (Paused && _pausedUntil > 0 && now >= _pausedUntil) Resume();

        // ---- read the machine ----
        var rows = ProcessTable.Read();
        _byPid = rows.ToDictionary(r => r.Pid);
        ReadMemory();
        // the window in front is an app of its own right away, without waiting for the next window scan,
        // so a freshly opened app is recognised (and restored) within milliseconds
        int fgPid = ForegroundPid();
        if (fgPid > 0 && _byPid.TryGetValue(fgPid, out var fgRow) && !ShellHosts.Contains(fgRow.Name)) _windowEver[fgPid] = fgRow.CreateTime;

        if (!fgOnly)
        {
            ReadCpuTotal();
            if (ms - _lastGpuMs >= 2000) { _gpuByPid = _gpu.Sample(); _lastGpuMs = ms; }
            if (ms - _lastAudioMs >= 3000) { _audioPids = AudioProbe.ActivePids(); _lastAudioMs = ms; }
            if (ms - _lastWinMs >= 2000)
            {
                _windowPids = VisibleWindowPids();
                _lastWinMs = ms;
                // sticky: a hung app's window can be swapped for a ghost window, which must not change which app it is
                foreach (int pid in _windowPids) if (_byPid.TryGetValue(pid, out var wr)) _windowEver[pid] = wr.CreateTime;
                foreach (int pid in _windowEver.Keys.ToList()) if (!_byPid.TryGetValue(pid, out var wr) || wr.CreateTime != _windowEver[pid]) _windowEver.Remove(pid);
            }
            if (ms - _lastHistMs >= 2000)
            {
                _history.Add((now, _availMB));
                while (_history.Count > 0 && now - _history[0].t > 600_000) _history.RemoveAt(0);
                _lastHistMs = ms;
            }
        }

        // ---- group processes into apps ----
        _self = Descendants(_selfPid);
        var self = _self;
        foreach (var g in _groups.Values) { g.Procs.Clear(); g.PrivateMB = 0; g.Cpu = 0; g.Gpu = 0; g.Audio = false; }
        var seenCpu = new HashSet<(int, long)>();
        foreach (var r in rows)
        {
            if (r.SessionId != _session || r.Pid == 0 || self.Contains(r.Pid)) continue;
            string key = KeyFor(r);
            if (!_groups.TryGetValue(key, out var g))
            {
                g = new Group { Key = key, LastActive = now };
                g.Guard = IsGuarded(key);
                _groups[key] = g;
            }
            g.Procs.Add(r);
            if (!_prevCpu.ContainsKey((r.Pid, r.CreateTime)) && !_inherited.ContainsKey(r.Pid)
                && _byPid.TryGetValue(r.ParentPid, out var par) && par.CreateTime <= r.CreateTime
                && ((_tweaks.TryGetValue(par.Pid, out var pt) && pt.Create == par.CreateTime && pt.Level > 0)
                    || (_inherited.TryGetValue(par.Pid, out long pc) && pc == par.CreateTime)))
                _inherited[r.Pid] = r.CreateTime;
            g.PrivateMB += r.PrivateBytes / 1048576.0;
            var id = (r.Pid, r.CreateTime);
            seenCpu.Add(id);
            if (!fgOnly)
            {
                if (_prevCpu.TryGetValue(id, out long prev) && r.CpuTime >= prev)
                    g.Cpu += (r.CpuTime - prev) / 10_000.0 / elapsed / _cpus * 100.0;
                _prevCpu[id] = r.CpuTime;
            }
            if (_gpuByPid.TryGetValue(r.Pid, out double gp)) g.Gpu = Math.Max(g.Gpu, gp);
            if (_audioPids.Contains(r.Pid)) g.Audio = true;
            if (_windowEver.ContainsKey(r.Pid)) g.HadWindow = true;
        }
        if (!fgOnly)
        {
            foreach (var k in _prevCpu.Keys.Where(k => !seenCpu.Contains(k)).ToList()) _prevCpu.Remove(k);
            foreach (var k in _keyCache.Keys.Where(k => !seenCpu.Contains(k)).ToList()) _keyCache.Remove(k);
        }

        // drop apps that closed; forget tweaks of dead processes
        // an app that vanishes for a moment keeps its history; it is forgotten after a minute
        foreach (var g in _groups.Values) if (g.Procs.Count > 0) g.EmptySince = 0; else if (g.EmptySince == 0) g.EmptySince = now;
        foreach (var dead in _groups.Values.Where(g => g.Procs.Count == 0 && now - g.EmptySince > 60_000).Select(g => g.Key).ToList()) _groups.Remove(dead);
        foreach (var pid in _inherited.Keys.ToList())
            if (!_byPid.TryGetValue(pid, out var ir) || ir.CreateTime != _inherited[pid]) _inherited.Remove(pid);
        foreach (var pid in _tweaks.Keys.ToList())
            if (!_byPid.TryGetValue(pid, out var r) || r.CreateTime != _tweaks[pid].Create) { _tweaks.Remove(pid); _journalDirty = true; }

        _fgKey = ForegroundKey(fgPid);

        // ---- decide and act ----
        bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        double factor = onBattery && _s.BatteryBoost ? 0.5 : 1.0;
        long graceMs = (long)(_s.GraceSec * 1000 * factor);
        long dormantMs = (long)(_s.DormantMin * 60_000 * factor);

        foreach (var g in _groups.Values)
        {
            if (g.Procs.Count == 0) { g.State = ""; continue; }
            if (g.Name == "") Describe(g);
            if (!fgOnly) UpdateBusy(g, ms);
            string prev = g.State;
            Decide(g, now, graceMs, dormantMs);
            if (prev != g.State) OnTransition(g, prev);
            int level = g.State switch { "calm" => 1, "sleep" => 2, _ => 0 };
            foreach (var p in g.Procs) Apply(p, level);
        }

        if (!fgOnly && !Paused) TrimIfShort(now);

        if (_journalDirty) SaveJournal();
        if (_statsDirty && ms - _lastStatsSave > 60_000) { _s.Save(); _statsDirty = false; _lastStatsSave = ms; }

        int sc = 0, ss = 0;
        foreach (var g in _groups.Values) { if (g.State == "calm") sc++; else if (g.State == "sleep") ss++; }
        _sumCalm = sc; _sumSleep = ss;

        if (UiAttached) Snapshot?.Invoke(BuildSnapshot(now));
    }

    // ======================================================================
    // decisions

    void UpdateBusy(Group g, long ms)
    {
        g.Samples.Enqueue((ms, g.Cpu, g.Gpu));
        while (g.Samples.Count > 0 && ms - g.Samples.Peek().t > 30_000) g.Samples.Dequeue();
        double c10 = 0, g10 = 0, c30 = 0, g30 = 0; int n10 = 0, n30 = 0;
        foreach (var s in g.Samples)
        {
            c30 += s.cpu; g30 += s.gpu; n30++;
            if (ms - s.t <= 10_000) { c10 += s.cpu; g10 += s.gpu; n10++; }
        }
        if (n10 == 0) return;
        c10 /= n10; g10 /= n10; c30 /= n30; g30 /= n30;
        g.Cpu30 = g.Samples.Count >= 10 ? c30 : 100;
        // need a few seconds of history before calling something busy
        bool enough = g.Samples.Count >= 4;
        if (!g.Busy && enough && (c10 > _s.BusyCpuPct || g10 > _s.BusyGpuPct)) g.Busy = true;
        else if (g.Busy && c30 < _s.BusyCpuPct * 0.5 && g30 < _s.BusyGpuPct * 0.5) g.Busy = false;
        if (g.Busy)
        {
            bool gpu = g10 > _s.BusyGpuPct && g10 >= c10;
            g.BusyWhy = gpu ? "busy-gpu" : "busy-cpu";
            g.BusyPct = gpu ? g10 : c10;
        }
    }

    bool Managed(Group g) => !g.Guard && (g.HadWindow || g.PrivateMB >= ManagedWithoutWindowMB || g.SeenFg);

    void Decide(Group g, long now, long graceMs, long dormantMs)
    {
        _s.Rules.TryGetValue(g.Key, out var rule);
        g.Reason = "";
        g.Locked = false;

        if (g.Key == _fgKey) { g.SeenFg = true; g.LastActive = now; g.State = "fg"; return; }
        if (g.Guard) { g.State = "guard"; g.Reason = GuardReason.GetValueOrDefault(g.Key, "system"); return; }
        if (rule == "never") { g.State = "guard"; g.Reason = "rule-never"; return; }
        if (!Managed(g)) { g.State = ""; return; }
        if (Paused) { g.State = "off"; return; }
        if (!_admin && !CanTouch(g.RootPid)) { g.State = "locked"; g.Locked = true; return; }
        if (rule == "free") { g.State = "work"; g.Reason = "rule-free"; g.LastActive = now; return; }
        if (rule != "calm")
        {
            if (g.Audio && _s.RespectAudio) { g.State = "work"; g.Reason = "audio"; g.LastActive = now; return; }
            if (g.Busy) { g.State = "work"; g.Reason = g.BusyWhy; g.LastActive = now; return; }
        }
        long idle = now - g.LastActive;
        if (idle < graceMs) { g.State = "wait"; g.Reason = ((graceMs - idle + 999) / 1000).ToString(); return; }
        g.State = idle >= dormantMs ? "sleep" : "calm";
    }

    void OnTransition(Group g, string prev)
    {
        switch (g.State)
        {
            case "calm" when prev == "wait" || prev == "work": Log("calm", "calmed", g.Name); break;
            case "sleep": Log("calm", "slept", g.Name, _s.DormantMin); break;
            case "work" when prev is "calm" or "sleep" or "wait":
                Log("work", g.Audio && _s.RespectAudio ? "audio" : "working", g.Name); break;
            case "fg" when prev == "sleep" && g.TrimmedMB > 0: Log("info", "back", g.Name); break;
        }
        if (g.State == "fg") g.TrimmedMB = 0;
    }

    // ======================================================================
    // acting on processes

    bool CanTouch(int pid)
    {
        if (pid == 0) return false;
        IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        Native.CloseHandle(h);
        return true;
    }

    void Apply(ProcRow p, int level)
    {
        _tweaks.TryGetValue(p.Pid, out var t);
        if (t != null && t.Create != p.CreateTime) { _tweaks.Remove(p.Pid); t = null; }
        if ((t?.Level ?? 0) == level && !(level == 0 && t == null && _inherited.ContainsKey(p.Pid))) return;
        if (p.Pid == _selfPid) return;

        IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h == IntPtr.Zero) return;
        try
        {
            bool inherited = _inherited.TryGetValue(p.Pid, out long ic) && ic == p.CreateTime;
            if (level == 0)
            {
                if (t != null) { Restore(h, t); _tweaks.Remove(p.Pid); _journalDirty = true; }
                else if (inherited)
                {
                    Restore(h, new Tweak { Prio = Native.NORMAL_PRIORITY_CLASS, Mem = Native.MEMORY_PRIORITY_NORMAL });
                    _inherited.Remove(p.Pid);
                }
                return;
            }
            if (t == null && inherited)
            {
                // its "original" is what it would have had if its parent hadn't been calmed
                t = new Tweak { Create = p.CreateTime, Prio = Native.NORMAL_PRIORITY_CLASS, Mem = Native.MEMORY_PRIORITY_NORMAL };
                _inherited.Remove(p.Pid);
            }
            if (t == null)
            {
                t = new Tweak { Create = p.CreateTime, Prio = Native.GetPriorityClass(h) };
                var mp = new Native.MEMORY_PRIORITY_INFORMATION();
                t.Mem = Native.GetProcessInformation(h, Native.ProcessMemoryPriority, ref mp, 4) ? mp.MemoryPriority : Native.MEMORY_PRIORITY_NORMAL;
                var pw = new Native.PROCESS_POWER_THROTTLING_STATE { Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
                if (Native.GetProcessInformation(h, Native.ProcessPowerThrottling, ref pw, 12)) { t.PowerControl = pw.ControlMask; t.PowerState = pw.StateMask; }
                if (t.Prio == 0) return; // couldn't even read it: leave the process alone
            }

            int s = _s.Level;
            uint wantPrio = level == 1
                ? (s == 0 ? t.Prio : s == 1 ? Native.BELOW_NORMAL_PRIORITY_CLASS : Native.IDLE_PRIORITY_CLASS)
                : (s == 0 ? Native.BELOW_NORMAL_PRIORITY_CLASS : Native.IDLE_PRIORITY_CLASS);
            uint wantMem = level == 1
                ? (s == 0 ? t.Mem : s == 1 ? Native.MEMORY_PRIORITY_LOW : Native.MEMORY_PRIORITY_VERY_LOW)
                : (s == 0 ? Native.MEMORY_PRIORITY_LOW : Native.MEMORY_PRIORITY_VERY_LOW);

            // only ever lower. An app that raised itself (audio, games) keeps its own priority.
            if (Rank(t.Prio) <= Rank(Native.NORMAL_PRIORITY_CLASS) && Rank(wantPrio) < Rank(t.Prio))
                Native.SetPriorityClass(h, wantPrio);
            else if (Rank(t.Prio) <= Rank(Native.NORMAL_PRIORITY_CLASS))
                Native.SetPriorityClass(h, t.Prio);

            var m = new Native.MEMORY_PRIORITY_INFORMATION { MemoryPriority = Math.Min(wantMem, t.Mem) };
            Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref m, 4);

            var eco = new Native.PROCESS_POWER_THROTTLING_STATE
            {
                Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            };
            Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref eco, 12);

            t.Level = level;
            _tweaks[p.Pid] = t;
            _journalDirty = true;
        }
        finally { Native.CloseHandle(h); }
    }

    static int Rank(uint cls) => cls switch
    {
        Native.IDLE_PRIORITY_CLASS => 0,
        Native.BELOW_NORMAL_PRIORITY_CLASS => 1,
        Native.NORMAL_PRIORITY_CLASS => 2,
        Native.ABOVE_NORMAL_PRIORITY_CLASS => 3,
        Native.HIGH_PRIORITY_CLASS => 4,
        Native.REALTIME_PRIORITY_CLASS => 5,
        _ => 2,
    };

    static void Restore(IntPtr h, Tweak t)
    {
        Native.SetPriorityClass(h, t.Prio);
        var m = new Native.MEMORY_PRIORITY_INFORMATION { MemoryPriority = t.Mem };
        Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref m, 4);
        var pw = new Native.PROCESS_POWER_THROTTLING_STATE
        {
            Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = t.PowerControl,
            StateMask = t.PowerState,
        };
        Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref pw, 12);
    }

    public void RestoreAll()
    {
        foreach (var (pid, t) in _tweaks.ToList())
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) continue;
            // make sure the pid still belongs to the process we changed
            if (_byPid.TryGetValue(pid, out var r) && r.CreateTime == t.Create) Restore(h, t);
            Native.CloseHandle(h);
        }
        _tweaks.Clear();
        foreach (var g in _groups.Values) if (g.State is "calm" or "sleep") g.State = "off";
        SaveJournal();
    }

    // ---------------- memory ----------------
    void TrimIfShort(long now)
    {
        double pct = _availMB / _totalMB * 100;
        if (pct >= _s.TrimBelowPct) return;
        bool emergency = pct < EmergencyPct;
        var pick = _groups.Values
            .Where(g => g.State == "sleep" || (emergency && g.State == "calm"))
            // only quiet apps: trimming one that is still working just makes it fault the pages straight back
            .Where(g => g.Cpu30 < 1.0 && (g.State == "sleep" || now - g.LastActive > 120_000))
            .Where(g => g.PrivateMB >= MinTrimMB && now - g.LastTrim > (emergency ? 5 : RetrimAfterMin) * 60_000)
            .OrderByDescending(g => g.PrivateMB)
            .FirstOrDefault();
        if (pick == null) return;
        double freed = Trim(pick, now);
        if (freed >= 1)
            Log("trim", "trim", pick.Name, freed, pct.ToString("0"));
    }

    double Trim(Group g, long now)
    {
        double before = g.PrivateMB;
        foreach (var p in g.Procs)
        {
            if (p.Pid == _selfPid) continue;
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
            if (h == IntPtr.Zero) continue;
            Native.SetProcessWorkingSetSizeEx(h, (IntPtr)(-1), (IntPtr)(-1), 0);
            Native.CloseHandle(h);
        }
        g.LastTrim = now;
        // measure what actually left RAM
        var pids = g.Procs.Select(p => (p.Pid, p.CreateTime)).ToHashSet();
        double after = ProcessTable.Read().Where(r => pids.Contains((r.Pid, r.CreateTime))).Sum(r => r.PrivateBytes) / 1048576.0;
        double freed = Math.Max(0, before - after);
        g.PrivateMB = after;
        g.TrimmedMB += freed;
        if (freed >= 1)
        {
            _trims.Add((now, freed));
            while (_trims.Count > 0 && now - _trims[0].t > 600_000) _trims.RemoveAt(0);
            RollStatsDay();
            _s.ReclaimedTodayMB += freed;
            _statsDirty = true;
        }
        return freed;
    }

    void RollStatsDay()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_s.StatsDay != today) { _s.StatsDay = today; _s.ReclaimedTodayMB = 0; _statsDirty = true; }
    }

    // ======================================================================
    // commands from the UI / tray (run on the engine thread via Post)

    public void SetMode(string mode)
    {
        if (!Settings.Presets.ContainsKey(mode)) return;
        _s.ApplyPreset(mode);
        _s.Save();
        ReapplyAll();
        Log("info", "mode", "", 0, mode);
        ModeChanged?.Invoke();
    }

    public void Pause(int minutes)
    {
        Paused = true;
        _pausedUntil = minutes > 0 ? UnixMs + minutes * 60_000L : 0;
        RestoreAll();
        Log("info", "paused", "", minutes);
        ModeChanged?.Invoke();
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        _pausedUntil = 0;
        long now = UnixMs;
        foreach (var g in _groups.Values) g.LastActive = Math.Max(g.LastActive, now - _s.GraceSec * 1000L + 5000); // short grace, don't slam everything at once
        Log("info", "resumed");
        ModeChanged?.Invoke();
    }

    public void SetRule(string key, string rule)
    {
        if (rule == "auto") _s.Rules.Remove(key); else _s.Rules[key] = rule;
        _s.Save();
        string name = _groups.TryGetValue(key, out var g) ? g.Name : key;
        Log("info", "rule", name, 0, rule);
    }

    public void TrimNow(string key)
    {
        if (!_groups.TryGetValue(key, out var g) || g.State is "fg" or "guard" or "locked") return;
        double freed = Trim(g, UnixMs);
        if (freed >= 1) Log("trim", "trimManual", g.Name, freed);
        Toast?.Invoke(JsonSerializer.Serialize(new { type = "toast", code = freed >= 1 ? "trimmed" : "nothing", app = g.Name, num = Math.Round(freed) }));
    }

    public void SetNumber(string key, int value)
    {
        switch (key)
        {
            case "graceSec": _s.GraceSec = value; break;
            case "dormantMin": _s.DormantMin = value; break;
            case "trimBelowPct": _s.TrimBelowPct = value; break;
            case "busyCpuPct": _s.BusyCpuPct = value; break;
            case "busyGpuPct": _s.BusyGpuPct = value; break;
            default: return;
        }
        _s.Clamp();
        _s.Mode = "custom";
        _s.Save();
        ModeChanged?.Invoke();
    }

    public void SetFlag(string key, bool value)
    {
        switch (key)
        {
            case "respectAudio": _s.RespectAudio = value; break;
            case "batteryBoost": _s.BatteryBoost = value; break;
            case "langArabic": _s.Lang = value ? "ar" : "en"; _s.Save(); ModeChanged?.Invoke(); return;
            default: return;
        }
        _s.Save();
    }

    /// <summary>Level changed: re-apply calm/sleep with the new strength.</summary>
    void ReapplyAll()
    {
        foreach (var (pid, t) in _tweaks) t.Level = -1; // forces Apply to run again for every tweaked process
    }

    public void SendIcons() => _iconsSent.Clear();

    // ======================================================================
    // helpers: grouping, naming, foreground

    string KeyFor(ProcRow r)
    {
        // Walk up to the app that owns this process: Chrome's renderers fold into Chrome,
        // Photoshop's helpers into Photoshop. A process with its own window is an app of its
        // own even if a terminal or launcher started it.
        var cur = r;
        if (!ShellHosts.Contains(r.Name))
        {
            for (int depth = 0; depth < 24; depth++)
            {
                if (!_byPid.TryGetValue(cur.ParentPid, out var parent)) break;
                if (parent.Pid == cur.Pid || parent.CreateTime > cur.CreateTime) break;   // pid reused
                if (parent.SessionId != r.SessionId || ShellHosts.Contains(parent.Name)) break;
                if (parent.Pid == _selfPid) break;
                if (_windowEver.ContainsKey(cur.Pid)) break;
                cur = parent;
            }
        }
        return cur.Name;
    }

    HashSet<int> Descendants(int root)
    {
        var set = new HashSet<int> { root };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var r in _byPid.Values)
                if (!set.Contains(r.Pid) && set.Contains(r.ParentPid) && _byPid.TryGetValue(r.ParentPid, out var p) && p.CreateTime <= r.CreateTime)
                { set.Add(r.Pid); grew = true; }
        }
        return set;
    }

    static bool IsGuarded(string key) =>
        Guarded.Contains(key) || GuardedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    void Describe(Group g)
    {
        var root = g.Procs.FirstOrDefault(p => p.Name == g.Key) ?? g.Procs[0];
        g.RootPid = root.Pid;
        g.Name = g.Key;
        try
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, root.Pid);
            if (h != IntPtr.Zero)
            {
                var buf = new char[1024]; int len = buf.Length;
                if (Native.QueryFullProcessImageName(h, 0, buf, ref len)) g.Path = new string(buf, 0, len);
                Native.CloseHandle(h);
            }
            if (g.Path != null)
            {
                var vi = FileVersionInfo.GetVersionInfo(g.Path);
                string? d = vi.FileDescription?.Trim();
                if (string.IsNullOrWhiteSpace(d) || d.Length > 40) d = vi.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(d) && d.Length <= 40) g.Name = d;
                else g.Name = char.ToUpper(g.Key[0]) + g.Key[1..];
            }
            else g.Name = char.ToUpper(g.Key[0]) + g.Key[1..];
        }
        catch { }
    }

    string? IconFor(Group g)
    {
        if (_icons.TryGetValue(g.Key, out var s)) return s;
        string? uri = null;
        try
        {
            if (g.Path != null)
            {
                using var ico = Icon.ExtractAssociatedIcon(g.Path);
                if (ico != null)
                {
                    using var bmp = new Bitmap(ico.ToBitmap(), 32, 32);
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    uri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch { }
        _icons[g.Key] = uri ?? "";
        return uri;
    }

    int ForegroundPid()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        Native.GetWindowThreadProcessId(hwnd, out int pid);
        if (_byPid.TryGetValue(pid, out var r) && r.Name == "applicationframehost")
        {
            // UWP apps: the real process owns a child window of the frame host
            int real = 0;
            Native.EnumChildWindows(hwnd, (c, _) =>
            {
                Native.GetWindowThreadProcessId(c, out int cp);
                if (cp != pid) { real = cp; return false; }
                return true;
            }, IntPtr.Zero);
            if (real != 0) pid = real;
        }
        return pid;
    }

    string ForegroundKey(int pid)
    {
        if (pid == 0) return _fgKey;
        if (_self.Contains(pid)) return "sakin";
        return _byPid.TryGetValue(pid, out var row) ? KeyFor(row) : _fgKey;
    }

    static HashSet<int> VisibleWindowPids()
    {
        var set = new HashSet<int>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if (((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.GetWindowTextLength(h) == 0) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(h, out int pid);
            set.Add(pid);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    void ReadMemory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref m);
        _totalMB = m.ullTotalPhys / 1048576.0;
        _availMB = m.ullAvailPhys / 1048576.0;
    }

    void ReadCpuTotal()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return;
        long total = kernel + user; // kernel time includes idle
        if (_prevSysTotal > 0 && total > _prevSysTotal)
            _cpuTotal = Math.Clamp(100.0 * (1 - (double)(idle - _prevSysIdle) / (total - _prevSysTotal)), 0, 100);
        _prevSysIdle = idle; _prevSysTotal = total;
    }

    // ======================================================================
    // journal: survives a crash so nothing stays throttled forever

    static string JournalPath => Path.Combine(Settings.Dir, "journal.json");

    void SaveJournal()
    {
        _journalDirty = false;
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            if (_tweaks.Count == 0) { if (File.Exists(JournalPath)) File.Delete(JournalPath); return; }
            var list = _tweaks.Select(kv => new long[] { kv.Key, kv.Value.Create, kv.Value.Prio, kv.Value.Mem, kv.Value.PowerControl, kv.Value.PowerState }).ToList();
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(list));
        }
        catch { }
    }

    void RestoreFromJournal()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            var list = JsonSerializer.Deserialize<List<long[]>>(File.ReadAllText(JournalPath)) ?? new();
            var live = ProcessTable.Read().ToDictionary(r => r.Pid);
            int n = 0;
            foreach (var e in list)
            {
                int pid = (int)e[0];
                if (!live.TryGetValue(pid, out var r) || r.CreateTime != e[1]) continue;
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                Restore(h, new Tweak { Prio = (uint)e[2], Mem = (uint)e[3], PowerControl = (uint)e[4], PowerState = (uint)e[5] });
                Native.CloseHandle(h);
                n++;
            }
            File.Delete(JournalPath);
            if (n > 0) Log("info", "recovered", "", n);
        }
        catch { }
    }

    // ======================================================================
    // snapshot for the UI


    /// <summary>Log entries are language-neutral codes; the UI words them in English or Arabic.</summary>
    void Log(string kind, string code, string app = "", double num = 0, string extra = "")
    {
        _log.Insert(0, (++_logId, UnixMs, kind, code, app, num, extra));
        if (_log.Count > 120) _log.RemoveAt(_log.Count - 1);
    }

    string BuildSnapshot(long now)
    {
        RollStatsDay();
        var shown = _groups.Values.Where(g => g.State != "" && (g.State != "guard" || g.HadWindow || _s.Rules.ContainsKey(g.Key))).ToList();

        var newIcons = new Dictionary<string, string>();
        foreach (var g in shown)
            if (!_iconsSent.Contains(g.Key)) { var ic = IconFor(g); if (!string.IsNullOrEmpty(ic)) newIcons[g.Key] = ic; _iconsSent.Add(g.Key); }
        if (newIcons.Count > 0) Snapshot?.Invoke(JsonSerializer.Serialize(new { type = "icons", map = newIcons }));

        var by = new Dictionary<string, double> { ["fg"] = 0, ["work"] = 0, ["calm"] = 0, ["sleep"] = 0 };
        foreach (var g in _groups.Values)
        {
            string b = g.State switch { "fg" or "wait" => "fg", "work" => "work", "calm" => "calm", "sleep" => "sleep", _ => "" };
            if (b != "") by[b] += g.PrivateMB;
        }
        double used = _totalMB - _availMB;
        by["other"] = Math.Max(0, used - by["fg"] - by["work"] - by["calm"] - by["sleep"]);

        var apps = shown.Select(g => new
        {
            key = g.Key,
            name = g.Name,
            state = g.State,
            ramMB = Math.Round(g.PrivateMB),
            cpu = Math.Round(g.Cpu, 1),
            rule = _s.Rules.GetValueOrDefault(g.Key, "auto"),
            reason = g.State == "wait" ? "" : g.Reason,
            reasonPct = g.State == "work" && g.Reason.StartsWith("busy") ? Math.Round(g.BusyPct) : 0,
            waitSec = g.State == "wait" && int.TryParse(g.Reason, out int w) ? w : 0,
            idleSec = Math.Max(0, (now - g.LastActive) / 1000),
            trimmedMB = Math.Round(g.TrimmedMB),
        }).ToList();

        var snap = new
        {
            type = "state",
            now,
            version = Application.ProductVersion.Split('+')[0],
            admin = _admin,
            selfMB = _byPid.TryGetValue(_selfPid, out var meRow) ? meRow.PrivateBytes / 1048576.0 : 0,
            mode = _s.Mode,
            paused = Paused,
            pausedUntil = _pausedUntil == 0 ? (long?)null : _pausedUntil,
            settings = new
            {
                graceSec = _s.GraceSec, dormantMin = _s.DormantMin, trimBelowPct = _s.TrimBelowPct,
                busyCpuPct = _s.BusyCpuPct, busyGpuPct = _s.BusyGpuPct,
                startWithWindows = _s.StartWithWindows, elevated = _s.Elevated,
                respectAudio = _s.RespectAudio, batteryBoost = _s.BatteryBoost, lang = _s.Lang,
            },
            mem = new { totalMB = _totalMB, availMB = _availMB, byState = by },
            cpu = _cpuTotal,
            stats = new { reclaimedTodayMB = _s.ReclaimedTodayMB },
            history = _history.Select(h => new { t = h.t, mb = h.mb }),
            trims = _trims.Select(t => new { t = t.t, mb = t.mb }),
            log = _log.Take(40).Select(l => new { id = l.id, t = l.t, kind = l.kind, code = l.code, app = l.app, num = Math.Round(l.num), extra = l.extra }),
            apps,
        };
        return JsonSerializer.Serialize(snap);
    }

    // ---------------- tray summary ----------------
    public (int calm, int sleep, double availMB, double totalMB) Summary()
    {
        return (_sumCalm, _sumSleep, _availMB, _totalMB);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(3000);
        RestoreAll();
        _s.Save();
        _gpu.Dispose();
    }
}
