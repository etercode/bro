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
let lastLayout = '';
let suppressClick = false;

const SND_ON = '<svg viewBox="0 0 24 24"><path d="M4.5 9.5h3l4.5-4v13l-4.5-4h-3z"/><path d="M16 9a4 4 0 0 1 0 6"/></svg>';
const SND_OFF = '<svg viewBox="0 0 24 24"><path d="M4.5 9.5h3l4.5-4v13l-4.5-4h-3z"/><path d="M15.5 9.5l5 5M20.5 9.5l-5 5"/></svg>';

const tabById = (id) => state.tabs.find((t) => t.id === id);
function hueOf(text) { let h = 0; for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) % 360; return h; }

function iconNode(favicon) {
  if (!favicon) { const s = document.createElement('span'); s.innerHTML = SITE_ICON; return s.firstChild; }
  const img = document.createElement('img');
  img.alt = '';
  img.src = favicon;
  return img;
}

function chipFor(id) {
  let el = chips.get(id);
  if (el) return el;
  el = document.createElement('div');
  el.className = 'chip';
  el.dataset.id = id;
  el.setAttribute('role', 'tab');
  el.innerHTML = `<span class="ico"></span><button class="x" title="Close tab (Ctrl+W)">${CLOSE_ICON}</button><button class="snd"></button>`;
  el.dataset.favicon = '-';
  el.addEventListener('click', (e) => {
    if (suppressClick) { suppressClick = false; return; }
    if (e.target.closest('.x')) post('closeTab', { id });
    else if (e.target.closest('.snd')) { const t = tabById(id); if (t) post('mute', { id, muted: !t.muted }); }
    else post('activate', { id });
  });
  el.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); }); // no autoscroll
  el.addEventListener('auxclick', (e) => { if (e.button === 1) post('closeTab', { id }); });
  chips.set(id, el);
  return el;
}

/** Tabs next to each other from the same site form a group (when grouping is on). */
function runsOf(tabs) {
  const runs = [];
  for (const t of tabs) {
    const last = runs[runs.length - 1];
    if (state.groupTabs && t.site && last && last.site === t.site) last.tabs.push(t);
    else runs.push({ site: t.site, tabs: [t] });
  }
  return runs;
}

function renderTabs(tabs) {
  const seen = new Set();
  for (const tab of tabs) {
    const el = chipFor(tab.id);
    seen.add(tab.id);
    el.classList.toggle('active', tab.active);
    el.classList.toggle('loading', tab.loading);
    el.classList.toggle('audible', tab.audible && !tab.muted);
    el.classList.toggle('muted', tab.muted);
    el.setAttribute('aria-selected', String(tab.active));
    el.title = tab.title + (tab.audible ? (tab.muted ? ' (muted)' : ' (playing sound)') : '');
    el.setAttribute('aria-label', tab.title);
    const snd = el.querySelector('.snd');
    const sndIcon = tab.muted ? 'off' : 'on';
    if (snd.dataset.icon !== sndIcon) { snd.dataset.icon = sndIcon; snd.innerHTML = tab.muted ? SND_OFF : SND_ON; }
    snd.title = tab.muted ? 'Unmute tab' : 'Mute tab';
    if (el.dataset.favicon !== tab.favicon) {
      el.dataset.favicon = tab.favicon;
      el.querySelector('.ico').replaceChildren(iconNode(tab.favicon));
    }
  }
  for (const [id, el] of chips) {
    if (!seen.has(id)) { el.remove(); chips.delete(id); }
  }

  // Rebuild the order/groups only when they changed (keeps hover states steady)
  const runs = runsOf(tabs);
  const layout = runs.map((r) => `${r.site}:${r.tabs.map((t) => t.id).join(',')}`).join('|');
  if (layout !== lastLayout) {
    lastLayout = layout;
    tabsEl.replaceChildren(...runs.map((r) => {
      if (r.tabs.length < 2) return chipFor(r.tabs[0].id);
      const group = document.createElement('div');
      group.className = 'group';
      group.title = r.site;
      group.style.setProperty('--hue', hueOf(r.site));
      group.append(...r.tabs.map((t) => chipFor(t.id)));
      return group;
    }));
  }

  const active = tabs.find((t) => t.active);
  if (active && active.id !== lastActiveId) {
    lastActiveId = active.id;
    chips.get(active.id).scrollIntoView({ block: 'nearest' });
  }
  reportTabMids();
}

