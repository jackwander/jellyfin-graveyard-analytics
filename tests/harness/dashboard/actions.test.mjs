// Verifies the dataset+addEventListener wiring actually reaches the right endpoint.
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
const NAME = 'Evil" onmouseover="alert(2)';
const ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

const dom = new JSDOM(readFileSync(HTML, 'utf8'), { runScripts: 'dangerously' });
const { window } = dom;
const doc = window.document;

const calls = [];
const confirms = [];
window.alert = () => calls.push('ALERT');
window.confirm = (msg) => { confirms.push(msg); return true; };
window.ApiClient = {
  getUrl: (u) => '/' + u,
  getJSON: () => Promise.resolve({ Items: [], TotalWastedSize: '0 B' }),
  getPluginConfiguration: () => Promise.resolve({}),
  updatePluginConfiguration: () => Promise.resolve({}),
  ajax: (opts) => { calls.push(opts.type + ' ' + opts.url); return Promise.resolve({}); },
};

doc.getElementById('GraveyardAnalyticsPage').dispatchEvent(new window.Event('viewshow'));

const item = {
  MediaId: ID, Name: NAME, Type: 'Movie', FormattedSize: '40 GB',
  PlayCount: 0, UniqueViewers: 0, FormattedDuration: '00:00:00',
  DateAdded: '2024-01-01T00:00:00Z', LastPlayed: null,
};

const results = [];
const check = (name, ok, detail) => results.push({ name, ok, detail });

// Morgue -> Condemn
window.currentTab = 'morgue';
window.renderMediaTable([item]);
doc.getElementById('morgueTableBody').querySelector('button').dispatchEvent(
  new window.MouseEvent('click', { bubbles: true }));
check('Condemn click hits the Condemn endpoint with the id',
  calls.includes('POST /GraveyardAnalytics/Condemn/' + ID), JSON.stringify(calls));
check('confirm() shows the raw name, unescaped and unmangled',
  confirms.length === 1 && confirms[0].includes(NAME), JSON.stringify(confirms));

// Chapel -> Pardon + Exorcise
window.currentTab = 'chapel';
window.renderMediaTable([item]);
const buttons = doc.getElementById('leastWatchedTableBody').querySelectorAll('button');
check('chapel row order is Pardon then Exorcise',
  buttons[0].textContent === 'Pardon' && buttons[1].textContent === 'Exorcise',
  [...buttons].map(b => b.textContent).join('|'));
buttons[0].dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
buttons[1].dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
check('Pardon click hits Pardon endpoint',
  calls.includes('POST /GraveyardAnalytics/Pardon/' + ID), JSON.stringify(calls));
check('Exorcise click hits LastRites endpoint',
  calls.includes('POST /GraveyardAnalytics/LastRites/' + ID), JSON.stringify(calls));
check('no alert() fired from any click', !calls.includes('ALERT'), JSON.stringify(calls));

// Numeric zero must still render as "0", not blank
window.currentTab = 'chapel';
window.renderMediaTable([{ ...item, PlayCount: 0, UniqueViewers: 0 }]);
const cells = doc.getElementById('leastWatchedTableBody').querySelectorAll('td');
check('PlayCount 0 renders as "0"', cells[3].textContent === '0', JSON.stringify(cells[3].textContent));
check('Reach 0 renders as "0 Souls"', cells[5].textContent.trim() === '0 Souls', JSON.stringify(cells[5].textContent));
check('morgue row has 6 cells / chapel row has 9', (() => {
  window.currentTab = 'morgue';
  window.renderMediaTable([item]);
  return doc.getElementById('morgueTableBody').querySelectorAll('td').length === 6 && cells.length === 9;
})(), `chapel=${cells.length}`);

let failed = 0;
for (const r of results) {
  if (!r.ok) failed++;
  console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '   <-- ' + r.detail}`);
}
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
