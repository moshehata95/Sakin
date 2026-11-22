using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Sakin;

/// <summary>
/// The Session Call folder: where the owner opens the one Claude session that never sleeps and that passes Sakin's
/// questions on. Sakin makes it (with its CLAUDE.md) on install and at every start, and keeps the file current
/// with each new version unless the owner has edited it.
/// </summary>
internal static class SessionCall
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Session Call");

    public static string Dir(Settings s) => string.IsNullOrWhiteSpace(s.ClaudeSessionCallDir) ? DefaultDir : s.ClaudeSessionCallDir;

    static string Template()
    {
        using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("session-call/CLAUDE.md");
        if (st == null) return "";
        using var r = new StreamReader(st, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Make sure the folder and its CLAUDE.md exist; returns true if settings changed.</summary>
    public static bool Ensure(Settings s)
    {
        try
        {
            string text = Template();
            if (text == "") return false;
            string dir = Dir(s);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "CLAUDE.md");
            string newHash = Hash(text);
            bool changed = false;
            if (string.IsNullOrWhiteSpace(s.ClaudeSessionCallDir)) { s.ClaudeSessionCallDir = dir; changed = true; }
            if (!File.Exists(file))
            {
                File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            else
            {
                string cur = Hash(File.ReadAllText(file, Encoding.UTF8));
                // only replace a file Sakin wrote and nobody changed since
                if (cur != newHash && cur == s.ClaudeSessionCallHash) File.WriteAllText(file, text, new UTF8Encoding(false));
                else if (cur != newHash) return changed;   // the owner's own version: leave it
            }
            if (s.ClaudeSessionCallHash != newHash) { s.ClaudeSessionCallHash = newHash; changed = true; }
            return changed;
        }
        catch { return false; }
    }

    static readonly string[] MenuKeys =
    {
        @"Software\Classes\Directory\shell\OpenInClaude",            // right-click on a folder
        @"Software\Classes\Directory\Background\shell\OpenInClaude", // right-click inside a folder
    };

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// "Open in Claude Code" on a folder's right-click menu: opens a new session on that folder in the Claude app
    /// (its claude://code/new?folder= link). Added only when the Claude app is installed; refreshed at every start,
    /// because the app's path, and so the icon, changes with each of its updates.
    /// </summary>
    public static void EnsureMenu()
    {
        try
        {
            string? cmd = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"claude\shell\open\command")?.GetValue("") as string;
            if (string.IsNullOrWhiteSpace(cmd)) return;   // no Claude app: nothing to open
            string icon = cmd.StartsWith('"') ? cmd[1..cmd.IndexOf('"', 1)] : cmd.Split(' ')[0];
            bool changed = false;
            for (int i = 0; i < MenuKeys.Length; i++)
            {
                string arg = i == 0 ? "%1" : "%V";
                string command = $"rundll32.exe url.dll,FileProtocolHandler \"claude://code/new?folder={arg}\"";
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(MenuKeys[i]);
                using var c = k.CreateSubKey("command");
                if (k.GetValue("") as string != "Open in Claude Code") { k.SetValue("", "Open in Claude Code"); changed = true; }
                if (k.GetValue("Icon") as string != icon) { k.SetValue("Icon", icon); changed = true; }
                if (c.GetValue("") as string != command) { c.SetValue("", command); changed = true; }
            }
            if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED: Explorer re-reads menus
        }
        catch { }
    }

    public static void RemoveMenu()
    {
        foreach (var key in MenuKeys) try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { }
        try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    public static void OpenFolder(Settings s)
    {
        try
        {
            Ensure(s);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Dir(s)}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
