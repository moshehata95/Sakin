namespace Sakin;

internal static class Program
{
    public const string DonateUrl = "https://paypal.me/DrMohamedShehata";

    /// <summary>Opens the donation page in the default browser. The address is fixed here; the page can't pass one.</summary>
    public static void OpenDonate()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DonateUrl) { UseShellExecute = true }); } catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);
        bool tray = args.Contains("--tray");
        using var quitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\Sakin.Quit");
        if (args.Contains("--quit")) { quitSignal.Set(); return 0; }   // clean exit of the running copy
        // Sakin.exe --answer <id> asked|keep|done|notmine [note]: Claude's reply to a question in claude-asks.json
        if (args.Contains("--answer")) return ClaudeAsks.AnswerFromCommandLine(args);
        using var claudeOnSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sakin.ClaudeOn");
        using var claudeOffSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sakin.ClaudeOff");
        // Sakin.exe --claude-mode on|off: lets a script (or Claude itself) switch Claude mode on the running copy
        int cm = Array.IndexOf(args, "--claude-mode");
        if (cm >= 0)
        {
            bool on = cm + 1 >= args.Length || args[cm + 1] != "off";
            (on ? claudeOnSignal : claudeOffSignal).Set();
            return 0;
        }
        if (args.Contains("--uninstall")) { ApplicationConfiguration.Initialize(); return Installer.Uninstall(args.Contains("--quiet"), quitSignal); }
        // run from anywhere else: install (or update) per-user and hand over to the installed copy
        if (!args.Contains("--portable") && !Installer.IsInstalledCopy && Installer.InstallAndRelaunch(args, quitSignal)) return 0;
        quitSignal.Reset();

        // One copy only. A second launch just brings the window up.
        using var mutex = new Mutex(false, @"Local\Sakin.Single");
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sakin.Show");
        bool owned;
        try { owned = mutex.WaitOne(args.Contains("--elevated-start") ? 10_000 : 1_500); }
        catch (AbandonedMutexException) { owned = true; } // previous copy crashed
        if (!owned)
        {
            if (!tray) showSignal.Set();
            return 0;
        }

        var settings = Settings.Load();
        bool firstRun = !File.Exists(Path.Combine(Settings.Dir, "settings.json"));
        bool admin = Startup.IsAdmin;

        // "Full permissions" is on but this copy isn't elevated: hand off to the task (no UAC prompt).
        if (settings.Elevated && !admin && Startup.RunTask())
        {
            mutex.ReleaseMutex();
            return 0;
        }

        // the Session Call folder ships with Sakin: made on the first start and kept current
        if (SessionCall.Ensure(settings) && !firstRun) settings.Save();
        SessionCall.EnsureMenu();   // "Open in Claude Code" on folders, when the Claude app is installed
        if (firstRun) settings.Save();
        // keep the startup entry pointing at this exe, wherever it has been moved to
        if (!settings.Elevated) Startup.SetRunKey(settings.StartWithWindows);
        else if (admin && settings.StartWithWindows) Startup.InstallTask(true);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var engine = new Engine(settings, admin);
        // whatever happens, put every process back the way it was
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { try { engine.RestoreAll(); } catch { } };
        Application.ThreadException += (_, e) => System.Diagnostics.Debug.WriteLine(e.Exception);
        engine.Start();

        var app = new TrayApp(settings, engine, showWindow: !tray);
        var listener = new Thread(() =>
        {
            while (true)
            {
                int which = WaitHandle.WaitAny(new WaitHandle[] { showSignal, quitSignal, claudeOnSignal, claudeOffSignal });
                if (which == 0) app.ShowFromAnyThread();
                else if (which == 2) engine.Post(() => engine.SetClaudeMode(true));
                else if (which == 3) engine.Post(() => engine.SetClaudeMode(false));
                else { app.ExitFromAnyThread(); return; }
            }
        }) { IsBackground = true, Name = "sakin-show" };
        listener.Start();

        Application.Run(app);
        mutex.ReleaseMutex();
        return 0;
    }
}
