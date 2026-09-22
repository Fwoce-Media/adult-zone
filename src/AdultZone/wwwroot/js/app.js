/* Adult Zone -- single page frontend */

const view = document.getElementById('view');
const topbar = document.getElementById('topbar');
const searchInput = document.getElementById('search-input');

/* ------------------------------------------------------------------ utils */

const api = {
  async get(url) {
    const r = await fetch(url);
    if (r.status === 423) return relock();
    if (!r.ok) throw new Error((await r.json().catch(() => ({}))).detail || r.statusText);
    return r.json();
  },
  async send(method, url, body) {
    const r = await fetch(url, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : {},
      body: body ? JSON.stringify(body) : undefined,
    });
    if (r.status === 423) return relock();
    if (!r.ok) throw new Error((await r.json().catch(() => ({}))).detail || r.statusText);
    return r.json();
  },
  post: (url, body) => api.send('POST', url, body),
  put: (url, body) => api.send('PUT', url, body),
  del: (url) => api.send('DELETE', url),
  async upload(url, file) {
    const fd = new FormData();
    fd.append('file', file);
    const r = await fetch(url, { method: 'POST', body: fd });
    if (!r.ok) throw new Error('Upload failed');
    return r.json();
  },
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

function fmtDuration(sec) {
  sec = Math.round(sec || 0);
  if (!sec) return '';
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
           : `${m}:${String(s).padStart(2, '0')}`;
}

// A bare YYYY-MM-DD is parsed as UTC midnight by the browser, which lands on the
// previous day for anyone west of Greenwich. Build those as a local date instead.
// Values that carry a time come from SQLite's datetime('now'), which is UTC.
function parseDate(d) {
  if (!d) return null;
  const s = String(d).trim();
  const dateOnly = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s);
  if (dateOnly) {
    return new Date(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3]));
  }
  const dt = new Date(/[zZ]|[+-]\d{2}:?\d{2}$/.test(s) ? s : s.replace(' ', 'T') + 'Z');
  return isNaN(dt) ? null : dt;
}

function fmtDate(d) {
  const dt = parseDate(d);
  if (!dt) return d || '';
  return dt.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function ageFrom(birthdate) {
  const dob = parseDate(birthdate);
  if (!dob) return null;
  const now = new Date();
  let age = now.getFullYear() - dob.getFullYear();
  const before = now.getMonth() < dob.getMonth()
    || (now.getMonth() === dob.getMonth() && now.getDate() < dob.getDate());
  if (before) age -= 1;
  return age >= 0 && age < 130 ? age : null;
}

function fmtViews(n) {
  n = n || 0;
  return n === 1 ? '1 view' : `${n.toLocaleString()} views`;
}

function fmtSize(bytes) {
  if (!bytes) return '';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, v = bytes;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v < 10 && i > 1 ? 1 : 0)} ${u[i]}`;
}

function initials(name) {
  return (name || '?').split(/\s+/).slice(0, 2).map((w) => w[0] || '').join('').toUpperCase();
}

let toastTimer;
function toast(msg) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 2800);
}

const splitList = (s) => (s || '').split(',').map((x) => x.trim()).filter(Boolean);

/* ---------------------------------------------------------------- countries */

// Flags are bundled as SVGs because Windows ships no flag emoji at all --
// an emoji flag renders there as two plain letters.
let COUNTRIES = null;
async function countries() {
  if (!COUNTRIES) {
    try { COUNTRIES = await fetch('/static/countries.json').then((r) => r.json()); }
    catch { COUNTRIES = []; }
  }
  return COUNTRIES;
}
function countryName(code) {
  const hit = (COUNTRIES || []).find((c) => c.code === code);
  return hit ? hit.name : (code || '').toUpperCase();
}

const STATUSES = [
  ['active', 'Active'],
  ['inactive', 'Inactive'],
  ['retired', 'Retired'],
  ['died', 'Died'],
];
function statusLabel(value) {
  const hit = STATUSES.find((x) => x[0] === value);
  return hit ? hit[1] : '';
}

/* ------------------------------------------------------------- components */

function qualityBadge(v) {
  return v.quality ? `<span class="q q-${v.quality}">${v.quality}</span>` : '';
}

// Shared by every page that lists videos.
const VIDEO_SORTS = [
  ['added', 'Recently added'],
  ['date', 'Release date — newest'],
  ['date_asc', 'Release date — oldest'],
  ['views', 'Most viewed'],
  ['title', 'Title A–Z'],
  ['duration', 'Longest'],
  ['shortest', 'Shortest'],
  ['random', 'Shuffle'],
];

function sortSelect(id, current) {
  return `<select id="${id}">${VIDEO_SORTS.map(([v, l]) =>
    `<option value="${v}" ${v === current ? 'selected' : ''}>${l}</option>`).join('')}</select>`;
}

function videoCard(v) {
  const shot = v.thumb
    ? `<img loading="lazy" src="/api/thumb/${v.id}?v=${v.thumb_v || 0}&w=640" alt="" onerror="this.remove()">`
    : `<div class="placeholder">No thumbnail</div>`;
  const prev = v.has_preview
    ? `<video muted loop playsinline preload="none" data-preview="/api/preview/${v.id}?v=${v.preview_v || 0}"></video>`
    : '';
  // Two names keep the line readable on a card; the rest are a count.
  const cast = v.actors || [];
  const shown = cast.slice(0, 2).map((a) =>
    `<a class="card-actor" href="#/actor/${a.id}">${esc(a.name)}</a>`).join('<span class="dot"></span>');
  const more = cast.length > 2 ? `<span class="card-more">+${cast.length - 2}</span>` : '';

  const meta = [
    v.release_date ? `<span>${esc(fmtDate(v.release_date))}</span>` : '',
    qualityBadge(v),
    v.studio_name
      ? `<span class="dot"></span>` + (v.studio_id
          ? `<a class="card-studio" href="#/studio/${v.studio_id}">${esc(v.studio_name)}</a>`
          : `<span class="card-studio">${esc(v.studio_name)}</span>`)
      : '',
    // On a card the sub-site reads as part of the studio name, so it stays in
    // the same text rhythm as everything else. A pill here breaks the line and
    // pushes the cast onto a second row. The detail page has room for the pill.
    v.subsite
      ? `<span class="sep">/</span><a class="card-subsite" href="#/videos?tag=${encodeURIComponent(v.subsite)}">${esc(v.subsite)}</a>`
      : '',
    shown ? `<span class="dot"></span>${shown}${more}` : '',
  ].filter(Boolean).join('');

  return `
  <article class="card" data-video="${v.id}" tabindex="0">
    <div class="card-shot">
      ${shot}${prev}
      ${v.favorite ? '<span class="card-fav"><svg viewBox="0 0 24 24"><path d="M12 21s-7.5-4.7-9.5-9A5.3 5.3 0 0 1 12 6.5 5.3 5.3 0 0 1 21.5 12c-2 4.3-9.5 9-9.5 9z"/></svg></span>' : ''}
      ${v.duration ? `<span class="card-dur">${fmtDuration(v.duration)}</span>` : ''}
    </div>
    <div class="card-body">
      <div class="card-title">${esc(v.title)}</div>
      <div class="card-meta">${meta}</div>
    </div>
  </article>`;
}

function starCard(a) {
  const photo = a.image
    ? `<img loading="lazy" src="/api/actor-photo/${a.id}?v=${a.image_v || 0}" alt="" onerror="this.remove()">`
    : `<div class="initials">${esc(initials(a.name))}</div>`;
  return `
  <article class="star${a.hidden ? ' is-hidden' : ''}" data-actor="${a.id}" tabindex="0">
    <div class="star-photo">${photo}${a.hidden ? '<span class="hidden-flag">Hidden</span>' : ''}</div>
    <div class="star-name">${esc(a.name)}</div>
    <div class="star-count">${a.video_count} ${a.video_count === 1 ? 'video' : 'videos'}</div>
  </article>`;
}

// Per-studio logo adjustment: how it fits, how big, where, and what sits behind.
const LOGO_BACKGROUNDS = {
  dark: '#0E0E13', black: '#000', white: '#fff', light: '#EDEDF2', none: 'transparent',
};

function logoImgStyle(s) {
  const zoom = (s.logo_zoom || 100) / 100;
  // Pan with translate rather than object-position. object-position can only
  // move an image when there is slack -- letterboxing under 'contain', or
  // overflow under 'cover' -- so on a logo whose shape nearly matches the box
  // it does nothing at all. translate always moves it, in every fit mode.
  const dx = (s.logo_x ?? 50) - 50;
  const dy = (s.logo_y ?? 50) - 50;
  return `object-fit:${s.logo_fit || 'contain'};object-position:center;`
    + `transform:translate(${dx}%, ${dy}%) scale(${zoom});`;
}

function logoBoxStyle(s) {
  return `background:${LOGO_BACKGROUNDS[s.logo_bg] ?? LOGO_BACKGROUNDS.dark};`;
}

function studioCard(s) {
  const logo = s.image
    ? `<img loading="lazy" src="/api/studio-image/${s.id}?v=${s.image_v || 0}" alt=""
        style="${logoImgStyle(s)}" onerror="this.remove()">`
    : `<div class="initials">${esc(s.name)}</div>`;
  return `
  <article class="studio" data-studio="${s.id}" tabindex="0">
    <div class="studio-logo" style="${logoBoxStyle(s)}">${logo}</div>
    <div>
      <div class="studio-name">${esc(s.name)}</div>
      <div class="studio-count">${s.video_count} ${s.video_count === 1 ? 'video' : 'videos'}</div>
    </div>
  </article>`;
}

function rail(title, items, moreHref) {
  if (!items.length) return '';
  return `
  <section class="row">
    <div class="row-head">
      <h2 class="row-title">${esc(title)}</h2>
      <span class="row-count">${items.length}</span>
      ${moreHref ? `<a class="row-more" href="${moreHref}">See all &rsaquo;</a>` : ''}
    </div>
    <div class="rail">${items.map(videoCard).join('')}</div>
  </section>`;
}

/* --------------------------------------------------- hover preview loops */

let hoverTimer = null;

function startPreview(shot) {
  const vid = shot.querySelector('video[data-preview]');
  if (!vid) return;
  if (!vid.src) vid.src = vid.dataset.preview;
  vid.currentTime = 0;
  vid.play().then(() => vid.classList.add('showing')).catch(() => {});
}

function stopPreview(shot) {
  const vid = shot.querySelector('video[data-preview]');
  if (!vid) return;
  vid.classList.remove('showing');
  vid.pause();
}

function wireRails(root) {
  root.querySelectorAll('.rail').forEach((rail) => {
    if (rail.parentElement?.classList.contains('rail-wrap')) return;

    const wrap = document.createElement('div');
    wrap.className = 'rail-wrap';
    rail.parentNode.insertBefore(wrap, rail);
    wrap.appendChild(rail);

    const arrow = (dir, path) => {
      const b = document.createElement('button');
      b.className = `rail-arrow rail-${dir}`;
      b.setAttribute('aria-label', dir === 'left' ? 'Scroll left' : 'Scroll right');
      b.innerHTML = `<svg viewBox="0 0 24 24"><path d="${path}"/></svg>`;
      wrap.appendChild(b);
      return b;
    };
    const left = arrow('left', 'M15 5l-7 7 7 7');
    const right = arrow('right', 'M9 5l7 7-7 7');

    const step = () => Math.max(260, rail.clientWidth * 0.82);
    left.addEventListener('click', () => rail.scrollBy({ left: -step(), behavior: 'smooth' }));
    right.addEventListener('click', () => rail.scrollBy({ left: step(), behavior: 'smooth' }));

    // an arrow with nowhere to go is hidden rather than dead
    const update = () => {
      const max = rail.scrollWidth - rail.clientWidth;
      left.classList.toggle('off', rail.scrollLeft <= 4);
      right.classList.toggle('off', max <= 4 || rail.scrollLeft >= max - 4);
    };
    rail.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
    setTimeout(update, 60);
    update();
  });
}

// A masked edge only earns its place when content is actually hidden behind it.
function wireChipFades(root) {
  root.querySelectorAll('.detail-body .chips').forEach((strip) => {
    const update = () => {
      const max = strip.scrollWidth - strip.clientWidth;
      strip.classList.toggle('fade-left', strip.scrollLeft > 2);
      strip.classList.toggle('fade-right', max > 2 && strip.scrollLeft < max - 2);
    };
    strip.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
    setTimeout(update, 50);
    update();
  });
}

function wireCards(root) {
  root.querySelectorAll('.card-shot').forEach((shot) => {
    shot.addEventListener('mouseenter', () => {
      clearTimeout(hoverTimer);
      hoverTimer = setTimeout(() => startPreview(shot), 380);
    });
    shot.addEventListener('mouseleave', () => {
      clearTimeout(hoverTimer);
      stopPreview(shot);
    });
  });
}

/* ------------------------------------------------------------- navigation */

function go(hash) {
  // Following a link is a fresh view, so drop any remembered position for it.
  scrollMemory.delete(hash);
  location.hash = hash;
}

document.addEventListener('click', (e) => {
  for (const [attr, route] of [['data-video', 'video'], ['data-actor', 'actor'], ['data-studio', 'studio']]) {
    const el = e.target.closest(`[${attr}]`);
    if (!el) continue;
    // Nested controls win over the card: a link goes where it points, a button
    // does its own job. The card itself may be either, hence the identity check.
    const link = e.target.closest('a[href]');
    if (link && link !== el) return;
    const btn = e.target.closest('button');
    if (btn && btn !== el) return;
    return go(`#/${route}/${el.getAttribute(attr)}`);
  }
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    const el = document.activeElement;
    if (el && (el.dataset.video || el.dataset.actor || el.dataset.studio)) el.click();
  }
  if (e.key === '/' && document.activeElement !== searchInput) {
    e.preventDefault(); searchInput.focus();
  }
  if (e.key === 'Escape') {
    if (stage) closeStage();
    else closeModal();
  }
});

