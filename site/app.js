// tools.jdg.dev — renders the tool list from catalog.json.
// Source of truth: the repo's catalog.json on GitHub (fresh); /catalog.json on this site is the fallback.
// Kept as a separate file so the page can run under a strict CSP (script-src 'self').
(function () {
  'use strict';
  var REMOTE = 'https://raw.githubusercontent.com/donggun-jung/pc-tools/main/catalog.json';
  var LOCAL = '/catalog.json';
  var REPO_ORIGIN = 'https://github.com';
  var REPO_PATH = '/donggun-jung/pc-tools/';
  var PLATFORMS = ['windows', 'mac'];
  var shown = null;

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }
  function link(cls, text, url, label) {
    var a = el('a', cls, text);
    a.href = url;
    if (label) a.setAttribute('aria-label', label);
    return a;
  }
  // Only links into this repository, judged after URL normalization (so "../" or "%2e%2e" cannot walk out).
  function safeLink(url) {
    if (typeof url !== 'string') return null;
    var u;
    try { u = new URL(url); } catch (e) { return null; }
    if (u.origin !== REPO_ORIGIN || u.pathname.indexOf(REPO_PATH) !== 0) return null;
    return u.href;
  }
  function fileName(url) {
    var last = url.split('/').pop() || '';
    try { return decodeURIComponent(last); } catch (e) { return last; }
  }
  function str(v) { return typeof v === 'string' ? v : ''; }
  function list(v) { return Array.isArray(v) ? v.filter(function (x) { return typeof x === 'string'; }) : []; }
  function validTool(t) {
    return t && typeof t === 'object' && str(t.name) && PLATFORMS.indexOf(t.platform) !== -1;
  }

  function step(n, text) {
    var li = el('li', 'st');
    li.appendChild(el('span', 'sn', n));
    li.appendChild(el('span', null, text));
    return li;
  }

  function card(t, openHow) {
    var name = str(t.name);
    var a = el('article', 'card');
    var meta = el('div', 'meta');
    [t.version ? 'v' + str(t.version) : '', str(t.released), str(t.requirements)].filter(Boolean).forEach(function (m, i) {
      if (i) meta.appendChild(el('span', null, '·'));
      meta.appendChild(el('span', null, m));
    });
    a.appendChild(meta);
    a.appendChild(el('h3', 'name', name));
    if (t.summary) a.appendChild(el('p', 'sum', str(t.summary)));

    var acts = el('div', 'acts');
    var dl = safeLink(t.download);
    if (dl) {
      var file = fileName(dl);
      var b = link('btn', '다운로드 ', dl, name + ' 다운로드 (' + file + ')');
      b.appendChild(el('span', 'btnm', file));
      acts.appendChild(b);
    }
    var src = safeLink(t.source);
    if (src) acts.appendChild(link('lnk', '설명·소스', src, name + ' 설명과 소스'));
    a.appendChild(acts);

    var keys = str(t.hotkey).split('+').map(function (k) { return k.trim(); }).filter(Boolean);
    if (keys.length) {
      var kw = el('div', 'keys');
      keys.forEach(function (k) { kw.appendChild(el('kbd', 'kc', k)); });
      if (t.hotkey_action) kw.appendChild(el('span', 'kres', str(t.hotkey_action)));
      a.appendChild(kw);
    }
    var details = list(t.details);
    if (details.length) {
      var ul = el('ul', 'dl');
      details.forEach(function (d) {
        var li = el('li', 'di');
        li.appendChild(el('span', 'dm'));
        li.appendChild(el('span', null, d));
        ul.appendChild(li);
      });
      a.appendChild(ul);
    }

    var steps = list(t.steps), extra = list(t.optional_steps), sum = safeLink(t.checksum);
    if (steps.length || extra.length || sum) {
      var how = el('details', 'how');
      how.open = openHow;
      how.appendChild(el('summary', 'hows', '설치 방법'));
      if (steps.length || extra.length) {
        var ol = el('ol', 'steps');
        steps.forEach(function (s, i) { ol.appendChild(step(String(i + 1), s)); });
        extra.forEach(function (s) { ol.appendChild(step('+', s)); });
        how.appendChild(ol);
      }
      if (sum) how.appendChild(link('sum2', '파일 확인값 (SHA-256)', sum, name + ' 파일 확인값 SHA-256'));
      a.appendChild(how);
    }
    return a;
  }

  // Build everything first; touch the page only when the catalog is usable.
  // A broken or empty remote answer never replaces a list that is already shown.
  function render(cat) {
    if (!cat || !Array.isArray(cat.tools)) return false;
    var tools = cat.tools.filter(validTool);
    if (!tools.length) return false;
    var key = JSON.stringify(tools);
    if (key === shown) return true;
    var built = {};
    PLATFORMS.forEach(function (p) {
      var mine = tools.filter(function (t) { return t.platform === p; });
      var frag = document.createDocumentFragment();
      if (!mine.length) frag.appendChild(el('div', 'empty', '아직 없습니다.'));
      // Install steps start open while a platform has a single tool; with more they start folded.
      mine.forEach(function (t) { frag.appendChild(card(t, mine.length === 1)); });
      built[p] = { frag: frag, count: mine.length };
    });
    PLATFORMS.forEach(function (p) {
      var box = document.querySelector('[data-platform="' + p + '"]');
      box.textContent = '';
      box.appendChild(built[p].frag);
      document.querySelector('[data-count="' + p + '"]').textContent = String(built[p].count);
    });
    shown = key;
    return true;
  }

  function failed() {
    PLATFORMS.forEach(function (p) {
      var box = document.querySelector('[data-platform="' + p + '"]');
      box.textContent = '';
      var e = el('div', 'empty', '목록을 불러오지 못했습니다. ');
      e.appendChild(link(null, 'GitHub Releases', REPO_ORIGIN + REPO_PATH + 'releases'));
      e.appendChild(document.createTextNode('에서 바로 받을 수 있습니다.'));
      box.appendChild(e);
    });
  }

  function load(url, ms) {
    var ctl = typeof AbortController === 'function' ? new AbortController() : null;
    var timer = ctl ? setTimeout(function () { ctl.abort(); }, ms) : null;
    return fetch(url, { cache: 'no-store', signal: ctl ? ctl.signal : undefined })
      .then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
      .finally(function () { if (timer) clearTimeout(timer); });
  }
  function attempt(url, ms) {
    return load(url, ms).then(render).catch(function () { return false; });
  }

  attempt(LOCAL, 4000)
    .then(function () { return attempt(REMOTE, 6000); })
    .then(function () { if (shown === null) failed(); });
})();
