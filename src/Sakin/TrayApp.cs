using System.Runtime.InteropServices;

namespace Sakin;

internal sealed class TrayApp : ApplicationContext
{
    readonly Settings _s;
    readonly Engine _engine;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly Native.WinEventDelegate _hookProc;   // kept alive: the hook calls into it
    readonly IntPtr _hook;
    readonly Control _ui;                         // marshals engine events onto this thread
    MainWindow? _window;
    readonly HotkeyWindow _hotkey;
    bool? _lastPaused;
    bool _exiting;

    bool Ar => _s.Lang == "ar";
    string T(string en, string ar) => Ar ? ar : en;

    public TrayApp(Settings s, Engine engine, bool showWindow)
    {
        _s = s;
        _engine = engine;
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;

        _tray = new NotifyIcon { Visible = true, Text = "Sakin" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        // Ctrl+Alt+B boosts whatever app you were just using, even when it has stopped responding
        _hotkey = new HotkeyWindow(() => _engine.Post(() => _engine.ToggleBoost(null)));
        RefreshTray();
        BuildMenu();

        _engine.ModeChanged += () => _ui.BeginInvoke(() => { BuildMenu(); RefreshTray(); });
        _engine.BoostChanged += (name, on) => _ui.BeginInvoke(() =>
            _tray.ShowBalloonTip(2500, "Sakin", on
                ? T($"Boosting {name}. Everything else is calmed and its RAM freed.", $"بوست لـ {name}. كل الباقي اتهدّى ورامه اتفضّت.")
                : T($"Boost for {name} is off.", $"البوست لـ {name} وقف."), ToolTipIcon.None));
        _engine.ClaudeChanged += on => _ui.BeginInvoke(() =>
            _tray.ShowBalloonTip(3000, "Sakin", on
                ? T("Claude mode is on. Only Claude and the apps it uses stay awake; the PC won't sleep.", "Claude mode اشتغل. Claude والبرامج اللي بيستخدمها بس صاحيين، والجهاز مش هينام.")
                : T("Claude mode is off. Your power plan is back.", "Claude mode وقف، وخطة الطاقة بتاعتك رجعت."), ToolTipIcon.None));

        _hookProc = (_, _, _, _, _, _, _) => _engine.ForegroundChanged();
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => RefreshTray();
        _timer.Start();

        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Exit();
        if (showWindow) ShowWindow();
    }

    // ---------------- tray ----------------
    void RefreshTray()
    {
        if (_lastPaused != _engine.Paused)
        {
            _lastPaused = _engine.Paused;
            var old = _tray.Icon;
            int size = SystemInformation.SmallIconSize.Width;
            _tray.Icon = Icons.Tray(_engine.Paused, Math.Max(16, size));
            old?.Dispose();
        }
        var (calm, sleep, avail, _) = _engine.Summary();
        string? b = _engine.BoostName;
        string text = _engine.Paused
            ? T("Sakin · paused", "ساكن · واقف")
            : b != null ? T($"Sakin · boosting {b}", $"ساكن · بوست لـ {b}")
            : _engine.ClaudeOn ? T($"Sakin · Claude mode · Claude {(_engine.ClaudeBusy ? "working" : "quiet")} · {sleep} asleep",
                $"ساكن · Claude mode · Claude {(_engine.ClaudeBusy ? "شغال" : "هادي")} · {sleep} نايم")
            : T($"Sakin · {avail / 1024:0.0} GB free · {calm + sleep} calmed",
                $"ساكن · {avail / 1024:0.0} جيجا فاضية · {calm + sleep} هادية");
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    void BuildMenu()
    {
        var old = _tray.ContextMenuStrip;
        var menu = new ContextMenuStrip
        {
            RightToLeft = Ar ? RightToLeft.Yes : RightToLeft.No,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()),
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Padding(4),
        };

        var open = new ToolStripMenuItem(T("Open Sakin", "افتح ساكن"), null, (_, _) => ShowWindow()) { Font = new Font("Segoe UI Semibold", 9.5f) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());

        var boost = new ToolStripMenuItem { ShortcutKeyDisplayString = _hotkey.Registered ? "Ctrl+Alt+B" : "" };
        void SetBoostText()
        {
            string? on = _engine.BoostName;
            string last = _engine.LastAppName;
            boost.Text = on != null ? T($"Stop boosting {on}", $"وقّف البوست لـ {on}")
                : last != "" ? T($"Boost {last}", $"بوست لـ {last}") : T("Boost the app you're using", "بوست للبرنامج اللي شغال عليه");
            boost.Enabled = on != null || last != "";
        }
        SetBoostText();
        boost.Click += (_, _) => _engine.Post(() => _engine.ToggleBoost(null));
        menu.Opening += (_, _) => SetBoostText();
        menu.Items.Add(boost);

        bool claudeOn = _engine.ClaudeOn;
        var claude = new ToolStripMenuItem(T("Claude mode", "Claude mode")) { Checked = claudeOn, ToolTipText =
            T("Leave the PC running for Claude: only Claude and the apps it uses stay awake.", "سيب الجهاز شغال لـ Claude: هو والبرامج اللي بيستخدمها بس اللي صاحيين.") };
        claude.Click += (_, _) => _engine.Post(() => _engine.SetClaudeMode(!claudeOn));
        menu.Items.Add(claude);
        if (claudeOn)
            menu.Items.Add(new ToolStripMenuItem(T("Turn the screen off", "اطفي الشاشة"), null, (_, _) => Task.Delay(700).ContinueWith(_ => Native.ScreenOff())));
        menu.Items.Add(new ToolStripSeparator());

        var modes = new ToolStripMenuItem(T("Intensity", "الشدة"));
        foreach (var (key, en, ar) in new[] { ("light", "Light", "خفيف"), ("balanced", "Balanced", "متوازن"), ("strong", "Strong", "قوي") })
        {
            var it = new ToolStripMenuItem(T(en, ar), null, (_, _) => _engine.Post(() => _engine.SetMode(key))) { Checked = _s.Mode == key };
            modes.DropDownItems.Add(it);
        }
        menu.Items.Add(modes);

        if (_engine.Paused)
            menu.Items.Add(new ToolStripMenuItem(T("Resume", "شغّل ساكن"), null, (_, _) => _engine.Post(_engine.Resume)));
        else
        {
            var pause = new ToolStripMenuItem(T("Pause", "إيقاف مؤقت"));
            pause.DropDownItems.Add(T("15 minutes", "١٥ دقيقة"), null, (_, _) => _engine.Post(() => _engine.Pause(15)));
            pause.DropDownItems.Add(T("1 hour", "ساعة"), null, (_, _) => _engine.Post(() => _engine.Pause(60)));
            pause.DropDownItems.Add(T("Until I turn it back on", "لحد ما أشغّله تاني"), null, (_, _) => _engine.Post(() => _engine.Pause(0)));
            menu.Items.Add(pause);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(T("Quit and restore everything", "اقفل ساكن ورجّع كل حاجة"), null, (_, _) => Exit()));

        foreach (ToolStripItem it in menu.Items) if (it is ToolStripMenuItem mi) mi.DropDown.RightToLeft = menu.RightToLeft;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    /// <summary>A message-only window that receives the global hotkey.</summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        readonly Action _onHotkey;
        public bool Registered { get; }
        public HotkeyWindow(Action onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /* HWND_MESSAGE */ });
            Registered = Native.RegisterHotKey(Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.B);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == 1) _onHotkey();
            base.WndProc(ref m);
        }
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { Native.UnregisterHotKey(Handle, 1); DestroyHandle(); }
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        static readonly Color Paper = Color.FromArgb(0xF5, 0xF3, 0xEE);
        static readonly Color Hover = Color.FromArgb(0xE1, 0xDD, 0xD5);
        static readonly Color Rule = Color.FromArgb(0xD3, 0xCE, 0xC4);
        public override Color ToolStripDropDownBackground => Paper;
        public override Color ImageMarginGradientBegin => Paper;
        public override Color ImageMarginGradientMiddle => Paper;
        public override Color ImageMarginGradientEnd => Paper;
        public override Color MenuBorder => Rule;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Rule;
        public override Color SeparatorLight => Paper;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    // ---------------- window ----------------
    public void ShowFromAnyThread() => _ui.BeginInvoke(ShowWindow);
    public void ExitFromAnyThread() => _ui.BeginInvoke(Exit);

    public void ShowWindow()
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainWindow(_engine, _s, OnFlag);
            _window.FormClosed += (_, _) =>
            {
                _window = null;
                if (!_exiting && !_s.TrayHintShown)
                {
                    _s.TrayHintShown = true;
                    _s.Save();
                    _tray.ShowBalloonTip(4000, "Sakin", T("Still working next to the clock. Right-click the icon to pause or quit.",
                        "لسه شغال جنب الساعة. كليك يمين على الأيقونة عشان توقفه أو تقفله."), ToolTipIcon.None);
                }
                // hand the web engine's memory back right away
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Native.SetProcessWorkingSetSizeEx(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1), 0);
            };
            _window.Show();
        }
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    /// <summary>Start-with-Windows and full-permission switches. Runs on the UI thread because it may show UAC or relaunch.</summary>
    void OnFlag(string key, bool on)
    {
        if (key == "startWithWindows")
        {
            _s.StartWithWindows = on;
            if (_s.Elevated) Startup.InstallTask(on);
            else Startup.SetRunKey(on);
            _s.Save();
            return;
        }
        if (key == "elevated")
        {
            if (on)
            {
                if (!Startup.InstallTask(_s.StartWithWindows)) { _s.Elevated = false; _s.Save(); return; } // UAC declined
                _s.Elevated = true;
                Startup.SetRunKey(false);
                _s.Save();
                if (!Startup.IsAdmin && Startup.RunTask()) Exit(); // the elevated copy takes over
            }
            else
            {
                Startup.RemoveTask();
                _s.Elevated = false;
                Startup.SetRunKey(_s.StartWithWindows);
                _s.Save();
            }
        }
    }

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hotkey.Dispose();
        _window?.Close();
        _engine.Dispose();      // restores every process it changed
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
