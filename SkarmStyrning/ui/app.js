'use strict';

/* ================= Brygga till C# ================= */
const bridge = window.chrome && window.chrome.webview;
const pending = new Map();
let seq = 0;

function call(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    if (!bridge) { reject(new Error('Ingen koppling till appen')); return; }
    const id = ++seq;
    pending.set(id, { resolve, reject });
    bridge.postMessage({ id, cmd, args });
  });
}

/* ================= Tillstånd ================= */
let state = { monitors: [], profiles: [], settings: { autoAdapt: true, extendIntent: false, theme: 'system' }, busy: null };
let selectedId = null;
let draft = {};          // id -> {x, y}  (osparade positioner)
let draftPrimary = null; // id
let pickingHome = false; // välj datorns ingång
let drag = null;
let frozenView = null;

const $ = (s, r = document) => r.querySelector(s);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/* ================= Ikoner ================= */
const ICON = {
  star: '<svg viewBox="0 0 24 24"><path d="m12 3 2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1-4.4-4.3 6.1-.9z"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="m5 12.5 4.5 4.5L19 7.5"/></svg>',
  warn: '<svg viewBox="0 0 24 24"><path d="M12 9v4m0 4h.01M10.3 3.9 2.4 18a2 2 0 0 0 1.7 3h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/></svg>',
  info: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 11v5m0-8h.01"/></svg>',
  error: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="m15 9-6 6m0-6 6 6"/></svg>',
  success: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="m8 12.5 2.8 2.8L16 10"/></svg>',
  monitor: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M9 20h6m-3-4v4"/></svg>',
  layers: '<svg viewBox="0 0 24 24"><path d="m12 3 9 5-9 5-9-5 9-5z"/><path d="m3 13 9 5 9-5"/></svg>',
  play: '<svg viewBox="0 0 24 24"><path d="M7 5v14l11-7z"/></svg>',
  copy: '<svg viewBox="0 0 24 24"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V6a2 2 0 0 1 2-2h9"/></svg>',
  trash: '<svg viewBox="0 0 24 24"><path d="M4 7h16M10 11v6m4-6v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3"/></svg>',
  close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  home: '<svg viewBox="0 0 24 24"><path d="m4 11 8-7 8 7v8a1 1 0 0 1-1 1h-5v-6h-4v6H5a1 1 0 0 1-1-1z"/></svg>',
  plug: '<svg viewBox="0 0 24 24"><path d="M9 7V3m6 4V3M7 7h10v4a5 5 0 0 1-10 0zm5 9v5"/></svg>',
  speaker: '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/></svg>',
  speakerOff: '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9z"/><path d="m17 9 5 5m0-5-5 5"/></svg>',
  laptop: '<svg viewBox="0 0 24 24"><rect x="4" y="5" width="16" height="11" rx="1.5"/><path d="M2 19h20"/></svg>',
  link: '<svg viewBox="0 0 24 24"><path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/></svg>',
  edit: '<svg viewBox="0 0 24 24"><path d="M4 20h4L19 9l-4-4L4 16z"/></svg>',
  keyboard: '<svg viewBox="0 0 24 24"><rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/></svg>',
  eye: '<svg viewBox="0 0 24 24"><path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/></svg>',
};
const PORT = {
  hdmi: '<svg viewBox="0 0 24 24"><path d="M3 8h18v5l-3 3H6l-3-3z"/><path d="M7 11h10"/></svg>',
  dp: '<svg viewBox="0 0 24 24"><path d="M3 8h15l3 3v5H3z"/><path d="M7 12h9"/></svg>',
  dvi: '<svg viewBox="0 0 24 24"><rect x="2.5" y="7.5" width="19" height="9" rx="2"/><path d="M6 10.5h.01M9 10.5h.01M12 10.5h.01M15 10.5h.01M6 13.5h.01M9 13.5h.01M12 13.5h.01M15 13.5h.01M18.5 12h.01"/></svg>',
  vga: '<svg viewBox="0 0 24 24"><path d="M3 8h18l-2 8H5z"/><path d="M7.5 11h.01M10.5 11h.01M13.5 11h.01M16.5 11h.01M9 13.5h.01M12 13.5h.01M15 13.5h.01"/></svg>',
  usbc: '<svg viewBox="0 0 24 24"><rect x="3" y="8.5" width="18" height="7" rx="3.5"/><path d="M7.5 12h9"/></svg>',
  other: '<svg viewBox="0 0 24 24"><rect x="4" y="7" width="16" height="10" rx="2"/><path d="M8 12h8"/></svg>',
};

/* ================= Hjälpfunktioner ================= */
const monitors = () => state.monitors || [];
const byId = id => monitors().find(m => m.id === id);
// Den här datorns skärmar (inte skärmar som bara sitter på en annan dator).
const connected = () => monitors().filter(m => m.state !== 'disconnected' && !m.clientScreen);
const shortInput = n => String(n || '').replace('DisplayPort', 'DP');
const posOf = m => draft[m.id] || { x: m.layout.x, y: m.layout.y };
const isPrimary = m => draftPrimary ? draftPrimary === m.id : m.primary;
const isDirty = () => Object.keys(draft).some(id => {
  const m = byId(id); return m && (draft[id].x !== m.layout.x || draft[id].y !== m.layout.y);
}) || (draftPrimary && !byId(draftPrimary)?.primary);

function modeOf(m) {
  if (m.state !== 'on') return 'off';
  return m.cloneOf ? 'clone' : 'extend';
}

function currentInputName(m) {
  const i = m.input;
  if (!i || i.loading || i.current == null) return null;
  const o = (i.options || []).find(o => o.code === i.current);
  return o ? o.name : null;
}

/* ================= Scen (dra och släpp) ================= */
const stage = $('#stage');

function view() {
  if (frozenView) return frozenView;
  const ms = monitors();
  if (!ms.length) return { s: .1, ox: 0, oy: 0 };
  const rects = ms.map(m => ({ ...posOf(m), w: m.layout.w, h: m.layout.h }));
  const l = Math.min(...rects.map(r => r.x)), t = Math.min(...rects.map(r => r.y));
  const r = Math.max(...rects.map(r => r.x + r.w)), b = Math.max(...rects.map(r => r.y + r.h));
  const W = stage.clientWidth, H = stage.clientHeight, pad = 44;
  const s = Math.min((W - 2 * pad) / Math.max(1, r - l), (H - 2 * pad) / Math.max(1, b - t), 0.2);
  return { s, ox: (W - (r - l) * s) / 2 - l * s, oy: (H - (b - t) * s) / 2 - t * s };
}

function renderStage() {
  if (drag) return;
  const ms = monitors();
  if (!ms.length) { stage.innerHTML = '<div class="stage-empty">Inga skärmar hittade ännu …</div>'; return; }
  const v = view();
  const existing = new Map([...stage.querySelectorAll('.mon')].map(el => [el.dataset.id, el]));
  stage.querySelector('.stage-empty')?.remove();

  for (const m of ms) {
    const p = posOf(m);
    let el = existing.get(m.id);
    if (!el) { el = document.createElement('div'); el.className = 'mon'; el.dataset.id = m.id; stage.appendChild(el); }
    existing.delete(m.id);

    const w = m.layout.w * v.s, h = m.layout.h * v.s;
    el.style.left = (v.ox + p.x * v.s) + 'px';
    el.style.top = (v.oy + p.y * v.s) + 'px';
    el.style.width = w + 'px';
    el.style.height = h + 'px';
    const owner = m.owner?.kind;
    const ownedByClient = owner === 'client';
    el.className = `mon state-${m.state}${m.id === selectedId ? ' selected' : ''}${m.excluded && !ownedByClient ? ' excluded' : ''}`
      + `${ownedByClient ? ' owner-client' : owner === 'other' ? ' owner-other' : ''}${ownedByClient && m.owner.id && state.remoteId === m.owner.id ? ' controlling' : ''}`;

    const numSize = Math.max(16, Math.min(46, h * 0.3));
    const small = Math.max(9.5, Math.min(13, h * 0.085));
    const inName = currentInputName(m);
    const res = m.state === 'disconnected' ? 'Inte ansluten'
      : `${m.width}×${m.height}${m.hz ? ' · ' + m.hz + ' Hz' : ''}${m.state === 'off' ? ' · Av' : ''}`;
    el.innerHTML = `
      <div class="screen">
        <div class="badges">
          <span>${isPrimary(m) ? `<span class="badge star" title="Huvudskärm">${ICON.star}${w > 170 ? 'Huvud' : ''}</span>` : ''}</span>
          <span>${inName && h > 70 ? `<span class="badge">${esc(shortInput(inName))}</span>` : ''}</span>
        </div>
        <div class="num" style="font-size:${numSize}px">${m.number}</div>
        ${h > (ownedByClient ? 120 : 60) ? `<div class="nm" style="font-size:${small + 1}px">${esc(m.label)}</div>` : ''}
        ${h > (ownedByClient ? 150 : 90) ? `<div class="res" style="font-size:${small}px">${esc(res)}</div>` : ''}
        ${ownedByClient && h > 60 ? `<div class="owner-tag">${ICON.laptop}${esc(m.owner.name)}${m.owner.reachable ? '' : ' · ej ansluten'}</div>`
          : m.excluded && h > 80 ? `<div class="warn">Hoppas över · ${esc(m.excluded)}</div>` : ''}
      </div>`;
  }
  for (const el of existing.values()) el.remove();
}