// Where the tabs are, so a tab dragged in from another window lands where it's dropped
let lastMids = '';
function reportTabMids() {
  const mids = state.tabs.map((t) => chips.get(t.id)).filter(Boolean).map((c) => {
    const r = c.getBoundingClientRect();
    return Math.round(r.top + r.height / 2);
  });
  const key = mids.join(',');
  if (key === lastMids) return;
  lastMids = key;
  post('tabMids', { mids });
}
window.addEventListener('resize', () => { lastMids = ''; reportTabMids(); });
tabsEl.addEventListener('scroll', () => { lastMids = ''; reportTabMids(); });

// ── drag tabs: reorder, or drop outside the window to open it in a new one ──

const rail = $('#rail');
const dropLine = $('#drop-line');
let drag = null;

tabsEl.addEventListener('pointerdown', (e) => {
  const chip = e.target.closest('.chip');
  if (!chip || e.button !== 0 || e.target.closest('.x, .snd')) return;
  e.preventDefault(); // otherwise grabbing the site icon starts the browser's own image drag
  drag = { id: Number(chip.dataset.id), chip, x: e.clientX, y: e.clientY, moved: false, index: -1, outside: false };
  chip.setPointerCapture(e.pointerId);
});
tabsEl.addEventListener('dragstart', (e) => e.preventDefault());

tabsEl.addEventListener('pointermove', (e) => {
  if (!drag) return;
  const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
  if (!drag.moved && Math.hypot(dx, dy) < 6) return;
  drag.moved = true;
  drag.chip.classList.add('dragging');
  drag.chip.style.transform = `translate(${dx}px, ${dy}px)`;
  drag.outside = e.clientX < 0 || e.clientY < 0 || e.clientX > innerWidth || e.clientY > innerHeight;
  document.body.classList.toggle('tab-detach', drag.outside);

  // Where would it land? Count the other tabs above the pointer.
  const others = state.tabs.filter((t) => t.id !== drag.id).map((t) => chips.get(t.id)).filter(Boolean);
  const rects = others.map((c) => c.getBoundingClientRect());
  drag.index = rects.filter((r) => r.top + r.height / 2 < e.clientY).length;
  if (drag.outside || others.length === 0) { dropLine.hidden = true; return; }
  const railTop = rail.getBoundingClientRect().top;
  const y = drag.index < rects.length ? rects[drag.index].top - 5 : rects[rects.length - 1].bottom + 4;
  dropLine.style.top = `${y - railTop - 1}px`;
  dropLine.hidden = false;
});

function endDrag(commit, e) {
  if (!drag) return;
  const d = drag;
  drag = null;
  dropLine.hidden = true;
  document.body.classList.remove('tab-detach');
  d.chip.classList.remove('dragging');
  d.chip.style.transform = '';
  if (!d.moved || !commit) return;
  suppressClick = true;
  setTimeout(() => { suppressClick = false; }, 0);
  // Released inside the window = reorder. Otherwise C# checks where the mouse really is:
  // outside the window opens the tab in a new window.
  const inside = e && e.type === 'pointerup' &&
    e.clientX >= 0 && e.clientY >= 0 && e.clientX < innerWidth && e.clientY < innerHeight;
  const from = state.tabs.findIndex((t) => t.id === d.id);
  post('dropTab', { id: d.id, index: d.index >= 0 && d.index !== from ? d.index : -1, inside });
}
tabsEl.addEventListener('pointerup', (e) => endDrag(true, e));
// If the mouse leaves the window the browser may drop the capture; treat that as the drop
tabsEl.addEventListener('lostpointercapture', (e) => endDrag(true, e));
tabsEl.addEventListener('pointercancel', (e) => endDrag(true, e));

// ── sound: tabs playing audio, listed at the bottom of the rail ──────────────

const soundEl = $('#sound');
const soundTabs = $('#sound-tabs');
let lastSound = '';

