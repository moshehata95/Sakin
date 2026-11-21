// Design-time stand-in for the engine. Only active when the page is opened
// in a normal browser; inside the app, window.chrome.webview exists instead.
(() => {
  if (window.chrome && window.chrome.webview) return;
  const listeners = [];
  const now = () => Date.now();
  const T0 = now();
  const total = 16076;
  let mode = "balanced", paused = false, pausedUntil = null, logId = 9;
  const settings = { graceSec: 30, dormantMin: 10, trimBelowPct: 20, busyCpuPct: 6, busyGpuPct: 15,
    startWithWindows: true, elevated: false, respectAudio: true, batteryBoost: true,
    lang: new URLSearchParams(location.search).get("lang") || "en", claudeLingerMin: 5, claudeScreenOffMin: 2, claudeCool: true, claudeQuiet: true, claudeReleaseScreen: true };
  let claudeOn = !!new URLSearchParams(location.search).get("claude");
  const apps = [
    { key: "chrome", name: "Google Chrome", state: "fg", ramMB: 1840, cpu: 4.2, rule: "auto" },
    { key: "premiere", name: "Adobe Premiere Pro", state: "work", ramMB: 3170, cpu: 38, rule: "free", reason: "rule-free" },
    { key: "ame", name: "Adobe Media Encoder", state: "work", ramMB: 980, cpu: 22, rule: "auto", reason: "busy-gpu", reasonPct: 61 },
    { key: "photoshop", name: "Adobe Photoshop", state: "sleep", ramMB: 1210, cpu: 0, rule: "auto", idleSec: 1460, trimmedMB: 1650 },
    { key: "claude", name: "Claude", state: "calm", ramMB: 1360, cpu: 1.1, rule: "auto", idleSec: 240 },
    { key: "illustrator", name: "Adobe Illustrator", state: "sleep", ramMB: 690, cpu: 0, rule: "auto", idleSec: 3900, trimmedMB: 820 },
    { key: "telegram", name: "Telegram", state: "calm", ramMB: 310, cpu: 0.2, rule: "auto", idleSec: 1100 },
    { key: "whatsapp", name: "WhatsApp", state: "work", ramMB: 420, cpu: 2.1, rule: "auto", reason: "audio" },
    { key: "code", name: "Visual Studio Code", state: "wait", ramMB: 780, cpu: 0.9, rule: "auto", waitSec: 18 },
    { key: "onedrive", name: "OneDrive", state: "calm", ramMB: 140, cpu: 0.1, rule: "calm", idleSec: 9000 },
    { key: "explorer", name: "Windows Explorer", state: "guard", ramMB: 210, cpu: 0.3, rule: "auto", reason: "shell" },
    { key: "obs", name: "OBS Studio", state: "locked", ramMB: 260, cpu: 0.6, rule: "auto" },
  ];
  const history = [];
  for (let i = 300; i >= 0; i--) {
    const base = 3100 + Math.sin(i / 23) * 500 - (i < 140 && i > 90 ? 900 : 0) + (i < 90 ? 600 : 0);
    history.push({ t: T0 - i * 2000, mb: base + Math.random() * 120 });
  }
  const trims = [{ t: T0 - 180000, mb: 1650 }, { t: T0 - 410000, mb: 820 }];
  const log = [
    { id: 9, t: T0 - 20000, kind: "calm", code: "calmed", app: "Claude" },
    { id: 8, t: T0 - 180000, kind: "trim", code: "trim", app: "Adobe Photoshop", num: 1650, extra: "18" },
    { id: 7, t: T0 - 300000, kind: "work", code: "working", app: "Adobe Media Encoder" },
    { id: 6, t: T0 - 410000, kind: "trim", code: "trim", app: "Adobe Illustrator", num: 820, extra: "19" },
    { id: 5, t: T0 - 900000, kind: "calm", code: "slept", app: "Adobe Photoshop", num: 10 },
    { id: 4, t: T0 - 1500000, kind: "calm", code: "calmed", app: "Telegram" },
    { id: 3, t: T0 - 2400000, kind: "work", code: "audio", app: "WhatsApp" },
    { id: 2, t: T0 - 3600000, kind: "info", code: "start", extra: "balanced", num: 0 },
  ];

  function snapshot() {
    const avail = history[history.length - 1].mb;
    const sum = (...st) => apps.filter((a) => st.includes(a.state)).reduce((s, a) => s + a.ramMB, 0);
    const byState = { fg: sum("fg"), work: sum("work"), calm: sum("calm", "wait"), sleep: sum("sleep") };
    byState.other = total - avail - byState.fg - byState.work - byState.calm - byState.sleep;
    return {
      type: "state", now: now(), version: "1.0.0", admin: false, selfMB: 38,
      mode, paused, pausedUntil, settings,
      mem: { totalMB: total, availMB: avail, byState },
      cpu: 14 + Math.random() * 6,
      stats: { reclaimedTodayMB: 6340 },
      history, trims, log,
      claude: claudeOn ? { since: T0, busy: true, quietSec: 0, cpu: 3.4, ramMB: 4210, asleep: 7, using_: 1, freedMB: 3870, present: false, plan: true } : null,
      apps: apps.map((a) => paused && !["fg", "guard"].includes(a.state) ? Object.assign({}, a, { state: "off" })
        : claudeOn ? claudeView(a) : a),
    };
  }
  function claudeView(a) {
    if (a.key === "claude") return Object.assign({}, a, { state: "claude", reason: "claude-self", ramMB: 3420, cpu: 3.4 });
    if (a.key === "chrome") return Object.assign({}, a, { state: "claude", reason: "claude-uses", lingerSec: 214, claudeApp: true });
    if (a.rule === "free" || ["guard", "locked"].includes(a.state)) return a;
    return Object.assign({}, a, { state: "sleep", reason: "claude-away", cpu: 0, ramMB: Math.round(a.ramMB * 0.15), trimmedMB: Math.round(a.ramMB * 0.85) });
  }
  function emit(msg) { const e = { data: JSON.stringify(msg) }; listeners.forEach((f) => f(e)); }
  function pushLog(kind, code, app = "", num = 0, extra = "") { log.unshift({ id: ++logId, t: now(), kind, code, app, num, extra }); }

  setInterval(() => {
    const last = history[history.length - 1];
    history.push({ t: now(), mb: Math.max(900, last.mb + (Math.random() - 0.5) * 140) });
    while (history.length && now() - history[0].t > 600000) history.shift();
    apps[1].cpu = 30 + Math.random() * 15;
    apps[0].cpu = 2 + Math.random() * 6;
    const w = apps.find((a) => a.key === "code");
    if (w.state === "wait") { w.waitSec--; if (w.waitSec <= 0) { w.state = "calm"; w.idleSec = 30; pushLog("calm", "calmed", w.name); } }
    apps.forEach((a) => { if (a.idleSec != null) a.idleSec++; });
    emit(snapshot());
  }, 1000);

  window.SakinMock = {
    addEventListener: (t, f) => listeners.push(f),
    postMessage: (raw) => {
      const m = JSON.parse(raw);
      if (m.type === "setMode") { mode = m.mode; pushLog("info", "mode", "", 0, m.mode); }
      if (m.type === "pause") { paused = true; pausedUntil = m.minutes ? now() + m.minutes * 60000 : null; pushLog("info", "paused", "", m.minutes); }
      if (m.type === "claude") { claudeOn = m.on; pushLog(m.on ? "claude" : "info", m.on ? "claudeOn" : "claudeOff"); }
      if (m.type === "resume") { paused = false; pausedUntil = null; pushLog("info", "resumed"); }
      if (m.type === "setSetting") {
        if (m.key === "langArabic") settings.lang = m.value ? "ar" : "en";
        else { settings[m.key] = m.value; if (typeof m.value === "number") mode = "custom"; }
      }
      if (m.type === "setRule") { const a = apps.find((x) => x.key === m.key); if (a) { a.rule = m.rule; pushLog("info", "rule", a.name, 0, m.rule); } }
      if (m.type === "trimNow") {
        const a = apps.find((x) => x.key === m.key);
        if (a) { const f = Math.round(a.ramMB * 0.7); a.ramMB -= f; pushLog("trim", "trimManual", a.name, f); emit({ type: "toast", code: "trimmed", app: a.name, num: f }); }
      }
      setTimeout(() => emit(snapshot()), 0);
    },
  };
})();
