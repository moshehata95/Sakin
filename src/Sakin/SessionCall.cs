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
