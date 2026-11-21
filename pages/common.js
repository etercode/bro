'use strict';

// Bridge to Bro (C#). Pages ask with bro.send/get and listen with bro.on.
const bro = (() => {
  const wv = window.chrome && window.chrome.webview;
  const handlers = {};
  const emit = (msg) => (handlers[msg.type] || []).forEach((fn) => fn(msg));
  if (wv) wv.addEventListener('message', (e) => e.data && emit(e.data));
  return {
    connected: Boolean(wv),
    send(type, data = {}) { if (wv) wv.postMessage({ type, ...data }); },
    get(...what) { this.send('get', { what: what.join(',') }); },
    on(type, fn) { (handlers[type] ||= []).push(fn); },
    emit, // lets a preview feed in sample data
  };
})();

bro.on('settings', (m) => {
  document.documentElement.style.setProperty('--accent', m.accent);
  document.body.classList.toggle('private', m.isPrivate);
});

// ── small helpers ────────────────────────────────────────────────────────

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') node.className = v;
    else if (k === 'dataset') Object.assign(node.dataset, v);
    else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
    else if (v !== undefined && v !== null && v !== false) node.setAttribute(k, v === true ? '' : v);
  }
  node.append(...children.flat().filter((c) => c !== null && c !== undefined && c !== false));
  return node;
}

function hostOf(url) {
  try { return new URL(url).hostname.replace(/^www\./, ''); } catch { return url; }
}

/** The site's icon, or its first letter on a tinted square. */
function siteIcon(url, favicon) {
  if (favicon) return el('span', { class: 'site-icon' }, el('img', { src: favicon, alt: '' }));
  const host = hostOf(url);
  let hue = 0;
  for (const ch of host) hue = (hue * 31 + ch.charCodeAt(0)) % 360;
  const icon = el('span', { class: 'site-icon letter' }, (host.charAt(0) || '?').toUpperCase());
  icon.style.setProperty('--hue', hue);
  return icon;
}

function formatBytes(n) {
  if (n < 0 || n === undefined) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(n >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}

function dayLabel(ms) {
  const d = new Date(ms);
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const day = new Date(d); day.setHours(0, 0, 0, 0);
  const diff = Math.round((today - day) / 86400000);
  if (diff === 0) return 'Today';
  if (diff === 1) return 'Yesterday';
  return d.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long', year: diff > 300 ? 'numeric' : undefined });
}

function timeLabel(ms) {
  return new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

const ICON = {
  trash: '<svg viewBox="0 0 24 24"><path d="M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12"/></svg>',
  edit: '<svg viewBox="0 0 24 24"><path d="M5 19h3.5L18 9.5 14.5 6 5 15.5z"/><path d="M13 7.5l3.5 3.5"/></svg>',
  x: '<svg viewBox="0 0 24 24"><path d="M7 7l10 10M17 7L7 17"/></svg>',
  folder: '<svg viewBox="0 0 24 24"><path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/></svg>',
  pause: '<svg viewBox="0 0 24 24"><path d="M9 6v12M15 6v12"/></svg>',
  play: '<svg viewBox="0 0 24 24"><path d="M8 5.5v13l10-6.5z"/></svg>',
  grip: '<svg viewBox="0 0 24 24" class="filled"><circle cx="9" cy="7" r="1.4"/><circle cx="15" cy="7" r="1.4"/><circle cx="9" cy="12" r="1.4"/><circle cx="15" cy="12" r="1.4"/><circle cx="9" cy="17" r="1.4"/><circle cx="15" cy="17" r="1.4"/></svg>',
};

function iconButton(name, title, onclick, extra = '') {
  const b = el('button', { class: `icon-btn ${extra}`, title, 'aria-label': title, onclick });
  b.innerHTML = ICON[name];
  return b;
}

// Sidebar: mark the current page
document.addEventListener('DOMContentLoaded', () => {
  const page = location.pathname.split('/').pop();
  document.querySelectorAll('.side a').forEach((a) => a.classList.toggle('current', a.getAttribute('href') === page));
});