stage.addEventListener('pointerdown', e => {
  const el = e.target.closest('.mon');
  if (!el) return;
  const m = byId(el.dataset.id);
  if (!m) return;
  frozenView = view();
  const p = posOf(m);
  drag = { id: m.id, el, sx: e.clientX, sy: e.clientY, px: p.x, py: p.y, moved: false };
  el.setPointerCapture(e.pointerId);
});

stage.addEventListener('pointermove', e => {
  if (!drag) return;
  const dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
  if (!drag.moved && Math.hypot(dx, dy) < 4) return;
  drag.moved = true;
  drag.el.classList.add('dragging');
  const v = frozenView;
  const x = Math.round(drag.px + dx / v.s), y = Math.round(drag.py + dy / v.s);
  draft[drag.id] = { x, y };
  drag.el.style.left = (v.ox + x * v.s) + 'px';
  drag.el.style.top = (v.oy + y * v.s) + 'px';
});

stage.addEventListener('pointerup', () => {
  if (!drag) return;
  const d = drag;
  drag = null;
  d.el.classList.remove('dragging');
  if (d.moved) {
    snap(d.id);
    renderStage();               // samma skala → mjuk animering till fäst plats
    frozenView = null;
    setTimeout(() => { renderStage(); renderDirty(); }, 300); // anpassa skalan efteråt
    renderDirty();
  } else {
    frozenView = null;
    select(d.id);
  }
});

/** Fäst skärmen kant i kant mot närmaste skärm, utan överlapp (som Windows). */
function snap(id) {
  const m = byId(id);
  const others = monitors().filter(o => o.id !== id).map(o => ({ ...posOf(o), w: o.layout.w, h: o.layout.h }));
  if (!others.length) return;
  const { x: x0, y: y0 } = draft[id];
  const w = m.layout.w, h = m.layout.h;
  const edge = Math.max(40, Math.min(w, h) / 10);
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
  const align = (v, size, os, osize) => Math.abs(v - os) < edge ? os : Math.abs(v + size - (os + osize)) < edge ? os + osize - size : v;
  const cands = [];
  for (const o of others) {
    const ya = align(clamp(y0, o.y - h + 1, o.y + o.h - 1), h, o.y, o.h);
    const xa = align(clamp(x0, o.x - w + 1, o.x + o.w - 1), w, o.x, o.w);
    cands.push([o.x + o.w, ya], [o.x - w, ya], [xa, o.y + o.h], [xa, o.y - h]);
  }
  const hits = (x, y) => others.some(o => x < o.x + o.w && o.x < x + w && y < o.y + o.h && o.y < y + h);
  let best = null, bd = Infinity;
  for (const [x, y] of cands) {
    if (hits(x, y)) continue;
    const d = (x - x0) ** 2 + (y - y0) ** 2;
    if (d < bd) { bd = d; best = { x, y }; }
  }
  if (best) draft[id] = best;
}

function renderDirty() { $('#dirtyBar').classList.toggle('show', !!isDirty()); }

function layoutPayload(apply, primary) {
  return {
    positions: monitors().map(m => ({ id: m.id, ...posOf(m) })),
    primary: primary || draftPrimary || monitors().find(m => m.primary)?.id || null,
    apply,
  };
}

async function saveLayout(apply, primary) {
  try {
    await call('saveLayout', layoutPayload(apply, primary));
    draft = {}; draftPrimary = null;
    render();
  } catch { /* felet visas som toast */ }
}

$('#btnUndo').onclick = () => { draft = {}; draftPrimary = null; render(); };
$('#btnSave').onclick = () => saveLayout(false);
$('#btnSaveApply').onclick = () => saveLayout(true);
$('#btnIdentify').onclick = () => call('identify').catch(() => {});
$('#btnLearn').onclick = () => { draft = {}; draftPrimary = null; call('learnLayout').catch(() => {}); };

/* ================= Lägen ================= */
function activeMode() {
  const on = connected().filter(m => m.state === 'on');
  if (state.settings.extendIntent) return 'extend';
  if (on.length > 1 && on.filter(m => !m.cloneOf).length === 1) return 'clone';
  if (on.length === 1) return 'only';
  return null;
}

function renderModes() {
  const mode = activeMode();
  for (const t of document.querySelectorAll('.mode-tile')) t.classList.toggle('active', t.dataset.mode === mode);
  const sel = byId(selectedId);
  const target = sel && sel.state !== 'disconnected' && !sel.clientScreen ? sel : null;
  const onlyOn = connected().filter(m => m.state === 'on');
  $('#onlyDesc').textContent = target ? `Visa bara ${target.label}`
    : mode === 'only' && onlyOn[0] ? `Just nu: ${onlyOn[0].label}` : 'Välj en skärm ovan';
  $('[data-mode="only"]').disabled = !target;
}

$('#modes').addEventListener('click', e => {
  const t = e.target.closest('.mode-tile');
  if (!t) return;
  if (t.dataset.mode === 'extend') call('extend').catch(() => {});
  if (t.dataset.mode === 'clone') call('clone').catch(() => {});
  if (t.dataset.mode === 'only' && selectedId) call('only', { id: selectedId }).catch(() => {});
});

/** Bygger grupper (en grupp = en bild) av hur alla skärmar ska visas och skickar dem. */
function applyMode(id, mode, withId) {
  const conn = connected();
  const modes = {};
  for (const m of conn) modes[m.id] = m.state !== 'on' ? { k: 'off' } : m.cloneOf ? { k: 'clone', w: m.cloneOf } : { k: 'extend' };
  if (mode === 'clone') {
    const partner = withId || conn.find(m => m.id !== id && m.state === 'on')?.id || conn.find(m => m.id !== id)?.id;
    if (!partner) { toast('Det finns ingen annan skärm att duplicera med.', 'error'); return; }
    modes[id] = { k: 'clone', w: partner };
  } else modes[id] = { k: mode };

  const parent = Object.fromEntries(conn.map(m => [m.id, m.id]));
  const find = x => parent[x] === x ? x : (parent[x] = find(parent[x]));
  const on = new Set();
  for (const [mid, md] of Object.entries(modes)) {
    if (md.k === 'off') continue;
    on.add(mid);
    if (md.k === 'clone' && md.w && parent[md.w] !== undefined) { on.add(md.w); parent[find(mid)] = find(md.w); }
  }
  if (!on.size) { toast('Minst en skärm måste vara på.', 'error'); return; }
  const groups = {};
  for (const mid of on) (groups[find(mid)] ||= []).push(mid);
  call('applyModes', { groups: Object.values(groups) }).catch(() => {});
}

/* ================= Inspektör ================= */
function select(id) {
  selectedId = id;
  pickingHome = false;
  render();
}