window.addEventListener('scroll', () => {
  topbar.classList.toggle('solid', window.scrollY > 30);
});

let searchDebounce;
let searchOrigin = null;   // where the user was before they started searching

const onSearchPage = () => location.hash.startsWith('#/videos?search=');

function leaveSearch() {
  const back = searchOrigin || '#/home';
  searchOrigin = null;
  if (onSearchPage()) go(back);
}

/* ------------------------------------------------------ search suggestions */

// Typing shows instant matches under the box -- performers with their photo,
// studios with their logo, videos with their thumbnail. Enter, or the button
// at the bottom, opens the full results page.
const suggestBox = document.createElement('div');
suggestBox.className = 'suggest';
suggestBox.hidden = true;
searchInput.closest('.search').appendChild(suggestBox);

let suggestItems = [];
let suggestIndex = -1;
let suggestToken = 0;

function openSearchResults(q) {
  closeSuggest();
  const next = `#/videos?search=${encodeURIComponent(q)}`;
  if (onSearchPage()) {
    history.replaceState(null, '', next);
    route();
  } else {
    searchOrigin = location.hash || '#/home';
    go(next);
  }
}

function closeSuggest() {
  suggestBox.hidden = true;
  suggestBox.innerHTML = '';
  suggestItems = [];
  suggestIndex = -1;
}

function highlightSuggest(index) {
  const rows = suggestBox.querySelectorAll('.sg-row');
  rows.forEach((row, i) => row.classList.toggle('on', i === index));
  suggestIndex = index;
  rows[index]?.scrollIntoView({ block: 'nearest' });
}

function renderSuggest(q, data) {
  const performers = data.performers || [];
  const studios = data.studios || [];
  const videos = data.videos || [];
  suggestItems = [];

  const section = (label, rows) => rows.length
    ? `<div class="sg-head">${label}</div>${rows.join('')}` : '';

  const performerRows = performers.map((a) => {
    suggestItems.push(`#/actor/${a.id}`);
    const pic = a.image
      ? `<img src="/api/actor-photo/${a.id}?v=${a.image_v || 0}" alt="" onerror="this.remove()">`
      : `<span class="sg-initials">${esc(initials(a.name))}</span>`;
    return `<a class="sg-row" href="#/actor/${a.id}" data-i="${suggestItems.length - 1}">
        <span class="sg-pic sg-portrait">${pic}</span>
        <span class="sg-text"><b>${esc(a.name)}</b>
          <small>${a.video_count} ${a.video_count === 1 ? 'video' : 'videos'}</small></span>
      </a>`;
  });

  const studioRows = studios.map((s) => {
    suggestItems.push(`#/studio/${s.id}`);
    const logo = s.image
      ? `<img src="/api/studio-image/${s.id}?v=${s.image_v || 0}" alt="" style="${logoImgStyle(s)}" onerror="this.remove()">`
      : `<span class="sg-initials">${esc(initials(s.name))}</span>`;
    return `<a class="sg-row" href="#/studio/${s.id}" data-i="${suggestItems.length - 1}">
        <span class="sg-pic sg-logo" style="${logoBoxStyle(s)}">${logo}</span>
        <span class="sg-text"><b>${esc(s.name)}</b>
          <small>Studio · ${s.video_count} ${s.video_count === 1 ? 'video' : 'videos'}</small></span>
      </a>`;
  });

  const videoRows = videos.map((v) => {
    suggestItems.push(`#/video/${v.id}`);
    const shot = v.thumb
      ? `<img src="/api/thumb/${v.id}?v=${v.thumb_v || 0}&w=320" alt="" onerror="this.remove()">` : '';
    const year = (v.release_date || '').slice(0, 4);
    const meta = [year, v.studio_name].filter(Boolean).map(esc).join(' · ');
    return `<a class="sg-row" href="#/video/${v.id}" data-i="${suggestItems.length - 1}">
        <span class="sg-pic sg-wide">${shot}</span>
        <span class="sg-text"><b>${esc(v.title)}</b>
          <small>${meta}${v.quality ? ` <span class="q q-${v.quality}">${v.quality}</span>` : ''}</small></span>
      </a>`;
  });

  if (!performerRows.length && !studioRows.length && !videoRows.length) {
    suggestBox.innerHTML = `<div class="sg-empty">Nothing matches “${esc(q)}”</div>`;
  } else {
    suggestBox.innerHTML =
      section('Pornstars', performerRows) +
      section('Studios', studioRows) +
      section('Videos', videoRows) +
      `<button class="sg-all" type="button">See all results for “${esc(q)}”</button>`;
  }
  suggestBox.hidden = false;
  suggestIndex = -1;

  suggestBox.querySelector('.sg-all')?.addEventListener('mousedown', (e) => {
    e.preventDefault();                 // keep focus, so the click lands
    openSearchResults(q);
  });
  suggestBox.querySelectorAll('.sg-row').forEach((row) => {
    row.addEventListener('mousedown', (e) => {
      e.preventDefault();
      closeSuggest();
      searchInput.blur();
      go(row.getAttribute('href'));
    });
    row.addEventListener('mouseenter', () => highlightSuggest(Number(row.dataset.i)));
  });
}

async function fetchSuggest(q) {
  const token = ++suggestToken;         // ignore answers that arrive out of order
  let data;
  try { data = await api.get(`/api/suggest?q=${encodeURIComponent(q)}`); } catch { return; }
  if (token !== suggestToken || searchInput.value.trim() !== q) return;
  renderSuggest(q, data);
}

searchInput.addEventListener('input', () => {
  clearTimeout(searchDebounce);
  const q = searchInput.value.trim();

  // Clearing the box closes the suggestions and, from a results page, goes back.
  if (!q) {
    closeSuggest();
    return leaveSearch();
  }
  searchDebounce = setTimeout(() => fetchSuggest(q), 140);
});

searchInput.addEventListener('focus', () => {
  const q = searchInput.value.trim();
  if (q && suggestBox.hidden) fetchSuggest(q);
});

searchInput.addEventListener('blur', () => setTimeout(closeSuggest, 120));

searchInput.addEventListener('keydown', (e) => {
  const open = !suggestBox.hidden && suggestItems.length > 0;

  if (e.key === 'ArrowDown' && open) {
    e.preventDefault();
    highlightSuggest((suggestIndex + 1) % suggestItems.length);
  } else if (e.key === 'ArrowUp' && open) {
    e.preventDefault();
    highlightSuggest(suggestIndex <= 0 ? suggestItems.length - 1 : suggestIndex - 1);
  } else if (e.key === 'Enter') {
    e.preventDefault();
    const q = searchInput.value.trim();
    if (open && suggestIndex >= 0) {
      const target = suggestItems[suggestIndex];
      closeSuggest();
      searchInput.blur();
      go(target);
    } else if (q) {
      openSearchResults(q);
    }
  } else if (e.key === 'Escape') {
    e.stopPropagation();                // don't also close the player or a modal
    if (open) {
      closeSuggest();
    } else if (searchInput.value) {
      searchInput.value = '';
      clearTimeout(searchDebounce);
      leaveSearch();
      searchInput.blur();
    }
  }
});

/* ------------------------------------------------------------------ views */

function setTab(name) {
  document.querySelectorAll('#tabs a').forEach((a) => {
    a.classList.toggle('on', a.dataset.tab === name);
  });
}

function loading() {
  view.innerHTML = `<div class="page"><div class="grid">${
    Array.from({ length: 8 }, () => '<div class="skeleton" style="aspect-ratio:16/9"></div>').join('')
  }</div></div>`;
}

async function renderHome() {
  setTab('home');
  loading();

  const [stats, latest, popular, stars, studios, random] = await Promise.all([
    api.get('/api/stats'),
    api.get('/api/videos?sort=added&limit=24'),
    api.get('/api/videos?sort=views&limit=24'),
    api.get('/api/actors?sort=count&limit=20'),
    api.get('/api/studios?sort=count'),
    api.get('/api/videos?sort=random&limit=24'),
  ]);

  if (!stats.videos) return renderEmptyLibrary();

  const featured = latest.items.find((v) => v.has_preview) || latest.items[0];
  const topStars = stars.items.filter((a) => a.video_count > 0).slice(0, 14);
  const topStudios = studios.items.filter((s) => s.video_count > 0).slice(0, 12);

  view.innerHTML = `
    <section class="hero">
      <div class="hero-media">
        ${featured.thumb ? `<img src="/api/thumb/${featured.id}?v=${featured.thumb_v || 0}" alt="">` : ''}
        ${featured.has_preview ? `<video id="hero-video" muted loop playsinline src="/api/preview/${featured.id}?v=${featured.preview_v || 0}"></video>` : ''}
      </div>
      <div class="hero-body">
        <div class="eyebrow">Latest in your library</div>
        <h1 class="hero-title">${esc(featured.title)}</h1>
        <div class="detail-meta">
          ${featured.studio_name ? `<span>${esc(featured.studio_name)}</span><span class="dot"></span>` : ''}
          ${featured.release_date ? `<span>${esc(fmtDate(featured.release_date))}</span>` : ''}
          ${qualityBadge(featured)}
          <span class="dot"></span><span>${fmtDuration(featured.duration)}</span>
        </div>
        ${featured.description ? `<p class="hero-desc">${esc(featured.description)}</p>` : ''}
        <div class="hero-actions">
          <button class="btn btn-play" data-play="${featured.id}">
            <svg viewBox="0 0 24 24" style="fill:currentColor;stroke:none"><path d="M7 4.5v15l13-7.5z"/></svg>
            Play
          </button>
          <a class="btn btn-ghost" href="#/video/${featured.id}">More info</a>
        </div>
      </div>
    </section>

    <div style="margin-top:-40px;position:relative;z-index:3">
      ${rail('Recently added', latest.items, '#/videos?sort=added')}
      ${rail('Most viewed', popular.items.filter((v) => v.views > 0), '#/videos?sort=views')}
      ${topStars.length ? `
      <section class="row">
        <div class="row-head">
          <h2 class="row-title">Top stars</h2>
          <a class="row-more" href="#/stars">See all &rsaquo;</a>
        </div>
        <div class="rail">${topStars.map((a) =>
          `<div style="width:152px">${starCard(a)}</div>`).join('')}</div>
      </section>` : ''}
      ${topStudios.length ? `
      <section class="row">
        <div class="row-head">
          <h2 class="row-title">Studios</h2>
          <a class="row-more" href="#/studios">See all &rsaquo;</a>
        </div>
        <div class="rail">${topStudios.map((s) =>
          `<div style="width:210px">${studioCard(s)}</div>`).join('')}</div>
      </section>` : ''}
      ${rail('Pick something at random', random.items)}
    </div>`;

  wireCards(view);
  wireRails(view);
  const hv = document.getElementById('hero-video');
  if (hv) setTimeout(() => { hv.play().then(() => hv.classList.add('showing')).catch(() => {}); }, 900);
  view.querySelector('[data-play]')?.addEventListener('click', (e) => {
    go(`#/video/${e.currentTarget.dataset.play}?play=1`);
  });
}

