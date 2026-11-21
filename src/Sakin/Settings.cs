using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sakin;

internal sealed class Settings
{
    public string Lang { get; set; } = "en";          // en | ar
    public string Mode { get; set; } = "balanced";   // light | balanced | strong | custom
    public int Level { get; set; } = 1;              // how hard calm/sleep act: 0 light, 1 balanced, 2 strong
    public int GraceSec { get; set; } = 30;
    public int DormantMin { get; set; } = 10;
    public int TrimBelowPct { get; set; } = 20;
    public int BusyCpuPct { get; set; } = 6;
    public int BusyGpuPct { get; set; } = 15;
    public bool StartWithWindows { get; set; } = true;
    public bool Elevated { get; set; } = false;
    public bool RespectAudio { get; set; } = true;
    public bool BatteryBoost { get; set; } = true;
    public bool TrayHintShown { get; set; } = false;
    public Dictionary<string, string> Rules { get; set; } = new();   // app key → free | calm | never

    // ---- Claude mode: the PC left running for Claude Code ----
    public bool ClaudeMode { get; set; } = false;
    public int ClaudeLingerMin { get; set; } = 5;       // an app Claude used stays awake this long after
    public int ClaudeScreenOffMin { get; set; } = 2;
    public bool ClaudeCool { get; set; } = true;        // CPU without turbo: cooler and quieter for days
    public List<string> ClaudeApps { get; set; } = new() { "chrome", "illustrator", "photoshop", "adobe premiere pro", "afterfx", "powerpnt", "winword", "excel" };
    public string ClaudeScheme { get; set; } = "";      // our power plan while Claude mode is on
    public string ClaudePrevScheme { get; set; } = "";  // the plan to go back to

    public string StatsDay { get; set; } = "";
    public double ReclaimedTodayMB { get; set; }

    public static readonly Dictionary<string, (int lvl, int grace, int dormant, int trim, int cpu, int gpu)> Presets = new()
    {
        ["light"] = (0, 60, 20, 10, 8, 20),
        ["balanced"] = (1, 30, 10, 20, 6, 15),
        ["strong"] = (2, 10, 3, 35, 10, 25),
    };

    public void ApplyPreset(string mode)
    {
        if (!Presets.TryGetValue(mode, out var p)) return;
        Mode = mode;
        Level = p.lvl; GraceSec = p.grace; DormantMin = p.dormant; TrimBelowPct = p.trim; BusyCpuPct = p.cpu; BusyGpuPct = p.gpu;
    }

    /// <summary>Clamp every number to a range where ساكن can't hurt the machine.</summary>
    public void Clamp()
    {
        GraceSec = Math.Clamp(GraceSec, 5, 600);
        DormantMin = Math.Clamp(DormantMin, 1, 120);
        TrimBelowPct = Math.Clamp(TrimBelowPct, 5, 60);
        BusyCpuPct = Math.Clamp(BusyCpuPct, 1, 50);
        BusyGpuPct = Math.Clamp(BusyGpuPct, 5, 90);
        Level = Math.Clamp(Level, 0, 2);
        ClaudeLingerMin = Math.Clamp(ClaudeLingerMin, 1, 60);
        ClaudeScreenOffMin = Math.Clamp(ClaudeScreenOffMin, 1, 60);
        ClaudeApps ??= new();
    }

    // ---------------- persistence ----------------
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sakin");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
                if (s != null) { s.Clamp(); return s; }
            }
        }
        catch { /* corrupt file → defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