function renderInspector() {
  const box = $('#inspector');
  const m = byId(selectedId);
  if (!m) {
    box.innerHTML = `
      <div class="insp-empty">
        <div>
          <div class="big">${ICON.monitor}</div>
          <h3 style="font-size:16px;margin-bottom:6px;color:var(--text)">Välj en skärm</h3>
          <p>Klicka på en skärm i layouten för att slå på/av den, duplicera den eller byta ingång.</p>
        </div>
      </div>`;
    return;
  }

  const mode = modeOf(m);
  const conn = connected();
  const stateChip = m.state === 'on' ? '<span class="chip on"><span class="dot"></span>På</span>'
    : m.state === 'remote' ? `<span class="chip on">${ICON.laptop}${esc(m.clientScreen?.client || '')}</span>`
    : m.state === 'off' ? '<span class="chip"><span class="dot"></span>Av</span>'
    : '<span class="chip">Inte ansluten</span>';
  const chips = [stateChip];
  if (isPrimary(m)) chips.push(`<span class="chip accent">${ICON.star}Huvudskärm</span>`);
  if (m.excluded) chips.push(`<span class="chip warn">${ICON.warn}${esc(m.excluded)}</span>`);
  if (m.cloneOf) chips.push(`<span class="chip">Duplicerar ${esc(byId(m.cloneOf)?.label || '')}</span>`);

  const sub = [m.connector, m.state !== 'disconnected' ? `${m.width}×${m.height}` : null, m.hz ? `${m.hz} Hz` : null].filter(Boolean).join(' · ');

  let html = `
    <div class="insp-hero">
      <div class="insp-top">
        <div class="glyph ${m.state === 'on' ? '' : m.state}"><div>${m.number}</div></div>
        <div class="insp-title"><h3>${esc(m.label)}</h3><p>${esc(sub || 'Sparad skärm')}</p></div>
      </div>
      <div class="chips">${chips.join('')}</div>
    </div>`;

  if (m.clientScreen) {
    // En skärm som bara sitter på en annan dator, t.ex. laptopens inbyggda skärm.
    const cs = m.clientScreen;
    html += `
      <div class="section">
        <div class="owner-card client">
          <span class="owner-icon">${ICON.laptop}</span>
          <div><div class="owner-name">${esc(cs.client)}s skärm</div>
          <div class="owner-sub">${cs.online
            ? (m.owner?.reachable ? 'Dra pekaren över kanten hit – mus och tangentbord följer med.' : 'Ansluten.')
            : `${esc(cs.client)} är inte ansluten just nu.`}</div></div>
        </div>
        <div class="notice info" style="margin-top:12px">${ICON.info}<div>Den här skärmen sitter bara på <b>${esc(cs.client)}</b>.
          Dra den i layouten dit den står bredvid dina andra skärmar, så går pekaren över kanten dit.
          Upplösning och ingång styrs på ${esc(cs.client)}.</div></div>
        ${cs.online ? '' : `<div class="row-actions"><button class="btn danger" data-act="forget">${ICON.trash}Glöm skärmen</button></div>`}
      </div>
      <div class="section"><h4>Detaljer</h4><dl class="details">
        <dt>Dator</dt><dd>${esc(cs.client)}</dd>
        <dt>Upplösning</dt><dd>${m.width}×${m.height}</dd>
        <dt>Position</dt><dd>${posOf(m).x}, ${posOf(m).y}</dd>
      </dl></div>`;
    box.innerHTML = html;
    return;
  }

  if (m.state === 'disconnected') {
    // Visar skärmen en annan dator kan ingången ändå bytas – via den datorn.
    if (m.input) html += inputSection(m);
    html += `
      <div class="section">
        <div class="notice info">${ICON.info}<div>${m.input
          ? `Den här datorn ser inte skärmen just nu, men <b>${esc(m.input.via)}</b> gör det. Byt ingång ovan för att få tillbaka den.`
          : 'Skärmen är inte ansluten just nu. Den behåller sin plats i layouten och hamnar där när den kopplas in igen.'}</div></div>
        <div class="row-actions"><button class="btn danger" data-act="forget">${ICON.trash}Glöm skärmen</button></div>
      </div>`;
    box.innerHTML = html;
    return;
  }

  // Visning – visar skärmen en annan dator styr knapparna den datorn, inte den här.
  const others = conn.filter(o => o.id !== m.id);
  const ownerClient = m.owner?.kind === 'client' ? m.owner : null;
  if (ownerClient) {
    const cm = ownerClient.clientMode;
    html += `
    <div class="section">
      <h4>Visning på ${esc(ownerClient.name)}</h4>
      ${ownerClient.connected ? `
      <div class="segmented">
        <button data-cmode="extend" class="${cm === 'extend' ? 'active accent' : ''}">Utökad</button>
        <button data-cmode="clone" class="${cm === 'clone' ? 'active accent' : ''}" ${ownerClient.clientScreens > 1 ? '' : 'disabled title="Datorn har bara en skärm"'}>Duplicera</button>
      </div>
      <div class="hint"><span>Gäller ${esc(ownerClient.name)}s skärmar – den här datorn påverkas inte.</span></div>`
      : `<div class="hint"><span>${esc(ownerClient.name)} är inte ansluten – läget kan inte ändras härifrån.</span></div>`}
      ${m.state === 'on'
        ? `<div class="notice" style="margin-top:10px">${ICON.warn}<div>Den här datorn har kvar en osynlig yta på skärmen.
            <div style="margin-top:8px"><button class="btn small primary" data-act="releaseHere">Släpp skärmen</button></div></div></div>`
        : `<div class="notice info" style="margin-top:10px">${ICON.info}<div>Den här datorn använder inte skärmen. Byt ingång till den här datorn för att ta tillbaka den.</div></div>`}
    </div>`;
  } else html += `
    <div class="section">
      <h4>Visning</h4>
      <div class="segmented">
        <button data-mode="off" class="${mode === 'off' ? 'active' : ''}">Av</button>
        <button data-mode="extend" class="${mode === 'extend' ? 'active accent' : ''}">Utökad</button>
        <button data-mode="clone" class="${mode === 'clone' ? 'active accent' : ''}" ${others.length ? '' : 'disabled'}>Duplicera</button>
      </div>
      ${mode === 'clone' ? `<select class="select" id="cloneWith">${others.map(o => `<option value="${esc(o.id)}" ${o.id === m.cloneOf ? 'selected' : ''}>Samma bild som ${esc(o.label)}</option>`).join('')}</select>` : ''}
      <div class="row-actions">
        ${!isPrimary(m) && m.state === 'on' ? `<button class="btn small" data-act="primary">${ICON.star}Gör till huvudskärm</button>` : ''}
        <button class="btn small" data-act="only">${ICON.eye}Visa endast denna</button>
      </div>
    </div>`;

  // Dator på skärmen
  if ((state.computers || []).length) {
    const ow = m.owner || { kind: 'server', name: 'Den här datorn', manual: 'auto' };
    const sub = ow.kind === 'server' ? 'Visar den här datorn.'
      : ow.kind === 'other' ? 'Visar en annan källa. Välj vilken dator det är nedan.'
      : ow.reachable ? 'Dra pekaren över kanten – mus och tangentbord följer med.'
      : ow.connected ? 'Ansluten, men skärmen hittas inte bland datorns skärmar – välj vilken det är nedan.' : 'Inte ansluten just nu.';
    // Vilken av klientens skärmar är det här? (EDID-koden kan skilja sig mellan ingångar)
    const cms = ow.kind === 'client' && ow.connected ? (ow.clientMonitors || []) : [];
    const autoLabel = ow.clientMonitor && !ow.clientMonitorChosen
      ? `Automatiskt: ${cms.find(c => c.ident === ow.clientMonitor)?.label || ''}` : 'Automatiskt (hittades inte)';
    const cmSelect = cms.length ? `
        <label class="cm-pick ${ow.clientMonitor ? '' : 'missing'}"><span>Vilken av ${esc(ow.name)}s skärmar är det här?</span>
          <select class="select" id="cmSel" data-client="${esc(ow.id)}">
            <option value="">${esc(autoLabel)}</option>
            ${cms.map(c => `<option value="${esc(c.ident)}" ${ow.clientMonitorChosen && ow.clientMonitor === c.ident ? 'selected' : ''}>${esc(c.label)}</option>`).join('')}
          </select></label>` : '';
    const opts = [['auto', 'Automatiskt (efter ingång)'], ['server', 'Den här datorn'], ...state.computers.map(c => [c.id, c.name])];
    const manualNote = ow.manual && ow.manual !== 'auto'
      ? `<div class="hint"><span>Du har angett vad skärmen visar. Det gäller tills ingången byts nästa gång.</span></div>` : '';
    html += `
      <div class="section">
        <h4>Dator på skärmen</h4>
        <div class="owner-card ${ow.kind}">
          <span class="owner-icon">${ow.kind === 'client' ? ICON.laptop : ow.kind === 'other' ? ICON.link : ICON.monitor}</span>
          <div><div class="owner-name">${esc(ow.name)}</div><div class="owner-sub">${esc(sub)}</div></div>
        </div>
        <select class="select" id="ownerSel" title="Vilken dator skärmen visar">
          ${opts.map(([v, l]) => `<option value="${esc(v)}" ${(ow.manual || 'auto') === v ? 'selected' : ''}>${v === 'auto' ? '' : 'Visar just nu: '}${esc(l)}</option>`).join('')}
        </select>
        ${manualNote}
        ${cmSelect}
        ${connectionsHtml(m, ow)}
        ${(m.computers || []).length > 1 ? `<div class="on-list">Ansluten till: ${m.computers.map(c => `<span class="chip">${c.id ? ICON.laptop : ICON.monitor}${esc(c.name)}${c.input ? ' · ' + esc(c.input) : ''}</span>`).join('')}</div>` : ''}
      </div>`;
  }

  html += inputSection(m);
  html += modeSection(m);
  const inp = m.input;

  // Detaljer
  html += detailsSection(m, inp);
  box.innerHTML = html;
}