function renderEmptyLibrary() {
  view.innerHTML = `
    <div class="page">
      <div class="empty">
        <h3>Your library is empty</h3>
        <p>Add the folders your videos live in, then run a scan.</p>
        <a class="btn btn-ember" href="#/settings">Add a storage folder</a>
      </div>
    </div>`;
}

async function renderVideos(params) {
  setTab('videos');
  loading();

  const search = params.get('search') || '';
  const sort = params.get('sort') || 'added';
  const quality = params.get('quality') || '';
  const tag = params.get('tag') || '';

  const qs = new URLSearchParams({ sort, limit: '0' });
  if (search) qs.set('search', search);
  if (quality) qs.set('quality', quality);
  if (tag) qs.set('tag', tag);

  // A search also looks for performers and studios by that name, so typing a
  // performer and pressing Enter shows her profile, not just her videos.
  const none = Promise.resolve({ items: [] });
  const term = encodeURIComponent(search);
  const [data, tags, people, labels] = await Promise.all([
    api.get(`/api/videos?${qs}`),
    api.get('/api/tags'),
    search ? api.get(`/api/actors?search=${term}&sort=count&limit=0`) : none,
    search ? api.get(`/api/studios?search=${term}&sort=count`) : none,
  ]);

  const heading = search ? `Results for "${search}"` : tag ? `Tagged "${tag}"` : 'All videos';

  const matchRow = (title, items, card, width) => items.length ? `
      <section class="results-block">
        <h2 class="section-title">${title} <span class="row-count">${items.length}</span></h2>
        <div class="rail results-rail">${items.map((item) =>
          `<div style="width:${width}px">${card(item)}</div>`).join('')}</div>
      </section>` : '';
  const matches = search
    ? matchRow('Pornstars', people.items || [], starCard, 178) +
      matchRow('Studios', (labels.items || []).filter((x) => x.video_count > 0), studioCard, 228)
    : '';

  view.innerHTML = `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">Library</div>
          <h1 class="page-title">${esc(heading)}</h1>
          <p class="page-sub">${data.total} ${data.total === 1 ? 'video' : 'videos'}</p>
        </div>
      </div>

      ${matches}
      ${matches ? '<h2 class="section-title">Videos</h2>' : ''}

      <div class="filters">
        ${sortSelect('f-sort', sort)}
        <select id="f-quality">
          <option value="">Any quality</option>
          ${['4K', '2K', 'HD', 'SD'].map((q) =>
            `<option value="${q}" ${q === quality ? 'selected' : ''}>${q} only</option>`).join('')}
        </select>
        <select id="f-tag">
          <option value="">Any tag</option>
          ${tags.items.map((t) =>
            `<option value="${esc(t.name)}" ${t.name === tag ? 'selected' : ''}>${esc(t.name)} (${t.video_count})</option>`).join('')}
        </select>
      </div>

      ${data.items.length
        ? `<div class="grid">${data.items.map(videoCard).join('')}</div>`
        : `<div class="empty"><h3>Nothing matched</h3><p>Try a different search or clear the filters.</p></div>`}
    </div>`;

  wireCards(view);
  wireRails(view);

  const rebuild = () => {
    const p = new URLSearchParams();
    if (search) p.set('search', search);
    const s = document.getElementById('f-sort').value;
    const q = document.getElementById('f-quality').value;
    const t = document.getElementById('f-tag').value;
    if (s !== 'added') p.set('sort', s);
    if (q) p.set('quality', q);
    if (t) p.set('tag', t);
    go(`#/videos${p.toString() ? '?' + p : ''}`);
  };
  ['f-sort', 'f-quality', 'f-tag'].forEach((id) =>
    document.getElementById(id).addEventListener('change', rebuild));
}

async function renderVideo(id, params = new URLSearchParams()) {
  setTab('');
  loading();

  const v = await api.get(`/api/videos/${id}`);
  const [byCast, byStudio] = await Promise.all([
    api.get(`/api/videos/${id}/related?source=cast&limit=24`),
    v.studio_id ? api.get(`/api/videos/${id}/related?source=studio&limit=24`)
                : Promise.resolve({ items: [] }),
  ]);

  const meta = [
    v.release_date ? esc(fmtDate(v.release_date)) : '',
    v.quality ? `<span class="q q-${v.quality}">${v.quality}</span>` : '',
    fmtDuration(v.duration),
    fmtViews(v.views),
    v.resolution !== '0x0' ? v.resolution : '',
  ].filter(Boolean).join('<span class="dot"></span>');

  const actorChips = v.actors.map((a) => `
    <button class="chip" data-actor="${a.id}">
      ${a.image ? `<img src="/api/actor-photo/${a.id}?v=${a.image_v || 0}" alt="">`
                : `<span class="chip-ph">${esc(initials(a.name))}</span>`}
      ${esc(a.name)}
    </button>`).join('');

  // The sub-site reads as part of the studio rather than as one tag among many,
  // so it sits beside the studio name and is kept out of the tag row.
  const tagChips = v.tags.filter((t) => t !== v.subsite).map((t) =>
    `<a class="chip chip-tag" href="#/videos?tag=${encodeURIComponent(t)}">${esc(t)}</a>`).join('');

  view.innerHTML = `
    <section class="detail-hero">
      <div class="detail-back">
        ${v.thumb ? `<img src="/api/thumb/${v.id}?v=${v.thumb_v || 0}" alt="">` : ''}
        ${v.has_preview ? `<video id="detail-video" muted loop playsinline src="/api/preview/${v.id}?v=${v.preview_v || 0}"></video>` : ''}
      </div>
      <button class="stage-play" id="btn-stage-play" aria-label="Play ${esc(v.title)}">
        <svg viewBox="0 0 24 24"><path d="M7 4.5v15l13-7.5z"/></svg>
      </button>
      <div class="detail-body">
        <div class="studio-line">
          ${v.studio_name
            ? `<a class="eyebrow" href="#/studio/${v.studio_id}">${esc(v.studio_name)}</a>`
            : '<span class="eyebrow">Untagged studio</span>'}
          ${v.subsite
            ? `<a class="subsite" href="#/videos?tag=${encodeURIComponent(v.subsite)}">${esc(v.subsite)}</a>`
            : ''}
        </div>
        <h1 class="detail-title">${esc(v.title)}</h1>
        <div class="detail-meta">${meta}</div>
        ${v.description ? `<p class="detail-desc">${esc(v.description)}</p>` : ''}
        ${actorChips ? `<div class="chips chips-cast">${actorChips}</div>` : ''}
        ${tagChips ? `<div class="chips chips-tags">${tagChips}</div>` : ''}
        <div class="hero-actions">
          <button class="btn btn-ghost" id="btn-edit">Edit details</button>
          <button class="btn btn-ghost" id="btn-find">Find info</button>
          <button class="btn btn-ghost" id="btn-fav">${v.favorite ? 'Remove from favourites' : 'Add to favourites'}</button>
        </div>
        ${!v.exists ? '<p class="warn" style="margin-top:14px">The file is no longer at its recorded location.</p>' : ''}
      </div>
    </section>

    <div class="section">
      <div class="tabbar">
        <button class="on" data-pane="cast">More with this cast${byCast.items.length ? ` <span class="tab-n">${byCast.items.length}</span>` : ''}</button>
        ${v.studio_id ? `<button data-pane="studio">From same studio${byStudio.items.length ? ` <span class="tab-n">${byStudio.items.length}</span>` : ''}</button>` : ''}
        <button data-pane="details">File details</button>
      </div>

      <div id="pane-cast">
        ${byCast.items.length
          ? `<div class="grid">${byCast.items.map(videoCard).join('')}</div>`
          : `<div class="empty"><h3>Nothing else with this cast</h3><p>${v.actors.length
              ? 'No other video in your library lists ' + esc(v.actors.map((a) => a.name).join(' or ')) + '.'
              : 'Add cast members with Edit details and their other videos will show up here.'}</p></div>`}
      </div>

      <div id="pane-studio" hidden>
        ${byStudio.items.length
          ? `<div class="grid">${byStudio.items.map(videoCard).join('')}</div>`
          : `<div class="empty"><h3>Nothing else from this studio</h3><p>This is the only ${esc(v.studio_name || 'studio')} video in your library.</p></div>`}
      </div>

      <div id="pane-details" hidden>
        <div class="panel">
          <div class="field-row">
            <div><div class="fact-label">Resolution</div><div class="fact-value">${esc(v.resolution)} ${v.quality ? `(${v.quality})` : ''}</div></div>
            <div><div class="fact-label">Duration</div><div class="fact-value">${fmtDuration(v.duration) || '—'}</div></div>
            <div><div class="fact-label">File size</div><div class="fact-value">${fmtSize(v.filesize) || '—'}</div></div>
            <div><div class="fact-label">Added</div><div class="fact-value">${esc(fmtDate(v.added_at))}</div></div>
            <div><div class="fact-label">Preview loop</div><div class="fact-value">${v.preview_width ? v.preview_width + 'px wide' : (v.has_preview ? 'built before sizes were tracked' : 'none')}</div></div>
          </div>
          <div class="fact-label" style="margin-top:14px">Path</div>
          <div class="loc-path">${esc(v.path)}</div>
          <div style="display:flex;gap:10px;margin-top:16px;flex-wrap:wrap">
            <button class="ghost-btn" id="btn-reveal">Open containing folder</button>
            <button class="ghost-btn" id="btn-regen">Rebuild thumbnail &amp; preview</button>
            <button class="ghost-btn warn" id="btn-remove">Remove from library</button>
          </div>
        </div>
      </div>
    </div>`;

  wireCards(view);
  wireRails(view);
  wireChipFades(view);

  const dv = document.getElementById('detail-video');
  if (dv) setTimeout(() => { dv.play().then(() => dv.classList.add('showing')).catch(() => {}); }, 700);

  const panes = ['cast', 'studio', 'details'];
  document.querySelectorAll('.tabbar button').forEach((b) => {
    b.addEventListener('click', () => {
      document.querySelectorAll('.tabbar button').forEach((x) => x.classList.remove('on'));
      b.classList.add('on');
      panes.forEach((name) => {
        const pane = document.getElementById(`pane-${name}`);
        if (pane) pane.hidden = b.dataset.pane !== name;
      });
    });
  });

  document.getElementById('btn-stage-play').addEventListener('click', () => openStage(v));
  if (params.get('play')) openStage(v);
  document.getElementById('btn-edit').addEventListener('click', () => editVideo(v));
  document.getElementById('btn-find').addEventListener('click', () =>
    openImport('video', v.id, v.title, () => renderVideo(id),
      { studio: v.studio_name || '', subsite: v.subsite || '',
        performer: (v.actors[0] || {}).name || '' }));
  document.getElementById('btn-fav').addEventListener('click', async () => {
    await api.put(`/api/videos/${v.id}`, { favorite: v.favorite ? 0 : 1 });
    renderVideo(id);
  });
  document.getElementById('btn-reveal').addEventListener('click', async () => {
    try { await api.post(`/api/videos/${v.id}/reveal`); } catch (e) { toast(e.message); }
  });
  document.getElementById('btn-regen').addEventListener('click', async () => {
    await api.post(`/api/videos/${v.id}/regenerate`, {});
    toast('Rebuilding in the background. Reload in a moment.');
  });
  document.getElementById('btn-remove').addEventListener('click', async () => {
    if (!confirm('Remove this video from the library? The file on disk is not deleted.')) return;
    await api.del(`/api/videos/${v.id}`);
    go('#/videos');
  });
}

