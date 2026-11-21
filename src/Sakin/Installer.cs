using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace Sakin;

/// <summary>
/// Per-user install, no admin needed. Running Sakin.exe from anywhere else (Downloads, a build folder)
/// copies it to %LOCALAPPDATA%\Programs\Sakin, adds a Start menu shortcut and an entry in
/// Settings › Apps › Installed apps, then starts the installed copy. Running a newer exe the same way
/// updates it. `--portable` skips all of this.
/// </summary>
internal static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sakin";
    public const string RepoUrl = "https://github.com/moshehata95/Sakin";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Sakin");
    public static string Exe => Path.Combine(Dir, "Sakin.exe");
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Sakin.lnk");

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Installs (or updates) this exe and starts the installed copy. False → keep running from here.</summary>
    public static bool InstallAndRelaunch(string[] args, EventWaitHandle quitSignal)
    {
        try
        {
            // stop any running copy first; it restores every app it changed on the way out
            var others = Process.GetProcessesByName("Sakin").Where(p => p.Id != Environment.ProcessId).ToList();
            if (others.Count > 0)
            {
                quitSignal.Set();
                foreach (var p in others) { try { if (!p.WaitForExit(10_000)) p.Kill(); } catch { } }
                quitSignal.Reset();
            }

            Directory.CreateDirectory(Dir);
            string tmp = Exe + ".new";
            File.Copy(Environment.ProcessPath!, tmp, true);
            File.Move(tmp, Exe, true);

            CreateShortcut();
            Register();

            // the elevated scheduled task (if used) must point at the new location; this asks UAC once
            var s = Settings.Load();
            if (s.Elevated && Startup.TaskExists()) Startup.InstallTask(s.StartWithWindows, Exe);

            Process.Start(new ProcessStartInfo(Exe, string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
            {
                UseShellExecute = true,               // like a double-click, so the window opens normally
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = Dir,
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false; // couldn't install: just run where we are
        }
    }

    static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Shortcut })!;
            var t = lnk.GetType();
            void Set(string name, object value) => t.InvokeMember(name, BindingFlags.SetProperty, null, lnk, new[] { value });
            Set("TargetPath", Exe);
            Set("WorkingDirectory", Dir);
            Set("IconLocation", Exe + ",0");
            Set("Description", "Give your PC to the app in front of you");
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(lnk);
        }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
    }

    static void Register()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        string version = Application.ProductVersion.Split('+')[0];
        k.SetValue("DisplayName", "Sakin");
        k.SetValue("DisplayVersion", version);
        k.SetValue("Publisher", "Sakin contributors");
        k.SetValue("DisplayIcon", Exe + ",0");
        k.SetValue("InstallLocation", Dir);
        k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
        k.SetValue("URLInfoAbout", RepoUrl);
        k.SetValue("HelpLink", RepoUrl);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    /// <summary>Settings › Apps › Uninstall. Restores every app, removes startup, shortcut, entry, settings and the exe.</summary>
    public static int Uninstall(bool quiet, EventWaitHandle quitSignal)
    {
        bool ar = Settings.Load().Lang == "ar";
        if (!quiet)
        {
            var answer = MessageBox.Show(
                ar ? "شيل ساكن من الجهاز؟ كل البرامج هترجع لطبيعتها، والإعدادات هتتمسح." : "Remove Sakin from this PC? Every app goes back to normal and your settings are deleted.",
                "Sakin", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
                ar ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0);
            if (answer != DialogResult.Yes) return 1;
        }

        // 1. stop the running copy: it puts every process back the way it was
        quitSignal.Set();
        foreach (var p in Process.GetProcessesByName("Sakin").Where(p => p.Id != Environment.ProcessId))
            try { if (!p.WaitForExit(10_000)) p.Kill(); } catch { }
        // if it couldn't put the power plan back itself (crash, killed), do it here
        PowerPlan.Leave(Settings.Load());

        // 2. startup, shortcut, Installed-apps entry
        Startup.SetRunKey(false);
        if (Startup.TaskExists()) Startup.RemoveTask();
        try { File.Delete(Shortcut); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        // 3. settings, journal and the window's web cache
        try { Directory.Delete(Settings.Dir, true); } catch { }

        // 4. the exe can't delete itself while running: a hidden shell removes the folder after we exit
        string dir = Dir;
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        if (!quiet)
            MessageBox.Show(ar ? "ساكن اتشال. كل البرامج رجعت لطبيعتها." : "Sakin was removed. Every app is back to normal.", "Sakin",
                MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                ar ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0);
        return 0;
    }
}