/** Kopplingar: vilken ingång varje dator sitter på. Profilerna och "Visar" bygger på den här tabellen. */
function connectionsHtml(m, ow) {
  const cn = ow.connections;
  if (!cn) return '';
  const missing = cn.rows.filter(r => r.code == null);
  return `
    <details class="connections" ${missing.length ? 'open' : ''}>
      <summary>Kopplingar${missing.length ? ` <span class="chip warn">${missing.length} okänd${missing.length > 1 ? 'a' : ''}</span>` : ''}</summary>
      <p class="conn-help">Vilken ingång på skärmen sitter varje dator på? Profiler byter ingång efter den här tabellen.</p>
      ${cn.rows.map(r => `
        <label class="conn-row">
          <span>${r.computer === 'server' ? ICON.monitor : ICON.laptop}${esc(r.name)}</span>
          <select class="select conn-sel" data-computer="${esc(r.computer)}">
            <option value="-1" ${r.code == null ? 'selected' : ''}>${r.code == null ? 'Okänd – välj ingång' : 'Inte ansluten'}</option>
            ${cn.options.map(o => `<option value="${o.code}" ${r.code === o.code ? 'selected' : ''}>${esc(o.name)}</option>`).join('')}
          </select>
        </label>`).join('')}
    </details>`;
}

/** Ingångar – direkt härifrån, eller via en ansluten dator när den här datorn inte når skärmen. */
function inputSection(m) {
  let html = `<div class="section"><h4>Ingång ${pickingHome ? '<button class="link" data-act="cancelPick">Avbryt</button>' : ''}</h4>`;
  const inp = m.input;
  if (!inp) {
    html += `<div class="notice info">${ICON.info}<div>Ingången kan styras när skärmen är på i Windows, eller när en ansluten dator ser den.</div></div>`;
  } else if (inp.loading) {
    html += `<div class="inputs"><div class="skeleton"></div><div class="skeleton"></div><div class="skeleton"></div><div class="skeleton"></div></div>`;
  } else {
    if (pickingHome) html += `<div class="notice info" style="margin-bottom:10px">${ICON.home}<div>Klicka på den ingång som <b>den här datorn</b> sitter på.</div></div>`;
    if (inp.via) html += `<div class="notice info via" style="margin-bottom:10px">${ICON.laptop}<div>Skärmen visar <b>${esc(inp.via)}</b>. Byten skickas via den datorn, eftersom den här datorn inte når skärmen just nu.</div></div>`;
    else if (inp.unreliable) html += `<div class="notice info" style="margin-bottom:10px">${ICON.info}<div>Den här skärmen rapporterar inte vilken ingång den visar${inp.assumed ? ' (den svarar alltid med sin egen port)' : ''}.
      Appen utgår från <b>senaste bytet</b>. Har du bytt med skärmens egna knappar – klicka på ingången den visar så stämmer det igen.</div></div>`;
    else if (inp.error) html += `<div class="notice" style="margin-bottom:10px">${ICON.warn}<div>${esc(inp.error)}. Du kan ändå prova att byta – vissa skärmar tar emot kommandon men svarar inte.</div></div>`;
    html += '<div class="inputs">';
    for (const o of inp.options || []) {
      const cur = o.code === inp.current;
      const home = inp.home === o.code;
      html += `
        <button class="input-btn ${cur ? 'current' : ''} ${pickingHome ? 'picking' : ''}" data-code="${o.code}">
          <span class="port">${PORT[o.kind] || PORT.other}</span>
          <span>
            <div class="input-name">${esc(o.name)}</div>
            <div class="input-sub">${cur ? 'Visas nu' : home || o.computer ? '' : 'Byt hit'}${home ? `<span class="home-tag">${ICON.home}Den här datorn</span>`
              : o.computer ? `<span class="home-tag who">${ICON.laptop}${esc(o.computer)}</span>` : ''}</div>
          </span>
          ${cur ? '<span class="live"></span>' : ''}
        </button>`;
    }
    html += '</div>';
    const homeName = (inp.options || []).find(o => o.code === inp.home)?.name;
    html += `<div class="hint"><span>${homeName ? `Datorn sitter på <b>${esc(homeName)}</b>.` : 'Appen vet inte säkert vilken ingång datorn sitter på.'}</span><button class="link" data-act="pickHome">${homeName ? 'Ändra' : 'Ange'}</button></div>`;
    if (inp.isHome === false && !inp.via) html += `<div class="notice" style="margin-top:10px">${ICON.warn}<div>Skärmen visar en annan källa just nu.</div></div>`;
  }
  return html + '</div>';
}

/** Upplösning och Hz – valet sparas och sätts tillbaka efter byten. */
function modeSection(m) {
  const md = m.modes;
  if (m.state !== 'on' || !md || !md.resolutions?.length) return '';
  const cur = md.current || { w: m.width, h: m.height, hz: m.hz };
  const pref = md.preferred;
  const resKey = r => `${r.w}x${r.h}`;
  const selRes = md.resolutions.find(r => r.w === cur.w && r.h === cur.h) || md.resolutions[0];
  const hzList = selRes.hz;
  const prefText = pref ? `${pref.w}×${pref.h} @ ${pref.hz} Hz` : null;
  const mismatch = pref && (pref.w !== cur.w || pref.h !== cur.h || pref.hz !== cur.hz);
  return `
    <div class="section">
      <h4>Upplösning och Hz</h4>
      <div class="mode-row">
        <select class="select" id="resSel">${md.resolutions.map(r =>
          `<option value="${resKey(r)}" ${r === selRes ? 'selected' : ''}>${r.w} × ${r.h}${r === md.resolutions[0] ? '  (högst)' : ''}</option>`).join('')}</select>
        <select class="select hz" id="hzSel">${hzList.map(h => `<option value="${h}" ${h === cur.hz ? 'selected' : ''}>${h} Hz</option>`).join('')}</select>
      </div>
      <div class="hint"><span>${pref
        ? (pref.pinned ? `Låst till <b>${prefText}</b> – sätts tillbaka efter byten.` : `Sparas automatiskt: <b>${prefText}</b> (bästa läget skärmen haft).`)
        : 'Välj ett läge så sparas det och sätts tillbaka efter byten.'}</span>
        ${pref?.pinned ? '<button class="link" data-act="autoMode">Automatiskt</button>' : ''}</div>
      ${mismatch ? `<div class="notice" style="margin-top:10px">${ICON.warn}<div>Skärmen står i ${cur.w}×${cur.h} @ ${cur.hz} Hz just nu.
        <div style="margin-top:8px"><button class="btn small primary" data-act="restoreMode">Byt till ${prefText}</button></div></div></div>` : ''}
    </div>`;
}

function detailsSection(m, inp) {
  return `
    <div class="section">
      <h4>Detaljer</h4>
      <dl class="details">
        <dt>Anslutning</dt><dd>${esc(m.connector || '–')}</dd>
        <dt>Upplösning</dt><dd>${m.width}×${m.height}</dd>
        <dt>Uppdatering</dt><dd>${m.hz ? m.hz + ' Hz' : '–'}</dd>
        <dt>Position</dt><dd>${posOf(m).x}, ${posOf(m).y}</dd>
        <dt>Windows-nummer</dt><dd>${m.windowsNumber ?? '–'}</dd>
        <dt>DDC/CI</dt><dd>${m.state !== 'on' ? '–' : !inp || inp.loading ? '…' : inp.via ? 'Via ' + esc(inp.via) : inp.error ? 'Svarar inte' : 'Ja'}</dd>
      </dl>
    </div>`;
}

$('#inspector').addEventListener('click', e => {
  const m = byId(selectedId);
  if (!m) return;
  const cseg = e.target.closest('[data-cmode]');
  if (cseg) {
    if (!cseg.classList.contains('active')) call('setClientMode', { client: m.owner?.id, mode: cseg.dataset.cmode }).catch(() => {});
    return;
  }
  const seg = e.target.closest('.segmented button');
  if (seg) {
    const mode = seg.dataset.mode;
    if (mode === modeOf(m)) return;
    applyMode(m.id, mode);
    return;
  }
  const inBtn = e.target.closest('.input-btn');
  if (inBtn) {
    const code = Number(inBtn.dataset.code);
    if (pickingHome) { pickingHome = false; call('setHome', { id: m.id, code }).catch(() => {}); return; }
    if (code === m.input?.current) return;
    call('setInput', { id: m.id, code }).catch(() => {});
    return;
  }
  const act = e.target.closest('[data-act]')?.dataset.act;
  if (act === 'primary') saveLayout(true, m.id);
  if (act === 'only') call('only', { id: m.id }).catch(() => {});
  if (act === 'releaseHere') applyMode(m.id, 'off');
  if (act === 'forget') { call('forget', { id: m.id }).catch(() => {}); selectedId = null; }
  if (act === 'autoMode') call('autoMode', { id: m.id }).catch(() => {});
  if (act === 'restoreMode' && m.modes?.preferred) {
    const p = m.modes.preferred;
    call('setMode', { id: m.id, w: p.w, h: p.h, hz: p.hz }).catch(() => {});
  }
  if (act === 'pickHome') { pickingHome = true; renderInspector(); }
  if (act === 'cancelPick') { pickingHome = false; renderInspector(); }
});

