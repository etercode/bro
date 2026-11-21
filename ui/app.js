'use strict';

// Talks to MainWindow.xaml.cs: we send commands, C# sends back the browser state.
const webview = window.chrome && window.chrome.webview;
const post = (type, data = {}) => webview && webview.postMessage({ type, ...data });
const $ = (selector) => document.querySelector(selector);

const PAGES_HOST = 'pages.bro';

const ICONS = {
  lock: '<svg viewBox="0 0 24 24"><rect x="5" y="10.5" width="14" height="10" rx="2.2"/><path d="M8.5 10.5V7.8a3.5 3.5 0 0 1 7 0v2.7"/></svg>',
  insecure: '<svg viewBox="0 0 24 24"><rect x="5" y="10.5" width="14" height="10" rx="2.2"/><path d="M8.5 10.5V7.8a3.5 3.5 0 0 1 6.7-1.4"/></svg>',
  search: '<svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="6.5"/><path d="M16 16l4 4"/></svg>',
  page: '<svg viewBox="0 0 24 24"><path d="M7 3.5h7l4 4v13H7z"/><path d="M14 3.5v4h4"/></svg>',
};
const CLOSE_ICON = '<svg viewBox="0 0 16 16"><path d="M5 5l6 6M11 5l-6 6"/></svg>';
// Shown when a site has no icon (and on the new tab page)
const SITE_ICON = '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.5"/><path d="M15.2 8.8l-1.9 4.5-4.5 1.9 1.9-4.5z"/></svg>';

let state = {
  tabs: [], url: '', loading: false, canBack: false, canForward: false, maximized: false,
  isPrivate: false, accent: '#1e66f5', searchName: 'DuckDuckGo',
  canBookmark: false, bookmarked: false, shield: 'none', blocked: 0, shieldSite: '',
  downloads: { active: 0, progress: 0, any: false, flash: false },
};
let editing = false;

// ── page area: tell C# where to put the tab views ─────────────────────────

const viewport = $('#viewport');
let lastViewport = '';
function reportViewport() {
  const r = viewport.getBoundingClientRect();
  const key = `${r.left},${r.top},${r.width},${r.height}`;
  if (key === lastViewport) return;
  lastViewport = key;
  post('viewport', { x: r.left, y: r.top, w: r.width, h: r.height });
}
new ResizeObserver(reportViewport).observe(viewport);
window.addEventListener('resize', reportViewport);

// ── tabs ──────────────────────────────────────────────────────────────────

const tabsEl = $('#tabs');
const chips = new Map();
let lastActiveId = null;

function chipFor(id) {
  let el = chips.get(id);
  if (el) return el;
  el = document.createElement('div');
  el.className = 'chip';
  el.setAttribute('role', 'tab');
  el.innerHTML = `<span class="ico"></span><button class="x" title="Close tab (Ctrl+W)">${CLOSE_ICON}</button>`;
  el.dataset.favicon = '-';
  el.addEventListener('click', (e) => post(e.target.closest('.x') ? 'closeTab' : 'activate', { id }));
  el.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); }); // no autoscroll
  el.addEventListener('auxclick', (e) => { if (e.button === 1) post('closeTab', { id }); });
  chips.set(id, el);
  return el;
}

function renderTabs(tabs) {
  const seen = new Set();
  tabs.forEach((tab, i) => {
    const el = chipFor(tab.id);
    seen.add(tab.id);
    el.classList.toggle('active', tab.active);
    el.classList.toggle('loading', tab.loading);
    el.setAttribute('aria-selected', String(tab.active));
    el.title = tab.title;
    el.setAttribute('aria-label', tab.title);
    if (el.dataset.favicon !== tab.favicon) {
      el.dataset.favicon = tab.favicon;
      const ico = el.querySelector('.ico');
      if (tab.favicon) {
        const img = document.createElement('img');
        img.alt = '';
        img.src = tab.favicon;
        ico.replaceChildren(img);
      } else {
        ico.innerHTML = SITE_ICON;
      }
    }
    if (tabsEl.children[i] !== el) tabsEl.insertBefore(el, tabsEl.children[i] || null);
  });
  for (const [id, el] of chips) {
    if (!seen.has(id)) { el.remove(); chips.delete(id); }
  }
  const active = tabs.find((t) => t.active);
  if (active && active.id !== lastActiveId) {
    lastActiveId = active.id;
    chips.get(active.id).scrollIntoView({ block: 'nearest' });
  }
}

