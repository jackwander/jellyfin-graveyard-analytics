// Drives the REAL dashboard.html render functions in a DOM and checks that
// attacker-influenced strings never become markup.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { JSDOM } from 'jsdom';

// Repo-relative by default so the suite runs from anywhere; pass a path to test a
// different copy (e.g. `node xss.test.mjs old-dashboard.html` against an old revision,
// which is how these checks were shown to be non-vacuous).
const HERE = dirname(fileURLToPath(import.meta.url));
const HTML = process.argv[2]
  ? resolve(process.cwd(), process.argv[2])
  : resolve(HERE, '../../../JellyfinGraveyardAnalytics/WebUI/dashboard.html');
const PAYLOAD = '<img src=x onerror=alert(1)>';
const BREAKOUT = 'Evil" onmouseover="alert(2)';

const dom = new JSDOM(readFileSync(HTML, 'utf8'), { runScripts: 'dangerously' });
const { window } = dom;
const doc = window.document;

let alerts = 0;
window.alert = () => { alerts++; };
window.confirm = () => false;
window.ApiClient = {
  getUrl: (u) => u,
  getJSON: () => Promise.resolve({ Items: [], TotalWastedSize: '0 B' }),
  getPluginConfiguration: () => Promise.resolve({}),
  updatePluginConfiguration: () => Promise.resolve({}),
  ajax: () => Promise.resolve({}),
};

// The render functions are defined by the viewshow handler.
doc.getElementById('GraveyardAnalyticsPage').dispatchEvent(new window.Event('viewshow'));

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok, detail });
}

// ---- 1. Media table (Morgue + Chapel): item.Name from a filename ----
const item = {
  MediaId: '11111111-1111-1111-1111-111111111111',
  Name: PAYLOAD,
  Type: 'Movie',
  FormattedSize: '40 GB',
  PlayCount: 0,
  UniqueViewers: 0,
  FormattedDuration: '00:00:00',
  DateAdded: '2024-01-01T00:00:00Z',
  LastPlayed: null,
};

window.currentTab = 'morgue';
window.renderMediaTable([item]);
let tbody = doc.getElementById('morgueTableBody');
check('morgue: no <img> injected', tbody.querySelectorAll('img').length === 0,
  `imgs=${tbody.querySelectorAll('img').length}`);
check('morgue: title is literal text', tbody.querySelector('a').textContent === PAYLOAD,
  JSON.stringify(tbody.querySelector('a').textContent));

// Attribute breakout via the old onclick="..." construction
window.renderMediaTable([{ ...item, Name: BREAKOUT }]);
tbody = doc.getElementById('morgueTableBody');
const btn = tbody.querySelector('button');
check('morgue: action button has no inline handler',
  !btn.hasAttribute('onclick') && !btn.hasAttribute('onmouseover'),
  `attrs=${[...btn.attributes].map(a => a.name).join(',')}`);
check('morgue: breakout name survives intact in dataset', btn.dataset.name === BREAKOUT,
  JSON.stringify(btn.dataset.name));

// Chapel tab renders the wider column set + two buttons
window.currentTab = 'chapel';
window.renderMediaTable([{ ...item, Name: PAYLOAD, PlayCount: 3, UniqueViewers: 1 }]);
tbody = doc.getElementById('leastWatchedTableBody');
check('chapel: no <img> injected', tbody.querySelectorAll('img').length === 0,
  `imgs=${tbody.querySelectorAll('img').length}`);
check('chapel: Pardon + Exorcise rendered', tbody.querySelectorAll('button').length === 2,
  `buttons=${tbody.querySelectorAll('button').length}`);
check('chapel: Reach cell keeps the "Souls" suffix',
  /^0 Souls$|^1 Souls$/.test(tbody.querySelectorAll('td')[5].textContent.trim()),
  JSON.stringify(tbody.querySelectorAll('td')[5].textContent));

// ---- 2. Tracearr visitor rows: username / mediaTitle / device ----
window.renderVisitorTable({
  data: [{
    user: { username: PAYLOAD },
    mediaTitle: PAYLOAD,
    showTitle: null,
    device: PAYLOAD,
    player: PAYLOAD,
    startedAt: '2026-07-01T10:00:00Z',
    durationMs: 3_600_000,
    isTranscode: true,
    videoDecision: 'transcode',
    progressMs: 500,
    totalDurationMs: 1000,
  }],
});
const tracearrBody = doc.getElementById('tracearrTableBody');
check('tracearr: no <img> injected', tracearrBody.querySelectorAll('img').length === 0,
  `imgs=${tracearrBody.querySelectorAll('img').length}`);
check('tracearr: username is literal text',
  tracearrBody.querySelectorAll('td')[0].textContent === PAYLOAD,
  JSON.stringify(tracearrBody.querySelectorAll('td')[0].textContent));
check('tracearr: fate cell still computes 50% -> Lingering',
  /Lingering/.test(tracearrBody.textContent) && /50% Complete/.test(tracearrBody.textContent),
  tracearrBody.querySelectorAll('td')[6].textContent);