async function renderStars(params) {
  setTab('stars');
  loading();
  const sort = params.get('sort') || 'count';
  const showHidden = params.get('hidden') === '1';
  const data = await api.get(
    `/api/actors?sort=${sort}&limit=0&include_hidden=${showHidden ? 1 : 0}`);
  const empty = data.items.filter((a) => !a.video_count);

  view.innerHTML = `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">Cast</div>
          <h1 class="page-title">Pornstars</h1>
          <p class="page-sub">${data.items.length} shown${data.hidden ? ` · ${data.hidden} hidden` : ''}${empty.length ? ` · ${empty.length} with no videos` : ''}</p>
        </div>
        <div class="filters" style="margin:0">
          ${data.hidden || showHidden ? `<button class="btn btn-ghost btn-sm" id="btn-toggle-hidden">${showHidden ? 'Hide them again' : `Show ${data.hidden} hidden`}</button>` : ''}
          ${empty.length ? `<button class="btn btn-ghost btn-sm" id="btn-prune-actors">Remove ${empty.length} with no videos</button>` : ''}
          <select id="f-star-sort">
            <option value="count" ${sort === 'count' ? 'selected' : ''}>Most videos</option>
            <option value="name" ${sort === 'name' ? 'selected' : ''}>Name A–Z</option>
          </select>
        </div>
      </div>
      ${data.items.length
        ? `<div class="grid grid-portrait">${data.items.map(starCard).join('')}</div>`
        : `<div class="empty"><h3>No stars yet</h3><p>Open a video, choose Edit details, and add cast names.</p></div>`}
    </div>`;

  document.getElementById('f-star-sort')?.addEventListener('change', (e) =>
    go(`#/stars?sort=${e.target.value}${showHidden ? '&hidden=1' : ''}`));
  document.getElementById('btn-toggle-hidden')?.addEventListener('click', () =>
    go(`#/stars?sort=${sort}${showHidden ? '' : '&hidden=1'}`));
  document.getElementById('btn-prune-actors')?.addEventListener('click', async () => {
    if (!confirm(`Remove ${empty.length} ${empty.length === 1 ? 'profile' : 'profiles'} with no videos? No files are deleted.`)) return;
    const r = await api.post('/api/actors/prune');
    toast(`Removed ${r.removed}`);
    renderStars(params);
  });
}

async function renderActor(id, params = new URLSearchParams()) {
  setTab('stars');
  loading();
  const sort = params.get('sort') || 'added';
  const a = await api.get(`/api/actors/${id}?sort=${encodeURIComponent(sort)}`);

  await countries();
  const derived = a.age || ageFrom(a.birthdate);
  const facts = [
    derived ? ['Age', derived] : null,
    a.birthdate ? ['Born', fmtDate(a.birthdate)] : null,
    ['Videos', a.video_count],
    a.country ? ['From',
      `<span class="fact-flag"><img src="/static/flags/${esc(a.country)}.svg" alt=""
        onerror="this.remove()"><span>${esc(countryName(a.country))}</span></span>`] : null,
    a.status ? ['Status',
      `<span class="fact-status st-${esc(a.status)}"><i></i>${esc(statusLabel(a.status))}</span>`] : null,
  ].filter(Boolean);

  view.innerHTML = `
    <div class="actor-head">
      ${a.banner ? `<div class="actor-banner"><img src="/api/actor-banner/${a.id}?v=${a.banner_v || 0}" alt="" onerror="this.remove()"></div>` : ''}
      <div class="actor-portrait">
        ${a.image ? `<img src="/api/actor-photo/${a.id}?v=${a.image_v || 0}" alt="">`
                  : `<div class="initials">${esc(initials(a.name))}</div>`}
      </div>
      <div class="actor-info">
        <div class="eyebrow">Pornstar</div>
        <div class="actor-name-row">
          <h1 class="actor-name">${esc(a.name)}</h1>
          <span class="count-badge">${a.video_count} ${a.video_count === 1 ? 'video' : 'videos'}</span>
        </div>
        <div class="fact-row">
          ${facts.map(([l, v]) => `<div><div class="fact-label">${l}</div><div class="fact-value">${
            typeof v === 'string' && v.startsWith('<span') ? v : esc(v)}</div></div>`).join('')}
        </div>
        <p class="actor-bio">${a.description ? esc(a.description) : 'No profile written yet.'}</p>
        <div class="hero-actions">
          <button class="btn btn-ghost btn-sm" id="btn-edit-actor">Edit profile</button>
          <label class="btn btn-ghost btn-sm" style="cursor:pointer">
            Upload photo (435×600)
            <input type="file" id="actor-photo" accept="image/*" hidden>
          </label>
          <label class="btn btn-ghost btn-sm" style="cursor:pointer">
            Upload wide photo (1200×800)
            <input type="file" id="actor-banner" accept="image/*" hidden>
          </label>
          <button class="btn btn-ghost btn-sm" id="btn-find-actor">Find info</button>
          <button class="btn btn-ghost btn-sm" id="btn-hide-actor">${a.hidden ? 'Show on Pornstars' : 'Hide from Pornstars'}</button>
          <button class="btn btn-ghost btn-sm warn" id="btn-delete-actor">Delete profile</button>
        </div>
      </div>
    </div>

    <div class="section">
      <div class="page-head" style="margin-bottom:14px">
        <h2 class="section-title" style="margin:0">Appears in <span class="row-count">${a.videos.length}</span></h2>
        ${a.videos.length > 1 ? `<div class="filters" style="margin:0">${sortSelect('f-actor-sort', sort)}</div>` : ''}
      </div>
      ${a.videos.length
        ? `<div class="grid">${a.videos.map(videoCard).join('')}</div>`
        : `<div class="empty"><h3>No videos linked</h3><p>Add this name to a video's cast list to see it here.</p></div>`}
    </div>`;

  wireCards(view);
  wireRails(view);
  document.getElementById('f-actor-sort')?.addEventListener('change', (e) =>
    go(`#/actor/${id}?sort=${e.target.value}`));
  document.getElementById('btn-edit-actor').addEventListener('click', () => editActor(a));
  document.getElementById('btn-hide-actor').addEventListener('click', async () => {
    await api.put(`/api/actors/${a.id}`, { hidden: a.hidden ? 0 : 1 });
    toast(a.hidden ? 'Showing on the Pornstars page' : 'Hidden from the Pornstars page');
    route();
  });
  document.getElementById('btn-find-actor').addEventListener('click', () =>
    openImport('actor', a.id, a.name, () => route()));
  document.getElementById('btn-delete-actor').addEventListener('click', async () => {
    const msg = a.video_count
      ? `Delete "${a.name}"?\n\nThe ${a.video_count} ${a.video_count === 1 ? 'video keeps' : 'videos keep'} playing, they just lose this credit. No files are deleted.`
      : `Delete "${a.name}"? No files are deleted.`;
    if (!confirm(msg)) return;
    await api.del(`/api/actors/${a.id}`);
    toast('Profile deleted');
    go('#/stars');
  });
  document.getElementById('actor-photo').addEventListener('change', async (e) => {
    const f = e.target.files[0];
    if (!f) return;
    await api.upload(`/api/actors/${a.id}/photo`, f);
    toast('Photo updated');
    route();
  });
  document.getElementById('actor-banner').addEventListener('change', async (e) => {
    const f = e.target.files[0];
    if (!f) return;
    await api.upload(`/api/actors/${a.id}/banner`, f);
    toast('Wide photo updated');
    route();
  });
}

async function renderStudios() {
  setTab('studios');
  loading();
  const data = await api.get('/api/studios?sort=count');
  const empty = data.items.filter((s) => !s.video_count);

  view.innerHTML = `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">Labels</div>
          <h1 class="page-title">Studios</h1>
          <p class="page-sub">${data.items.length} in your library${empty.length ? ` · ${empty.length} with no videos` : ''}</p>
        </div>
        ${empty.length ? `<button class="btn btn-ghost btn-sm" id="btn-prune-studios">Remove ${empty.length} empty ${empty.length === 1 ? 'studio' : 'studios'}</button>` : ''}
      </div>
      ${data.items.length
        ? `<div class="grid grid-studio">${data.items.map(studioCard).join('')}</div>`
        : `<div class="empty"><h3>No studios yet</h3><p>Studios are picked up from folder names during a scan, or you can set one when editing a video.</p></div>`}
    </div>`;

  document.getElementById('btn-prune-studios')?.addEventListener('click', async () => {
    if (!confirm(`Remove ${empty.length} ${empty.length === 1 ? 'studio' : 'studios'} with no videos? No files are deleted.`)) return;
    const r = await api.post('/api/studios/prune');
    toast(`Removed ${r.removed}`);
    renderStudios();
  });
}

async function renderStudio(id, params = new URLSearchParams()) {
  setTab('studios');
  loading();
  const sort = params.get('sort') || 'added';
  const s = await api.get(`/api/studios/${id}?sort=${encodeURIComponent(sort)}`);

  view.innerHTML = `
    <div class="page">
      <div class="page-head">
        <div style="display:flex;gap:20px;align-items:center">
          ${s.image ? `<div class="studio-logo" style="width:132px;height:74px;flex:none;${logoBoxStyle(s)}">
            <img src="/api/studio-image/${s.id}?v=${s.image_v || 0}" alt="" style="${logoImgStyle(s)}"></div>` : ''}
          <div>
            <div class="eyebrow">Studio</div>
            <h1 class="page-title">${esc(s.name)}</h1>
            <p class="page-sub">${s.video_count} ${s.video_count === 1 ? 'video' : 'videos'}</p>
          </div>
        </div>
        <div class="hero-actions" style="margin:0">
          <button class="btn btn-ghost btn-sm" id="btn-edit-studio">Edit studio</button>
          <label class="btn btn-ghost btn-sm" style="cursor:pointer">
            Upload logo
            <input type="file" id="studio-image" accept="image/*" hidden>
          </label>
          ${s.image ? '<button class="btn btn-ghost btn-sm" id="btn-fit-logo">Adjust logo</button>' : ''}
          <button class="btn btn-ghost btn-sm" id="btn-find-studio">Find info</button>
          <button class="btn btn-ghost btn-sm warn" id="btn-delete-studio">Delete studio</button>
        </div>
      </div>
      ${s.description ? `<p class="actor-bio" style="margin-bottom:22px">${esc(s.description)}</p>` : ''}
      ${s.videos.length > 1 ? `<div class="filters">${sortSelect('f-studio-sort', sort)}</div>` : ''}
      <div class="grid">${s.videos.map(videoCard).join('')}</div>
    </div>`;

  wireCards(view);
  wireRails(view);
  document.getElementById('btn-edit-studio').addEventListener('click', () => editStudio(s));
  document.getElementById('f-studio-sort')?.addEventListener('change', (e) =>
    go(`#/studio/${id}?sort=${e.target.value}`));
  document.getElementById('btn-fit-logo')?.addEventListener('click', () => adjustLogo(s, id));
  document.getElementById('btn-find-studio').addEventListener('click', () =>
    openImport('studio', s.id, s.name, () => route()));
  document.getElementById('btn-delete-studio').addEventListener('click', async () => {
    const msg = s.video_count
      ? `Delete "${s.name}"?\n\nIts ${s.video_count} ${s.video_count === 1 ? 'video stays' : 'videos stay'} in the library, just without a studio. No files are deleted.`
      : `Delete "${s.name}"? No files are deleted.`;
    if (!confirm(msg)) return;
    await api.del(`/api/studios/${s.id}`);
    toast('Studio deleted');
    go('#/studios');
  });
  document.getElementById('studio-image').addEventListener('change', async (e) => {
    const f = e.target.files[0];
    if (!f) return;
    await api.upload(`/api/studios/${s.id}/image`, f);
    toast('Logo updated');
    route();
  });
}

/* --------------------------------------------------------------- settings */

let scanPoll = null;