function renderSound() {
  const list = state.tabs.filter((t) => t.audible || t.muted || t.volume < 1);
  soundEl.hidden = list.length === 0;
  const key = list.map((t) => `${t.id}:${t.favicon.length}:${t.muted}:${t.active}:${t.audible}`).join('|');
  if (key === lastSound) return;
  lastSound = key;
  soundTabs.replaceChildren(...list.map((t) => {
    const b = document.createElement('button');
    b.className = 'snd-chip' + (t.muted ? ' muted' : '') + (t.active ? ' active' : '');
    b.title = `${t.title}${t.muted ? ' (muted)' : ''} — volume`;
    b.append(iconNode(t.favicon));
    const bars = document.createElement('span');
    bars.className = 'bars';
    bars.innerHTML = '<i></i><i></i><i></i>';
    if (t.audible || t.muted) b.append(bars);
    b.addEventListener('click', () => openVolume(t.id, b));
    return b;
  }));
}

// ── dropdowns: drawn here; the page cuts a hole where one is open ────────────

const popovers = [...document.querySelectorAll('.popover')];
const isOpen = (p) => !p.hidden;

function reportOverlays() {
  const rects = popovers.filter(isOpen).map((p) => {
    const r = p.getBoundingClientRect();
    return { x: r.left - 1, y: r.top - 1, w: r.width + 2, h: r.height + 2 };
  });
  post('overlays', { rects });
}

function openPopover(p) {
  closePopovers(p);
  p.hidden = false;
  reportOverlays();
}

function closePopovers(except) {
  let changed = false;
  for (const p of popovers) if (p !== except && isOpen(p)) { p.hidden = true; changed = true; }
  if (changed) reportOverlays();
}

popovers.forEach((p) => new ResizeObserver(() => { if (isOpen(p)) reportOverlays(); }).observe(p));
document.addEventListener('pointerdown', (e) => {
  if (!e.target.closest('.popover, #shield, .snd-chip, #zoom')) closePopovers();
});
window.addEventListener('blur', () => closePopovers()); // clicked into the page
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closePopovers(); });

// Ad blocker dropdown
const abPop = $('#adblock-pop');
let abLastBlocked = -1, abTimer = 0;

$('#shield').addEventListener('click', () => {
  if (isOpen(abPop)) { closePopovers(); return; }
  const r = $('#shield').getBoundingClientRect();
  abPop.style.left = `${Math.max(8, r.right - 320)}px`;
  abPop.style.top = `${r.bottom + 8}px`;
  post('adblockPanel');
});

function renderAdblockPanel(m) {
  $('#ab-site').textContent = m.site ? `on ${m.site}` : '';
  const toggle = $('#ab-toggle');
  toggle.checked = m.state === 'on';
  toggle.disabled = m.state === 'none';
  const count = $('#ab-count');
  count.replaceChildren();
  if (m.state === 'off') {
    count.append('The ad blocker is turned off. ');
    const on = document.createElement('button');
    on.className = 'primary-btn';
    on.textContent = 'Turn on';
    on.addEventListener('click', () => { post('adblockEnable'); closePopovers(); });
    count.append(on);
  } else if (m.state === 'paused') {
    count.textContent = `Paused on ${m.site}. Ads and trackers are allowed here.`;
  } else if (m.state === 'none') {
    count.textContent = 'Nothing to block on this page.';
  } else {
    const b = document.createElement('b');
    b.textContent = m.blocked.toLocaleString();
    count.append(b, m.blocked === 1 ? 'ad or tracker blocked on this page' : 'ads and trackers blocked on this page');
  }
  $('#ab-hosts').replaceChildren(...(m.state === 'on' ? m.hosts : []).map((h) => {
    const row = document.createElement('div');
    row.className = 'row';
    const name = document.createElement('span');
    name.textContent = h.host;
    name.title = h.host;
    const n = document.createElement('b');
    n.textContent = `×${h.count}`;
    row.append(name, n);
    return row;
  }));
  $('#ab-status').textContent = m.status;
  abLastBlocked = m.blocked;
  if (!isOpen(abPop)) openPopover(abPop);
}

$('#ab-toggle').addEventListener('change', () => { post('shield'); setTimeout(() => post('adblockPanel'), 400); });
$('#ab-settings').addEventListener('click', () => { post('openSettings'); closePopovers(); });

