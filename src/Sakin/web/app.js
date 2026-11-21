// UI for Sakin. The engine pushes a full snapshot ("state") about once a second;
// the UI only renders it and sends small commands back.
(() => {
  "use strict";
  const $ = (s) => document.querySelector(s);
  const bridge = window.chrome && window.chrome.webview ? window.chrome.webview : window.SakinMock;
  const send = (type, data) => bridge.postMessage(JSON.stringify(Object.assign({ type }, data || {})));

  const ORDER = { boost: -1, fg: 0, claude: 0.5, work: 1, wait: 2, calm: 3, sleep: 4, locked: 5, guard: 6, off: 7 };
  const CLS = { boost: "st-boost", fg: "st-fg", claude: "st-claude", work: "st-work", wait: "st-wait", calm: "st-calm", sleep: "st-sleep", guard: "st-guard", locked: "st-locked", off: "st-wait" };
  const SEGS = ["fg", "work", "calm", "sleep", "other"];

  let S = null;
  let L = SakinI18n.en;
  let lang = "";
  let filter = "all";
  let menuKey = null;
  let lastLogId = 0;
  const icons = {};

  const gb = (mb, d = 1) => (mb / 1024).toFixed(d);
  const sizeHtml = (mb) => mb >= 1024 ? `${gb(mb)}<small>GB</small>` : `${Math.round(mb)}<small>MB</small>`;
  const clock = (t) => new Date(t).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
  const esc = (t) => String(t).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const rtl = () => lang === "ar";
  // English names inside Arabic text ("Claude", "Claude mode") are isolated so they don't flip the sentence around them
  const iso = (s) => rtl() && typeof s === "string" ? s.replace(/Claude( mode)?/g, (m) => "\u2068" + m + "\u2069") : s;

  // ---------- language ----------
  function applyLang(next) {
    if (next === lang) return;
    lang = next;
    L = SakinI18n[lang] || SakinI18n.en;
    document.documentElement.lang = lang;
    document.documentElement.dir = rtl() ? "rtl" : "ltr";
    document.title = L.brand;
    for (const el of document.querySelectorAll("[data-t]")) {
      const v = L[el.dataset.t];
      if (typeof v === "string") el.textContent = iso(v);
    }
    for (const el of document.querySelectorAll("[data-t-aria]")) el.setAttribute("aria-label", L[el.dataset.tAria]);
    $(".about").textContent = L.about;
    $("#ticks").dataset.n = "";
    lastLogId = -1;
    $("#rows").replaceChildren();
  }

  // ---------- gauge ----------
  function renderGauge() {
    const m = S.mem;
    const free = m.availMB;
    $("#freeNum").textContent = gb(free);
    $("#freeOf").textContent = L.of(gb(m.totalMB));

    const pct = free / m.totalMB * 100;
    const v = S.paused ? "paused" : pct >= 35 ? "easy" : pct >= S.settings.trimBelowPct ? "ok" : pct >= 8 ? "tight" : "full";
    $("#verdict").innerHTML = L.verdict[v];

    $("#tReclaimed").innerHTML = sizeHtml(S.stats.reclaimedTodayMB);
    $("#tCalm").textContent = S.apps.filter((a) => a.state === "calm" || a.state === "sleep").length;
    $("#tCpu").innerHTML = `${Math.round(S.cpu)}<small>%</small>`;

    const strip = $("#strip");
    if (!strip.children.length) {
      for (const k of SEGS) {
        const d = document.createElement("div");
        d.className = `seg ${k}`;
        d.dataset.k = k;
        d.style.width = "0%";
        strip.appendChild(d);
      }
    }
    for (const d of strip.children) {
      const mb = m.byState[d.dataset.k] || 0;
      d.style.width = `${(mb / m.totalMB) * 100}%`;
      d.title = `${L.seg[d.dataset.k]}: ${L.size(mb)}`;
    }

    // ticks follow the strip's reading direction: 0 sits where the strip starts
    const ticks = $("#ticks");
    const totalGb = Math.round(m.totalMB / 1024);
    if (ticks.dataset.n != totalGb) {
      ticks.dataset.n = totalGb;
      const step = totalGb > 24 ? 8 : 4;
      let h = "";
      for (let g = 0; g <= totalGb; g += step) {
        const p = (g * 1024 / m.totalMB) * 100;
        h += `<span style="left:${rtl() ? 100 - p : p}%">${g}</span>`;
      }
      ticks.innerHTML = h;
    }

    $("#legend").innerHTML = SEGS.map((k) =>
      `<li><i class="seg ${k}"></i>${L.seg[k]} <span class="v">${L.size(m.byState[k] || 0)}</span></li>`).join("") +
      `<li><i class="free-sw"></i>${L.seg.free} <span class="v">${L.size(free)}</span></li>`;

    renderSpark();
  }

  function renderSpark() {
    const h = S.history;
    const svg = $("#spark");
    svg.classList.toggle("mirror", rtl());
    if (!h.length) { svg.innerHTML = ""; return; }
    const W = 600, H = 44, now = S.now, span = 600_000;
    const max = S.mem.totalMB * 0.6;
    const x = (t) => W - ((now - t) / span) * W;
    const y = (mb) => H - Math.min(mb / max, 1) * (H - 2) - 1;
    let d = "";
    h.forEach((p, i) => { d += `${i ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.mb).toFixed(1)}`; });
    const area = `${d}L${x(h[h.length - 1].t).toFixed(1)},${H}L${x(h[0].t).toFixed(1)},${H}Z`;
    const floorY = y(S.mem.totalMB * S.settings.trimBelowPct / 100);
    const trims = S.trims.filter((t) => now - t.t < span)
      .map((t) => `<line class="trim" x1="${x(t.t)}" x2="${x(t.t)}" y1="${H}" y2="${H - 10}"/>`).join("");
    svg.innerHTML = `<line class="floor" x1="0" x2="${W}" y1="${floorY}" y2="${floorY}"/><path class="area" d="${area}"/><path class="line" d="${d}"/>${trims}`;
  }

  // ---------- apps ----------
  function why(a) {
    const w = L.why;
    switch (a.state) {
      case "boost": return w.boost(a.pinnedMB);
      case "fg": return w.fg;
      case "claude": return a.reason === "claude-uses" ? w["claude-uses"](L.ago(a.lingerSec)) : (w[a.reason] || "");
      case "work": return typeof w[a.reason] === "function" ? w[a.reason](a.reasonPct) : (w[a.reason] || "");
      case "wait": return w.wait(a.waitSec);
      case "calm": return w.calm(L.ago(a.idleSec));
      case "sleep": return a.reason === "claude-away" ? w["claude-away"](a.trimmedMB ? L.size(a.trimmedMB) : "") : w.sleep(L.ago(a.idleSec), a.trimmedMB ? L.size(a.trimmedMB) : "");
      case "guard": return w[a.reason] || w.system;
      case "locked": return w.locked;
      case "off": return w.off;
    }
    return "";
  }

  function renderApps() {
    let list = S.apps.slice();
    if (filter === "calm") list = list.filter((a) => a.state === "calm" || a.state === "sleep");
    else if (filter === "working") list = list.filter((a) => a.state === "work" || a.state === "fg" || a.state === "claude");
    else if (filter === "rules") list = list.filter((a) => a.rule && a.rule !== "auto");
    list.sort((a, b) => (ORDER[a.state] - ORDER[b.state]) || (b.ramMB - a.ramMB));

    $("#appCount").textContent = L.count(list.length);
    const empty = $("#empty");
    empty.hidden = list.length > 0;
    if (!list.length) empty.textContent = filter === "rules" ? L.emptyRules : L.emptyAll;

    const rows = $("#rows");
    const old = new Map([...rows.children].map((li) => [li.dataset.key, li]));
    const frag = document.createDocumentFragment();
    for (const a of list) {
      const icon = icons[a.key] ? `<img src="${icons[a.key]}" alt="">` : `<span class="ph">${esc(a.name.slice(0, 1).toUpperCase())}</span>`;
      const tag = a.rule && L.rule[a.rule] ? `<span class="rule-tag">${L.rule[a.rule]}</span>` : "";
      const html =
        `<div class="who">${icon}<span class="nm" dir="auto" title="${esc(a.name)}">${esc(a.name)}</span>${tag}</div>` +
        `<div class="state ${CLS[a.state] || "st-wait"}"><span class="pip"></span><span class="lbl">${iso(L.state[a.state] || "")}</span><span class="why">${esc(iso(why(a)))}</span></div>` +
        `<div class="ram">${sizeHtml(a.ramMB)}</div>` +
        `<div class="cpu${a.cpu < 0.5 ? " zero" : ""}">${a.cpu < 0.5 ? "0" : a.cpu.toFixed(a.cpu < 10 ? 1 : 0)}<small>%</small></div>` +
        `<button class="more" aria-haspopup="menu" aria-expanded="${menuKey === a.key}" aria-label="${esc(a.name)}"><svg viewBox="0 0 16 16"><path d="M3.5 8h.01M8 8h.01M12.5 8h.01"/></svg></button>`;
      let li = old.get(a.key);
      if (li) {
        if (li._html !== html) { li.innerHTML = html; li._html = html; }
        li.style.animation = "none";
      } else {
        li = document.createElement("li");
        li.className = "row";
        li.dataset.key = a.key;
        li.innerHTML = html;
        li._html = html;
      }
      frag.appendChild(li);
    }
    rows.replaceChildren(frag);
  }

  // ---------- log ----------
  function renderLog() {
    const ol = $("#log");
    const newest = S.log.length ? S.log[0].id : 0;
    if (newest === lastLogId) return;
    ol.innerHTML = S.log.slice(0, 40).map((e) => {
      const f = L.log[e.code];
      const text = iso(f ? f(e) : esc(e.code));
      return `<li class="k-${e.kind}${lastLogId > 0 && e.id > lastLogId ? " new" : ""}">${text}<time>${clock(e.t)}</time></li>`;
    }).join("");
    lastLogId = newest;
  }

  // ---------- chrome ----------
  function renderBar() {
    const modes = $("#modes");
    modes.classList.toggle("custom", S.mode === "custom");
    for (const btn of modes.querySelectorAll("button")) btn.setAttribute("aria-checked", String(btn.dataset.mode === S.mode));

    $("#markDot").classList.toggle("off", S.paused);
    $("#pausedBand").hidden = !S.paused;
    $("#pauseBtn").classList.toggle("on", S.paused);
    $("#pauseLabel").textContent = S.paused ? L.paused : L.pause;
    if (S.paused) {
      const left = S.pausedUntil ? Math.max(1, Math.ceil((S.pausedUntil - S.now) / 60000)) : 0;
      $("#pausedText").textContent = S.pausedUntil ? L.pausedFor(left) : L.pausedOpen;
    }

    const flags = Object.assign({}, S.settings, { langArabic: S.settings.lang === "ar" });
    for (const el of document.querySelectorAll("[data-set]")) el.checked = !!flags[el.dataset.set];
    for (const el of document.querySelectorAll("[data-out]")) el.textContent = S.settings[el.dataset.out];
    $("#aboutLine").textContent = L.aboutLine(S.version, S.admin, S.selfMB);

    const band = $("#boostBand");
    band.hidden = !S.boost;
    if (S.boost) $("#boostText").innerHTML = L.boostBand(esc(S.boost.name || ""), S.boost.firstPassDone ? S.boost.freedMB : 0);

    const c = S.claude;
    $("#claudeBtn").classList.toggle("on", !!c);
    $("#claudeBtn").setAttribute("aria-pressed", String(!!c));
    $("#claudeBand").hidden = !c;
    if (c) {
      $("#claudePip").classList.toggle("quiet", !c.busy);
      $("#claudeTitle").innerHTML = iso(c.busy ? L.claudeBusy : L.claudeQuiet(L.ago(c.quietSec)));
      $("#claudeSub").textContent = iso(c.present ? L.claudePresent : L.claudeAway(S.settings.claudeScreenOffMin));
      $("#cbUsing").innerHTML = `${c.using_}<small> · ${L.size(c.ramMB)}</small>`;
      $("#cbAsleep").textContent = c.asleep;
      $("#cbFreed").innerHTML = sizeHtml(c.freedMB);
      if (!c.plan) $("#claudeSub").textContent = iso(L.claudeNoPlan);

    }
  }

  function render() {
    if (!S) return;
    applyLang(S.settings.lang || "en");
    renderBar();
    renderGauge();
    renderApps();
    renderLog();
  }

  // ---------- interactions ----------
  $("#modes").addEventListener("click", (e) => {
    const b = e.target.closest("button[data-mode]");
    if (b) send("setMode", { mode: b.dataset.mode });
  });

  const pauseMenu = $("#pauseMenu");
  $("#pauseBtn").addEventListener("click", (e) => {
    e.stopPropagation();
    if (S && S.paused) { send("resume"); return; }
    const open = pauseMenu.hidden;
    closeMenus();
    pauseMenu.hidden = !open;
  });
  pauseMenu.addEventListener("click", (e) => {
    const b = e.target.closest("button[data-min]");
    if (!b) return;
    send("pause", { minutes: +b.dataset.min });
    pauseMenu.hidden = true;
  });
  $("#resumeBtn").addEventListener("click", () => send("resume"));
  $("#boostStop").addEventListener("click", () => send("unboost"));
  $("#claudeBtn").addEventListener("click", () => send("claude", { on: !(S && S.claude) }));
  $("#claudeStop").addEventListener("click", () => send("claude", { on: false }));
  $("#donateBtn").addEventListener("click", () => send("donate"));

  $("#tabs").addEventListener("click", (e) => {
    const b = e.target.closest("button[data-f]");
    if (!b) return;
    filter = b.dataset.f;
    for (const t of $("#tabs").children) t.setAttribute("aria-selected", String(t === b));
    if (S) renderApps();
  });
  $("#tabs").children[0].setAttribute("aria-selected", "true");

  const rowMenu = $("#rowMenu");
  $("#rows").addEventListener("click", (e) => {
    const b = e.target.closest(".more");
    if (!b) return;
    e.stopPropagation();
    const key = b.closest(".row").dataset.key;
    if (menuKey === key && !rowMenu.hidden) { closeMenus(); return; }
    closeMenus();
    menuKey = key;
    b.setAttribute("aria-expanded", "true");
    const a = S.apps.find((x) => x.key === key);
    $("#rowMenuTitle").textContent = a ? a.name : "";
    for (const m of rowMenu.querySelectorAll("[data-rule]")) m.setAttribute("aria-checked", String((a && a.rule || "auto") === m.dataset.rule));
    rowMenu.querySelector('[data-act="trim"]').hidden = !a || ["fg", "guard", "locked", "off", "boost"].includes(a.state);
    const bi = rowMenu.querySelector('[data-act="boost"]');
    bi.hidden = !a || ["guard", "locked"].includes(a.state);
    bi.classList.toggle("on", !!a && a.state === "boost");
    $("#boostItemLabel").textContent = a && a.state === "boost" ? L.stopBoostThis : L.boostThis;
    bi.querySelector("small").hidden = !!a && a.state === "boost";
    const ci = rowMenu.querySelector('[data-act="claudeApp"]');
    ci.hidden = !a || a.state === "guard" || key === "claude";
    ci.setAttribute("aria-checked", String(!!a && !!a.claudeApp));
    rowMenu.hidden = false;
    const r = b.getBoundingClientRect();
    const mh = rowMenu.offsetHeight, mw = rowMenu.offsetWidth;
    const top = r.bottom + 4 + mh > innerHeight ? r.top - mh - 4 : r.bottom + 4;
    rowMenu.style.top = `${top}px`;
    rowMenu.style.left = `${rtl() ? Math.max(8, r.left) : Math.min(innerWidth - mw - 8, r.right - mw)}px`;
  });
  rowMenu.addEventListener("click", (e) => {
    const b = e.target.closest("button");
    if (!b || !menuKey) return;
    if (b.dataset.rule) send("setRule", { key: menuKey, rule: b.dataset.rule });
    if (b.dataset.act === "trim") send("trimNow", { key: menuKey });
    if (b.dataset.act === "boost") send("boost", { key: menuKey });
    if (b.dataset.act === "claudeApp") {
      const a = S.apps.find((x) => x.key === menuKey);
      send("claudeApp", { key: menuKey, on: !(a && a.claudeApp) });
    }
    closeMenus();
  });

  function closeMenus() {
    pauseMenu.hidden = true;
    rowMenu.hidden = true;
    menuKey = null;
    for (const b of document.querySelectorAll('.more[aria-expanded="true"]')) b.setAttribute("aria-expanded", "false");
  }
  document.addEventListener("click", (e) => { if (!e.target.closest(".menu")) closeMenus(); });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") { closeMenus(); closeSheet(); } });
  $("#rows").addEventListener("scroll", closeMenus);

  const sheet = $("#sheet"), scrim = $("#scrim");
  function openSheet() { sheet.classList.add("open"); sheet.setAttribute("aria-hidden", "false"); scrim.hidden = false; }
  function closeSheet() { sheet.classList.remove("open"); sheet.setAttribute("aria-hidden", "true"); scrim.hidden = true; }
  $("#settingsBtn").addEventListener("click", openSheet);
  $("#sheetClose").addEventListener("click", closeSheet);
  scrim.addEventListener("click", closeSheet);

  sheet.addEventListener("change", (e) => {
    const el = e.target.closest("[data-set]");
    if (el) send("setSetting", { key: el.dataset.set, value: el.checked });
  });
  sheet.addEventListener("click", (e) => {
    const b = e.target.closest("button[data-step]");
    if (!b || !S) return;
    const k = b.dataset.for;
    send("setSetting", { key: k, value: S.settings[k] + +b.dataset.step });
  });

  let toastT;
  function toast(msg) {
    const t = $("#toast");
    const f = L.toast[msg.code];
    t.textContent = iso(f ? f(msg) : "");
    t.hidden = false;
    clearTimeout(toastT);
    toastT = setTimeout(() => (t.hidden = true), 2600);
  }

  bridge.addEventListener("message", (e) => {
    const msg = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
    if (msg.type === "state") { S = msg; render(); }
    else if (msg.type === "icons") { Object.assign(icons, msg.map); if (S) { $("#rows").replaceChildren(); renderApps(); } }
    else if (msg.type === "toast") toast(msg);
  });
  send("ready");
})();