async function renderSettings() {
  setTab('');
  const [locs, stats] = await Promise.all([api.get('/api/locations'), api.get('/api/stats')]);

  view.innerHTML = `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">Library</div>
          <h1 class="page-title">Settings</h1>
          <p class="page-sub">${stats.videos} videos · ${stats.actors} stars · ${stats.studios} studios · ${stats.tags} tags</p>
        </div>
      </div>

      ${!stats.ffmpeg ? `<div class="panel" style="border-color:#5A2A2A">
        <h3 class="warn">ffmpeg was not found</h3>
        <p class="hint">Adult Zone tried to run <code>${esc(stats.ffprobe_bin)}</code> and Windows or your shell could not find it. Playback and metadata still work, but thumbnails and hover previews cannot be generated without it.</p>
        <p class="hint">Install ffmpeg, close this terminal, open a new one, and start the app again. Then use <strong>Build missing artwork</strong> below. If ffmpeg lives somewhere that is not on PATH, set <code>FFMPEG_BIN</code> and <code>FFPROBE_BIN</code> to the full paths of the two .exe files before launching.</p>
      </div>` : ''}

      <div class="panel">
        <h3>Storage folders</h3>
        <p class="hint">Every enabled folder is searched, including subfolders. Add as many as you like.</p>
        <div id="loc-list">
          ${locs.items.length ? locs.items.map((l) => `
            <div class="loc">
              <label class="switch">
                <input type="checkbox" data-toggle="${l.id}" ${l.enabled ? 'checked' : ''}>
              </label>
              <div style="min-width:0">
                <div class="loc-path">${esc(l.path)}</div>
                <div class="loc-meta">${l.video_count} videos${l.exists ? '' : ' · <span class="warn">folder not found</span>'}</div>
              </div>
              <div class="loc-actions">
                <button class="ghost-btn" data-forget="${l.id}">Remove</button>
              </div>
            </div>`).join('')
          : '<p class="hint">No folders added yet.</p>'}
        </div>

        <div style="display:flex;gap:10px;margin-top:18px;flex-wrap:wrap">
          <input type="text" id="new-loc" placeholder="D:\\Videos  or  /home/you/Videos" style="flex:1;min-width:220px">
          <button class="btn btn-ghost btn-sm" id="btn-browse">Browse…</button>
          <button class="btn btn-ember btn-sm" id="btn-add-loc">Add folder</button>
        </div>
      </div>

      <div class="panel">
        <h3>Scan</h3>
        <p class="hint">New files are added, existing entries keep their metadata, and thumbnails plus preview loops are generated in the background.</p>
        <label class="switch" style="margin-bottom:10px">
          <input type="checkbox" id="opt-folder-studio" checked>
          Use the containing folder name as the studio when none is in the filename
        </label>
        <label class="switch" style="margin-bottom:14px">
          <input type="checkbox" id="opt-two-part" checked>
          In <code>Studio - Something.mp4</code>, read "Something" as the performer rather than the title
        </label>
        <label class="field" style="max-width:420px">
          <span>Preview loop quality</span>
          <select id="opt-quality">
            ${Object.entries(stats.preview_profiles).map(([k, v]) =>
              `<option value="${k}" ${k === stats.preview_quality ? 'selected' : ''}>${k} — up to ${v.width}px wide</option>`).join('')}
          </select>
        </label>
        <p class="hint" style="margin:-6px 0 16px">Videos are never upscaled past their own resolution. A wider setting looks sharper as the full-window backdrop and costs more disk.${stats.below_quality ? ` <strong>${stats.below_quality}</strong> existing previews are below the current setting — rebuild to raise them.` : ''}</p>
        <div style="display:flex;gap:10px;align-items:center;flex-wrap:wrap">
          <button class="btn btn-ember btn-sm" id="btn-scan">Scan now</button>
          <button class="btn btn-ghost btn-sm" id="btn-rebuild" ${stats.no_artwork ? '' : 'disabled'}>
            Build missing artwork${stats.no_artwork ? ` (${stats.no_artwork})` : ''}
          </button>
          <button class="btn btn-ghost btn-sm" id="btn-rebuild-all">Rebuild everything</button>
          <span id="scan-msg" class="page-sub" style="margin:0"></span>
        </div>
        <div class="progress"><i id="scan-bar"></i></div>
        <p class="hint" style="margin:12px 0 0">Scanning only builds artwork for newly found files. If ffmpeg was missing during an earlier scan, use <strong>Build missing artwork</strong> to fill in the gaps without re-adding anything.</p>
      </div>

      <div class="panel">
        <h3>Metadata sources</h3>
        <p class="hint">Wikipedia and page addresses work with no setup. ThePornDB needs your own API key from their site, and is only used when you pick it.</p>
        <div style="display:flex;gap:10px;align-items:flex-end;flex-wrap:wrap;max-width:560px">
          <label class="field" style="flex:1;min-width:240px;margin:0">
            <span>ThePornDB API key (optional)</span>
            <input type="password" id="tpdb-key" placeholder="leave blank to disable" autocomplete="off">
          </label>
          <button class="btn btn-ghost btn-sm" id="tpdb-save">Save key</button>
          <button class="btn btn-ghost btn-sm" id="tpdb-test">Test key</button>
        </div>
        <label class="field" style="max-width:560px;margin-top:12px">
          <span>ThePornDB address — blank uses the default, https://api.theporndb.net</span>
          <input type="text" id="tpdb-base" placeholder="https://api.theporndb.net">
        </label>
        <p class="hint" id="tpdb-state" style="margin:10px 0 0"></p>
      </div>

      <div class="panel">
        <h3>Screen lock</h3>
        <p class="hint" id="lock-state">…</p>
        <div id="lock-controls"></div>
        <p class="hint" style="margin:14px 0 0">The PIN is stored as a salted hash, and while locked the app refuses to serve any video, image, or metadata. It is not encryption — someone with access to this Windows account can still open the video files directly — but it keeps the library off the screen at a glance.</p>
      </div>

      <div class="panel">
        <h3>Version</h3>
        <p class="hint" style="margin:0">Adult Zone <strong id="app-version">…</strong> — if this doesn't match the build you just installed, an older copy is still running. Close every Adult Zone console window and start it again.</p>
      </div>

      <div class="panel">
        <h3>Filename conventions</h3>
        <p class="hint">Scanning reads what it can from the filename. Anything it gets wrong stays editable per video.</p>
        <div class="loc-path" style="color:var(--muted);line-height:2">
          Studio - Actor Name.mp4<br>
          Studio - Actor One, Actor Two.mp4<br>
          Studio - Actor Name - 27.07.2021.mp4<br>
          Studio - Actor One, Actor Two - 27.07.2021.mp4<br>
          Studio - Actor Name - Title.mp4<br>
          Studio - Actor One, Actor Two - Title (2024).mp4<br>
          Actor Name - Title - [Studio].mp4<br>
          Actor One, Actor Two - Title (2024) - [Studio].mp4<br>
          Studio.24.03.15.Actor.Name.Title.mp4
        </div>
        <p class="hint" style="margin-top:12px">Dates are read as day-first, so 27.07.2021 is 27 July. A studio in brackets is picked up wherever it appears; release tags such as [1080p] or [x265] are ignored.</p>
      </div>
    </div>`;

  document.getElementById('btn-add-loc').addEventListener('click', addLocation);
  document.getElementById('new-loc').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') addLocation();
  });
  document.getElementById('btn-browse').addEventListener('click', () => openPicker());
  document.getElementById('btn-scan').addEventListener('click', startScan);
  renderLockSettings();
  api.get('/api/scrape/providers').then((p) => {
    const state = document.getElementById('tpdb-state');
    const base = document.getElementById('tpdb-base');
    if (base) base.value = p.tpdb_base || '';
    if (state) state.textContent = p.tpdb_key_set
      ? 'A key is saved. Use Test key to check it before searching.'
      : 'No key saved — ThePornDB is greyed out in the Find info dialog.';
  }).catch(() => {});

  document.getElementById('tpdb-save')?.addEventListener('click', async () => {
    await api.put('/api/scrape/tpdb-key', {
      key: document.getElementById('tpdb-key').value,
      base: document.getElementById('tpdb-base').value,
    });
    toast('Saved');
    renderSettings();
  });

  document.getElementById('tpdb-test')?.addEventListener('click', async () => {
    const state = document.getElementById('tpdb-state');
    state.textContent = 'Checking…';
    state.classList.remove('warn');
    try {
      const r = await api.get('/api/scrape/tpdb-test');
      state.textContent = r.ok
        ? `${r.message} (key is ${r.key_length} characters)`
        : r.message;
      state.classList.toggle('warn', !r.ok);
    } catch (e) {
      state.textContent = e.message;
      state.classList.add('warn');
    }
  });
  api.get('/api/version').then((v) => {
    const el = document.getElementById('app-version');
    if (el) el.textContent = v.version;
  }).catch(() => {});
  document.getElementById('btn-rebuild').addEventListener('click', () => rebuildArtwork(1));
  document.getElementById('opt-quality').addEventListener('change', async (e) => {
    await api.put('/api/settings/preview-quality', { quality: e.target.value });
    toast('Saved. Use Rebuild everything to apply it to existing videos.');
    renderSettings();
  });
  document.getElementById('btn-rebuild-all').addEventListener('click', () => {
    if (confirm('Regenerate the thumbnail and preview for every video? Uploaded thumbnails are kept.')) rebuildArtwork(0);
  });

  view.querySelectorAll('[data-toggle]').forEach((cb) => cb.addEventListener('change', async () => {
    await api.put(`/api/locations/${cb.dataset.toggle}`, { enabled: cb.checked });
    toast(cb.checked ? 'Folder enabled' : 'Folder skipped on next scan');
  }));

  view.querySelectorAll('[data-forget]').forEach((b) => b.addEventListener('click', async () => {
    const purge = confirm('Also remove this folder\'s videos from the library?\n\nOK = remove entries, Cancel = keep them.');
    await api.del(`/api/locations/${b.dataset.forget}?purge=${purge ? 1 : 0}`);
    renderSettings();
  }));

  pollScan();
}

async function renderLockSettings() {
  const state = document.getElementById('lock-state');
  const box = document.getElementById('lock-controls');
  if (!state || !box) return;

  let status;
  try { status = await api.get('/api/lock/status'); } catch { return; }

  if (!status.enabled) {
    state.textContent = 'Off — the app opens straight into the library.';
    box.innerHTML = `
      <div class="field-row" style="max-width:460px">
        <label class="field"><span>New PIN (4–8 digits)</span>
          <input type="password" id="pin-new" inputmode="numeric" autocomplete="off" maxlength="8"></label>
        <label class="field"><span>Confirm</span>
          <input type="password" id="pin-new2" inputmode="numeric" autocomplete="off" maxlength="8"></label>
      </div>
      <button class="btn btn-ember btn-sm" id="pin-enable">Turn on screen lock</button>`;

    document.getElementById('pin-enable').addEventListener('click', async () => {
      const a = document.getElementById('pin-new').value.trim();
      const b = document.getElementById('pin-new2').value.trim();
      if (a !== b) return toast('The two PINs do not match');
      if (!/^[0-9]{4,8}$/.test(a)) return toast('Use 4 to 8 digits');
      try {
        await api.post('/api/lock/set', { pin: a });
        toast('Screen lock is on');
        renderLockSettings();
      } catch (e) { toast(e.message); }
    });
    return;
  }

  state.textContent = `On — ${status.length} digits. The app locks itself every time it starts.`;
  box.innerHTML = `
    <div class="field-row" style="max-width:700px;grid-template-columns:1fr 1fr 1fr">
      <label class="field"><span>Current PIN</span>
        <input type="password" id="pin-cur" inputmode="numeric" autocomplete="off" maxlength="8"></label>
      <label class="field"><span>New PIN</span>
        <input type="password" id="pin-new" inputmode="numeric" autocomplete="off" maxlength="8"></label>
      <label class="field"><span>Confirm new</span>
        <input type="password" id="pin-new2" inputmode="numeric" autocomplete="off" maxlength="8"></label>
    </div>
    <div style="display:flex;gap:10px;flex-wrap:wrap">
      <button class="btn btn-ember btn-sm" id="pin-change">Change PIN</button>
      <button class="btn btn-ghost btn-sm" id="pin-lock">Lock now</button>
      <button class="btn btn-ghost btn-sm warn" id="pin-off">Turn off</button>
    </div>`;

  document.getElementById('pin-change').addEventListener('click', async () => {
    const cur = document.getElementById('pin-cur').value.trim();
    const a = document.getElementById('pin-new').value.trim();
    const b = document.getElementById('pin-new2').value.trim();
    if (a !== b) return toast('The two PINs do not match');
    if (!/^[0-9]{4,8}$/.test(a)) return toast('Use 4 to 8 digits');
    try {
      await api.post('/api/lock/set', { current: cur, pin: a });
      toast('PIN changed');
      renderLockSettings();
    } catch (e) { toast(e.message); }
  });

  document.getElementById('pin-off').addEventListener('click', async () => {
    const cur = document.getElementById('pin-cur').value.trim();
    if (!cur) return toast('Enter your current PIN first');
    try {
      await api.post('/api/lock/disable', { current: cur });
      toast('Screen lock is off');
      renderLockSettings();
    } catch (e) { toast(e.message); }
  });

  document.getElementById('pin-lock').addEventListener('click', async () => {
    await api.post('/api/lock/now');
    showLock(await fetch('/api/lock/status').then((r) => r.json()));
  });
}

async function addLocation() {
  const input = document.getElementById('new-loc');
  const path = input.value.trim();
  if (!path) return;
  try {
    await api.post('/api/locations', { path });
    input.value = '';
    toast('Folder added. Run a scan to pull the videos in.');
    renderSettings();
  } catch (e) { toast(e.message); }
}

