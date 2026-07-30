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
  getJSON: () => Promise.resolve({ Items: [], TotalSize: '0 B' }),
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

// ---- 2. Tracearr-sourced visitor rows ----
// Item 7 normalized the Tracearr payload server-side, so the renderer sees the same
// VisitorResponse either engine produced. What used to be a separate table is now the
// optional Fate cell, driven by ProgressPercent / Watched.
const visitorBody = () => doc.getElementById('visitorTableBody');
const visitorCells = () => visitorBody().querySelectorAll('td');

window.renderVisitorTable({
  Leaderboard: [], Ghosts: [], Truncated: false, RowLimit: 5000,
  Sessions: [{
    Time: 'Jul 01, 2026 - 10:00 AM', Visitor: PAYLOAD, Subject: PAYLOAD, Type: 'Episode',
    Device: PAYLOAD, Player: PAYLOAD, Method: 'TRANSCODE', Duration: '01:00:00',
    IsTranscode: true, ProgressPercent: 50, Watched: false,
  }],
});
check('tracearr: no <img> injected', visitorBody().querySelectorAll('img').length === 0,
  `imgs=${visitorBody().querySelectorAll('img').length}`);
check('tracearr: visitor is literal text',
  visitorCells()[1].textContent === PAYLOAD,
  JSON.stringify(visitorCells()[1].textContent));
check('tracearr: device cell keeps the player sub-line as text',
  visitorCells()[3].textContent === PAYLOAD + PAYLOAD
    && visitorCells()[3].querySelectorAll('img').length === 0,
  JSON.stringify(visitorCells()[3].textContent));
check('tracearr: fate cell still computes 50% -> Lingering',
  /Lingering/.test(visitorCells()[6].textContent) && /50% Complete/.test(visitorCells()[6].textContent),
  visitorCells()[6].textContent);

// ---- 3. Local visitor rows + leaderboard ----
// The local engine cannot report progress, so ProgressPercent is absent and the Fate
// cell must fall back to a dash rather than inventing a verdict.
window.renderVisitorTable({
  Leaderboard: [{ Name: PAYLOAD, TotalTime: '5h' }],
  Ghosts: [PAYLOAD],
  Truncated: false, RowLimit: 5000,
  Sessions: [{
    Time: '2026-07-01 10:00', Visitor: PAYLOAD, Subject: PAYLOAD, Type: 'Movie',
    Device: PAYLOAD, Player: '', Method: 'DirectPlay', Duration: '01:00:00',
    IsTranscode: false, ProgressPercent: null, Watched: null,
  }],
});
check('local: no <img> injected in rows', visitorBody().querySelectorAll('img').length === 0,
  `imgs=${visitorBody().querySelectorAll('img').length}`);
check('local: no <img> injected in leaderboard',
  doc.getElementById('leaderboardList').querySelectorAll('img').length === 0,
  `imgs=${doc.getElementById('leaderboardList').querySelectorAll('img').length}`);
check('local: leaderboard keeps "<strong>name</strong> - time" shape',
  doc.getElementById('leaderboardList').querySelector('strong').textContent === PAYLOAD
    && doc.getElementById('leaderboardList').textContent.includes(' - 5h'),
  JSON.stringify(doc.getElementById('leaderboardList').textContent));
check('local: ghosts list is literal text',
  doc.getElementById('ghostsList').textContent === PAYLOAD && doc.getElementById('ghostsList').querySelectorAll('*').length === 0,
  JSON.stringify(doc.getElementById('ghostsList').textContent));
check('local: fate cell is a dash, not a guessed verdict',
  visitorCells()[6].textContent.trim() === '—',
  JSON.stringify(visitorCells()[6].textContent));
check('local: absent Player leaves the device cell single-line',
  visitorCells()[3].textContent === PAYLOAD,
  JSON.stringify(visitorCells()[3].textContent));

// ---- 4. Empty state + truncation notice ----
window.renderVisitorTable({ Sessions: [], Leaderboard: [], Ghosts: [] });
check('visitors: empty state renders one row',
  visitorBody().querySelectorAll('tr').length === 1
    && /No sessions recorded/.test(visitorBody().textContent), '');
check('visitors: empty leaderboard says so, rather than staying blank',
  /No active visitors/.test(doc.getElementById('leaderboardList').textContent), '');

window.renderVisitorTable({
  Sessions: [{ Time: 't', Visitor: 'v', Subject: 's', Type: 'Movie', Device: 'd', Player: '', Method: 'm', Duration: '00:00:01', IsTranscode: false }],
  Leaderboard: [], Ghosts: [], Truncated: true, RowLimit: 5000,
});
check('visitors: truncation notice spans the full row and names the cap',
  /Showing the most recent 5000 sessions/.test(visitorBody().textContent)
    && visitorBody().querySelector('td').colSpan === 7,
  JSON.stringify(visitorBody().querySelector('td').textContent));

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
