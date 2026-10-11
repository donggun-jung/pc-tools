// Scenario tests for pc-tools/site/app.js with a minimal DOM stub (no jsdom needed).
// usage (repo root): node tests/site-app.test.js            — exits 1 on any FAIL
const fs = require('fs');
const vm = require('vm');
const path = require('path');
const APP = process.argv[2] || path.join(__dirname, '..', 'site', 'app.js');
const src = fs.readFileSync(APP, 'utf8');

function makeNode(tag) {
  return {
    tag, className: '', children: [], attrs: {}, _text: '', href: '', open: false,
    set textContent(v) { this._text = String(v); this.children = []; },
    get textContent() { return this._text + this.children.map(c => c.textContent).join(''); },
    appendChild(c) { if (c.isFrag) { this.children.push(...c.children); } else { this.children.push(c); } return c; },
    setAttribute(k, v) { this.attrs[k] = v; },
    all() { return [this].concat(...this.children.map(c => c.all ? c.all() : [])); },
  };
}

function run(name, responses, check) {
  const boxes = { windows: makeNode('div'), mac: makeNode('div') };
  const counts = { windows: makeNode('span'), mac: makeNode('span') };
  const document = {
    createElement: makeNode,
    createDocumentFragment() { const f = makeNode('#frag'); f.isFrag = true; return f; },
    createTextNode(t) { const n = makeNode('#text'); n._text = t; return n; },
    querySelector(sel) {
      let m = sel.match(/data-platform="(\w+)"/); if (m) return boxes[m[1]];
      m = sel.match(/data-count="(\w+)"/); if (m) return counts[m[1]];
      return null;
    },
  };
  const fetch = (url) => {
    const r = responses[url.indexOf('raw.githubusercontent') >= 0 ? 'remote' : 'local'];
    if (r === 'fail') return Promise.reject(new Error('net'));
    return Promise.resolve({ ok: true, json: () => (typeof r === 'string' ? Promise.reject(new SyntaxError('bad json')) : Promise.resolve(r)) });
  };
  const ctx = { document, fetch, URL, setTimeout, clearTimeout, AbortController, console, Promise, JSON };
  vm.createContext(ctx);
  vm.runInContext(src, ctx);
  return new Promise(res => setTimeout(res, 50)).then(() => {
    const ok = check(boxes, counts);
    console.log((ok ? 'PASS ' : 'FAIL ') + name);
    if (!ok) process.exitCode = 1;
  });
}

const good = JSON.parse(fs.readFileSync(APP.replace(/app\.js$/, 'catalog.json'), 'utf8'));
const tool = good.tools[0];
const links = box => box.all().filter(n => n.tag === 'a').map(n => n.href);
const hasCard = box => box.all().some(n => n.className === 'card');

(async () => {
  await run('local ok, remote {} keeps the list', { local: good, remote: {} },
    b => hasCard(b.windows) && links(b.windows).includes(tool.download));
  await run('local ok, remote invalid json keeps the list', { local: good, remote: 'x' },
    b => hasCard(b.windows));
  await run('local fail, remote ok renders remote', { local: 'fail', remote: good },
    b => hasCard(b.windows));
  await run('both fail shows the error with a Releases link', { local: 'fail', remote: 'fail' },
    b => !hasCard(b.windows) && /불러오지 못했습니다/.test(b.windows.textContent) && links(b.windows)[0] === 'https://github.com/donggun-jung/pc-tools/releases');
  const pct = JSON.parse(JSON.stringify(good)); pct.tools[0].download = 'https://github.com/donggun-jung/pc-tools/releases/download/x/bad%';
  await run('download ending with % does not crash', { local: good, remote: pct },
    b => hasCard(b.windows) && b.windows.all().some(n => n.className === 'btnm' && n.textContent === 'bad%'));
  const trav = JSON.parse(JSON.stringify(good));
  trav.tools[0].download = 'https://github.com/donggun-jung/pc-tools/../../evil/repo/releases/download/x.zip';
  trav.tools[0].source = 'https://github.com/donggun-jung/pc-tools/%2e%2e/%2E%2E/evil/repo';
  trav.tools[0].checksum = 'https://github.com.evil.example/donggun-jung/pc-tools/x';
  await run('path traversal and look-alike hosts are dropped', { local: trav, remote: 'fail' },
    b => hasCard(b.windows) && links(b.windows).length === 0);
  const plat = { tools: [{ name: 'X', platform: 'linux' }] };
  await run('unknown platform only → treated as unusable, local list kept', { local: good, remote: plat },
    b => hasCard(b.windows));
  await run('counts reflect platforms', { local: good, remote: 'fail' },
    (b, c) => c.windows.textContent === '1' && c.mac.textContent === '0' && /아직 없습니다/.test(b.mac.textContent));
})();
