// Verifies the dataset+addEventListener wiring actually reaches the right endpoint, that the
// top card reads its two figures separately, and — since Phase 7 — that the page registers its
// listeners once, shows a loading state, and reports a failed fetch instead of leaving stale
// rows on screen.
import { JSDOM } from 'jsdom';
import { adapt, hidden, htmlPath, read, reporter, sleep, text } from './support.mjs';

const HTML = htmlPath(process.argv);
const NAME = 'Evil" onmouseover="alert(2)';
const ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

const dom = new JSDOM(read(HTML), { runScripts: 'dangerously' });
const { window } = dom;
const doc = window.document;

const calls = [];
const confirms = [];
let fetches = 0;
let respond = () => Promise.resolve({ Items: [], TotalSize: '0 B' });

window.alert = () => calls.push('ALERT');
window.confirm = (msg) => { confirms.push(msg); return true; };
window.ApiClient = {
  getUrl: (u) => '/' + u,
  getJSON: () => { fetches++; return respond(); },
  getPluginConfiguration: () => Promise.resolve({}),
  updatePluginConfiguration: () => Promise.resolve({}),
  ajax: (opts) => { calls.push(opts.type + ' ' + opts.url); return Promise.resolve({}); },
};

doc.getElementById('GraveyardAnalyticsPage').dispatchEvent(new window.Event('viewshow'));

const ui = adapt(window);
const { check, skip, finish } = reporter();

const item = {
  MediaId: ID, Name: NAME, Type: 'Movie', FormattedSize: '40 GB',
  PlayCount: 0, UniqueViewers: 0, FormattedDuration: '00:00:00',
  DateAdded: '2024-01-01T00:00:00Z', LastPlayed: null,
};

// Morgue -> Condemn
ui.setTab('morgue');
ui.renderMediaTable([item]);
ui.mediaBody().querySelector('button').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
check('Condemn click hits the Condemn endpoint with the id',
  calls.includes('POST /GraveyardAnalytics/Condemn/' + ID), JSON.stringify(calls));
check('confirm() shows the raw name, unescaped and unmangled',
  confirms.length === 1 && confirms[0].includes(NAME), JSON.stringify(confirms));

// Chapel -> Pardon + Exorcise
ui.setTab('chapel');
ui.renderMediaTable([item]);
const buttons = ui.mediaBody().querySelectorAll('button');
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
ui.setTab('chapel');
ui.renderMediaTable([{ ...item, PlayCount: 0, UniqueViewers: 0 }]);
const cells = ui.mediaBody().querySelectorAll('td');
check('PlayCount 0 renders as "0"', cells[3].textContent === '0', JSON.stringify(cells[3].textContent));
check('Reach 0 renders as "0 Souls"', cells[5].textContent.trim() === '0 Souls', JSON.stringify(cells[5].textContent));
check('morgue row has 6 cells / chapel row has 9', (() => {
  ui.setTab('morgue');
  ui.renderMediaTable([item]);
  return ui.mediaBody().querySelectorAll('td').length === 6 && cells.length === 9;
})(), `chapel=${cells.length}`);

// The top card, after TotalWastedSize was split into TotalSize + TotalWasted (item 20).
// The old field carried "dead weight" and "total size of living media" in one string and
// the label was rewritten per tab to cover for it; these check the two figures are read
// separately and that the second one only appears when it says something new.
const title = doc.getElementById('totalWastedTitle');
const value = doc.getElementById('totalWasteValue');
const subline = doc.getElementById('totalWastedSubline');

ui.setTab('morgue');
ui.renderTotals({ TotalSize: '400 GB', TotalWasted: '400 GB', TotalCoversAllMatches: false });
check('morgue: label says the total covers the listed rows only, value is TotalSize',
  text(title) === 'Total Dead Weight (listed rows)' && text(value) === '400 GB',
  `${text(title)} / ${text(value)}`);
check('morgue: equal figures leave the sub-line hidden',
  hidden(subline) && text(subline) === '',
  `hidden=${hidden(subline)} / ${text(subline)}`);

// Barely-touched widens the play test, so some listed rows have plays and the two figures
// separate — the only state in which the Morgue reports less than it lists.
ui.renderTotals({ TotalSize: '400 GB', TotalWasted: '250 GB', TotalCoversAllMatches: false });
check('morgue: a smaller reclaimable figure is shown as its own line',
  !hidden(subline) && text(subline) === 'Never played: 250 GB',
  `hidden=${hidden(subline)} / ${text(subline)}`);

ui.setTab('living');
ui.renderTotals({ TotalSize: '2 TB', TotalWasted: null, TotalCoversAllMatches: true });
check('living: label names living media, uncapped, no reclaimable line at all',
  text(title) === 'Total Size of Living Media' && text(value) === '2 TB' && hidden(subline),
  `${text(title)} / ${text(value)} / hidden=${hidden(subline)}`);