// ---- 3. Local (fallback) visitor rows + leaderboard ----
window.renderVisitorTable({
  Leaderboard: [{ Name: PAYLOAD, TotalTime: '5h' }],
  Ghosts: [PAYLOAD],
  Sessions: [{
    Time: '2026-07-01 10:00', Visitor: PAYLOAD, Subject: PAYLOAD, Type: 'Movie',
    Device: PAYLOAD, Method: 'DirectPlay', Duration: '01:00:00', IsTranscode: false,
  }],
});
const fallbackBody = doc.getElementById('fallbackTableBody');
check('fallback: no <img> injected in rows', fallbackBody.querySelectorAll('img').length === 0,
  `imgs=${fallbackBody.querySelectorAll('img').length}`);
check('fallback: no <img> injected in leaderboard',
  doc.getElementById('leaderboardList').querySelectorAll('img').length === 0,
  `imgs=${doc.getElementById('leaderboardList').querySelectorAll('img').length}`);
check('fallback: leaderboard keeps "<strong>name</strong> - time" shape',
  doc.getElementById('leaderboardList').querySelector('strong').textContent === PAYLOAD
    && doc.getElementById('leaderboardList').textContent.includes(' - 5h'),
  JSON.stringify(doc.getElementById('leaderboardList').textContent));
check('fallback: ghosts list is literal text',
  doc.getElementById('ghostsList').textContent === PAYLOAD && doc.getElementById('ghostsList').querySelectorAll('*').length === 0,
  JSON.stringify(doc.getElementById('ghostsList').textContent));

// ---- 4. Empty states still render ----
window.renderVisitorTable({ data: [] });
check('tracearr: empty state renders one row',
  doc.getElementById('tracearrTableBody').querySelectorAll('tr').length === 1
    && /No Tracearr records/.test(doc.getElementById('tracearrTableBody').textContent), '');

// ---- 5. Whole-document sweep: did anything execute? ----
await new Promise(r => setTimeout(r, 200));
check('no alert() fired anywhere', alerts === 0, `alerts=${alerts}`);
check('document contains zero injected <img>', doc.querySelectorAll('img').length === 0,
  `imgs=${doc.querySelectorAll('img').length}`);

// ---- 6. Coverage banner + floor gate (D1 as decided: gate, not clamp) ----
const bannerCases = [
  { name: 'no history at all',
    data: { CoverageDays: 0, GraceDays: 180, UnverifiableCandidateCount: 0, IncludingUnverifiable: false, HistoryFloorUtc: null },
    expect: /No playback history is available yet/ },
  { name: 'withheld items are disclosed with the way to see them',
    data: { CoverageDays: 20, GraceDays: 180, UnverifiableCandidateCount: 7, IncludingUnverifiable: false, HistoryFloorUtc: '2026-07-10T00:00:00Z' },
    expect: /History covers 20 days.*at least 180 days.*7 further item\(s\).*withheld.*Include unverifiable/s },
  { name: 'opted in: shown rows are called unverified',
    data: { CoverageDays: 20, GraceDays: 180, UnverifiableCandidateCount: 7, IncludingUnverifiable: true, HistoryFloorUtc: '2026-07-10T00:00:00Z' },
    expect: /7 shown row\(s\) predate that history and are marked "unverified"/ },
  { name: 'full coverage, nothing withheld -> one line only',
    data: { CoverageDays: 400, GraceDays: 180, UnverifiableCandidateCount: 0, IncludingUnverifiable: false, HistoryFloorUtc: '2025-06-01T00:00:00Z' },
    expect: /^History covers 400 days\. Showing items with no plays that have been in the library at least 180 days\.$/ },
];
window.currentTab = 'morgue';
for (const c of bannerCases) {
  window.renderCoverageBanner(c.data);
  const banner = doc.getElementById('coverageBanner');
  check('banner: ' + c.name,
    banner.style.display === 'block' && c.expect.test(banner.textContent),
    JSON.stringify(banner.textContent));
}
window.currentTab = 'chapel';
window.renderCoverageBanner({ CoverageDays: 20, GraceDays: 180 });
check('banner: hidden outside the Morgue',
  doc.getElementById('coverageBanner').style.display === 'none', '');

// ---- 7. Per-row "unverified" marker ----
const floor = { HistoryFloorUtc: '2026-01-01T00:00:00Z' };
window.currentTab = 'morgue';
window.renderMediaTable([{ ...item, DateAdded: '2024-05-01T00:00:00Z' }], floor);
let mBody = doc.getElementById('morgueTableBody');
check('row: item predating history is marked unverified',
  /unverified — predates history/.test(mBody.textContent), JSON.stringify(mBody.textContent.slice(0, 90)));
check('row: marker is text, not markup (title still safe)',
  mBody.querySelectorAll('img').length === 0 && mBody.querySelector('a').textContent === PAYLOAD, '');

window.renderMediaTable([{ ...item, DateAdded: '2026-06-01T00:00:00Z' }], floor);
mBody = doc.getElementById('morgueTableBody');
check('row: item inside history is NOT marked',
  !/unverified/.test(mBody.textContent), JSON.stringify(mBody.textContent.slice(0, 90)));

window.renderMediaTable([{ ...item, DateAdded: '2024-05-01T00:00:00Z' }]);
check('row: no context -> no marker, no crash',
  !/unverified/.test(doc.getElementById('morgueTableBody').textContent), '');

let failed = 0;
for (const r of results) {
  if (!r.ok) failed++;
  console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '   <-- ' + r.detail}`);
}
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