// ── address bar ───────────────────────────────────────────────────────────

const addr = $('#addr');
const input = $('#addr-input');
const display = $('#addr-display');
const addrIcon = $('#addr-icon');

function parseUrl(url) { try { return new URL(url); } catch { return null; } }
function isNewTab(url) { const u = parseUrl(url); return !url || (u && u.host === PAGES_HOST && u.pathname === '/newtab.html'); }
function broPage(u) { return u && u.host === PAGES_HOST ? u.pathname.replace(/^\/|\.html$/g, '') : null; }
function editableUrl(url) {
  if (isNewTab(url)) return '';
  const page = broPage(parseUrl(url));
  return page ? `bro://${page}` : url;
}
function safeDecode(s) { try { return decodeURIComponent(s); } catch { return s; } }

function renderAddress() {
  const url = state.url;
  const u = parseUrl(url);
  let icon = 'search', host = '', path = '';

  if (!isNewTab(url) && u) {
    if (broPage(u)) {
      icon = 'page';
      host = `bro://${broPage(u)}`;
    } else if (u.protocol === 'https:' || u.protocol === 'http:') {
      icon = u.protocol === 'https:' ? 'lock' : 'insecure';
      host = u.hostname.replace(/^www\./, '') + (u.port ? `:${u.port}` : '');
      path = u.pathname.split('/').filter(Boolean).map(safeDecode).join(' / ');
    } else {
      icon = 'page';
      host = url;
    }
  }

  if (addr.dataset.security !== icon) { addrIcon.innerHTML = ICONS[icon]; addr.dataset.security = icon; }

  display.replaceChildren();
  if (host) {
    const h = document.createElement('span');
    h.className = 'host';
    h.textContent = host;
    display.append(h);
    if (path) {
      const p = document.createElement('span');
      p.className = 'path';
      p.textContent = ` / ${path}`;
      display.append(p);
    }
  }
  addr.classList.toggle('has-display', Boolean(host));
  if (!editing) input.value = editableUrl(url);
}

let selectOnMouseUp = false;
input.addEventListener('focus', () => {
  editing = true;
  input.value = editableUrl(state.url);
  input.select();
  selectOnMouseUp = true;
});
input.addEventListener('mouseup', (e) => {
  if (selectOnMouseUp) { e.preventDefault(); input.select(); }
  selectOnMouseUp = false;
});
input.addEventListener('blur', () => {
  editing = false;
  input.value = editableUrl(state.url);
  post('suggestClose');
});

// Suggestions are drawn by C# in a popup just under the address bar
function suggest() {
  const r = addr.getBoundingClientRect();
  post('suggest', { text: input.value, x: r.left, y: r.bottom, w: r.width });
}
input.addEventListener('input', suggest);
input.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    const text = input.value.trim();
    if (text) { post('navigate', { text }); input.blur(); }
  } else if (e.key === 'Escape') {
    post('suggestClose');
    if (input.value !== editableUrl(state.url)) { input.value = editableUrl(state.url); input.select(); }
    else input.blur();
  } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    post('suggestMove', { delta: e.key === 'ArrowDown' ? 1 : -1 });
  }
});

// ── buttons ───────────────────────────────────────────────────────────────

$('#back').addEventListener('click', () => post('back'));
$('#forward').addEventListener('click', () => post('forward'));
$('#reload').addEventListener('click', () => post(state.loading ? 'stop' : 'reload'));
$('#new-tab').addEventListener('click', () => post('newTab'));
$('#more').addEventListener('click', (e) => {
  const r = e.currentTarget.getBoundingClientRect();
  post('menu', { bottom: r.bottom, right: r.right });
});
$('#bookmark').addEventListener('click', () => post('bookmark'));
$('#shield').addEventListener('click', () => post('shield'));
$('#downloads').addEventListener('click', () => post('downloads'));
document.querySelectorAll('.window-btns [data-cmd]').forEach((b) =>
  b.addEventListener('click', () => post(b.dataset.cmd)));