$('#inspector').addEventListener('change', e => {
  if (e.target.id === 'cloneWith') applyMode(selectedId, 'clone', e.target.value);
  if (e.target.id === 'ownerSel') call('setOwner', { id: selectedId, owner: e.target.value }).catch(() => {});
  if (e.target.classList.contains('conn-sel')) call('setConnection', { id: selectedId, computer: e.target.dataset.computer, code: Number(e.target.value) }).catch(() => {});
  if (e.target.id === 'cmSel') call('mapClientMonitor', { id: selectedId, client: e.target.dataset.client, ident: e.target.value }).catch(() => {});
  if (e.target.id === 'resSel' || e.target.id === 'hzSel') {
    const m = byId(selectedId);
    const [w, h] = $('#resSel').value.split('x').map(Number);
    const res = m?.modes?.resolutions.find(r => r.w === w && r.h === h);
    if (!res) return;
    // Byter man upplösning: ta samma Hz om den finns, annars den högsta.
    const wanted = Number($('#hzSel').value);
    const hz = e.target.id === 'hzSel' || res.hz.includes(wanted) ? (res.hz.includes(wanted) ? wanted : res.hz[0]) : res.hz[0];
    call('setMode', { id: selectedId, w, h, hz }).catch(() => {});
  }
});

/* ================= Datorer ================= */
function renderComputers() {
  const box = $('#computers');
  if (volDragging) return; // rita inte om medan volymen dras
  const cs = state.computers || [];
  const hk = state.settings.hotkeys?.home?.keys || [];
  $('#btnAllHome').hidden = !cs.some(c => c.connected);
  $('#btnAllHome').title = 'Byter tillbaka alla skärmar till den här datorn – via den andra datorn om det behövs' + (hk.length ? ` (${hk.join('+')})` : '');
  const n = state.net;
  let html = '';
  if (n?.error) html += `<div class="notice" style="grid-column:1/-1">${ICON.warn}<div>${esc(n.error)}</div></div>`;
  if (n?.firewallBlocked) html += firewallNotice();
  if (!cs.length) {
    html += `<div class="empty">Inga datorer ännu. Starta <b>Skärmstyrning klient</b> på t.ex. jobbdatorn och tryck <b>Lägg till dator</b> här.</div>`;
    box.innerHTML = html;
    return;
  }
  html += cs.map(c => `
    <div class="computer ${c.connected ? 'online' : ''} ${c.active ? 'active' : ''}" data-id="${esc(c.id)}">
      <div class="computer-head">
        <div class="computer-icon">${ICON.laptop}<span class="status-dot"></span></div>
        <div class="computer-title">
          <div class="computer-name">${esc(c.name)}</div>
          <div class="computer-sub">${c.active ? 'Styrs nu' : c.connected ? `Ansluten${c.address ? ' · ' + esc(c.address) : ''}` : 'Inte ansluten'}</div>
        </div>
        ${c.active ? '<span class="chip accent">Styrs nu</span>' : ''}
      </div>
      <div class="computer-meta">
        ${c.shownOn.length ? `<span>${ICON.monitor.replace('<svg', '<svg style="width:13px;height:13px;vertical-align:-2px"')} Visas på ${esc(c.shownOn.join(', '))}</span>`
          : c.connected ? '<span>Visas inte på någon skärm just nu</span>' : ''}
        ${c.monitors.length ? `<span class="faint">Ser skärmarna: ${esc(c.monitors.join(', '))}</span>` : ''}
      </div>
      ${c.audio ? `
      <div class="audio-row ${c.audio.sending ? 'live' : ''}">
        <span class="audio-icon" title="${c.audio.enabled ? 'Ljud från datorn spelas upp här' : 'Ljud är avstängt i inställningarna'}">${c.audio.enabled ? ICON.speaker : ICON.speakerOff}</span>
        <div class="audio-text">
          <div>${!c.audio.enabled ? 'Ljud av' : c.audio.sending ? 'Spelar upp ljud' : 'Ljud: tyst just nu'}${c.audio.sending && c.audio.transport ? ` <span class="faint">· ${esc(c.audio.transport)}</span>` : ''}</div>
          ${c.audio.device ? `<div class="faint">Från ${esc(c.audio.device)}</div>` : ''}
        </div>
        ${c.audio.enabled ? `<input type="range" class="vol" min="0" max="100" value="${Math.round(c.audio.volume * 100)}" data-vol="${esc(c.id)}" title="Volym">` : ''}
      </div>` : ''}
      <div class="profile-actions">
        <button class="btn small ghost" data-act="rename">${ICON.edit}Byt namn</button>
        <button class="btn small ghost danger" data-act="remove" title="Ta bort">${ICON.trash}</button>
      </div>
    </div>`).join('');
  box.innerHTML = html;
}

$('#computers').addEventListener('click', e => {
  const card = e.target.closest('.computer');
  const act = e.target.closest('[data-act]')?.dataset.act;
  if (!card || !act) return;
  const c = (state.computers || []).find(x => x.id === card.dataset.id);
  if (!c) return;
  if (act === 'remove') confirmDialog(`Ta bort ${c.name}?`, 'Datorn kopplas bort och måste parkopplas igen för att kunna anslutas.', 'Ta bort',
    () => call('removeComputer', { id: c.id }).catch(() => {}));
  if (act === 'rename') modal(`
      <h3>Byt namn</h3><p>Namnet visas på skärmarna och i menyerna.</p>
      <div class="field"><label for="cName">Namn</label><input class="input" id="cName" value="${esc(c.name)}" maxlength="40"></div>
      <div class="modal-actions"><button class="btn ghost" data-close>Avbryt</button><button class="btn primary" id="cSave">Spara</button></div>`,
    root => {
      const inp = $('#cName', root); inp.focus(); inp.select();
      const save = () => { call('renameComputer', { id: c.id, name: inp.value }).catch(() => {}); closeModal(); };
      $('#cSave', root).onclick = save;
      inp.addEventListener('keydown', ev => { if (ev.key === 'Enter') save(); });
    });
});

$('#btnAddComputer').onclick = openPairing;
// Volym per dator – skickas medan man drar, men gränssnittet ritas inte om under tiden.
let volDragging = false;
$('#computers').addEventListener('input', e => {
  const r = e.target.closest('[data-vol]');
  if (!r) return;
  volDragging = true;
  call('setAudioVolume', { id: r.dataset.vol, value: Number(r.value) / 100 }).catch(() => {});
});
$('#computers').addEventListener('change', e => { if (e.target.closest('[data-vol]')) volDragging = false; });
$('#btnAllHome').onclick = () => call('allHome').catch(() => {});

function firewallNotice() {
  const pub = state.net?.publicNetwork;
  return `<div class="notice fw-notice" style="grid-column:1/-1">${ICON.warn}
    <div><b>Windows brandvägg släpper inte in andra datorer.</b>
      ${pub ? 'Ditt nätverk är markerat som <i>offentligt</i>, så ' : ''}klienten kan varken hitta eller nå den här datorn.
      Regeln gäller bara Skärmstyrning och portarna 47811/47812 – anslutningar kräver ändå parkoppling och är krypterade.
      <div style="margin-top:10px"><button class="btn primary small" data-act="firewall">Tillåt i brandväggen</button></div>
    </div></div>`;
}

document.addEventListener('click', e => {
  if (e.target.closest('[data-act="firewall"]')) call('allowFirewall').catch(() => {});
});

function openPairing() {
  call('startPairing').catch(() => {});
  modal(`
    <h3>Lägg till dator</h3>
    <div id="pairFw" style="margin-bottom:12px"></div>
    <p>Gör så här på den andra datorn (t.ex. jobbdatorn):</p>
    <ol class="steps">
      <li>Starta <b>Skärmstyrning klient</b> (se README för hur du bygger den).</li>
      <li>Skriv in adressen till den här datorn, eller tryck <b>Sök</b>:
        <div class="addr-list" id="pairAddrs"></div></li>
      <li>Skriv in koden och tryck <b>Parkoppla</b>:</li>
    </ol>
    <div class="pair-code" id="pairCode">······</div>
    <div class="pair-timer" id="pairTimer"></div>
    <div class="notice info" style="margin-top:14px">${ICON.info}<div>Allt som skickas mellan datorerna är krypterat. Koden fungerar en gång och i 5 minuter.
      Om Windows frågar om brandväggen – tillåt <b>privata nätverk</b>.</div></div>
    <div class="modal-actions"><button class="btn" id="pairNew">Ny kod</button><span style="flex:1"></span><button class="btn primary" data-close>Klar</button></div>`,
    root => {
      root.dataset.kind = 'pairing';
      $('#pairNew', root).onclick = () => call('startPairing').catch(() => {});
      root._cleanup = () => call('stopPairing').catch(() => {});
      updatePairing();
    });
}

