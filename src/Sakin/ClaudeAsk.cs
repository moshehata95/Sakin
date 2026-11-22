using System.Diagnostics;
using System.Text.Json;

namespace Sakin;

/// <summary>
/// One question to Claude: "you opened this app and haven't used it for a while; do you still need it?"
/// Sakin never decides or closes anything itself. It writes the question to claude-asks.json; a Claude session
/// (the Session Call one) passes it to the session that opened the app, which answers with
/// <c>Sakin.exe --answer &lt;id&gt; keep|done</c> and, if the app isn't needed, saves the work and closes it itself.
/// </summary>
internal sealed class ClaudeAsk
{
    public string Id { get; set; } = "";         // app key + process start, stable for this instance of the app
    public string App { get; set; } = "";        // process name, e.g. "photoshop"
    public string Name { get; set; } = "";       // what people call it, e.g. "Adobe Photoshop 2026"
    public string Title { get; set; } = "";      // its main window title (often the open document)
    public string Exe { get; set; } = "";
    public int Pid { get; set; }
    public string OpenedAt { get; set; } = "";   // local time, ISO
    public string LastUsedByClaude { get; set; } = "";
    public int IdleMin { get; set; }
    public int MemoryMB { get; set; }            // committed (private) memory it holds
    public List<string> Sessions { get; set; } = new();   // Claude Code session ids that were working when it was opened or last used
    public string Status { get; set; } = "open"; // open → asked → keep | done
    public string AskedTo { get; set; } = "";
    public string AskedAt { get; set; } = "";
    public string Note { get; set; } = "";       // the session's reason, if it gave one
    public List<string> NotMine { get; set; } = new();   // sessions that said they didn't open it
    public long Until { get; set; }              // unix ms: when to ask again (asked / keep / done)
    public long Create { get; set; }             // process create time (FILETIME), to tell instances apart
}

internal static class ClaudeAsks
{
    public static string FilePath => Path.Combine(Settings.Dir, "claude-asks.json");
    static string AnswersPath => Path.Combine(Settings.Dir, "claude-answers.txt");
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void Save(List<ClaudeAsk> asks)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(asks, Pretty));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    static string UsedPath => Path.Combine(Settings.Dir, "claude-used.json");

    /// <summary>Which apps Claude used and when (unix ms), kept across restarts of Sakin so a question can still be asked.</summary>
    public static void SaveUsed(Dictionary<string, long> used)
    {
        try { File.WriteAllText(UsedPath, JsonSerializer.Serialize(used)); } catch { }
    }

    public static Dictionary<string, long> LoadUsed()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(UsedPath)) ?? new(); }
        catch { return new(); }
    }

    public static List<ClaudeAsk> Load()
    {
        try { return JsonSerializer.Deserialize<List<ClaudeAsk>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Sakin.exe --answer &lt;id&gt; asked|keep|done [words]: appended for the running copy to pick up.</summary>
    public static int AnswerFromCommandLine(string[] args)
    {
        int i = Array.IndexOf(args, "--answer");
        if (i < 0 || i + 2 >= args.Length) return 2;
        string id = args[i + 1], status = args[i + 2].ToLowerInvariant();
        if (status is not ("asked" or "keep" or "done" or "notmine")) return 2;
        string note = string.Join(' ', args.Skip(i + 3)).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(AnswersPath, $"{id}\t{status}\t{note}{Environment.NewLine}");
            return 0;
        }
        catch { return 1; }
    }

    /// <summary>Answers written since the last call, oldest first; the file is emptied.</summary>
    public static List<(string id, string status, string note)> TakeAnswers()
    {
        var list = new List<(string, string, string)>();
        try
        {
            if (!File.Exists(AnswersPath)) return list;
            string tmp = AnswersPath + ".taking";
            File.Move(AnswersPath, tmp, true);
            foreach (var line in File.ReadAllLines(tmp))
            {
                var p = line.Split('\t');
                if (p.Length >= 2 && p[0] != "") list.Add((p[0].Trim(), p[1].Trim(), p.Length > 2 ? p[2].Trim() : ""));
            }
            File.Delete(tmp);
        }
        catch { }
        return list;
    }
}

internal sealed partial class Engine
{
    List<ClaudeAsk> _asks = new();
    bool _asksLoaded;
    long _lastAskMs;
    readonly Dictionary<string, HashSet<string>> _usedBySessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember which sessions were working when Claude used an app: the likeliest owners.</summary>
    void NoteUsedBy(Group g, long now)
    {
        var ids = _sessions.Values.Where(s => !s.Keep && s.Id != "" && now - s.LastBusy < 90_000).Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        if (!_usedBySessions.TryGetValue(g.Key, out var set)) _usedBySessions[g.Key] = set = new();
        foreach (var id in ids) set.Add(id);
        if (set.Count > 6) { var keep = ids.ToHashSet(); set.RemoveWhere(x => !keep.Contains(x)); }
    }