async function openPicker(start) {
  const data = await api.post('/api/browse', { path: start || '' });
  showModal(`
    <div class="modal-head">
      <h3>Choose a folder</h3>
      <button class="icon-btn" data-close><svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
    </div>
    <div class="picker-cwd">${esc(data.path)}</div>
    ${data.drives.length ? `<div style="display:flex;gap:8px;margin-bottom:10px;flex-wrap:wrap">
      ${data.drives.map((d) => `<button class="ghost-btn" data-nav="${esc(d)}">${esc(d)}</button>`).join('')}
    </div>` : ''}
    <div class="picker">
      ${data.parent ? `<div class="picker-item" data-nav="${esc(data.parent)}">
        <svg viewBox="0 0 24 24"><path d="M12 19V5M5 12l7-7 7 7"/></svg> Up one level</div>` : ''}
      ${data.dirs.map((d) => `<div class="picker-item" data-nav="${esc(d.path)}">
        <svg viewBox="0 0 24 24"><path d="M3 7h6l2 2h10v9a2 2 0 0 1-2 2H3z"/></svg> ${esc(d.name)}</div>`).join('')}
    </div>
    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" data-close>Cancel</button>
      <button class="btn btn-ember btn-sm" data-choose="${esc(data.path)}">Use this folder</button>
    </div>`);

  document.querySelectorAll('[data-nav]').forEach((el) =>
    el.addEventListener('click', () => openPicker(el.dataset.nav)));
  document.querySelector('[data-choose]').addEventListener('click', async (e) => {
    closeModal();
    try {
      await api.post('/api/locations', { path: e.currentTarget.dataset.choose });
      toast('Folder added. Run a scan to pull the videos in.');
      renderSettings();
    } catch (err) { toast(err.message); }
  });
}

async function startScan() {
  const folderStudio = document.getElementById('opt-folder-studio').checked ? 1 : 0;
  const twoPart = document.getElementById('opt-two-part').checked ? 1 : 0;
  await api.post(`/api/scan?prune=1&folder_as_studio=${folderStudio}&two_part_actor=${twoPart}`);
  toast('Scan started');
  pollScan();
}

async function rebuildArtwork(onlyMissing) {
  try {
    const r = await api.post(`/api/assets/rebuild?only_missing=${onlyMissing}`);
    toast(r.queued ? `Building artwork for ${r.queued} videos` : 'Nothing to build');
    pollScan();
  } catch (e) { toast(e.message); }
}

async function pollScan() {
  clearInterval(scanPoll);
  const tick = async () => {
    const msg = document.getElementById('scan-msg');
    const bar = document.getElementById('scan-bar');
    if (!msg || !bar) return clearInterval(scanPoll);

    const s = await api.get('/api/scan/status');
    if (s.phase === 'idle') { msg.textContent = ''; return; }

    const assetsPending = s.assets_total > s.assets_done;
    if (s.running) {
      msg.textContent = `Scanning — ${s.found} files seen, ${s.added} added · ${s.current}`;
      bar.style.width = '35%';
    } else if (assetsPending) {
      msg.textContent = `Building thumbnails and previews — ${s.assets_done} of ${s.assets_total} · ${s.current}`;
      bar.style.width = `${Math.round((s.assets_done / Math.max(s.assets_total, 1)) * 100)}%`;
    } else {
      msg.textContent = `Done — ${s.added} added, ${s.updated} restored, ${s.removed} missing`;
      bar.style.width = '100%';
      clearInterval(scanPoll);
    }
    if (s.error) { msg.textContent = s.error; clearInterval(scanPoll); }
  };
  tick();
  scanPoll = setInterval(tick, 1200);
}

/* ----------------------------------------------------------------- editors */

/* ------------------------------------------------------- metadata import */

// Fields each kind of record can take, and the label shown against them.
// Everything is ticked by default -- untick what you do not want. Only fields
// the source actually returned are shown at all.
const IMPORT_FIELDS = {
  actor: [
    ['name', 'Name', true],
    ['description', 'Biography', true],
    ['birthdate', 'Date of birth', true],
    ['image', 'Portrait', true],
    ['banner', 'Wide photo', true],
  ],
  studio: [
    ['name', 'Name', true],
    ['description', 'Description', true],
    ['image', 'Logo', true],
  ],
  video: [
    ['name', 'Title', true],
    ['description', 'Description', true],
    ['date', 'Release date', true],
    ['performers', 'Cast', true],
    ['site', 'Sub-site', true],
    ['tags', 'Tags from the database', true],
    ['studio', 'Studio', true],
    ['image', 'Thumbnail', true],
  ],
};

const IMPORT_KIND = { actor: 'performer', studio: 'studio', video: 'scene' };

async function openImport(kind, targetId, query, onDone, hints = {}) {
  let providers = [];
  try { providers = (await api.get('/api/scrape/providers')).items; } catch { }
  const searchKind = IMPORT_KIND[kind];
  const usable = providers.filter((p) => p.kinds.includes(searchKind));
  // ThePornDB first when it is usable, since it is the richest source here.
  const ordered = [...usable].sort((a, b) => {
    const rank = (p) => (p.id === 'tpdb' && p.ready ? 0 : p.ready ? 1 : 2);
    return rank(a) - rank(b);
  });

  showModal(`
    <div class="modal-head">
      <h3>Find info</h3>
      <button class="icon-btn" data-close><svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
    </div>
    ${kind === 'video' ? `
    <div class="imp-modes">
      <button class="imp-mode on" data-mode="title">Search by title</button>
      <button class="imp-mode" data-mode="pair">Search by studio + sub-site + performer</button>
    </div>` : ''}
    <div id="imp-pair" hidden style="display:flex;gap:10px;flex-wrap:wrap;margin-bottom:12px">
      <label class="field" style="flex:1;min-width:140px;margin:0">
        <span>Studio</span><input type="text" id="imp-studio" value="${esc(hints.studio || '')}">
      </label>
      <label class="field" style="flex:1;min-width:140px;margin:0">
        <span>Sub-site</span><input type="text" id="imp-subsite" value="${esc(hints.subsite || '')}">
      </label>
      <label class="field" style="flex:1;min-width:140px;margin:0">
        <span>Performer</span><input type="text" id="imp-performer" value="${esc(hints.performer || '')}">
      </label>
    </div>
    <div style="display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end">
      <label class="field" style="flex:0 0 170px;margin:0">
        <span>Source</span>
        <select id="imp-provider">
          ${ordered.map((p) => `<option value="${p.id}" ${p.ready ? '' : 'disabled'}>${esc(p.name)}${p.ready ? '' : ' — needs a key'}</option>`).join('')}
        </select>
      </label>
      <label class="field" style="flex:1;min-width:220px;margin:0">
        <span id="imp-label">Search for</span>
        <input type="text" id="imp-query" value="${esc(query || '')}">
      </label>
      <button class="btn btn-ember btn-sm" id="imp-go">Search</button>
    </div>
    <p class="hint" id="imp-note" style="margin:10px 0 0"></p>
    <div id="imp-results" style="margin-top:16px"></div>`);

  const providerSel = document.getElementById('imp-provider');
  const queryInput = document.getElementById('imp-query');
  const label = document.getElementById('imp-label');
  const note = document.getElementById('imp-note');
  const results = document.getElementById('imp-results');

  const syncProvider = () => {
    const chosen = usable.find((p) => p.id === providerSel.value);
    const isUrl = providerSel.value === 'url';
    label.textContent = isUrl ? 'Page address' : 'Search for';
    queryInput.placeholder = isUrl ? 'https://example.com/the-page' : '';
    // Fall back to the normal note; only ThePornDB has a different one to show
    // once its key is in place.
    note.textContent = chosen ? (chosen.ready ? (chosen.readyNote || chosen.note) : chosen.note) : '';
  };
  providerSel.addEventListener('change', syncProvider);
  syncProvider();

  let mode = 'title';
  const pairBox = document.getElementById('imp-pair');
  const titleField = queryInput.closest('.field');

  document.querySelectorAll('.imp-mode').forEach((b) => {
    b.addEventListener('click', () => {
      mode = b.dataset.mode;
      document.querySelectorAll('.imp-mode').forEach((x) => x.classList.toggle('on', x === b));
      pairBox.hidden = mode !== 'pair';
      titleField.style.display = mode === 'pair' ? 'none' : '';
    });
  });

  // Studio and performer together, for when the filename gave a wrong title.
  // Any combination of the three; blanks are simply left out.
  const currentQuery = () => {
    if (mode !== 'pair') return queryInput.value.trim();
    return ['imp-studio', 'imp-subsite', 'imp-performer']
      .map((id) => document.getElementById(id).value.trim())
      .filter(Boolean)
      .join(' ');
  };

  async function run() {
    const query = currentQuery();
    if (!query) { results.innerHTML = '<p class="hint">Type something to search for.</p>'; return; }
    results.innerHTML = '<p class="hint">Looking…</p>';
    let items = [];
    try {
      items = (await api.post('/api/scrape/search', {
        provider: providerSel.value,
        kind: searchKind,
        query,
      })).items;
    } catch (e) {
      results.innerHTML = `<p class="hint warn">${esc(e.message)}</p>`;
      return;
    }
    if (!items.length) {
      results.innerHTML = '<p class="hint">Nothing found. Try a different spelling.</p>';
      return;
    }
    results.innerHTML = items.map((r, i) => `
      <div class="imp-row" data-i="${i}">
        <div class="imp-thumb">${r.image ? `<img src="${esc(r.image)}" alt="" onerror="this.remove()">` : ''}</div>
        <div style="min-width:0">
          <div class="imp-name">${esc(r.name || 'Untitled')}</div>
          <div class="imp-sub">${esc(r.subtitle || '')}${r.date ? ` · ${esc(r.date)}` : ''}</div>
          <div class="imp-desc">${esc((r.description || '').slice(0, 180))}</div>
          <div class="imp-src">${esc(r.attribution || r.source)}</div>
        </div>
      </div>`).join('');

    results.querySelectorAll('.imp-row').forEach((row) => {
      row.addEventListener('click', () => choose(items[Number(row.dataset.i)]));
    });
  }

  function choose(result) {
    const available = IMPORT_FIELDS[kind].filter(([key]) => {
      if (key === 'name') return !!result.name;
      if (key === 'date') return !!result.date;
      if (key === 'birthdate') return !!result.birthdate;
      if (key === 'site') return !!result.site;
      if (key === 'studio') return !!result.studio;
      if (key === 'performers') return (result.performers || []).length > 0;
      if (key === 'tags') return (result.tags || []).length > 0;
      return !!result[key];
    });

    if (!available.length) {
      toast('That result has nothing to import');
      return;
    }

    results.innerHTML = `
      <p class="hint" style="margin:0 0 12px">From ${esc(result.attribution || result.source)} — tick what you want to keep.</p>
      ${available.map(([key, label, on]) => {
        const value = key === 'birthdate' ? result.birthdate : result[key];
        const preview = key === 'image' || key === 'banner'
          ? `<img class="imp-preview" src="${esc(value)}" alt="" onerror="this.remove()">`
          : Array.isArray(value)
            ? `<div class="imp-value">${value.map((x) => `<span class="imp-chip">${esc(x)}</span>`).join('')}</div>`
            : `<div class="imp-value">${esc(String(value).slice(0, 400))}${String(value).length > 400 ? '…' : ''}</div>`;
        return `
          <label class="imp-field">
            <input type="checkbox" data-field="${key}" ${on ? 'checked' : ''}>
            <div style="min-width:0"><b>${label}</b>${preview}</div>
          </label>`;
      }).join('')}
      ${kind === 'video' ? `
      <label class="switch" style="margin-top:14px">
        <input type="checkbox" id="imp-replace" checked>
        Replace the cast and tags already on this video
      </label>` : ''}
      <div class="modal-foot">
        <button class="btn btn-ghost btn-sm" id="imp-back">Back to results</button>
        <button class="btn btn-ember btn-sm" id="imp-apply">Apply selected</button>
      </div>`;

    document.getElementById('imp-back').addEventListener('click', run);
    document.getElementById('imp-apply').addEventListener('click', async () => {
      const fields = { source: result.url || result.attribution || result.source };
      results.querySelectorAll('[data-field]').forEach((box) => {
        if (!box.checked) return;
        const key = box.dataset.field;
        if (key === 'name') fields[kind === 'video' ? 'title' : 'name'] = result.name;
        else if (key === 'date') fields.release_date = result.date;
        else if (key === 'birthdate') fields.birthdate = result.birthdate;
        else if (key === 'site') fields.subsite = result.site;
        else if (key === 'tags') fields.tags = [...(fields.tags || []), ...result.tags];
        else if (key === 'performers') fields.actors = [...result.performers];
        else fields[key] = result[key];
      });
      try {
        const replace = document.getElementById('imp-replace')?.checked ?? true;
        const res = await api.post('/api/scrape/apply',
          { kind, id: targetId, fields, replace });
        toast(res.applied.length ? `Updated ${res.applied.join(', ')}` : 'Nothing selected');
        closeModal();
        if (onDone) onDone();
      } catch (e) { toast(e.message); }
    });
  }

  document.getElementById('imp-go').addEventListener('click', run);
  queryInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') run(); });
  ['imp-studio', 'imp-subsite', 'imp-performer'].forEach((id) => {
    document.getElementById(id)?.addEventListener('keydown', (e) => { if (e.key === 'Enter') run(); });
  });
  if ((query || '').trim() && providerSel.value !== 'url') run();
}