// ── empty areas move the window; double-click maximizes ───────────────────

let lastDown = { t: 0, x: 0, y: 0 };
document.addEventListener('mousedown', (e) => {
  if (e.button !== 0 || e.target.closest('button, input, .addr, .chip')) return;
  e.preventDefault();
  const now = performance.now();
  if (now - lastDown.t < 450 && Math.abs(e.clientX - lastDown.x) < 6 && Math.abs(e.clientY - lastDown.y) < 6) {
    lastDown.t = 0;
    post('toggleMax');
    return;
  }
  lastDown = { t: now, x: e.clientX, y: e.clientY };
  post('drag');
});

// ── state from C# ─────────────────────────────────────────────────────────

function render() {
  renderTabs(state.tabs);
  renderAddress();
  $('#back').disabled = !state.canBack;
  $('#forward').disabled = !state.canForward;
  const reload = $('#reload');
  reload.classList.toggle('is-loading', state.loading);
  reload.title = state.loading ? 'Stop' : 'Reload (F5)';
  document.body.classList.toggle('maximized', state.maximized);
  document.body.classList.toggle('private', state.isPrivate);
  document.documentElement.style.setProperty('--accent', state.accent);
  document.documentElement.style.setProperty('--accent-hover', `color-mix(in srgb, ${state.accent} 88%, white)`);
  input.placeholder = `Search ${state.searchName} or enter address`;
  renderBookmark();
  renderShield();
  renderDownloads();
  $('#max-btn').title = state.maximized ? 'Restore' : 'Maximize';
}

// Same page + newly saved → a small "pop" so you can see it worked
let lastBookmark = { url: '', on: false };
function renderBookmark() {
  const btn = $('#bookmark');
  btn.disabled = !state.canBookmark;
  btn.classList.toggle('on', state.bookmarked);
  btn.title = state.bookmarked ? 'Remove bookmark (Ctrl+D)' : 'Bookmark this page (Ctrl+D)';
  if (state.bookmarked && !lastBookmark.on && state.url === lastBookmark.url) {
    btn.classList.remove('pop');
    void btn.offsetWidth; // restart the animation
    btn.classList.add('pop');
  }
  lastBookmark = { url: state.url, on: state.bookmarked };
}

function renderShield() {
  const btn = $('#shield');
  btn.className = `icon-btn ${state.shield}`;
  btn.disabled = state.shield === 'none';
  $('#blocked').textContent = state.shield === 'on' && state.blocked > 0 ? (state.blocked > 99 ? '99+' : String(state.blocked)) : '';
  btn.title =
    state.shield === 'on' ? `Ad blocker: ${state.blocked} blocked on this page. Click to pause on ${state.shieldSite}` :
    state.shield === 'paused' ? `Ad blocker paused on ${state.shieldSite}. Click to turn it back on` :
    state.shield === 'off' ? 'Ad blocker is off. Click to turn it on' : 'Ad blocker';
}

function renderDownloads() {
  const btn = $('#downloads');
  const d = state.downloads;
  btn.classList.toggle('active', d.active > 0);
  btn.classList.toggle('indeterminate', d.active > 0 && d.progress < 0);
  if (d.active > 0 && d.progress >= 0)
    btn.querySelector('.ring circle').style.strokeDasharray = `${Math.max(2, d.progress * 100)} 100`;
  btn.title = d.active > 0 ? `Downloading ${d.active} file${d.active > 1 ? 's' : ''} (Ctrl+J)` : 'Downloads (Ctrl+J)';
  if (d.flash) {
    btn.classList.remove('flash');
    void btn.offsetWidth;
    btn.classList.add('flash');
  }
}

if (webview) {
  webview.addEventListener('message', (e) => {
    const msg = e.data;
    if (msg.type === 'state') { state = msg; render(); }
    else if (msg.type === 'focusAddress') { input.focus(); input.select(); }
    else if (msg.type === 'fill') { input.value = msg.text; input.setSelectionRange(msg.text.length, msg.text.length); }
    else if (msg.type === 'blurAddress') input.blur();
  });
}

render();
reportViewport();
post('ready');