function updatePairing() {
  const root = $('#modalRoot').firstElementChild;
  if (!root || root.dataset.kind !== 'pairing') return;
  const n = state.net || {};
  const fw = $('#pairFw', root);
  if (fw) fw.innerHTML = n.firewallBlocked ? firewallNotice() : '';
  $('#pairCode', root).textContent = n.pairCode ? n.pairCode.replace(/(\d{3})(\d{3})/, '$1 $2') : '––– –––';
  $('#pairCode', root).classList.toggle('expired', !n.pairCode);
  $('#pairTimer', root).textContent = n.pairCode ? `Giltig i ${Math.floor(n.pairSecondsLeft / 60)}:${String(n.pairSecondsLeft % 60).padStart(2, '0')}` : 'Koden har gått ut – tryck Ny kod.';
  const port = n.port && n.port !== 47811 ? ':' + n.port : '';
  $('#pairAddrs', root).innerHTML = (n.addresses || []).map((a, i) => `<code class="${i === 0 ? 'main' : ''}">${esc(a + port)}</code>`).join('') || '<span class="muted">Hittade ingen nätverksadress.</span>';
}
setInterval(() => {
  const n = state.net;
  if (n?.pairCode && n.pairSecondsLeft > 0) { n.pairSecondsLeft--; if (n.pairSecondsLeft === 0) n.pairCode = null; updatePairing(); }
}, 1000);

/* ================= Profiler ================= */
function renderProfiles() {
  const box = $('#profiles');
  const ps = state.profiles || [];
  if (!ps.length) {
    box.innerHTML = `<div class="empty">Inga profiler ännu. Ställ in skärmarna som du vill ha dem och tryck <b>Ny profil</b> – t.ex. ”Spel”, ”Jobb” eller ”Film”.</div>`;
    return;
  }
  const next = state.settings.hotkeys?.next;
  const tip = ps.length > 1 && next?.keys?.length
    ? `<div class="profiles-tip">${ICON.keyboard}<span>Tryck ${kbdHtml(next.keys)} för att växla till nästa profil – fungerar i hela Windows.</span></div>` : '';
  box.innerHTML = tip + ps.map(p => `
    <div class="profile ${p.active ? 'active' : ''}" data-name="${esc(p.name)}">
      <div class="profile-head">
        <div class="profile-icon">${ICON.layers}</div>
        <div class="profile-name" title="${esc(p.name)}">${esc(p.name)}</div>
        ${p.active ? '<span class="chip accent profile-active">Senast</span>' : ''}
      </div>
      ${hotkeyButton(p.hotkey, 'hotkey')}
      <div class="profile-meta">
        ${p.layout ? `<span title="${esc(p.layout)}">${ICON.monitor.replace('<svg', '<svg style="width:12px;height:12px;vertical-align:-2px"')} ${esc(p.layout)}</span>` : ''}

      </div>
      ${(p.requires || []).length ? `<div class="requires">${p.requires.map(r =>
        `<span class="chip ${r.connected ? 'on' : 'warn'}" title="${r.connected ? 'Ansluten' : 'Inte ansluten – starta klienten där'}">${ICON.laptop}Kräver ${esc(r.name)}${r.connected ? '' : ' · ej ansluten'}</span>`).join('')}</div>` : ''}
      ${(p.changes || []).length ? `
      <div class="profile-changed">
        <div class="pc-head">${ICON.edit}<b>Ändrat sedan profilen lades på</b></div>
        <ul>${p.changes.map(c => `<li>${esc(c)}</li>`).join('')}</ul>
        <div class="pc-actions">
          <button class="btn small primary" data-act="update" title="Skriv över ”${esc(p.name)}” med hur det är just nu">Spara ändringar</button>
          <button class="btn small" data-act="saveNew">Ny profil</button>
        </div>
      </div>` : ''}
      <div class="profile-actions">
        <button class="btn primary small" data-act="apply" ${p.blocked ? `disabled title="${esc(p.blocked)}"` : ''}>${ICON.play}Använd</button>
        <button class="btn small ghost" data-act="edit" title="Redigera profilen utan att byta till den">${ICON.edit}</button>
        <button class="btn small ghost" data-act="copy" title="Kopiera genvägskommando">${ICON.copy}</button>
        <button class="btn small ghost danger" data-act="delete" title="Ta bort">${ICON.trash}</button>
      </div>
    </div>`).join('');
}

$('#profiles').addEventListener('click', e => {
  const card = e.target.closest('.profile');
  const act = e.target.closest('[data-act]')?.dataset.act;
  if (!card || !act) return;
  const name = card.dataset.name;
  if (act === 'apply') call('applyProfile', { name }).catch(() => {});
  if (act === 'copy') call('copyCommand', { name }).catch(() => {});
  if (act === 'edit') editProfile(name);
  if (act === 'update') call('saveProfile', { name }).catch(() => {});
  if (act === 'saveNew') newProfileDialog();
  if (act === 'hotkey') {
    const p = (state.profiles || []).find(x => x.name === name);
    recordHotkey(`Kortkommando för ”${name}”`, p?.hotkey, value => call('setProfileHotkey', { name, hotkey: value }));
  }
  if (act === 'delete') confirmDialog(`Ta bort ”${name}”?`, 'Profilen tas bort. Det går inte att ångra.', 'Ta bort', () => call('deleteProfile', { name }).catch(() => {}));
});

$('#btnNewProfile').onclick = () => newProfileDialog();

function newProfileDialog() {
  const def = `Profil ${(state.profiles || []).length + 1}`;
  modal(`
    <h3>Ny profil</h3>
    <p>Sparar hur det är just nu: vilken dator varje skärm visar, och den här datorns skärmlayout och upplösningar.
      Stämmer inte "Visar" på någon skärm – rätta det i skärmens panel först.</p>
    <div class="field"><label for="pName">Namn</label><input class="input" id="pName" value="${esc(def)}" maxlength="40"></div>
    <div class="modal-actions"><button class="btn ghost" data-close>Avbryt</button><button class="btn primary" id="pSave">Spara profil</button></div>`,
    root => {
      const name = $('#pName', root);
      name.focus(); name.select();
      const save = () => {
        call('saveProfile', { name: name.value }).catch(() => {});
        closeModal();
      };
      $('#pSave', root).onclick = save;
      name.addEventListener('keydown', e => { if (e.key === 'Enter') save(); });
    });
}

/* ================= Redigera profil ================= */
async function editProfile(name) {
  let pr;
  try { pr = await call('getProfile', { name }); } catch { return; }
  const mons = pr.monitors;

  const resOptions = m => {
    const list = [];
    for (const r of m.resolutions || []) for (const hz of r.hz) list.push({ w: r.w, h: r.h, hz });
    if (m.width && !list.some(o => o.w === m.width && o.h === m.height && o.hz === m.hz)) list.unshift({ w: m.width, h: m.height, hz: m.hz });
    const sel = m.width ? `${m.width}|${m.height}|${m.hz}` : '';
    return `<option value="">Standard${m.preferred ? ` (${esc(m.preferred)})` : ''}</option>`
      + list.map(o => { const v = `${o.w}|${o.h}|${o.hz}`; return `<option value="${v}" ${v === sel ? 'selected' : ''}>${o.w} × ${o.h} @ ${o.hz} Hz</option>`; }).join('');
  };
  const showOptions = m => m.options.map(o =>
    `<option value="${esc(o.value)}" ${o.value === m.value ? 'selected' : ''}>${esc(o.label)}</option>`).join('');

  modal(`
    <h3>Redigera profil</h3>
    <p>Välj vad varje skärm ska visa. Ändringarna sparas utan att profilen läggs på.</p>
    <div class="notice info" style="margin-bottom:14px">${ICON.info}<div>Väljer du en annan dator byter skärmen till den datorns ingång,
      och den här datorn slutar använda skärmen. Vilken ingång varje dator sitter på ställer du in under <b>Kopplingar</b> i skärmens panel.</div></div>
    <div class="field"><label for="peName">Namn</label><input class="input" id="peName" value="${esc(pr.name)}" maxlength="40"></div>
    <div class="pe-list" id="peList">
      ${mons.map(m => `
        <div class="pe-mon" data-dev="${esc(m.dev)}">
          <div class="pe-head">
            <span class="pe-num">${m.number}</span>
            <div class="pe-title"><b>${esc(m.name)}</b><span>${m.connected ? '' : 'Inte ansluten just nu'}</span></div>
            <label class="pe-primary" title="Huvudskärm i profilen"><input type="radio" name="pePrim" value="${esc(m.dev)}" ${pr.primary === m.dev ? 'checked' : ''}>${ICON.star}Huvudskärm</label>
          </div>
          <div class="pe-grid two">
            <label><span>Visar</span><select class="select pe-show">${showOptions(m)}</select></label>
            <label><span>Upplösning och Hz</span><select class="select pe-res">${resOptions(m)}</select></label>
          </div>
          <div class="pe-note"></div>
        </div>`).join('')}
    </div>
    <div class="modal-actions">
      <button class="btn ghost" data-close>Avbryt</button>
      <span style="flex:1"></span>
      <button class="btn" id="peSave">Spara</button>
      <button class="btn primary" id="peApply">Spara och använd</button>
    </div>`,
    root => {
      root.querySelector('.modal').classList.add('wide');
      const monOf = row => mons.find(x => x.dev === row.dataset.dev);
      const showOf = row => row.querySelector('.pe-show').value;

      const sync = () => {
        for (const row of root.querySelectorAll('.pe-mon')) {
          const m = monOf(row), v = showOf(row);
          const opt = m.options.find(o => o.value === v);
          const isClient = !['server', 'off', 'keep'].includes(v) && !v.startsWith('clone:');
          const note = row.querySelector('.pe-note');
          note.className = 'pe-note' + (opt?.needsMapping ? ' warn' : '');
          note.innerHTML = opt?.needsMapping
            ? `${ICON.warn}Appen vet inte vilken ingång datorn sitter på. Ange det under <b>Kopplingar</b> i skärmens panel.`
            : isClient ? `${ICON.laptop}Skärmen byter till den datorn – den här datorn använder den inte.`
            : v === 'keep' ? 'Profilen rör inte den här skärmen.' : '';
          const ext = v === 'server';
          row.querySelector('.pe-res').disabled = !ext;
          row.querySelector('.pe-primary input').disabled = !ext;
          row.querySelector('.pe-primary').classList.toggle('disabled', !ext);
          row.classList.toggle('off', v === 'off' || isClient);
        }
        const prim = root.querySelector('input[name="pePrim"]:checked');
        if (!prim || prim.disabled) {
          const first = [...root.querySelectorAll('input[name="pePrim"]')].find(r => !r.disabled);
          if (first) first.checked = true;
        }
      };
      root.addEventListener('change', sync);
      sync();

      const collect = apply => {
        const monitors = [...root.querySelectorAll('.pe-mon')].map(row => {
          const v = showOf(row), rv = row.querySelector('.pe-res').value;
          const [w, h, hz] = v === 'server' && rv ? rv.split('|').map(Number) : [null, null, null];
          return { dev: row.dataset.dev, value: v, width: w, height: h, hz };
        });
        if (!monitors.some(m => m.value === 'server' || m.value === 'keep' || m.value.startsWith('clone:'))) {
          toast('Den här datorn behöver minst en skärm i profilen.', 'error'); return;
        }
        call('saveProfileEdit', {
          original: pr.name, name: $('#peName', root).value,
          primary: root.querySelector('input[name="pePrim"]:checked')?.value || null, monitors, apply,
        }).then(() => closeModal()).catch(() => {});
      };
      $('#peSave', root).onclick = () => collect(false);
      $('#peApply', root).onclick = () => collect(true);
    });
}