ui.setTab('chapel');
ui.renderTotals({ TotalSize: '80 GB', TotalWasted: '30 GB', TotalCoversAllMatches: true });
check('chapel: label names The Chapel with no cap qualifier, watched part excluded',
  text(title) === 'Total Size in The Chapel'
  && text(value) === '80 GB'
  && text(subline) === 'Never played: 30 GB',
  `${text(title)} / ${text(value)} / ${text(subline)}`);

// Null means "this view has nothing it can claim" — no history, or nothing reclaimable. It
// must not read as a claim of zero, which is what a "Never played: 0 B" line would be.
ui.renderTotals({ TotalSize: '80 GB', TotalWasted: null, TotalCoversAllMatches: true });
check('chapel: a null reclaimable figure prints no claim at all',
  hidden(subline) && text(subline) === '',
  `hidden=${hidden(subline)} / ${text(subline)}`);

// A response with neither field must not print "undefined" in the card.
ui.renderTotals({});
check('missing totals render as 0 B rather than undefined',
  text(value) === '0 B' && hidden(subline), text(value));

// The sub-line makes a claim about one tab's rows, so leaving a tab must clear it — a failed
// fetch on the next tab would otherwise leave the previous tab's claim under a new card.
ui.setTab('chapel');
ui.renderTotals({ TotalSize: '80 GB', TotalWasted: '30 GB', TotalCoversAllMatches: true });
const claimBeforeSwitch = text(subline);
ui.switchTab('settings');
check('switching tabs clears a stale "Never played" claim',
  claimBeforeSwitch === 'Never played: 30 GB' && text(subline) === '' && hidden(subline),
  `${claimBeforeSwitch} -> ${text(subline)}`);

// ---- Phase 7: one registration, one fetch (finding 7) ------------------------------------
// Jellyfin fires viewshow on every return to the page. While the listeners were registered
// inside that handler, the Nth visit held N copies of each and one keystroke issued N fetches.
// Back onto a tab that actually fetches — the check above left the page on Settings, which
// deliberately issues no request at all.
await ui.switchTab('morgue');

const view = doc.getElementById('GraveyardAnalyticsPage');
view.dispatchEvent(new window.Event('viewshow'));
view.dispatchEvent(new window.Event('viewshow'));

// Each of the three action clicks above queued its own refresh 500ms out. Those have to land
// before the counter is meaningful, or they show up as duplicate listeners that are not there.
await sleep(700);
fetches = 0;
doc.getElementById('mediaSearch').dispatchEvent(new window.Event('input'));
await sleep(700);   // past the 500ms debounce
check('three viewshows later, one keystroke still issues exactly one fetch',
  fetches === 1, `fetches=${fetches}`);

fetches = 0;
doc.getElementById('limitFilter').dispatchEvent(new window.Event('change'));
await sleep(20);
check('and one filter change issues exactly one fetch', fetches === 1, `fetches=${fetches}`);

// ---- Phase 7: loading / error states ----------------------------------------------------
if (!ui.modern) {
  skip('loading and error states', 'this revision has no .catch on any fetch and no loading row');
} else {
  respond = () => new Promise(() => {});          // never settles
  ui.switchTab('morgue');
  await sleep(20);
  check('an in-flight fetch shows a loading row rather than the previous tab\'s rows',
    /Consulting the records/.test(ui.mediaBody().textContent),
    JSON.stringify(ui.mediaBody().textContent.slice(0, 60)));

  respond = () => Promise.reject({ status: 500 });
  await ui.switchTab('morgue');
  await sleep(20);
  check('a failed fetch says so in the table and names the status',
    /HTTP 500/.test(ui.mediaBody().textContent) && /server log/.test(ui.mediaBody().textContent),
    JSON.stringify(ui.mediaBody().textContent.slice(0, 90)));
  check('and it clears the total card, which was describing rows that are now gone',
    text(value) === '—' && hidden(subline), `${text(value)} / hidden=${hidden(subline)}`);

  // Since Phase 1 the server answers with { message }, written for this UI and carrying no
  // filesystem path — so the actionable one ("Playback Reporting is not installed") survives.
  const actionable = 'Playback Reporting is not installed.';
  respond = () => Promise.reject({ status: 400, json: () => Promise.resolve({ message: actionable }) });
  await ui.switchTab('morgue');
  await sleep(20);
  check('the server\'s own message is preferred over the generic one',
    ui.mediaBody().textContent.includes(actionable),
    JSON.stringify(ui.mediaBody().textContent.slice(0, 90)));

  // One table serves every tab now, so a response that outlives its tab would render the
  // wrong shape of row under the wrong header.
  let release;
  respond = () => new Promise((resolve) => { release = () => resolve({ Items: [{ ...item }], TotalSize: '9 TB' }); });
  ui.switchTab('living');
  await sleep(20);
  respond = () => Promise.resolve({ Sessions: [], Leaderboard: [], Ghosts: [] });
  await ui.switchTab('visitors');
  release();
  await sleep(20);
  check('a response that arrives after its tab was left is dropped',
    /No sessions recorded/.test(ui.visitorBody().textContent) && text(value) !== '9 TB',
    `${JSON.stringify(ui.visitorBody().textContent.slice(0, 40))} / card=${text(value)}`);
}

finish();