// Zoom: a magnifier with the level appears in the address bar when the page isn't at 100%
const zoomPop = $('#zoom-pop');
const zoomText = () => `${Math.round((state.zoom || 1) * 100)}%`;
function renderZoom() {
  const zoomed = Math.abs((state.zoom || 1) - 1) > 0.001;
  const btn = $('#zoom');
  btn.hidden = !zoomed;
  $('#zoom-value').textContent = zoomText();
  btn.querySelector('.plus').style.display = (state.zoom || 1) > 1 ? '' : 'none';
  $('#zoom-pop-value').textContent = zoomText();
  if (!zoomed && isOpen(zoomPop) && !zoomPop.contains(document.activeElement)) { /* keep open while using it */ }
}
$('#zoom').addEventListener('click', (e) => {
  e.preventDefault(); // it sits inside the address bar's label
  if (isOpen(zoomPop)) { closePopovers(); return; }
  const r = $('#zoom').getBoundingClientRect();
  openPopover(zoomPop);
  zoomPop.style.left = `${Math.max(8, r.right - zoomPop.offsetWidth)}px`;
  zoomPop.style.top = `${r.bottom + 12}px`;
  reportOverlays();
});
$('#zoom-in').addEventListener('click', () => post('zoomStep', { delta: 1 }));
$('#zoom-out').addEventListener('click', () => post('zoomStep', { delta: -1 }));
$('#zoom-reset').addEventListener('click', () => { post('zoom', { factor: 1 }); closePopovers(); });

// Volume dropdown (from the sound section)
const volPop = $('#volume-pop');
const volRange = $('#vol-range');
let volTabId = null, volPending = null, volDragging = false;

function openVolume(id, anchor) {
  if (isOpen(volPop) && volTabId === id) { closePopovers(); return; }
  volTabId = id;
  fillVolume(true);
  openPopover(volPop);
  const r = anchor.getBoundingClientRect();
  const h = volPop.getBoundingClientRect().height;
  volPop.style.left = `${r.left - volPop.offsetWidth - 12}px`;
  volPop.style.top = `${Math.max(60, Math.min(innerHeight - h - 8, r.top + r.height / 2 - h / 2))}px`;
  reportOverlays();
}

function fillVolume(force) {
  const t = tabById(volTabId);
  if (!t) { if (isOpen(volPop)) closePopovers(); return; }
  $('#vol-title').textContent = t.title;
  $('#vol-ico').replaceChildren(iconNode(t.favicon));
  const mute = $('#vol-mute');
  mute.innerHTML = t.muted ? SND_OFF : SND_ON;
  mute.classList.toggle('muted', t.muted);
  mute.title = t.muted ? 'Unmute' : 'Mute';
  if (force || (!volDragging && volPending === null)) volRange.value = Math.round(t.volume * 100);
  $('#vol-value').textContent = t.muted ? 'Muted' : `${volRange.value}%`;
}

volRange.addEventListener('input', () => {
  $('#vol-value').textContent = `${volRange.value}%`;
  if (volPending === null) requestAnimationFrame(() => {
    post('volume', { id: volTabId, level: volPending / 100 });
    volPending = null;
  });
  volPending = Number(volRange.value);
});
volRange.addEventListener('pointerdown', () => { volDragging = true; });
volRange.addEventListener('pointerup', () => { volDragging = false; });
$('#vol-mute').addEventListener('click', () => { const t = tabById(volTabId); if (t) post('mute', { id: t.id, muted: !t.muted }); });
$('#vol-tab').addEventListener('click', () => post('activate', { id: volTabId }));

// ── bookmarks drawer ──────────────────────────────────────────────────────

const ICON_SVG = {
  chev: '<svg class="chev" viewBox="0 0 24 24"><path d="M7 9.5l5 5 5-5"/></svg>',
  folder: '<svg class="fold" viewBox="0 0 24 24"><path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/></svg>',
  edit: '<svg viewBox="0 0 24 24"><path d="M5 19h3.5L18 9.5 14.5 6 5 15.5z"/></svg>',
  trash: '<svg viewBox="0 0 24 24"><path d="M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12"/></svg>',
  plus: '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>',
};

let bm = { items: [], folders: [] };
let drawerOpen = false;
let flashUrl = '';
let dragBm = null;
let newFolderInput = false;
const bmList = $('#bm-list');
const bmSearch = $('#bm-search');
const collapsed = new Set((() => { try { return JSON.parse(localStorage.getItem('bro.collapsed') || '[]'); } catch { return []; } })());
const saveCollapsed = () => { try { localStorage.setItem('bro.collapsed', JSON.stringify([...collapsed])); } catch { /* not available */ } };

