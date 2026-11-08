'use strict';
// The Session Archive viewer: plain JavaScript over the local API. Everything from the export is
// untrusted text: it is escaped before it reaches the page, and links only go to http(s) or this app.

const $ = (sel, root = document) => root.querySelector(sel);
const main = $('#main');
let sessions = [];
const sessionCache = new Map();

// ------------------------------------------------------------------ small helpers
function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
async function api(url) {
  const r = await fetch(url);
  if (!r.ok) {
    let msg = r.status + ' ' + r.statusText;
    try { const j = await r.json(); msg = j.error || j.detail || j.title || msg; } catch { }
    throw new Error(msg);
  }
  return r.json();
}
function when(ts) {
  if (!ts) return '';
  const d = new Date(ts);
  return isNaN(d) ? String(ts) : d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}
function bytes(n) {
  if (n == null) return '';
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; n = Number(n);
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return (i ? n.toFixed(1) : n) + ' ' + u[i];
}
function el(html) { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstElementChild; }
function enc(s) { return encodeURIComponent(s); }
function rawUrl(path, download) { return '/api/raw?path=' + enc(path) + (download ? '&download=1' : ''); }
function shortPath(p) { if (!p) return ''; const parts = String(p).split(/[\\/]/).filter(Boolean); return parts.slice(-2).join('\\'); }
function badges(s) {
  let b = '';
  if (s.archived) b += '<span class="badge archived">archived</span>';
  if (s.deleted) b += '<span class="badge deleted">deleted</span>';
  if (s.kind === 'cowork') b += '<span class="badge cowork">cowork</span>';
  if (s.kind === 'cli') b += '<span class="badge cli">not in the app</span>';
  if (s.starred) b += '<span class="badge">starred</span>';
  return b;
}
function snippet(s) {
  return esc(s).replace(/\u0001/g, '<mark>').replace(/\u0002/g, '</mark>');
}
function loading(text = 'Loading…') { main.innerHTML = `<div class="empty">${esc(text)}</div>`; }
function fail(e) { main.innerHTML = `<div class="empty warn">${esc(e.message || e)}</div>`; }

// ------------------------------------------------------------------ markdown (a safe subset)
function inline(s) {
  return s.split(/(`[^`]*`)/g).map(part => {
    if (/^`[^`]*`$/.test(part)) return '<code>' + esc(part.slice(1, -1)) + '</code>';
    let h = esc(part);
    h = h.replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
    h = h.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '<span title="$2">$1</span>');
    h = h.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    h = h.replace(/(^|[\s(])\*([^*\s][^*]*)\*(?=[\s).,;:!?]|$)/g, '$1<em>$2</em>');
    return h;
  }).join('');
}
function md(src) {
  const lines = String(src ?? '').replace(/\r\n/g, '\n').split('\n');
  const out = [];
  let i = 0;
  const isBlock = l => /^\s*(```|~~~|#{1,6}\s|[-*+]\s|\d+\.\s|\|)/.test(l);
  while (i < lines.length) {
    const l = lines[i];
    const fence = l.match(/^\s*(```|~~~)/);
    if (fence) {
      const buf = []; i++;
      while (i < lines.length && !lines[i].trimStart().startsWith(fence[1])) buf.push(lines[i++]);
      i++;
      out.push('<pre><code>' + esc(buf.join('\n')) + '</code></pre>');
      continue;
    }
    const h = l.match(/^(#{1,6})\s+(.*)$/);
    if (h) { const n = Math.min(h[1].length + 1, 4); out.push(`<h${n}>${inline(h[2])}</h${n}>`); i++; continue; }
    if (/^\s*\|.*\|\s*$/.test(l) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      const row = r => r.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
      let t = '<table><thead><tr>' + row(l).map(c => `<th>${inline(c)}</th>`).join('') + '</tr></thead><tbody>';
      i += 2;
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) t += '<tr>' + row(lines[i++]).map(c => `<td>${inline(c)}</td>`).join('') + '</tr>';
      out.push(t + '</tbody></table>');
      continue;
    }
    const li = l.match(/^\s*([-*+]|\d+\.)\s+(.*)$/);
    if (li) {
      const ordered = /\d/.test(li[1]);
      const items = [];
      while (i < lines.length) {
        const m = lines[i].match(/^\s*([-*+]|\d+\.)\s+(.*)$/);
        if (m) { items.push(m[2]); i++; continue; }
        if (/^\s{2,}\S/.test(lines[i]) && items.length) { items[items.length - 1] += ' ' + lines[i].trim(); i++; continue; }
        break;
      }
      out.push((ordered ? '<ol>' : '<ul>') + items.map(x => `<li>${inline(x)}</li>`).join('') + (ordered ? '</ol>' : '</ul>'));
      continue;
    }
    if (!l.trim()) { i++; continue; }
    const para = [];
    while (i < lines.length && lines[i].trim() && !(para.length && isBlock(lines[i]))) para.push(lines[i++]);
    out.push('<p>' + para.map(inline).join('<br>') + '</p>');
  }
  return '<div class="md">' + out.join('') + '</div>';
}