function showModal(html) {
  const modal = document.getElementById('modal');
  document.getElementById('modal-card').innerHTML = html;
  modal.hidden = false;
  modal.querySelectorAll('[data-close]').forEach((b) => b.addEventListener('click', closeModal));
  modal.onclick = (e) => { if (e.target === modal) closeModal(); };
}

function closeModal() { document.getElementById('modal').hidden = true; }

function editVideo(v) {
  showModal(`
    <div class="modal-head">
      <h3>Edit details</h3>
      <button class="icon-btn" data-close><svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
    </div>
    <label class="field"><span>Title</span><input type="text" id="e-title" value="${esc(v.title)}"></label>
    <label class="field"><span>Description</span><textarea id="e-desc">${esc(v.description || '')}</textarea></label>
    <div class="field-row">
      <label class="field"><span>Studio</span><input type="text" id="e-studio" value="${esc(v.studio_name || '')}"></label>
      <label class="field"><span>Sub-site</span><input type="text" id="e-subsite" value="${esc(v.subsite || '')}"></label>
      <label class="field"><span>Release date</span><input type="date" id="e-date" value="${esc((v.release_date || '').slice(0, 10))}"></label>
    </div>
    <label class="field"><span>Cast (comma separated)</span>
      <input type="text" id="e-actors" value="${esc(v.actors.map((a) => a.name).join(', '))}"></label>
    <label class="field"><span>Tags (comma separated)</span>
      <input type="text" id="e-tags" value="${esc(v.tags.join(', '))}"></label>
    <label class="field"><span>Thumbnail — upload your own screenshot</span>
      <input type="file" id="e-thumb" accept="image/*"></label>
    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" data-close>Cancel</button>
      <button class="btn btn-ember btn-sm" id="e-save">Save changes</button>
    </div>`);

  document.getElementById('e-save').addEventListener('click', async () => {
    const file = document.getElementById('e-thumb').files[0];
    await api.put(`/api/videos/${v.id}`, {
      title: document.getElementById('e-title').value.trim() || v.title,
      description: document.getElementById('e-desc').value.trim(),
      studio: document.getElementById('e-studio').value.trim(),
      subsite: document.getElementById('e-subsite').value.trim(),
      release_date: document.getElementById('e-date').value || null,
      actors: splitList(document.getElementById('e-actors').value),
      tags: splitList(document.getElementById('e-tags').value),
    });
    if (file) await api.upload(`/api/videos/${v.id}/thumbnail`, file);
    closeModal();
    toast('Saved');
    renderVideo(v.id);
  });
}

async function editActor(a) {
  const list = await countries();
  showModal(`
    <div class="modal-head">
      <h3>Edit profile</h3>
      <button class="icon-btn" data-close><svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
    </div>
    <label class="field"><span>Name</span><input type="text" id="a-name" value="${esc(a.name)}"></label>
    <div class="field-row">
      <label class="field"><span>Age</span><input type="number" id="a-age" min="18" max="99" value="${esc(a.age || '')}"></label>
      <label class="field"><span>Date of birth</span><input type="date" id="a-birth" value="${esc((a.birthdate || '').slice(0, 10))}"></label>
    </div>
    <div class="field-row">
      <label class="field"><span>Nationality</span>
        <select id="a-country">
          <option value="">Not set</option>
          ${list.map((c) => `<option value="${c.code}" ${c.code === a.country ? 'selected' : ''}>${esc(c.name)}</option>`).join('')}
        </select>
      </label>
      <label class="field"><span>Career status</span>
        <select id="a-status">
          <option value="">Not set</option>
          ${STATUSES.map(([v, l]) => `<option value="${v}" ${v === a.status ? 'selected' : ''}>${l}</option>`).join('')}
        </select>
      </label>
    </div>
    <label class="field"><span>Description</span><textarea id="a-desc">${esc(a.description || '')}</textarea></label>
    <label class="field"><span>Photo (435 × 600 works best)</span>
      <input type="file" id="a-photo" accept="image/*"></label>
    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" data-close>Cancel</button>
      <button class="btn btn-ember btn-sm" id="a-save">Save profile</button>
    </div>`);

  document.getElementById('a-save').addEventListener('click', async () => {
    const file = document.getElementById('a-photo').files[0];
    const res = await api.put(`/api/actors/${a.id}`, {
      name: document.getElementById('a-name').value.trim() || a.name,
      age: document.getElementById('a-age').value,
      birthdate: document.getElementById('a-birth').value,
      country: document.getElementById('a-country').value,
      status: document.getElementById('a-status').value,
      description: document.getElementById('a-desc').value.trim(),
    });
    closeModal();
    if (res.merged_into) {
      toast(`Merged ${res.merged} ${res.merged === 1 ? 'credit' : 'credits'} into ${res.name}`);
      return go(`#/actor/${res.merged_into}`);
    }
    if (file) await api.upload(`/api/actors/${a.id}/photo`, file);
    toast('Profile saved');
    route();
  });
}

function adjustLogo(s, studioId) {
  const start = {
    logo_fit: s.logo_fit || 'contain',
    logo_zoom: s.logo_zoom || 100,
    logo_x: s.logo_x ?? 50,
    logo_y: s.logo_y ?? 50,
    logo_bg: s.logo_bg || 'dark',
  };
  const live = { ...start };
  const src = `/api/studio-image/${s.id}?v=${s.image_v || 0}`;

  showModal(`
    <div class="modal-head">
      <h3>Adjust logo</h3>
      <button class="icon-btn" data-close><svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
    </div>

    <p class="hint" style="margin:0 0 14px">This is exactly how it will look on the Studios page.</p>
    <div class="logo-preview">
      <div class="studio-logo" id="lp-box"><img id="lp-img" src="${src}" alt=""></div>
      <div class="studio-name" style="margin-top:9px">${esc(s.name)}</div>
      <div class="studio-count">${s.video_count} videos</div>
    </div>

    <div class="field-row" style="margin-top:18px">
      <label class="field"><span>Fit</span>
        <select id="lf-fit">
          <option value="contain">Contain — whole logo, letterboxed</option>
          <option value="cover">Cover — fill the box, crop the edges</option>
          <option value="fill">Stretch to the box</option>
          <option value="none">Original size</option>
        </select>
      </label>
      <label class="field"><span>Behind it</span>
        <select id="lf-bg">
          <option value="dark">Dark (matches the app)</option>
          <option value="black">Black</option>
          <option value="white">White</option>
          <option value="light">Light grey</option>
          <option value="none">Transparent</option>
        </select>
      </label>
    </div>

    <label class="field"><span>Zoom — <b id="lf-zoom-val"></b></span>
      <input type="range" id="lf-zoom" min="25" max="400" step="5"></label>
    <div class="field-row">
      <label class="field"><span>Across — <b id="lf-x-val"></b></span>
        <input type="range" id="lf-x" min="0" max="100"></label>
      <label class="field"><span>Down — <b id="lf-y-val"></b></span>
        <input type="range" id="lf-y" min="0" max="100"></label>
    </div>

    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" id="lf-reset">Reset</button>
      <button class="btn btn-ghost btn-sm" data-close>Cancel</button>
      <button class="btn btn-ember btn-sm" id="lf-save">Save</button>
    </div>`);

  const box = document.getElementById('lp-box');
  const img = document.getElementById('lp-img');
  const controls = {
    logo_fit: document.getElementById('lf-fit'),
    logo_bg: document.getElementById('lf-bg'),
    logo_zoom: document.getElementById('lf-zoom'),
    logo_x: document.getElementById('lf-x'),
    logo_y: document.getElementById('lf-y'),
  };

  function paint() {
    img.style.cssText = logoImgStyle(live);
    box.style.cssText = logoBoxStyle(live);
    document.getElementById('lf-zoom-val').textContent = `${live.logo_zoom}%`;
    document.getElementById('lf-x-val').textContent = `${live.logo_x}%`;
    document.getElementById('lf-y-val').textContent = `${live.logo_y}%`;
  }

  function load(values) {
    Object.assign(live, values);
    Object.entries(controls).forEach(([key, el]) => { el.value = live[key]; });
    paint();
  }

  Object.entries(controls).forEach(([key, el]) => {
    el.addEventListener('input', () => {
      live[key] = el.type === 'range' ? Number(el.value) : el.value;
      paint();
    });
  });

  document.getElementById('lf-reset').addEventListener('click', () =>
    load({ logo_fit: 'contain', logo_zoom: 100, logo_x: 50, logo_y: 50, logo_bg: 'dark' }));

  document.getElementById('lf-save').addEventListener('click', async () => {
    await api.put(`/api/studios/${s.id}`, live);
    closeModal();
    toast('Logo adjusted');
    route();
  });

  load(start);
}

function editStudio(s) {
  showModal(`
    <div class="modal-head">
      <h3>Edit studio</h3>
      <button class="icon-btn" data-close><svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
    </div>
    <label class="field"><span>Name</span><input type="text" id="s-name" value="${esc(s.name)}"></label>
    <label class="field"><span>Description</span><textarea id="s-desc">${esc(s.description || '')}</textarea></label>
    <label class="field"><span>Logo</span><input type="file" id="s-image" accept="image/*"></label>
    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" data-close>Cancel</button>
      <button class="btn btn-ember btn-sm" id="s-save">Save studio</button>
    </div>`);

  document.getElementById('s-save').addEventListener('click', async () => {
    const file = document.getElementById('s-image').files[0];
    const res = await api.put(`/api/studios/${s.id}`, {
      name: document.getElementById('s-name').value.trim() || s.name,
      description: document.getElementById('s-desc').value.trim(),
    });
    closeModal();
    // Renaming onto a studio that already exists folds them together, so this
    // page no longer exists -- follow it to the one that absorbed it.
    if (res.merged_into) {
      toast(`Merged ${res.merged} ${res.merged === 1 ? 'video' : 'videos'} into ${res.name}`);
      return go(`#/studio/${res.merged_into}`);
    }
    if (file) await api.upload(`/api/studios/${s.id}/image`, file);
    toast('Studio saved');
    route();
  });
}

/* ------------------------------------------------------------------ stage */

// The player opens inside the hero rather than over the whole page. The
// backdrop loop pauses while it's open and picks up again on close.
let stage = null;

function openStage(v) {
  if (stage) closeStage();

  const hero = document.querySelector('.detail-hero');
  const loop = document.getElementById('detail-video');
  if (loop) loop.pause();
  hero?.classList.add('playing');

  const box = document.createElement('div');
  box.className = 'stage-player';
  box.id = 'stage-player';
  box.innerHTML = `
    <div class="stage-bar">
      <span class="stage-name">${esc(v.title)}</span>
      <button class="ghost-btn" id="btn-frame-thumb">Use this frame as thumbnail</button>
      <button class="icon-btn" id="btn-close-stage" title="Close player" aria-label="Close player">
        <svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>
      </button>
    </div>
    <video id="stage-video" controls playsinline autoplay
           src="/api/stream/${v.id}"></video>`;
  hero?.insertBefore(box, hero.querySelector('.detail-body'));

  const video = box.querySelector('#stage-video');
  stage = { id: v.id, video, box, loop, hero };

  video.play().catch(() => {});
  video.addEventListener('ended', closeStage);
  box.querySelector('#btn-close-stage').addEventListener('click', closeStage);
  box.querySelector('#btn-frame-thumb').addEventListener('click', async () => {
    try {
      await api.post(`/api/videos/${v.id}/thumbnail/frame`, { time: video.currentTime });
      toast('Thumbnail updated from this frame');
    } catch (e) { toast(e.message); }
  });

  api.post(`/api/videos/${v.id}/view`).catch(() => {});
}