function toggleDrawer(open = !drawerOpen) {
  drawerOpen = open;
  document.body.classList.toggle('drawer-open', open);
  if (open) post('getBookmarks');
}
$('#bm-toggle').addEventListener('click', () => toggleDrawer());
$('#bm-close').addEventListener('click', () => toggleDrawer(false));
bmSearch.addEventListener('input', renderDrawer);
$('#bm-new-folder').addEventListener('click', () => { newFolderInput = true; bmSearch.value = ''; renderDrawer(); });
$('#bm-add').addEventListener('click', () => {
  if (state.bookmarked) return;
  flashUrl = state.url;
  post('addBookmark', { folder: '' });
});

function renderDrawerAdd() {
  const add = $('#bm-add');
  add.hidden = !state.canBookmark;
  add.disabled = state.bookmarked;
  add.innerHTML = state.bookmarked ? `${ICON_SVG.check} Saved. Drag it into a folder below.` : `${ICON_SVG.plus} Add this page`;
}

function favNode(b) {
  const s = document.createElement('span');
  s.className = 'fav';
  if (b.favicon) s.append(iconNode(b.favicon));
  else s.textContent = (hostOf(b.url).charAt(0) || '?').toUpperCase();
  return s;
}
function hostOf(url) { const u = parseUrl(url); return u ? u.hostname.replace(/^www\./, '') : url; }

function actionButton(svg, title, onClick) {
  const b = document.createElement('button');
  b.title = title;
  b.innerHTML = svg;
  b.addEventListener('click', (e) => { e.stopPropagation(); onClick(); });
  return b;
}

function inlineInput(value, onDone) {
  const input = document.createElement('input');
  input.className = 'bm-rename';
  input.value = value;
  let done = false;
  const finish = (save) => { if (done) return; done = true; onDone(save ? input.value.trim() : null); };
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') finish(true);
    else if (e.key === 'Escape') finish(false);
    e.stopPropagation();
  });
  input.addEventListener('blur', () => finish(true));
  input.addEventListener('click', (e) => e.stopPropagation());
  setTimeout(() => { input.focus(); input.select(); }, 0);
  return input;
}

function clearDropMarks() {
  bmList.querySelectorAll('.drop, .drop-before, .drop-after, .drop-into').forEach((n) =>
    n.classList.remove('drop', 'drop-before', 'drop-after', 'drop-into'));
}

/** Top edge = put before, bottom edge = put after, middle = combine into a folder. */
function dropZone(e, row) {
  const r = row.getBoundingClientRect();
  const y = (e.clientY - r.top) / r.height;
  return y < 0.3 ? 'before' : y > 0.7 ? 'after' : 'into';
}

/** The bookmark that comes after b in the saved order (skipping the one being dragged). */
function idAfter(b) {
  const i = bm.items.findIndex((x) => x.id === b.id);
  const next = bm.items.slice(i + 1).find((x) => x.id !== dragBm.id);
  return next ? next.id : '';
}

function openEditor(row, b) {
  row.draggable = false;
  row.classList.add('editing');
  const name = document.createElement('input');
  name.className = 'bm-field';
  name.value = b.title;
  name.placeholder = 'Name';
  const url = document.createElement('input');
  url.className = 'bm-field';
  url.value = b.url;
  url.placeholder = 'Address';
  url.spellcheck = false;
  const save = () => post('updateBookmark', { id: b.id, title: name.value.trim() || b.title, url: url.value.trim() || b.url, folder: b.folder });
  const buttons = document.createElement('div');
  buttons.className = 'bm-edit-buttons';
  const cancelBtn = document.createElement('button');
  cancelBtn.textContent = 'Cancel';
  cancelBtn.addEventListener('click', (e) => { e.stopPropagation(); renderDrawer(); });
  const saveBtn = document.createElement('button');
  saveBtn.className = 'primary';
  saveBtn.textContent = 'Save';
  saveBtn.addEventListener('click', (e) => { e.stopPropagation(); save(); });
  buttons.append(cancelBtn, saveBtn);
  row.replaceChildren(name, url, buttons);
  row.addEventListener('keydown', (e) => {
    e.stopPropagation();
    if (e.key === 'Enter') save();
    else if (e.key === 'Escape') renderDrawer();
  });
  setTimeout(() => { name.focus(); name.select(); }, 0);
}