// ------------------------------------------------------------------ the session list
async function loadSessions() {
  sessions = await api('/api/sessions');
  drawSessions();
}
function drawSessions() {
  const f = $('#filter').value.trim().toLowerCase();
  const show = {
    active: $('#show-active').checked, archived: $('#show-archived').checked,
    deleted: $('#show-deleted').checked, cli: $('#show-cli').checked,
  };
  const current = currentSessionKey();
  const list = sessions.filter(s => {
    if (s.deleted && !show.deleted) return false;
    if (s.archived && !show.archived) return false;
    if (s.kind === 'cli' && !show.cli) return false;
    if (!s.deleted && !s.archived && s.kind !== 'cli' && !show.active) return false;
    if (!f) return true;
    return [s.title, s.cwd, s.key, s.sid, s.first_prompt, s.project].some(x => x && String(x).toLowerCase().includes(f));
  });
  $('#session-count').textContent = `${list.length} of ${sessions.length} sessions`;
  $('#sessions').innerHTML = list.map(s => `
    <li><a href="#/s/${enc(s.key)}" class="${s.key === current ? 'current' : ''}">
      <div class="t">${esc(s.title || s.key)}${badges(s)}</div>
      <div class="m">${esc(shortPath(s.cwd) || s.project || '')} · ${esc(when(s.last_activity))} · ${s.user_msgs ?? 0} prompts${s.subagents ? ` · ${s.subagents} sub-agents` : ''}</div>
    </a></li>`).join('');
}
for (const id of ['filter', 'show-active', 'show-archived', 'show-deleted', 'show-cli'])
  $('#' + id).addEventListener('input', drawSessions);