function closeStage() {
  if (!stage) return;
  const { video, box, loop, hero } = stage;
  video.pause();
  video.removeAttribute('src');
  video.load();
  box.remove();
  hero?.classList.remove('playing');
  if (loop) loop.play().catch(() => {});
  stage = null;
}

/* --------------------------------------------------------------- heartbeat */

// Holding this connection open is what tells the server someone is here, so
// closing the tab closes the console window too. It deliberately isn't a timer:
// browsers throttle background tabs to about one tick a minute, which would make
// a minimised window look closed. A dropped connection is unambiguous.
(function presence() {
  let source = null;
  let retry = null;

  const connect = () => {
    clearTimeout(retry);
    try {
      source = new EventSource('/api/live');
    } catch {
      return;   // no EventSource means no auto-close; the server simply stays up
    }
    source.onerror = () => {
      source.close();
      retry = setTimeout(connect, 1500);   // survive a server restart
    };
  };

  connect();

  // Closing brings the shutdown forward instead of waiting on the socket.
  // A reload fires this too, which the grace period absorbs.
  window.addEventListener('pagehide', () => {
    if (source) source.close();
    if (navigator.sendBeacon) navigator.sendBeacon('/api/goodbye');
  });
})();

/* ------------------------------------------------------------------- lock */

const lockEl = document.getElementById('lock');
const lockCard = document.getElementById('lock-card');
const lockHint = document.getElementById('lock-hint');
const pinDots = document.getElementById('pin-dots');

let pinEntry = '';
let pinLength = 4;
let lockBusy = false;

function drawDots() {
  const total = Math.max(pinLength || 4, pinEntry.length, 4);
  pinDots.innerHTML = Array.from({ length: total },
    (_, i) => `<i class="${i < pinEntry.length ? 'on' : ''}"></i>`).join('');
  if (!lockBusy) {
    lockHint.textContent = pinEntry.length
      ? 'Press the tick or Enter to unlock'
      : 'Enter your PIN';
    lockHint.classList.remove('bad');
  }
}

function showLock(status, keepEntry = false) {
  const alreadyShowing = !lockEl.hidden;
  pinLength = status.length || 4;
  // Never clear digits the user is in the middle of typing.
  if (!keepEntry && !alreadyShowing) pinEntry = '';
  drawDots();
  lockHint.textContent = 'Enter your PIN';
  lockHint.classList.remove('bad');
  lockEl.hidden = false;
  document.body.classList.add('locked');
  if (status.wait > 0) countdown(status.wait);
}

function hideLock() {
  lockEl.hidden = true;
  document.body.classList.remove('locked');
  pinEntry = '';
}

let waitTimer = null;
function countdown(seconds) {
  clearInterval(waitTimer);
  let left = Math.ceil(seconds);
  const tick = () => {
    if (left <= 0) {
      clearInterval(waitTimer);
      lockHint.textContent = 'Enter your PIN';
      lockHint.classList.remove('bad');
      lockBusy = false;
      return;
    }
    lockHint.textContent = `Too many attempts — wait ${left}s`;
    lockHint.classList.add('bad');
    left -= 1;
  };
  lockBusy = true;
  tick();
  waitTimer = setInterval(tick, 1000);
}

async function submitPin() {
  if (lockBusy || !pinEntry) return;
  lockBusy = true;
  let res;
  try {
    res = await fetch('/api/lock/unlock', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: pinEntry }),
    }).then((r) => r.json());
  } catch {
    lockBusy = false;
    return;
  }

  if (res.ok) {
    hideLock();
    lockBusy = false;
    route();
    warmUp();
    return;
  }

  pinEntry = '';
  drawDots();
  lockCard.classList.add('wrong');
  setTimeout(() => lockCard.classList.remove('wrong'), 450);
  if (res.wait > 0) {
    countdown(res.wait);
  } else {
    lockHint.textContent = 'Wrong PIN';
    lockHint.classList.add('bad');
    lockBusy = false;
  }
}

function pinPress(key) {
  if (lockBusy) return;
  if (key === 'back') {
    pinEntry = pinEntry.slice(0, -1);
  } else if (key === 'enter') {
    return submitPin();
  } else if (/^\d$/.test(key) && pinEntry.length < 8) {
    pinEntry += key;
  }
  drawDots();
}

document.getElementById('keypad').addEventListener('click', (e) => {
  const key = e.target.closest('[data-key]');
  if (key) pinPress(key.dataset.key);
});

document.addEventListener('keydown', (e) => {
  if (lockEl.hidden) return;
  if (/^\d$/.test(e.key)) pinPress(e.key);
  else if (e.key === 'Backspace') pinPress('back');
  else if (e.key === 'Enter') pinPress('enter');
});

async function bootLock() {
  let status = { enabled: false, locked: false };
  try { status = await fetch('/api/lock/status').then((r) => r.json()); } catch {}
  if (status.locked) {
    showLock(status);
    return false;
  }
  hideLock();
  return true;
}

// After unlocking, build the card-sized thumbnails the first screens will ask
// for. Without this the first scroll competes with hundreds of resample jobs.
const warmEl = document.getElementById('warm');

async function warmUp() {
  let plan = { total: 0, pending: 0 };
  try { plan = await api.get('/api/warm/plan?width=640&limit=400'); } catch { return; }
  if (plan.pending < 12) return;          // not enough to be worth a screen

  const hint = document.getElementById('warm-hint');
  const fill = document.getElementById('warm-fill');
  warmEl.hidden = false;
  hint.textContent = `Preparing ${plan.pending} thumbnails…`;

  let done = false;
  document.getElementById('warm-skip').onclick = () => { done = true; warmEl.hidden = true; };

  // The build runs server-side in one call; poll separately for progress.
  const poll = setInterval(async () => {
    if (done) return clearInterval(poll);
    try {
      const now = await api.get('/api/warm/plan?width=640&limit=400');
      const built = plan.pending - now.pending;
      const pct = Math.min(99, Math.round((built / Math.max(plan.pending, 1)) * 100));
      fill.style.width = `${pct}%`;
      hint.textContent = `Preparing thumbnails — ${built} of ${plan.pending}`;
    } catch { }
  }, 700);

  try { await api.post('/api/warm?width=640&limit=400'); } catch { }
  clearInterval(poll);
  if (!done) {
    fill.style.width = '100%';
    hint.textContent = 'Ready';
    setTimeout(() => { warmEl.hidden = true; }, 320);
  }
}

/* --------------------------------------------------------- scroll memory */

// Every render replaces the whole view, so the browser has nothing to restore.
// Positions are kept per address and reapplied once the new page has laid out.
const scrollMemory = new Map();
let currentKey = location.hash || '#/home';

function rememberScroll() {
  scrollMemory.set(currentKey, window.scrollY || 0);
}

// Catch it on the way out, whichever way the person leaves.
window.addEventListener('scroll', () => {
  scrollMemory.set(currentKey, window.scrollY || 0);
}, { passive: true });

function restoreScroll(key, wanted) {
  if (!wanted) return window.scrollTo(0, 0);

  // Images and rails settle over a few frames, so the page keeps growing after
  // the first paint. Keep nudging until the target is reachable or time is up.
  let tries = 0;
  const settle = () => {
    if (currentKey !== key) return;         // they navigated again; leave it
    const max = document.body.scrollHeight - window.innerHeight;
    window.scrollTo(0, Math.min(wanted, Math.max(0, max)));
    if (++tries < 24 && Math.abs((window.scrollY || 0) - wanted) > 4) {
      requestAnimationFrame(settle);
    }
  };
  requestAnimationFrame(settle);
}

/* ------------------------------------------------------------------- back */

// Depth of our own navigation, so the arrow only shows when there is somewhere
// to go. history.length can't be trusted here -- it counts the whole session.
const backBtn = document.getElementById('btn-back');
let backDepth = 0;
let goingBack = false;

function updateBack() {
  backBtn.hidden = backDepth <= 0;
}

backBtn.addEventListener('click', () => {
  if (backDepth <= 0) return;
  goingBack = true;
  history.back();
});

window.addEventListener('hashchange', () => {
  if (goingBack) {
    backDepth = Math.max(0, backDepth - 1);
    goingBack = false;
  } else {
    backDepth += 1;
  }
  updateBack();
});

/* ------------------------------------------------------- topbar actions */

const scanBtn = document.getElementById('btn-quick-scan');
let quickPoll = null;

async function quickScan() {
  if (scanBtn.classList.contains('busy')) return;
  try {
    await api.post('/api/scan?prune=1&folder_as_studio=1&two_part_actor=1');
  } catch (e) {
    return toast(e.message);
  }
  scanBtn.classList.add('busy');
  toast('Scanning your folders…');

  clearInterval(quickPoll);
  quickPoll = setInterval(async () => {
    let s;
    try { s = await api.get('/api/scan/status'); } catch { return; }
    const building = s.assets_total > s.assets_done;
    if (s.running || building) return;
    clearInterval(quickPoll);
    scanBtn.classList.remove('busy');
    toast(s.added ? `Added ${s.added} ${s.added === 1 ? 'video' : 'videos'}` : 'No new videos found');
    if (s.added) route();          // refresh the current page with the new items
  }, 1500);
}

scanBtn.addEventListener('click', quickScan);

document.getElementById('btn-quit').addEventListener('click', async () => {
  document.body.innerHTML =
    '<div style="display:grid;place-items:center;height:100vh;color:#8D8B99;' +
    'font:500 14px system-ui">Closing…</div>';
  try { await fetch('/api/quit', { method: 'POST', keepalive: true }); } catch {}
  window.close();   // blocked unless the window was opened by script

  // Windows closes the app window server-side. If it is still here a moment
  // later, nothing else can close it, so say so plainly.
  setTimeout(() => {
    document.body.innerHTML =
      '<div style="display:grid;place-items:center;height:100vh;color:#8D8B99;' +
      'font:500 14px system-ui">Adult Zone closed. You can close this window.</div>';
  }, 2500);
});

/* ------------------------------------------------------------------ router */

// The server locked us out mid-session -- put the screen back and stop the
// in-flight call quietly rather than surfacing an error.
function relock() {
  fetch('/api/lock/status').then((r) => r.json())
    .then((st) => showLock(st, true)).catch(() => {});
  throw new Error('__locked__');
}

function route() {
  const raw = location.hash.slice(1) || '/home';
  const [path, qs] = raw.split('?');
  const params = new URLSearchParams(qs || '');
  const parts = path.split('/').filter(Boolean);

  const previousKey = currentKey;
  const nextKey = location.hash || '#/home';
  // A page we have seen before is returned to where it was left. A fresh one
  // has no entry and starts at the top. This covers the back button, Alt+Left
  // and the mouse's back thumb button alike, without having to tell them apart.
  const wanted = scrollMemory.get(nextKey) || 0;
  if (previousKey !== nextKey) scrollMemory.set(previousKey, window.scrollY || 0);
  currentKey = nextKey;
  window.scrollTo(0, 0);
  closeStage();
  if (parts[0] !== 'videos') {
    searchInput.value = params.get('search') || '';
    searchOrigin = null;
  }
  clearInterval(scanPoll);

  const render = () => {
    switch (parts[0]) {
      case 'home':     return renderHome();
      case 'videos':   return renderVideos(params);
      case 'video':    return renderVideo(parts[1], params);
      case 'stars':    return renderStars(params);
      case 'actor':    return renderActor(parts[1], params);
      case 'studios':  return renderStudios();
      case 'studio':   return renderStudio(parts[1], params);
      case 'settings': return renderSettings();
      default:         return renderHome();
    }
  };

  Promise.resolve(render()).then(() => restoreScroll(nextKey, wanted)).catch((e) => {
    if (e.message === '__locked__') return;
    view.innerHTML = `<div class="page"><div class="empty">
      <h3>That did not load</h3><p>${esc(e.message)}</p>
      <a class="btn btn-ghost" href="#/home">Back to home</a></div></div>`;
  });
}

window.addEventListener('hashchange', route);

// Check the lock before rendering anything, so content never appears first.
bootLock().then((open) => {
  if (!open) return;
  route();
  warmUp();
});