function itemNode(b) {
  const row = document.createElement('div');
  row.className = 'bm-item' + (flashUrl && b.url === flashUrl ? ' flash' : '');
  row.draggable = true;
  row.title = `${b.title}\n${b.url}`;
  const title = document.createElement('span');
  title.className = 't';
  title.textContent = b.title || hostOf(b.url);
  const actions = document.createElement('span');
  actions.className = 'bm-actions';
  actions.append(
    actionButton(ICON_SVG.edit, 'Edit name and address', () => openEditor(row, b)),
    actionButton(ICON_SVG.trash, 'Delete bookmark', () => post('removeBookmark', { id: b.id })));
  row.append(favNode(b), title, actions);

  row.addEventListener('click', (e) => { if (!row.classList.contains('editing')) post('openUrl', { url: b.url, newTab: e.ctrlKey }); });
  row.addEventListener('auxclick', (e) => { if (e.button === 1) post('openUrl', { url: b.url, newTab: true }); });
  row.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); });
  row.addEventListener('dragstart', (e) => {
    dragBm = b;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', b.url);
    document.body.classList.add('bm-dragging');
    document.body.classList.toggle('bm-dragging-filed', Boolean(b.folder));
  });
  row.addEventListener('dragend', () => {
    dragBm = null;
    clearDropMarks();
    document.body.classList.remove('bm-dragging', 'bm-dragging-filed');
  });
  row.addEventListener('dragover', (e) => {
    if (!dragBm || dragBm.id === b.id) return;
    e.preventDefault();
    e.stopPropagation();
    const zone = dropZone(e, row);
    row.classList.toggle('drop-before', zone === 'before');
    row.classList.toggle('drop-after', zone === 'after');
    row.classList.toggle('drop-into', zone === 'into');
  });
  row.addEventListener('dragleave', (e) => {
    if (!row.contains(e.relatedTarget)) row.classList.remove('drop-before', 'drop-after', 'drop-into');
  });
  row.addEventListener('drop', (e) => {
    e.preventDefault();
    e.stopPropagation();
    if (!dragBm || dragBm.id === b.id) return;
    const zone = dropZone(e, row);
    if (zone === 'into') post('groupBookmarks', { id: dragBm.id, targetId: b.id });
    else post('moveBookmark', { id: dragBm.id, beforeId: zone === 'before' ? b.id : idAfter(b), folder: b.folder });
  });
  return row;
}

function folderNode(name, items, searching) {
  const box = document.createElement('div');
  box.className = 'bm-folder' + (name === '' ? ' top' : '') + (!searching && name && collapsed.has(name) ? ' collapsed' : '');
  if (name) {
    const head = document.createElement('div');
    head.className = 'bm-folder-head';
    head.innerHTML = ICON_SVG.chev + ICON_SVG.folder;
    const label = document.createElement('span');
    label.className = 'name';
    label.textContent = name;
    const count = document.createElement('span');
    count.className = 'count';
    count.textContent = items.length;
    const actions = document.createElement('span');
    actions.className = 'bm-actions';
    const rename = () => {
      if (!label.isConnected) return;
      label.replaceWith(inlineInput(name, (v) => {
        if (v && v !== name) post('renameFolder', { name, newName: v });
        else renderDrawer();
      }));
    };
    box.dataset.folder = name;
    box.startRename = rename;
    actions.append(
      actionButton(ICON_SVG.edit, 'Rename folder', rename),
      actionButton(ICON_SVG.trash, 'Delete folder (its bookmarks move out of it)', () => post('deleteFolder', { name })));
    head.append(label, count, actions);
    head.addEventListener('click', () => {
      if (collapsed.has(name)) collapsed.delete(name); else collapsed.add(name);
      saveCollapsed();
      box.classList.toggle('collapsed');
    });
    box.append(head);
  }
  const list = document.createElement('div');
  list.className = 'bm-items';
  list.append(...items.map(itemNode));
  box.append(list);

  // Drop onto the folder (its header or empty space) to file a bookmark there
  box.addEventListener('dragover', (e) => { if (dragBm) { e.preventDefault(); box.classList.add('drop'); } });
  box.addEventListener('dragleave', (e) => { if (!box.contains(e.relatedTarget)) box.classList.remove('drop'); });
  box.addEventListener('drop', (e) => {
    e.preventDefault();
    box.classList.remove('drop');
    if (dragBm && dragBm.folder !== name) post('moveBookmark', { id: dragBm.id, beforeId: '', folder: name });
  });
  return box;
}

