// Drives the REAL dashboard.html render functions in a DOM and checks that
// attacker-influenced strings never become markup.
import { JSDOM } from 'jsdom';
import { adapt, headers, hidden, htmlPath, read, reporter } from './support.mjs';

// Repo-relative by default so the suite runs from anywhere; pass a path to test a
// different copy (e.g. `node xss.test.mjs old-dashboard.html` against an old revision,
// which is how these checks were shown to be non-vacuous).
const HTML = htmlPath(process.argv);
const PAYLOAD = '<img src=x onerror=alert(1)>';
const BREAKOUT = 'Evil" onmouseover="alert(2)';

const dom = new JSDOM(read(HTML), { runScripts: 'dangerously' });
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

// Pre-Phase-7 the render functions were only defined by the viewshow handler; since the
// rewrite they exist as soon as the script runs and viewshow merely refreshes. Dispatching
// it is what a real page does either way.
doc.getElementById('GraveyardAnalyticsPage').dispatchEvent(new window.Event('viewshow'));

const ui = adapt(window);
const { check, skip, finish } = reporter();

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

ui.setTab('morgue');
ui.renderMediaTable([item]);
let tbody = ui.mediaBody();
check('morgue: no <img> injected', tbody.querySelectorAll('img').length === 0,
  `imgs=${tbody.querySelectorAll('img').length}`);
check('morgue: title is literal text', tbody.querySelector('a').textContent === PAYLOAD,
  JSON.stringify(tbody.querySelector('a').textContent));

// Attribute breakout via the old onclick="..." construction
ui.renderMediaTable([{ ...item, Name: BREAKOUT }]);
tbody = ui.mediaBody();
const btn = tbody.querySelector('button');
check('morgue: action button has no inline handler',
  !btn.hasAttribute('onclick') && !btn.hasAttribute('onmouseover'),
  `attrs=${[...btn.attributes].map(a => a.name).join(',')}`);
check('morgue: breakout name survives intact in dataset', btn.dataset.name === BREAKOUT,
  JSON.stringify(btn.dataset.name));

// Chapel tab renders the wider column set + two buttons
ui.setTab('chapel');
ui.renderMediaTable([{ ...item, Name: PAYLOAD, PlayCount: 3, UniqueViewers: 1 }]);
tbody = ui.mediaBody();
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
const visitorCells = () => ui.visitorBody().querySelectorAll('td');

if (!ui.supports.unifiedVisitors) {
  skip('visitor sections (2-4)', 'this revision predates Phase 2 item 7 and has two visitor tables');
} else {
ui.renderVisitorTable({
  Leaderboard: [], Ghosts: [], Truncated: false, RowLimit: 5000,
  Sessions: [{
    Time: 'Jul 01, 2026 - 10:00 AM', Visitor: PAYLOAD, Subject: PAYLOAD, Type: 'Episode',
    Device: PAYLOAD, Player: PAYLOAD, Method: 'TRANSCODE', Duration: '01:00:00',
    IsTranscode: true, ProgressPercent: 50, Watched: false,
  }],
});
check('tracearr: no <img> injected', ui.visitorBody().querySelectorAll('img').length === 0,
  `imgs=${ui.visitorBody().querySelectorAll('img').length}`);
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
ui.renderVisitorTable({
  Leaderboard: [{ Name: PAYLOAD, TotalTime: '5h' }],
  Ghosts: [PAYLOAD],
  Truncated: false, RowLimit: 5000,
  Sessions: [{
    Time: '2026-07-01 10:00', Visitor: PAYLOAD, Subject: PAYLOAD, Type: 'Movie',
    Device: PAYLOAD, Player: '', Method: 'DirectPlay', Duration: '01:00:00',
    IsTranscode: false, ProgressPercent: null, Watched: null,
  }],
});
check('local: no <img> injected in rows', ui.visitorBody().querySelectorAll('img').length === 0,
  `imgs=${ui.visitorBody().querySelectorAll('img').length}`);
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
ui.renderVisitorTable({ Sessions: [], Leaderboard: [], Ghosts: [] });
check('visitors: empty state renders one row',
  ui.visitorBody().querySelectorAll('tr').length === 1
    && /No sessions recorded/.test(ui.visitorBody().textContent), '');
check('visitors: empty leaderboard says so, rather than staying blank',
  /No active visitors/.test(doc.getElementById('leaderboardList').textContent), '');

ui.renderVisitorTable({
  Sessions: [{ Time: 't', Visitor: 'v', Subject: 's', Type: 'Movie', Device: 'd', Player: '', Method: 'm', Duration: '00:00:01', IsTranscode: false }],
  Leaderboard: [], Ghosts: [], Truncated: true, RowLimit: 5000,
});
check('visitors: truncation notice spans the full row and names the cap',
  /Showing the most recent 5000 sessions/.test(ui.visitorBody().textContent)
    && ui.visitorBody().querySelector('td').colSpan === 7,
  JSON.stringify(ui.visitorBody().querySelector('td').textContent));

// The notice's colSpan is the whole reason the column count is a descriptor rather than a
// constant, so pin it to the header the same render produced.
check('visitors: that colSpan matches the header the same render built',
  ui.visitorBody().querySelector('td').colSpan === headers(ui.visitorBody()).length,
  `colSpan=${ui.visitorBody().querySelector('td').colSpan} headers=${headers(ui.visitorBody()).length}`);
}

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
ui.setTab('morgue');
if (!ui.supports.coverageBanner) {
  skip('coverage banner (6)', 'this revision predates D1, so there is no banner to render');
} else {
for (const c of bannerCases) {
  ui.renderCoverageBanner(c.data);
  const banner = doc.getElementById('coverageBanner');
  check('banner: ' + c.name,
    !hidden(banner) && c.expect.test(banner.textContent),
    JSON.stringify(banner.textContent));
}
ui.setTab('chapel');
ui.renderCoverageBanner({ CoverageDays: 20, GraceDays: 180 });
check('banner: hidden outside the Morgue', hidden(doc.getElementById('coverageBanner')), '');
}

// ---- 7. Per-row "unverified" marker ----
const floor = { HistoryFloorUtc: '2026-01-01T00:00:00Z' };
ui.setTab('morgue');
ui.renderMediaTable([{ ...item, DateAdded: '2024-05-01T00:00:00Z' }], floor);
let mBody = ui.mediaBody();
check('row: item predating history is marked unverified',
  /unverified — predates history/.test(mBody.textContent), JSON.stringify(mBody.textContent.slice(0, 90)));
check('row: marker is text, not markup (title still safe)',
  mBody.querySelectorAll('img').length === 0 && mBody.querySelector('a').textContent === PAYLOAD, '');

ui.renderMediaTable([{ ...item, DateAdded: '2026-06-01T00:00:00Z' }], floor);
mBody = ui.mediaBody();
check('row: item inside history is NOT marked',
  !/unverified/.test(mBody.textContent), JSON.stringify(mBody.textContent.slice(0, 90)));

ui.renderMediaTable([{ ...item, DateAdded: '2024-05-01T00:00:00Z' }]);
check('row: no context -> no marker, no crash',
  !/unverified/.test(ui.mediaBody().textContent), '');

finish();