function currentSessionKey() {
  const m = location.hash.match(/^#\/s\/([^/]+)/);
  return m ? decodeURIComponent(m[1]) : null;
}

// ------------------------------------------------------------------ routing
$('#global-search').addEventListener('submit', e => {
  e.preventDefault();
  const q = $('#q').value.trim();
  if (q) location.hash = '#/search/' + enc(q);
});
window.addEventListener('hashchange', route);

async function route() {
  const h = location.hash.replace(/^#\/?/, '');
  const parts = h.split('/');
  drawSessions();
  try {
    if (parts[0] === 's' && parts[1]) {
      const key = decodeURIComponent(parts[1]);
      const tab = parts[2] || 'chat';
      await showSession(key, tab, parts.slice(3).map(decodeURIComponent));
    } else if (parts[0] === 'search') {
      await showSearch(decodeURIComponent(parts.slice(1).join('/')));
    } else if (parts[0] === 'memory') {
      await showAllMemory();
    } else if (parts[0] === 'browse') {
      await showBrowse(decodeURIComponent(parts.slice(1).join('/')));
    } else if (parts[0] === 'file') {
      await showFile(decodeURIComponent(parts.slice(1).join('/')));
    } else if (parts[0] === 'about') {
      await showAbout();
    } else {
      await showHome();
    }
  } catch (e) {
    fail(e);
  }
}

// ------------------------------------------------------------------ home, about
async function showHome() {
  loading();
  const info = await api('/api/info');
  const c = info.counts;
  const recent = sessions.slice(0, 15);
  main.innerHTML = `
    <h1>Session Archive</h1>
    <div class="meta"><span>Export: ${esc(info.folder)}</span><span>Indexed ${esc(when(info.info.indexedAt))}</span></div>
    <p>${c.sessions} sessions, ${c.transcripts} transcripts (sessions and sub-agents), ${Number(c.records).toLocaleString()} records,
       ${c.memory} memory files, ${c.dirs} session folders.</p>
    <h2>Most recent</h2>
    <table class="grid"><tr><th>Session</th><th>Folder</th><th>Last activity</th><th>Prompts</th></tr>
    ${recent.map(s => `<tr><td><a href="#/s/${enc(s.key)}">${esc(s.title || s.key)}</a>${badges(s)}</td>
      <td class="path">${esc(s.cwd || '')}</td><td>${esc(when(s.last_activity))}</td><td>${s.user_msgs ?? 0}</td></tr>`).join('')}
    </table>`;
}

async function showAbout() {
  loading();
  const info = await api('/api/info');
  let m = null;
  try { m = JSON.parse(info.info.manifest || 'null'); } catch { }
  if (!m) { main.innerHTML = '<h1>About this export</h1><p class="muted">No manifest: the index was built without an export run.</p>'; return; }
  main.innerHTML = `
    <h1>About this export</h1>
    <div class="meta"><span>Exported ${esc(when(m.exportedAt))} on ${esc(m.machine)} as ${esc(m.user)}</span><span>${esc(m.seconds)} s</span><span>tool ${esc(m.version)}</span></div>
    <p class="muted">${esc(m.additive)}.</p>
    <h2>Sources</h2>
    <table class="grid"><tr><th>Source</th><th>Read from</th><th>Files</th><th>Size</th><th>Copied now</th><th>Errors</th></tr>
    ${m.sources.map(s => `<tr><td><a href="#/browse/${enc(s.name)}">${esc(s.name)}</a><div class="small muted">${esc(s.description || '')}</div></td>
      <td class="path">${esc(s.root)}</td><td>${s.missing ? 'missing' : Number(s.files).toLocaleString()}</td><td>${bytes(s.bytes)}</td>
      <td>${s.copied ?? ''}</td><td>${s.errorCount ? `<span class="warn">${s.errorCount}</span>` : 0}</td></tr>`).join('')}
    </table>
    <h2>Always left out</h2>
    <ul>${m.alwaysExcluded.map(x => `<li>${esc(x)}</li>`).join('')}</ul>
    ${m.sources.filter(s => (s.errors && s.errors.length) || (s.skipped && s.skipped.length)).map(s => `
      <h2>${esc(s.name)}: not copied</h2>
      ${s.errors.length ? `<pre>${esc(s.errors.join('\n'))}</pre>` : ''}
      ${s.skipped.length ? `<details><summary>${s.skipped.length} links and files skipped on purpose</summary><pre>${esc(s.skipped.join('\n'))}</pre></details>` : ''}`).join('')}`;
}

// ------------------------------------------------------------------ one session
async function getSession(key) {
  if (!sessionCache.has(key)) sessionCache.set(key, await api('/api/session/' + enc(key)));
  return sessionCache.get(key);
}

async function showSession(key, tab, rest) {
  loading();
  const data = await getSession(key);
  const s = data.session;
  const mains = data.transcripts.filter(t => t.kind === 'main');
  const agents = data.transcripts.filter(t => t.kind !== 'main');
  const tabs = [
    ['chat', 'Conversation'], ['agents', `Sub-agents (${agents.length})`], ['files', `Files (${new Set([...data.touched.map(t => t.path), ...data.backups.map(b => b.path)]).size})`],
    ['folders', `Session folders (${data.dirs.length})`], ['memory', `Memory (${data.memory.length})`], ['details', 'Details'],
  ];
  main.innerHTML = `
    <h1>${esc(s.title || s.key)}${badges(s)}</h1>
    <div class="meta">
      <span>${esc(s.cwd || '')}</span><span>started ${esc(when(s.created))}</span><span>last ${esc(when(s.last_activity))}</span>
      ${s.model ? `<span>${esc(s.model)}</span>` : ''}<span>${s.user_msgs ?? 0} prompts · ${s.tool_calls ?? 0} tool calls · ${bytes(s.bytes)}</span>
    </div>
    <nav class="tabs">${tabs.map(([id, label]) => `<a href="#/s/${enc(key)}/${id}" class="${id === tab ? 'on' : ''}">${esc(label)}</a>`).join('')}</nav>
    <section id="tab"></section>`;
  const box = $('#tab');
  if (tab === 'chat') {
    const tid = rest[0] ? Number(rest[0]) : (s.main_tid ?? mains[mains.length - 1]?.id ?? data.transcripts[0]?.id);
    if (!tid) { box.innerHTML = '<div class="empty">This session has no transcript in the export.</div>'; return; }
    await showChat(data, tid, rest[1] ? Number(rest[1]) : null, box);
  } else if (tab === 'agents') {
    box.innerHTML = agents.length ? `<table class="grid"><tr><th>Sub-agent</th><th>Type</th><th>Started</th><th>Records</th><th>Tool calls</th></tr>
      ${agents.map(t => { const m = parseMeta(t.meta); return `<tr><td><a href="#/s/${enc(key)}/chat/${t.id}">${esc(m.description || t.agent_name || t.agent_id || t.sid)}</a></td>
        <td>${esc(m.agentType || t.kind)}</td><td>${esc(when(t.first_ts))}</td><td>${t.visible}</td><td>${t.tool_calls}</td></tr>`; }).join('')}</table>`
      : '<div class="empty">No sub-agents.</div>';
  } else if (tab === 'files') {
    drawFiles(data, box, key);
  } else if (tab === 'folders') {
    box.innerHTML = data.dirs.length ? `<p class="muted small">Folders named after this session, copied whole into the export: its scratchpad, background-task output, tool results, file history and uploads.</p>
      <table class="grid"><tr><th>What</th><th>Folder in the export</th><th>Files</th><th>Size</th></tr>
      ${data.dirs.map(d => `<tr><td>${esc(d.label)}</td><td class="path"><a href="#/browse/${enc(d.path)}">${esc(d.path)}</a></td><td>${d.files}</td><td>${bytes(d.bytes)}</td></tr>`).join('')}</table>`
      : '<div class="empty">No folders named after this session.</div>';
  } else if (tab === 'memory') {
    await drawMemory(data.memory, box, s.project);
  } else {
    box.innerHTML = `
      <table class="grid">${Object.entries(s).map(([k, v]) => `<tr><th>${esc(k)}</th><td class="path">${esc(v ?? '')}</td></tr>`).join('')}</table>
      <h2>Transcripts</h2>
      <table class="grid"><tr><th>Kind</th><th>File in the export</th><th>Lines</th><th>Size</th><th>From</th><th>To</th></tr>
      ${data.transcripts.map(t => `<tr><td>${esc(t.kind)}</td><td class="path"><a href="#/s/${enc(key)}/chat/${t.id}">${esc(t.path)}</a> · <a href="${rawUrl(t.path, true)}">download</a></td>
        <td>${t.lines}</td><td>${bytes(t.bytes)}</td><td>${esc(when(t.first_ts))}</td><td>${esc(when(t.last_ts))}</td></tr>`).join('')}</table>
      ${data.meta ? `<h2>The desktop app's record</h2><pre>${esc(pretty(data.meta))}</pre>` : ''}`;
  }
}
function parseMeta(m) { try { return JSON.parse(m || '{}') || {}; } catch { return {}; } }
function pretty(text) { try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; } }

function drawFiles(data, box, key) {
  const versions = new Map();
  for (const b of data.backups) { if (!versions.has(b.path)) versions.set(b.path, []); versions.get(b.path).push(b); }
  const paths = new Map(data.touched.map(t => [t.path, t]));
  for (const p of versions.keys()) if (!paths.has(p)) paths.set(p, { path: p, ops: 'edited (file history)', exists: null });
  if (!paths.size) { box.innerHTML = '<div class="empty">This session touched no files.</div>'; return; }
  box.innerHTML = `<p class="muted small">Files this session read, wrote or searched, linked by their path on this machine (not copied). "Open" shows the file as it is now.
    Versions are Claude's own file-history copies, which are in the export.</p>
    <table class="grid"><tr><th>Path</th><th>What the session did</th><th>Last</th><th>Now</th><th>Versions in the export</th></tr>
    ${[...paths.values()].map(t => {
      const v = versions.get(t.path) || [];
      const now = t.exists === true ? `<a href="/api/local?path=${enc(t.path)}" target="_blank" rel="noopener">open</a>`
        : t.exists === false ? '<span class="muted">gone</span>' : '';
      const jump = t.tid ? ` · <a href="#/s/${enc(key)}/chat/${t.tid}/${t.first_line}">first use</a>` : '';
      return `<tr><td class="path">${esc(t.path)}</td><td>${esc(t.ops)}${jump}</td><td>${esc(when(t.last_ts))}</td><td>${now}</td>
        <td>${v.map(b => b.present ? `<a href="#/file/${enc(b.backup)}">v${b.version}</a>` : `<span class="muted">v${b.version}</span>`).join(' ')}</td></tr>`;
    }).join('')}</table>`;
}

async function drawMemory(files, box, project) {
  if (!files.length) { box.innerHTML = '<div class="empty">No memory files for this project.</div>'; return; }
  box.innerHTML = `<p class="muted small">The memory of project ${esc(project)}: shared by every session in that folder.</p>` +
    files.map(f => `<details open><summary><a href="#/file/${enc(f.path)}">${esc(f.name)}</a> · ${bytes(f.bytes)} · ${esc(when(f.mtime))}</summary><div data-mem="${esc(f.path)}" class="bubble muted">…</div></details>`).join('');
  for (const f of files.slice(0, 60)) {
    const div = box.querySelector(`[data-mem="${CSS.escape(f.path)}"]`);
    try { div.innerHTML = md(await (await fetch(rawUrl(f.path))).text()); div.classList.remove('muted'); } catch (e) { div.textContent = e.message; }
  }
}

// ------------------------------------------------------------------ the conversation
async function showChat(data, tid, line, box) {
  const t = data.transcripts.find(x => x.id === tid);
  const key = data.session.key;
  const agentTids = new Map();
  for (const a of data.transcripts) { const m = parseMeta(a.meta); if (m.toolUseId) agentTids.set(m.toolUseId, a.id); }
  const mains = data.transcripts.filter(x => x.kind === 'main');
  const state = { start: null, tid, all: localStorage.getItem('showAll') === '1', first: null, last: null, done: false, busy: false, tools: new Map() };
  box.innerHTML = `
    <div class="bar">
      ${mains.length > 1 || t?.kind !== 'main' ? `<select id="tpick">${data.transcripts.map(x => `<option value="${x.id}" ${x.id === tid ? 'selected' : ''}>${esc(x.kind === 'main' ? 'Conversation from ' + when(x.first_ts) : 'Sub-agent: ' + (parseMeta(x.meta).description || x.agent_id))}</option>`).join('')}</select>` : ''}
      <label><input type="checkbox" id="all" ${state.all ? 'checked' : ''}> Show every record (API requests, file history, bookkeeping)</label>
      <form id="find"><input type="search" id="findq" placeholder="Find in this session"></form>
      <span class="muted small">${t ? `${t.visible} messages · ${t.lines} records · ${esc(t.path)}` : ''}</span>
    </div>
    <div id="findres"></div>
    <div id="earlier"></div>
    <div id="records"></div>
    <div id="more" class="more muted small"></div>`;
  $('#tpick')?.addEventListener('change', e => location.hash = `#/s/${enc(key)}/chat/${e.target.value}`);
  $('#all').addEventListener('change', e => { localStorage.setItem('showAll', e.target.checked ? '1' : '0'); route(); });
  $('#find').addEventListener('submit', async e => {
    e.preventDefault();
    const q = $('#findq').value.trim();
    const res = $('#findres');
    if (!q) { res.innerHTML = ''; return; }
    res.innerHTML = '<div class="muted">Searching…</div>';
    try {
      const hits = await api(`/api/search?q=${enc(q)}&session=${enc(key)}&limit=300`);
      res.innerHTML = `<details open class="hits"><summary>${hits.length} matches in this session</summary>${hits.map(h =>
        `<div class="hit"><a href="#/s/${enc(key)}/chat/${h.tid}/${h.line}">${esc(h.tkind === 'main' ? '' : 'sub-agent · ')}${esc(h.type)} · ${esc(when(h.ts))}</a><div class="snip">${snippet(h.snippet)}</div></div>`).join('')}</details>`;
    } catch (err) { res.innerHTML = `<div class="warn">${esc(err.message)}</div>`; }
  });

  const records = $('#records');
  const more = $('#more');
  const ctx = { key, tid, agentTids, tools: state.tools };

  async function load(params, prepend) {
    if (state.busy) return [];
    state.busy = true;
    try {
      const items = await api(`/api/transcript/${tid}/records?${params}&count=200&all=${state.all ? 1 : 0}`);
      const frag = document.createDocumentFragment();
      for (const it of items) { const node = renderRecord(it, ctx); if (node) frag.appendChild(node); }
      if (prepend) {
        const before = main.scrollHeight;
        records.prepend(frag);
        main.scrollTop += main.scrollHeight - before;
      } else {
        records.appendChild(frag);
      }
      return items;
    } finally {
      state.busy = false;
    }
  }
  function updateEarlier() {
    const e = $('#earlier');
    if (state.start > 1 && state.first > 1) {
      e.innerHTML = '<button id="loadearlier">Load earlier messages</button>';
      $('#loadearlier').onclick = async () => {
        const items = await load(`before=${state.first}`, true);
        state.first = items.length ? items[0].line : 1;
        if (!items.length) state.start = 1;
        updateEarlier();
      };
    } else e.innerHTML = '';
  }
  async function next() {
    if (state.done) return;
    const from = (state.last ?? (line ? Math.max(1, line - 10) - 1 : 0)) + 1;
    state.start ??= from;
    const items = await load(`from=${from}`, false);
    if (items.length) {
      state.first ??= items[0].line;
      state.last = items[items.length - 1].line;
    }
    if (items.length < 200) { state.done = true; more.textContent = 'End of the transcript.'; }
    else more.textContent = 'Scroll for more…';
  }
  await next();
  if (line && state.first == null) state.first = line;
  updateEarlier();
  if (line) {
    const target = records.querySelector(`[data-line="${line}"]`);
    if (target) {
      // A hit can sit in a folded tool call or attachment: unfold everything around it.
      for (let d = target.closest('details'); d; d = d.parentElement?.closest('details')) d.open = true;
      target.querySelectorAll(':scope > details').forEach(d => d.open = true);
      target.classList.add('hit');
      target.scrollIntoView({ block: 'center' });
    }
  }
  const obs = new IntersectionObserver(async es => {
    if (es.some(e => e.isIntersecting) && !state.done && !state.busy) await next();
  }, { root: main, rootMargin: '800px' });
  obs.observe(more);
}

function recHead(it, ctx, label) {
  return `<div class="when">${label ? `<span class="who">${esc(label)}</span> · ` : ''}<a href="#/s/${enc(ctx.key)}/chat/${ctx.tid}/${it.line}">#${it.line}</a>
    · ${esc(when(it.ts))} · <a href="/api/record/${it.id}" target="_blank" rel="noopener">raw</a></div>`;
}
function truncated(rec) { return JSON.stringify(rec).includes('__truncated'); }

function renderRecord(it, ctx) {
  const r = it.record || {};
  const type = it.type;
  const wrap = el(`<div class="rec ${esc(type)}" data-line="${it.line}"></div>`);
  const full = truncated(r) ? `<button class="small" data-full="${it.id}">Show the full record (cut for display)</button>` : '';
  if (type === 'user' || type === 'assistant') {
    const msg = r.message || {};
    const blocks = typeof msg.content === 'string' ? [{ type: 'text', text: msg.content }] : (msg.content || []);
    const parts = [];
    for (const b of blocks) {
      if (b.type === 'tool_result') {
        const node = renderResult(b, it, ctx);
        const slot = ctx.tools.get(b.tool_use_id);
        if (slot) { slot.appendChild(node); continue; }
        parts.push(node.outerHTML);
      } else if (b.type === 'text') {
        parts.push(renderText(b.text, r, type));
      } else if (b.type === 'thinking') {
        if (b.thinking) parts.push(`<details class="think"><summary>Thinking</summary><pre>${esc(b.thinking)}</pre></details>`);
      } else if (b.type === 'redacted_thinking') {
        parts.push('<details class="think"><summary>Thinking (redacted)</summary></details>');
      } else if (b.type === 'tool_use') {
        parts.push(renderToolUse(b, ctx));
      } else if (b.type === 'image') {
        parts.push(imgTag(b));
      } else {
        parts.push(`<details><summary>${esc(b.type)}</summary><pre>${esc(JSON.stringify(b, null, 2))}</pre></details>`);
      }
    }
    if (!parts.length) {
      // Only tool results, drawn under their tool calls: keep an anchor for links to this line.
      wrap.style.margin = '0';
      return wrap;
    }
    const label = type === 'user' ? (r.isMeta ? 'Meta' : 'You') : ('Claude' + (msg.model ? ' · ' + msg.model : ''));
    const sameReply = type === 'assistant' && msg.id && ctx.lastMsgId === msg.id;
    ctx.lastMsgId = type === 'assistant' ? msg.id : null;
    if (sameReply) wrap.classList.add('cont');
    wrap.innerHTML = (sameReply ? '' : recHead(it, ctx, label)) + (type === 'user' && !r.isMeta && !r.isCompactSummary && blocks.some(b => b.type === 'text' && !String(b.text).trimStart().startsWith('<'))
      ? `<div class="bubble">${parts.join('')}</div>` : parts.join('')) + full;
    // Wire the slots of the tool calls just drawn, for their results.
    for (const d of wrap.querySelectorAll('[data-tool]')) ctx.tools.set(d.dataset.tool, d);
  } else if (type === 'system') {
    ctx.lastMsgId = null;
    const text = r.content ? String(r.content) : '';
    wrap.innerHTML = recHead(it, ctx, 'System' + (r.subtype ? ' · ' + r.subtype : '')) +
      (text.length > 300 ? `<details><summary>${esc(text.slice(0, 160))}</summary><pre>${esc(text)}</pre></details>` : `<div class="sys">${esc(text)}</div>`) + full;
  } else if (type === 'attachment') {
    const a = r.attachment || {};
    wrap.classList.add('compact');
    wrap.innerHTML = `<details><summary>Attachment · ${esc(a.type || '?')}${a.filename ? ' · ' + esc(a.filename) : ''} · #${it.line}</summary>${recHead(it, ctx, '')}<pre>${esc(JSON.stringify(a, null, 2))}</pre>${full}</details>`;
  } else {
    wrap.innerHTML = recHead(it, ctx, type) + `<details><summary>${esc(type)} record</summary><pre>${esc(JSON.stringify(r, null, 2))}</pre></details>` + full;
  }
  wrap.querySelector('[data-full]')?.addEventListener('click', async e => {
    const id = e.target.dataset.full;
    const record = await api('/api/record/' + id);
    const node = renderRecord({ ...it, record }, ctx);
    node.querySelector('[data-full]')?.remove();
    wrap.replaceWith(node);
  });
  return wrap;
}

function renderText(text, r, type) {
  const t = String(text ?? '');
  if (type === 'user') {
    if (r.isCompactSummary) return `<details><summary>Summary of the earlier conversation (written when the context was compacted)</summary>${md(t)}</details>`;
    const tag = t.trimStart().match(/^<([a-z][\w-]*)/i);
    if (r.isMeta || tag) return `<details><summary>${esc(tag ? '<' + tag[1] + '>' : 'meta')} ${esc(t.replace(/<[^>]+>/g, ' ').trim().slice(0, 140))}</summary><pre>${esc(t)}</pre></details>`;
  }
  return md(t);
}

function brief(name, input) {
  if (!input || typeof input !== 'object') return '';
  const pick = input.command ?? input.file_path ?? input.notebook_path ?? input.pattern ?? input.description ?? input.url ?? input.query ?? input.prompt ?? input.path;
  if (pick != null) return String(pick);
  const first = Object.values(input).find(v => typeof v === 'string');
  return first ?? '';
}
function renderToolUse(b, ctx) {
  const agentTid = ctx.agentTids.get(b.id);
  return `<details class="tool"><summary><span class="tn">${esc(b.name)}</span> ${esc(brief(b.name, b.input).slice(0, 200))}</summary>
    ${agentTid ? `<div><a href="#/s/${enc(ctx.key)}/chat/${agentTid}">Open this sub-agent's transcript</a></div>` : ''}
    <pre>${esc(JSON.stringify(b.input, null, 2))}</pre><div data-tool="${esc(b.id)}"></div></details>`;
}
function renderResult(b, it, ctx) {
  const node = el(`<div class="result ${b.is_error ? 'err' : ''}" data-line="${it.line}"></div>`);
  let html = `<div class="when">${b.is_error ? '<span class="warn">error</span> · ' : ''}result · #${it.line} · ${esc(when(it.ts))}</div>`;
  const c = b.content;
  if (typeof c === 'string') html += `<pre>${esc(c)}</pre>`;
  else if (Array.isArray(c)) for (const x of c) {
    if (x.type === 'text') html += `<pre>${esc(x.text)}</pre>`;
    else if (x.type === 'image') html += imgTag(x);
    else html += `<pre>${esc(JSON.stringify(x, null, 2))}</pre>`;
  }
  node.innerHTML = html;
  return node;
}
function imgTag(b) {
  const src = b.src || (b.source?.data ? `data:${b.source.media_type};base64,${b.source.data}` : b.source?.url);
  if (!src || !/^(\/api\/|data:image\/|https?:)/.test(src)) return '<div class="muted small">[image not in the record]</div>';
  return `<a href="${esc(src)}" target="_blank" rel="noopener"><img class="att" loading="lazy" src="${esc(src)}" alt="attached image"></a>`;
}

// ------------------------------------------------------------------ search, memory, files
async function showSearch(q) {
  $('#q').value = q;
  loading('Searching…');
  const hits = await api('/api/search?q=' + enc(q) + '&limit=300');
  const recs = hits.filter(h => h.kind === 'record'), mems = hits.filter(h => h.kind === 'memory');
  main.innerHTML = `<h1>Search: ${esc(q)}</h1><p class="muted">${recs.length} in conversations${recs.length >= 300 ? ' (the best 300)' : ''}, ${mems.length} in memory.</p>
    ${mems.length ? `<h2>Memory</h2><div class="hits">${mems.map(h => `<div class="hit"><a href="#/file/${enc(h.path)}">${esc(h.project)} / ${esc(h.name)}</a><div class="snip">${snippet(h.snippet)}</div></div>`).join('')}</div>` : ''}
    <h2>Conversations</h2>
    <div class="hits">${recs.map(h => `<div class="hit"><a href="#/s/${enc(h.session_key)}/chat/${h.tid}/${h.line}">${esc(h.title || h.session_key)}</a>
      ${badges(h)} <span class="muted small">${esc(h.tkind === 'main' ? '' : 'sub-agent · ')}${esc(h.type)} · ${esc(when(h.ts))}</span>
      <div class="snip">${snippet(h.snippet)}</div></div>`).join('') || '<div class="empty">Nothing found.</div>'}</div>`;
}

async function showAllMemory() {
  loading();
  const files = await api('/api/memory');
  const byProject = new Map();
  for (const f of files) { if (!byProject.has(f.project)) byProject.set(f.project, []); byProject.get(f.project).push(f); }
  main.innerHTML = `<h1>Memory</h1><p class="muted">${files.length} files in ${byProject.size} projects. Each project folder's memory is shared by its sessions.</p>` +
    [...byProject].map(([p, fs]) => `<h2>${esc(p)}</h2><table class="grid">${fs.map(f => `<tr><td><a href="#/file/${enc(f.path)}">${esc(f.name)}</a></td><td>${bytes(f.bytes)}</td><td>${esc(when(f.mtime))}</td></tr>`).join('')}</table>`).join('');
}

function crumbs(path) {
  const parts = path.split('/').filter(Boolean);
  let acc = '';
  return `<div class="crumbs"><a href="#/browse/">export</a>${parts.map(p => { acc += (acc ? '/' : '') + p; return ` / <a href="#/browse/${enc(acc)}">${esc(p)}</a>`; }).join('')}</div>`;
}

async function showBrowse(path) {
  loading();
  const t = await api('/api/tree?path=' + enc(path));
  main.innerHTML = `<h1>Everything exported</h1>${crumbs(t.path)}
    <table class="grid"><tr><th>Name</th><th>Size</th><th>Modified</th></tr>
    ${t.entries.map(e => `<tr><td>${e.dir ? `<a href="#/browse/${enc(e.path)}">${esc(e.name)}/</a>` : `<a href="#/file/${enc(e.path)}">${esc(e.name)}</a>`}</td>
      <td>${e.dir ? '' : bytes(e.bytes)}</td><td>${esc(when(e.mtime))}</td></tr>`).join('') || '<tr><td colspan="3" class="muted">Empty folder.</td></tr>'}
    </table>`;
}

async function showFile(path) {
  const parent = path.split('/').slice(0, -1).join('/');
  const name = path.split('/').pop();
  main.innerHTML = `<h1>${esc(name)}</h1>${crumbs(parent)}<div class="bar"><a class="btn" href="${rawUrl(path, true)}">Download</a>
    <a class="btn" href="${rawUrl(path)}" target="_blank" rel="noopener">Open in a new tab</a></div><div id="fileview" class="muted">Loading…</div>`;
  const view = $('#fileview');
  if (/\.(png|jpe?g|gif|webp|bmp|ico)$/i.test(name)) { view.innerHTML = `<img class="att" src="${rawUrl(path)}" alt="">`; return; }
  if (/\.(pdf|zip|nupkg|exe|dll|db|sqlite|vhdx|bin|7z|gz|msix|appx)$/i.test(name)) { view.textContent = 'A binary file: download it or open it in a new tab.'; return; }
  const r = await fetch(rawUrl(path));
  if (!r.ok) { view.textContent = r.status + ' ' + r.statusText; return; }
  const size = Number(r.headers.get('content-length') || 0);
  if (size > 5 * 1024 * 1024) { view.textContent = `${bytes(size)}: too large to show here. Download it instead.`; r.body?.cancel(); return; }
  const text = await r.text();
  view.classList.remove('muted');
  if (/\.md$/i.test(name)) view.innerHTML = `<div class="bubble">${md(text)}</div><details><summary>Source</summary><pre>${esc(text)}</pre></details>`;
  else if (/\.jsonl$/i.test(name)) view.innerHTML = `<p class="muted small">${text.split('\n').length} lines. Transcripts are easier to read from their session.</p><pre>${esc(text.slice(0, 2_000_000))}</pre>`;
  else if (/\.json$/i.test(name)) view.innerHTML = `<pre>${esc(pretty(text))}</pre>`;
  else view.innerHTML = `<pre>${esc(text)}</pre>`;
}

// ------------------------------------------------------------------ start
loadSessions().then(route).catch(fail);