/** Shown while dragging a bookmark that's in a folder: drop here to take it out. */
function outZone() {
  const zone = document.createElement('div');
  zone.className = 'bm-out';
  zone.textContent = 'Drop here to move out of the folder';
  zone.addEventListener('dragover', (e) => { if (dragBm) { e.preventDefault(); zone.classList.add('drop'); } });
  zone.addEventListener('dragleave', () => zone.classList.remove('drop'));
  zone.addEventListener('drop', (e) => {
    e.preventDefault();
    if (!dragBm) return;
    const firstTop = bm.items.find((x) => !x.folder && x.id !== dragBm.id);
    post('moveBookmark', { id: dragBm.id, beforeId: firstTop ? firstTop.id : '', folder: '' });
  });
  return zone;
}

function renderDrawer() {
  renderDrawerAdd();
  const words = bmSearch.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const match = (b) => words.every((w) => `${b.title} ${b.url} ${b.folder}`.toLowerCase().includes(w));
  const searching = words.length > 0;
  const nodes = [];

  if (newFolderInput) {
    const row = document.createElement('div');
    row.className = 'bm-folder-head';
    row.innerHTML = ICON_SVG.folder;
    row.append(inlineInput('New folder', (v) => {
      newFolderInput = false;
      if (v) post('createFolder', { name: v }); else renderDrawer();
    }));
    nodes.push(row);
  }

  nodes.push(outZone());
  const top = bm.items.filter((b) => !b.folder && match(b));
  nodes.push(folderNode('', top, searching));
  for (const f of bm.folders) {
    const items = bm.items.filter((b) => b.folder === f && match(b));
    if (searching && items.length === 0) continue;
    nodes.push(folderNode(f, items, searching));
  }
  if (bm.items.length === 0 && bm.folders.length === 0 && !newFolderInput) {
    const empty = document.createElement('div');
    empty.className = 'bm-empty';
    empty.textContent = 'No bookmarks yet. Use "Add this page" or the bookmark icon in the toolbar.';
    nodes.push(empty);
  }
  bmList.replaceChildren(...nodes);
  if (flashUrl) setTimeout(() => { flashUrl = ''; }, 1700);

  // Two bookmarks were just dropped together: let the user name the new folder right away
  if (pendingRename) {
    const box = [...bmList.querySelectorAll('.bm-folder')].find((n) => n.dataset.folder === pendingRename);
    pendingRename = '';
    if (box && box.startRename) setTimeout(box.startRename, 0);
  }
}
let pendingRename = '';

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
$('#downloads').addEventListener('click', () => post('downloads'));
document.querySelectorAll('.window-btns [data-cmd]').forEach((b) =>
  b.addEventListener('click', () => post(b.dataset.cmd)));

// ── empty areas move the window; double-click maximizes ───────────────────

let lastDown = { t: 0, x: 0, y: 0 };
document.addEventListener('mousedown', (e) => {
  if (e.button !== 0 || e.target.closest('button, input, .addr, .chip, #drawer, .popover')) return;
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
  renderSound();
  renderZoom();
  if (drawerOpen) renderDrawerAdd();
  if (isOpen(volPop)) fillVolume(false);
  // Keep the ad blocker dropdown's numbers live (at most twice a second)
  if (isOpen(abPop) && state.blocked !== abLastBlocked && !abTimer)
    abTimer = setTimeout(() => { abTimer = 0; post('adblockPanel'); }, 500);
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
    // Show it in the drawer so it can be filed into a folder right away
    flashUrl = state.url;
    if (!drawerOpen) toggleDrawer(true);
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
    else if (msg.type === 'closePopovers') closePopovers();
    else if (msg.type === 'adblockPanel') renderAdblockPanel(msg);
    else if (msg.type === 'bookmarks') {
      bm = msg;
      if (msg.renameFolder && msg.renameFolder.startsWith('New folder')) { pendingRename = msg.renameFolder; collapsed.delete(msg.renameFolder); }
      renderDrawer();
    }
  });
}

render();
reportViewport();
post('ready');
