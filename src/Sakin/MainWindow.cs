using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Sakin;

/// <summary>
/// The window is built only while it's open. Closing it disposes the browser view,
/// so ساكن in the tray costs a few MB instead of a whole web engine.
/// </summary>
internal sealed class MainWindow : Form
{
    const string Origin = "https://sakin.app/";
    readonly Engine _engine;
    readonly Settings _settings;
    readonly Action<string, bool> _onFlag;
    WebView2? _web;
    bool _ready;

    public MainWindow(Engine engine, Settings settings, Action<string, bool> onFlag)
    {
        _engine = engine;
        _settings = settings;
        _onFlag = onFlag;
        Text = "Sakin";
        Icon = Icons.Tray(false, 32);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        float k = DeviceDpi / 96f;                     // sizes below are in CSS pixels
        var work = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size(Math.Min((int)(1140 * k), work.Width - 40), Math.Min((int)(780 * k), work.Height - 40));
        MinimumSize = new Size((int)(900 * k), (int)(620 * k));
        bool dark = IsDark();
        BackColor = dark ? Color.FromArgb(0x16, 0x16, 0x14) : Color.FromArgb(0xEC, 0xE9, 0xE2);
    }

    static bool IsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // title bar in the same paper/ink colour as the page, so the window reads as one surface
        bool dark = IsDark();
        int on = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
        int cap = dark ? 0x00141616 : 0x00E2E9EC;          // COLORREF is 0x00BBGGRR
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_CAPTION_COLOR, ref cap, 4);
        int txt = dark ? 0x00DFE8EC : 0x001A1C1D;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_TEXT_COLOR, ref txt, 4);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);
        try
        {
            string data = Path.Combine(Settings.Dir, "WebView");
            var env = await CoreWebView2Environment.CreateAsync(null, data,
                new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --renderer-process-limit=1"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Sakin needs the Microsoft Edge WebView2 runtime to show this window. It's part of Windows 11; if it's missing, install it from microsoft.com/edge/webview2.\n\n" + ex.Message,
                "Sakin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }
        var core = _web.CoreWebView2;
        var st = core.Settings;
        st.AreDevToolsEnabled = false;
        st.AreDefaultContextMenusEnabled = false;
        st.IsZoomControlEnabled = false;
        st.IsStatusBarEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        st.IsPasswordAutosaveEnabled = false;
        st.IsGeneralAutofillEnabled = false;

        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Serve;
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith(Origin)) a.Cancel = true; };

        _engine.Snapshot += OnSnapshot;
        _engine.Toast += OnSnapshot;
        core.Navigate(Origin + "index.html");
    }

    void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path == "") path = "index.html";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + path);
        if (stream == null)
        {
            e.Response = _web!.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        string type = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".ttf" => "font/ttf",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        e.Response = _web!.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK",
            $"Content-Type: {type}\r\nCache-Control: no-store");
    }

    void OnSnapshot(string json)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_web?.CoreWebView2 != null && _ready) _web.CoreWebView2.PostWebMessageAsString(json);
            });
        }
        catch (InvalidOperationException) { }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(e.TryGetWebMessageAsString()).RootElement; }
        catch { return; }
        string type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        string Str(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        switch (type)
        {
            case "ready":
                _ready = true;
                _engine.SendIcons();
                _engine.UiAttached = true;
                _engine.Post(() => { });
                break;
            case "setMode": { string mode = Str("mode"); _engine.Post(() => _engine.SetMode(mode)); break; }
            case "pause": { int min = m.TryGetProperty("minutes", out var v) ? v.GetInt32() : 0; _engine.Post(() => _engine.Pause(min)); break; }
            case "resume": _engine.Post(_engine.Resume); break;
            case "setRule": { string k = Str("key"), r = Str("rule"); _engine.Post(() => _engine.SetRule(k, r)); break; }
            case "trimNow": { string k = Str("key"); _engine.Post(() => _engine.TrimNow(k)); break; }
            case "setSetting":
            {
                string key = Str("key");
                if (!m.TryGetProperty("value", out var v)) break;
                if (v.ValueKind == JsonValueKind.Number) { int n = v.GetInt32(); _engine.Post(() => _engine.SetNumber(key, n)); }
                else if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    bool b = v.GetBoolean();
                    if (key is "startWithWindows" or "elevated") _onFlag(key, b);   // needs the UI thread (UAC, relaunch)
                    else _engine.Post(() => _engine.SetFlag(key, b));
                }
                break;
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _engine.UiAttached = false;
        _engine.Snapshot -= OnSnapshot;
        _engine.Toast -= OnSnapshot;
        _web?.Dispose();
        _web = null;
        base.OnFormClosed(e);
    }
}
