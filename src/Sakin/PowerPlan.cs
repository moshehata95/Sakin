using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Sakin;

/// <summary>
/// Claude mode's power plan: a copy of Windows' Balanced plan that never sleeps, ignores the lid,
/// turns the screen off early and (in "cool" mode) runs the CPU without turbo. The plan you had before
/// is remembered in the settings and put back when Claude mode ends, when ساكن quits, or on uninstall.
/// Everything goes through powercfg, which works without administrator rights.
/// </summary>
internal static class PowerPlan
{
    const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    const string Name = "Sakin - Claude mode";
    static readonly Regex Guid = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    /// <summary>Switches to the Claude-mode plan. Returns null on success, or what went wrong.</summary>
    public static string? Enter(Settings s)
    {
        try
        {
            string? active = Active();
            if (active == null) return "no active plan";
            var schemes = List();
            bool ours = s.ClaudeScheme != "" && schemes.Contains(s.ClaudeScheme, StringComparer.OrdinalIgnoreCase);
            // after a crash we may already be on our own plan: keep the plan from before that
            if (!(ours && active.Equals(s.ClaudeScheme, StringComparison.OrdinalIgnoreCase))) s.ClaudePrevScheme = active;
            if (!ours)
            {
                string from = schemes.Contains(Balanced, StringComparer.OrdinalIgnoreCase) ? Balanced : active;
                var m = Guid.Match(Run("/duplicatescheme " + from));
                if (!m.Success) return "duplicate failed";
                s.ClaudeScheme = m.Value;
                Run($"/changename {s.ClaudeScheme} \"{Name}\" \"Made by Sakin while Claude mode is on. Removed when it ends.\"");
            }
            Apply(s);
            Run("/setactive " + s.ClaudeScheme);
            return string.Equals(Active(), s.ClaudeScheme, StringComparison.OrdinalIgnoreCase) ? null : "setactive failed";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Writes the current Claude-mode numbers into our plan (also while it is active).</summary>
    public static void Apply(Settings s)
    {
        if (s.ClaudeScheme == "") return;
        string g = s.ClaudeScheme;
        void Both(string sub, string setting, int v)
        {
            Run($"/setacvalueindex {g} {sub} {setting} {v}");
            Run($"/setdcvalueindex {g} {sub} {setting} {v}");
        }
        Both("SUB_SLEEP", "STANDBYIDLE", 0);              // never sleep
        Both("SUB_SLEEP", "HIBERNATEIDLE", 0);            // never hibernate
        Both("SUB_BUTTONS", "LIDACTION", 0);              // closing the lid does nothing
        Both("SUB_VIDEO", "VIDEOIDLE", s.ClaudeScreenOffMin * 60);
        Both("SUB_PROCESSOR", "PROCTHROTTLEMIN", 5);
        Both("SUB_PROCESSOR", "PROCTHROTTLEMAX", s.ClaudeCool ? 99 : 100);  // 99 % already keeps most laptops off turbo
        Both("SUB_PROCESSOR", "PERFBOOSTMODE", s.ClaudeCool ? 0 : 2);       // 0 = turbo off, 2 = aggressive (Balanced's own)
        Both("SUB_PROCESSOR", "PERFEPP", s.ClaudeCool ? 60 : 33);           // lean towards efficiency
        if (string.Equals(Active(), g, StringComparison.OrdinalIgnoreCase)) Run("/setactive " + g);  // re-read the changed values
    }

    /// <summary>Back to the plan from before Claude mode, and our copy deleted.</summary>
    public static void Leave(Settings s)
    {
        try
        {
            if (s.ClaudeScheme == "" && s.ClaudePrevScheme == "") return;
            var schemes = List();
            if (s.ClaudeScheme == "" || string.Equals(Active(), s.ClaudeScheme, StringComparison.OrdinalIgnoreCase))
            {
                // On Modern Standby laptops Windows hides High performance from the list while another plan is
                // active, yet still switches to it, so try the old plan first and only then fall back to Balanced.
                if (s.ClaudePrevScheme != "") Run("/setactive " + s.ClaudePrevScheme);
                if (s.ClaudePrevScheme == "" || !string.Equals(Active(), s.ClaudePrevScheme, StringComparison.OrdinalIgnoreCase)) Run("/setactive " + Balanced);
            }
            if (s.ClaudeScheme != "" && schemes.Contains(s.ClaudeScheme, StringComparer.OrdinalIgnoreCase)) Run("/delete " + s.ClaudeScheme);
            s.ClaudeScheme = "";
            s.ClaudePrevScheme = "";
            s.Save();
        }
        catch { }
    }

    public static string? Active()
    {
        var m = Guid.Match(Run("/getactivescheme"));
        return m.Success ? m.Value : null;
    }

    static List<string> List() => Guid.Matches(Run("/list")).Select(m => m.Value).ToList();

    static string Run(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return o;
    }
}