/* ================= Kortkommandon ================= */
const kbdHtml = keys => keys.map(k => `<kbd>${esc(k)}</kbd>`).join('<span class="plus">+</span>');

function hotkeyButton(hk, act, extra = '') {
  const keys = hk?.keys || [];
  const title = hk?.failed ? 'Upptaget av ett annat program – klicka för att välja ett annat' : 'Klicka för att ändra kortkommando';
  return `<button class="kbd-btn ${hk?.failed ? 'failed' : ''}" data-act="${act}" ${extra} title="${title}">
    ${keys.length ? kbdHtml(keys) : '<span class="kbd-empty">+ Lägg till kortkommando</span>'}
    ${hk?.failed ? `<span class="kbd-warn">${ICON.warn}Upptaget</span>` : ''}
  </button>`;
}

/** Översätter KeyboardEvent.code till namnet i .NET:s Keys-uppräkning. */
function keyName(code) {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit[0-9]$/.test(code)) return 'D' + code.slice(5);
  if (/^Numpad[0-9]$/.test(code)) return 'NumPad' + code.slice(6);
  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) return code;
  return ({ ArrowLeft: 'Left', ArrowRight: 'Right', ArrowUp: 'Up', ArrowDown: 'Down', Home: 'Home', End: 'End',
    PageUp: 'PageUp', PageDown: 'PageDown', Insert: 'Insert', Delete: 'Delete', Space: 'Space' })[code] || null;
}
function keyLabel(k) {
  if (/^D[0-9]$/.test(k)) return k.slice(1);
  if (k.startsWith('NumPad')) return 'Num ' + k.slice(6);
  return ({ Left: '←', Right: '→', Up: '↑', Down: '↓', PageUp: 'PgUp', PageDown: 'PgDn', Delete: 'Del', Insert: 'Ins' })[k] || k;
}

/** Visar en ruta där man trycker in ett nytt kortkommando. save(value) får "" för borttaget. */
function recordHotkey(title, current, save, after) {
  call('suspendHotkeys').catch(() => {});
  let value = current?.value || '';
  let saved = false;

  modal(`
    <h3>${esc(title)}</h3>
    <p>Tryck den tangentkombination du vill använda. Den fungerar i hela Windows – även när appen ligger i systemfältet.</p>
    <div class="recorder" id="rec"></div>
    <div class="rec-hint" id="recHint"></div>
    <div class="modal-actions">
      <button class="btn ghost danger" id="rClear">${ICON.trash}Ta bort</button>
      <span style="flex:1"></span>
      <button class="btn ghost" data-close>Avbryt</button>
      <button class="btn primary" id="rSave">Spara</button>
    </div>`,
    root => {
      const rec = $('#rec', root), hint = $('#recHint', root);
      const show = (keys, listening) => {
        rec.innerHTML = keys.length ? kbdHtml(keys) : '<span class="rec-empty">Inget kortkommando</span>';
        rec.classList.toggle('listening', !!listening);
      };
      const setHint = (text, kind = '') => { hint.textContent = text; hint.className = 'rec-hint ' + kind; };
      show(current?.keys || [], true);
      setHint('Håll in Ctrl, Alt eller Shift och tryck en tangent');

      const onKey = e => {
        if (e.key === 'Escape') return;
        if (e.key === 'Tab') return;
        e.preventDefault();
        e.stopPropagation();
        const mods = [];
        if (e.ctrlKey) mods.push('Ctrl');
        if (e.shiftKey) mods.push('Shift');
        if (e.altKey) mods.push('Alt');
        const k = keyName(e.code);
        if (!k) { show(mods, true); return; }
        if (!e.ctrlKey && !e.altKey) { show([...mods, keyLabel(k)], true); setHint('Använd minst Ctrl eller Alt, annars krockar det med vanligt skrivande.', 'bad'); return; }
        value = [...mods, k].join('+');
        show([...mods, keyLabel(k)], false);
        if (e.ctrlKey && e.altKey && !e.shiftKey)
          setHint('Obs! Ctrl+Alt är samma som AltGr på svenskt tangentbord och kan hindra tecken som @, $ och {. Lägg gärna till Shift.', 'warn');
        else setHint('Bra! Tryck Spara – eller en annan kombination.', 'good');
      };
      document.addEventListener('keydown', onKey, true);
      root._cleanup = () => {
        document.removeEventListener('keydown', onKey, true);
        if (!saved) call('resumeHotkeys').catch(() => {});
        after && after();
      };
      const done = v => { saved = true; save(v).catch(() => call('resumeHotkeys').catch(() => {})); closeModal(); };
      $('#rSave', root).onclick = () => done(value);
      $('#rClear', root).onclick = () => done('');
    });
}

/* ================= Inställningar ================= */
$('#btnSettings').onclick = openSettings;
$('#autoPill').onclick = openSettings;

