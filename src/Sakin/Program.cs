namespace Sakin;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);
        bool tray = args.Contains("--tray");
        using var quitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\Sakin.Quit");
        if (args.Contains("--quit")) { quitSignal.Set(); return 0; }   // clean exit of the running copy
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
                int which = WaitHandle.WaitAny(new WaitHandle[] { showSignal, quitSignal });
                if (which == 0) app.ShowFromAnyThread();
                else { app.ExitFromAnyThread(); return; }
            }
        }) { IsBackground = true, Name = "sakin-show" };
        listener.Start();

        Application.Run(app);
        mutex.ReleaseMutex();
        return 0;
    }
}
