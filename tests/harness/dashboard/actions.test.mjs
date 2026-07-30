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
  getJSON: () => Promise.resolve({ Items: [], TotalSize: '0 B' }),
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

// The top card, after TotalWastedSize was split into TotalSize + TotalWasted (item 20).
// The old field carried "dead weight" and "total size of living media" in one string and
// the label was rewritten per tab to cover for it; these check the two figures are read
// separately and that the second one only appears when it says something new.
const title = doc.getElementById('totalWastedTitle');
const value = doc.getElementById('totalWasteValue');
const subline = doc.getElementById('totalWastedSubline');

window.currentTab = 'morgue';
window.renderTotals({ TotalSize: '400 GB', TotalWasted: '400 GB', TotalCoversAllMatches: false });
check('morgue: label says the total covers the listed rows only, value is TotalSize',
  title.innerText === 'Total Dead Weight (listed rows)' && value.innerText === '400 GB',
  `${title.innerText} / ${value.innerText}`);
check('morgue: equal figures leave the sub-line hidden',
  subline.style.display === 'none' && subline.innerText === '',
  `${subline.style.display} / ${subline.innerText}`);

// Barely-touched widens the play test, so some listed rows have plays and the two figures
// separate — the only state in which the Morgue reports less than it lists.
window.renderTotals({ TotalSize: '400 GB', TotalWasted: '250 GB', TotalCoversAllMatches: false });
check('morgue: a smaller reclaimable figure is shown as its own line',
  subline.style.display === 'block' && subline.innerText === 'Never played: 250 GB',
  `${subline.style.display} / ${subline.innerText}`);

window.currentTab = 'living';
window.renderTotals({ TotalSize: '2 TB', TotalWasted: null, TotalCoversAllMatches: true });
check('living: label names living media, uncapped, no reclaimable line at all',
  title.innerText === 'Total Size of Living Media'
  && value.innerText === '2 TB'
  && subline.style.display === 'none',
  `${title.innerText} / ${value.innerText} / ${subline.style.display}`);

window.currentTab = 'chapel';
window.renderTotals({ TotalSize: '80 GB', TotalWasted: '30 GB', TotalCoversAllMatches: true });
check('chapel: label names The Chapel with no cap qualifier, watched part excluded',
  title.innerText === 'Total Size in The Chapel'
  && value.innerText === '80 GB'
  && subline.innerText === 'Never played: 30 GB',
  `${title.innerText} / ${value.innerText} / ${subline.innerText}`);

// Null means "this view has nothing it can claim" — no history, or nothing reclaimable. It
// must not read as a claim of zero, which is what a "Never played: 0 B" line would be.
window.renderTotals({ TotalSize: '80 GB', TotalWasted: null, TotalCoversAllMatches: true });
check('chapel: a null reclaimable figure prints no claim at all',
  subline.style.display === 'none' && subline.innerText === '',
  `${subline.style.display} / ${subline.innerText}`);

// A response with neither field must not print "undefined" in the card.
window.renderTotals({});
check('missing totals render as 0 B rather than undefined',
  value.innerText === '0 B' && subline.style.display === 'none', value.innerText);

// The sub-line makes a claim about one tab's rows, so leaving a tab must clear it — a failed
// fetch on the next tab would otherwise leave the previous tab's claim under a new card.
window.currentTab = 'chapel';
window.renderTotals({ TotalSize: '80 GB', TotalWasted: '30 GB', TotalCoversAllMatches: true });
const claimBeforeSwitch = subline.innerText;
window.switchTab('settings');
check('switching tabs clears a stale "Never played" claim',
  claimBeforeSwitch === 'Never played: 30 GB'
  && subline.innerText === '' && subline.style.display === 'none',
  `${claimBeforeSwitch} -> ${subline.innerText}`);

let failed = 0;
for (const r of results) {
  if (!r.ok) failed++;
  console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '   <-- ' + r.detail}`);
}
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