function openSettings() {
  const s = state.settings;
  modal(`
    <div class="drawer-head"><h3>Inställningar</h3><button class="icon-btn" data-close>${ICON.close}</button></div>
    <div class="group">
      <h4>Smart utökning</h4>
      <div class="toggle-row"><div><strong>Anpassa automatiskt</strong><span>När en skärm stängs av, byter källa eller kommer tillbaka</span></div><label class="switch"><input type="checkbox" id="sAuto" ${s.autoAdapt ? 'checked' : ''}><i></i></label></div>
      <div class="about"><ul>
        <li>Gäller efter att du valt <b>Utöka alla</b>. Väljer du något annat läge ändras ingenting automatiskt.</li>
        <li>Skärmar som visar en annan källa eller är avstängda hoppas över, och de andra skjuts ihop.</li>
        <li>Skärmar som ligger kvar i Windows fast de visar något annat (vanligt med HDMI/DVI) kommer tillbaka när du trycker <b>Utöka alla</b>.</li>
      </ul></div>
    </div>
    <div class="group">
      <h4>Andra datorer</h4>
      <div class="toggle-row"><div><strong>Dela mus och tangentbord</strong><span>Pekaren går över till skärmar som visar en annan dator</span></div><label class="switch"><input type="checkbox" id="sShare" ${s.shareInput ? 'checked' : ''}><i></i></label></div>
      <div class="toggle-row"><div><strong>Spela upp ljud från andra datorer</strong><span>Allt ljud från en ansluten dator hörs i den här datorns högtalare</span></div><label class="switch"><input type="checkbox" id="sAudio" ${s.shareAudio ? 'checked' : ''}><i></i></label></div>
      <div class="toggle-row"><div><strong>Håll vaken</strong><span>Den här datorn och anslutna datorer går inte i viloläge (trycker en osynlig F15 när ingen använder dem)</span></div><label class="switch"><input type="checkbox" id="sAwake" ${s.keepAwake ? 'checked' : ''}><i></i></label></div>
      <div class="toggle-row"><div><strong>Dela urklipp</strong><span>Kopiera text på en dator och klistra in på den andra</span></div><label class="switch"><input type="checkbox" id="sClip" ${s.shareClipboard ? 'checked' : ''}><i></i></label></div>
      <div class="about"><ul>
        <li><b>Ctrl+Shift+Alt</b> + valfri tangent tar alltid tillbaka musen.</li>
        <li>Urklipp delas krypterat så fort du kopierar text, med alla anslutna datorer. Stäng av ovan om du kopierar sådant som inte ska lämna datorn.</li>
        <li>Klienter ansluter till port <b>${state.net?.port ?? 47811}</b> på ${esc((state.net?.addresses || []).join(', ') || 'den här datorn')}.</li>
        <li>Fungerar det inte via VPN? Tryck <b>Testa</b> i klienten – den säger om nätverket blockeras.</li>
      </ul></div>
    </div>
    <div class="group">
      <h4>Kortkommandon</h4>
      <div class="hk-row"><div><strong>Nästa profil</strong><span>Växlar runt mellan dina profiler</span></div>${hotkeyButton(s.hotkeys?.next, 'hk', 'data-which="next"')}</div>
      <div class="hk-row"><div><strong>Alla skärmar hit</strong><span>Byt tillbaka alla skärmar till den här datorn</span></div>${hotkeyButton(s.hotkeys?.home, 'hk', 'data-which="home"')}</div>
      <div class="hk-row"><div><strong>Utöka alla</strong><span>Smart utökning till skärmarna som visar datorn</span></div>${hotkeyButton(s.hotkeys?.extend, 'hk', 'data-which="extend"')}</div>
      <p class="about" style="margin-top:8px">Varje profil har också ett eget kortkommando – ändra det på profilkortet. Kortkommandona fungerar i hela Windows så länge appen körs.</p>
    </div>
    <div class="group">
      <h4>Utseende</h4>
      <div class="segmented" id="sTheme">
        <button data-v="system" class="${s.theme === 'system' ? 'active accent' : ''}">System</button>
        <button data-v="light" class="${s.theme === 'light' ? 'active accent' : ''}">Ljust</button>
        <button data-v="dark" class="${s.theme === 'dark' ? 'active accent' : ''}">Mörkt</button>
      </div>
    </div>
    <div class="group">
      <h4>Windows</h4>
      <button class="btn" id="sWin">${ICON.monitor}Öppna bildskärmsinställningar</button>
    </div>
    <div class="group">
      <h4>Om</h4>
      <p class="about">Positioner, inställningar och profiler sparas i <code>%APPDATA%\\SkarmStyrning</code>. Ingången styrs via DDC/CI – slå på det i skärmens meny om en skärm inte svarar.</p>
    </div>`,
    root => {
      $('#sAuto', root).onchange = e => call('setAuto', { value: e.target.checked }).catch(() => {});
      $('#sShare', root).onchange = e => call('setShareInput', { value: e.target.checked }).catch(() => {});
      $('#sClip', root).onchange = e => call('setShareClipboard', { value: e.target.checked }).catch(() => {});
      $('#sAudio', root).onchange = e => call('setShareAudio', { value: e.target.checked }).catch(() => {});
      $('#sAwake', root).onchange = e => call('setKeepAwake', { value: e.target.checked }).catch(() => {});
      $('#sTheme', root).onclick = e => {
        const b = e.target.closest('button'); if (!b) return;
        for (const x of root.querySelectorAll('#sTheme button')) x.className = x === b ? 'active accent' : '';
        call('setTheme', { value: b.dataset.v }).catch(() => {});
      };
      $('#sWin', root).onclick = () => call('openWindowsSettings').catch(() => {});
      for (const b of root.querySelectorAll('[data-act="hk"]')) b.onclick = () => {
        const which = b.dataset.which;
        recordHotkey({ next: 'Kortkommando: Nästa profil', home: 'Kortkommando: Alla skärmar hit', extend: 'Kortkommando: Utöka alla' }[which],
          state.settings.hotkeys?.[which],
          value => call('setHotkey', { which, hotkey: value }),
          () => setTimeout(openSettings, 0));
      };
    }, true);
}

/* ================= Modal, bekräftelse, toast ================= */
function modal(html, init, drawer = false) {
  closeModal();
  const root = document.createElement('div');
  root.className = 'backdrop' + (drawer ? ' drawer-backdrop' : '');
  root.innerHTML = `<div class="${drawer ? 'drawer' : 'modal'}" role="dialog" aria-modal="true">${html}</div>`;
  root.addEventListener('mousedown', e => { if (e.target === root) closeModal(); });
  root.addEventListener('click', e => { if (e.target.closest('[data-close]')) closeModal(); });
  $('#modalRoot').appendChild(root);
  init && init(root);
}
function closeModal() {
  const root = $('#modalRoot').firstElementChild;
  $('#modalRoot').innerHTML = '';
  if (root && root._cleanup) root._cleanup();
}
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeModal(); });

function confirmDialog(title, text, okText, onOk) {
  modal(`<h3>${esc(title)}</h3><p>${esc(text)}</p>
    <div class="modal-actions"><button class="btn ghost" data-close>Avbryt</button><button class="btn primary" id="cOk">${esc(okText)}</button></div>`,
    root => { $('#cOk', root).onclick = () => { closeModal(); onOk(); }; });
}

function toast(text, kind = 'info') {
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.innerHTML = `${ICON[kind] || ICON.info}<div>${esc(text)}</div>`;
  $('#toasts').appendChild(el);
  const list = $('#toasts').children;
  while (list.length > 3) list[0].remove();
  setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 220); }, kind === 'error' ? 7000 : 4500);
}

/* ================= Toppfält ================= */
function renderTop() {
  const ms = monitors();
  const conn = connected();
  const on = conn.filter(m => m.state === 'on').length;
  const sub = $('#subtitle');
  if (state.busy) sub.innerHTML = `<span class="spinner"></span>${esc(state.busy)} …`;
  else {
    const remote = ms.filter(m => m.state === 'remote').length;
    const saved = ms.filter(m => m.state === 'disconnected').length;
    sub.textContent = ms.length
      ? `${conn.length} anslutna · ${on} på${remote ? ` · ${remote} på andra datorer` : ''}${saved ? ` · ${saved} sparade` : ''}`
      : 'Läser in skärmar …';
  }

  const rp = $('#remotePill');
  rp.hidden = !state.remote;
  if (state.remote) $('#remoteText').textContent = `Styr ${state.remote} · Ctrl+Shift+Alt + tangent = tillbaka`;

  const live = state.settings.autoAdapt && state.settings.extendIntent;
  $('#autoPill').classList.toggle('live', live);
  $('#autoText').textContent = live ? 'Anpassar automatiskt' : state.settings.autoAdapt ? 'Auto: väntar på Utöka alla' : 'Auto av';
  document.body.classList.toggle('busy', !!state.busy);
  const t = state.settings.theme;
  if (t === 'system') document.documentElement.removeAttribute('data-theme'); else document.documentElement.dataset.theme = t;
}

$('#btnRefresh').onclick = async () => {
  const b = $('#btnRefresh');
  b.classList.add('spin');
  try { await call('refresh'); } catch { }
  b.classList.remove('spin');
};

/* ================= Rendering ================= */
function render() {
  if (selectedId && !byId(selectedId)) selectedId = null;
  if (!selectedId) selectedId = monitors().find(m => m.state === 'on')?.id ?? null;
  renderTop();
  renderStage();
  renderDirty();
  renderModes();
  renderInspector();
  renderComputers();
  renderProfiles();
  updatePairing();
}

window.addEventListener('resize', () => { frozenView = null; renderStage(); });

if (bridge) {
  bridge.addEventListener('message', e => {
    const m = e.data;
    switch (m.type) {
      case 'reply': {
        const p = pending.get(m.id);
        if (!p) return;
        pending.delete(m.id);
        m.ok ? p.resolve(m.result) : p.reject(new Error(m.error));
        break;
      }
      case 'state':
        state = m.state;
        // Släng utkast för skärmar som inte finns längre.
        for (const id of Object.keys(draft)) if (!byId(id)) delete draft[id];
        render();
        break;
      case 'busy':
        state.busy = m.text;
        renderTop();
        break;
      case 'toast':
        toast(m.text, m.kind);
        break;
      case 'paired': {
        const root = $('#modalRoot').firstElementChild;
        if (root?.dataset.kind === 'pairing') { root._cleanup = null; closeModal(); }
        break;
      }
    }
  });
  call('ready').catch(() => {});
} else {
  render();
}