    void UpdateAsks(long now, long ms)
    {
        if (ms - _lastAskMs < 15_000) return;
        _lastAskMs = ms;
        if (!_asksLoaded) { _asks = ClaudeAsks.Load(); _asksLoaded = true; }
        bool dirty = false;

        foreach (var (id, status, note) in ClaudeAsks.TakeAnswers())
        {
            var a = _asks.FirstOrDefault(x => x.Id == id);
            if (a == null) continue;
            if (status == "notmine")
            {
                string who = note != "" ? note : a.AskedTo;
                if (who != "" && !a.NotMine.Contains(who)) a.NotMine.Add(who);
                a.Status = "open"; a.AskedTo = ""; a.AskedAt = "";
                dirty = true;
                continue;
            }
            a.Status = status;
            if (note != "") { if (status == "asked") a.AskedTo = note; else a.Note = note; }
            if (status == "asked") a.AskedAt = DateTime.Now.ToString("s");
            a.Until = now + _s.ClaudeAskAgainMin * 60_000L;
            dirty = true;
            Log("claude", status == "keep" ? "askKeep" : status == "done" ? "askDone" : "askAsked", a.Name, 0, note);
        }

        long after = _s.ClaudeAskAfterMin * 60_000L;
        var liveIds = new HashSet<string>();
        foreach (var g in _groups.Values)
        {
            if (after <= 0 || g.Procs.Count == 0 || g.Guard || g.Key == "sakin" || g.HasClaude) continue;
            if (!_claudeUsedAt.TryGetValue(g.Key, out long used)) continue;           // Claude never touched it
            var root = g.Procs.FirstOrDefault(p => p.Pid == g.RootPid) ?? g.Procs[0];
            string id = $"{g.Key}-{root.CreateTime % 100_000_000}";
            var a = _asks.FirstOrDefault(x => x.Id == id);
            if (a != null) liveIds.Add(id);
            bool idle = g.State == "sleep" && now - used >= after && !(g.Key == _fgKey && _userPresent);
            if (!idle)
            {
                // Claude (or you) went back to it: the question is moot
                if (a != null && now - used < after) { _asks.Remove(a); liveIds.Remove(id); dirty = true; }
                continue;
            }
            if (a != null)
            {
                if (a.Status != "open" && now >= a.Until) { a.Status = "open"; a.AskedTo = ""; a.AskedAt = ""; dirty = true; }
                if (a.Status == "open") { a.IdleMin = (int)((now - used) / 60_000); }
                continue;
            }
            long commit = 0;
            foreach (var p in g.Procs)
                try { using var pr = Process.GetProcessById(p.Pid); commit += pr.PrivateMemorySize64; } catch { }
            int mb = (int)(commit / 1048576);
            if (mb < _s.ClaudeAskMinMB) continue;
            string title = "";
            try { using var pr = Process.GetProcessById(root.Pid); title = pr.MainWindowTitle; } catch { }
            a = new ClaudeAsk
            {
                Id = id, App = g.Key, Name = g.Name, Title = title, Exe = g.Path ?? "", Pid = root.Pid, Create = root.CreateTime,
                OpenedAt = DateTime.FromFileTimeUtc(root.CreateTime).ToLocalTime().ToString("s"),
                LastUsedByClaude = DateTimeOffset.FromUnixTimeMilliseconds(used).LocalDateTime.ToString("s"),
                IdleMin = (int)((now - used) / 60_000), MemoryMB = mb,
                Sessions = _usedBySessions.TryGetValue(g.Key, out var ss) ? ss.ToList() : new(),
            };
            _asks.Add(a);
            liveIds.Add(id);
            dirty = true;
            Log("claude", "askSent", g.Name, mb);
        }
        // apps that closed take their question with them
        if (_asks.RemoveAll(x => !liveIds.Contains(x.Id)) > 0) dirty = true;
        if (dirty) ClaudeAsks.Save(_asks);
        if (_usedDirty) { ClaudeAsks.SaveUsed(new Dictionary<string, long>(_claudeUsedAt)); _usedDirty = false; }
    }

    bool _usedDirty;

    /// <summary>On entering Claude mode: what Claude used in the last day, so a restart of Sakin doesn't forget it.</summary>
    void RestoreUsed(long now)
    {
        foreach (var (k, t) in ClaudeAsks.LoadUsed())
            if (now - t < 24 * 3_600_000L && t <= now) _claudeUsedAt[k] = t;
    }

    void ClearAsks()
    {
        _asks.Clear();
        _asksLoaded = true;
        _usedBySessions.Clear();
        ClaudeAsks.Save(_asks);
        ClaudeAsks.SaveUsed(new());
    }
}
