// Drives the real page the way an admin does: clicks the tab bar and checks, per tab, which
// endpoint it asks for, how many columns the one table has, and which panels are left on
// screen. Nothing else asserts that mapping — before Phase 7 it was six inline
// `onclick="window.switchTab(...)"` attributes and a hand-written list of ten
// `style.display` assignments, and it is now one delegated listener over `data-tab` plus one
// block of `hidden` flags.
//
// This one is gated on the rewrite: the panel ids it names did not exist before it.
import { JSDOM } from 'jsdom';
import { adapt, htmlPath, read, reporter, sleep } from './support.mjs';

const HTML = htmlPath(process.argv);
const dom = new JSDOM(read(HTML), { runScripts: 'dangerously' });
const { window } = dom;
const doc = window.document;

let urls = [];
window.confirm = () => false;
window.ApiClient = {
  getUrl: (u) => '/' + u,
  getJSON: (u) => { urls.push(u); return Promise.resolve({ Items: [], TotalSize: '1 GB', Sessions: [], Leaderboard: [], Ghosts: [] }); },
  // Tracearr on, so the sixth tab is reachable at all.
  getPluginConfiguration: () => Promise.resolve({ EnableTracearr: true, TracearrUrl: 'http://tracearr:3000', TracearrApiKey: 'trr_pub_x' }),
  updatePluginConfiguration: () => Promise.resolve({}),
  ajax: () => Promise.resolve({}),
};

doc.getElementById('GraveyardAnalyticsPage').dispatchEvent(new window.Event('viewshow'));
await sleep(50);

const ui = adapt(window);
const { check, skip, finish } = reporter();

const id = (name) => doc.getElementById(name);
const shown = (name) => !id(name).hidden;

// Every panel the tab switch owns. Listed once here so a tab's expectation below can name only
// what it shows, and anything unlisted is asserted hidden — a new panel that someone forgets to
// hide on the other five tabs fails rather than lingering.
const PANELS = ['mediaTopCard', 'mediaFilters', 'barelyTouchedFilter', 'unverifiableFilter',
  'tableContainer', 'visitorSummary', 'visitorFilters', 'settingsContainer', 'tracearrContainer'];

const TABS = [
  {
    tab: 'living', button: 'tabLiving', columns: 9, title: 'THE GRAVEYARD',
    url: 'GraveyardAnalytics/Living?mediaType=Movie&limit=10&mediaSearch=',
    shows: ['mediaTopCard', 'mediaFilters', 'tableContainer'],
  },
  {
    tab: 'morgue', button: 'tabMorgue', columns: 6, title: 'THE GRAVEYARD',
    url: 'GraveyardAnalytics/LeastWatched?mediaType=Movie&limit=10&mediaSearch=&includeBarelyTouched=false&includeUnverifiable=false',
    shows: ['mediaTopCard', 'mediaFilters', 'barelyTouchedFilter', 'unverifiableFilter', 'tableContainer'],
  },
  {
    tab: 'chapel', button: 'tabChapel', columns: 9, title: 'THE GRAVEYARD',
    url: 'GraveyardAnalytics/Purgatory?mediaType=Movie&limit=10&mediaSearch=',
    shows: ['mediaTopCard', 'mediaFilters', 'tableContainer'],
  },
  {
    // The end date defaults to today, so this one is a pattern: the shape and the timeframe are
    // the assertion, not the calendar day the harness happens to run on.
    tab: 'visitors', button: 'tabVisitors', columns: 7, title: 'THE GUESTBOOK',
    url: /^\/GraveyardAnalytics\/Visitors\?endDate=\d{4}-\d{2}-\d{2}&weeksBack=1$/,
    shows: ['tableContainer', 'visitorSummary', 'visitorFilters'],
  },
  {
    // Settings and Tracearr deliberately issue no request at all.
    tab: 'settings', button: 'tabSettings', columns: null, title: 'PLUGIN CONFIGURATION',
    url: null, shows: ['settingsContainer'],
  },
  {
    tab: 'tracearr', button: 'tabTracearr', columns: null, title: 'TRACEARR COMMAND CENTER',
    url: null, shows: ['tracearrContainer'],
  },
];

if (!ui.modern) {
  skip('tab wiring', 'this revision predates Phase 7 and has none of the panel ids named here');
  finish();
}

check('the Tracearr tab appears because the saved configuration enables the engine',
  shown('tabTracearr'), `hidden=${id('tabTracearr').hidden}`);
check('viewshow lands on the Morgue and fetches it',
  /LeastWatched/.test(urls.join(',')) && id('pageMainTitle').textContent === 'THE GRAVEYARD',
  urls.join(','));

for (const spec of TABS) {
  urls = [];
  id(spec.button).dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  await sleep(50);

  const active = [...doc.querySelectorAll('.gy-tab.is-active')].map(b => b.dataset.tab);
  check(`${spec.tab}: exactly one tab reads as active, and it is this one`,
    active.length === 1 && active[0] === spec.tab, active.join(','));

  if (spec.url === null) {
    check(`${spec.tab}: issues no request`, urls.length === 0, urls.join(','));
  } else {
    const matches = spec.url instanceof RegExp
      ? spec.url.test(urls[0] || '')
      : urls[0] === '/' + spec.url;
    const name = (spec.url instanceof RegExp ? spec.url.source : spec.url)
      .replace(/\\/g, '').replace(/.*Analytics\//, '').replace(/[?^$].*/, '');
    check(`${spec.tab}: asks for ${name} with the filter values`,
      urls.length === 1 && matches, urls.join(',') || '(none)');
  }

  if (spec.columns !== null) {
    check(`${spec.tab}: the one table carries its ${spec.columns} columns`,
      doc.querySelectorAll('#tableHead th').length === spec.columns,
      `cols=${doc.querySelectorAll('#tableHead th').length}`);
  }

  const wrong = PANELS.filter(p => shown(p) !== spec.shows.includes(p));
  check(`${spec.tab}: shows exactly ${spec.shows.join(' + ')}`, wrong.length === 0,
    `wrong=${wrong.join(',')}`);

  check(`${spec.tab}: heading says ${spec.title}`,
    id('pageMainTitle').textContent === spec.title, id('pageMainTitle').textContent);
}

// Saving with the engine switched off while sitting on the Tracearr tab has to take the tab away
// *and* move off it — otherwise the admin is left looking at a panel for a disabled engine.
id('EnableTracearr').checked = false;
id('saveConfig').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await sleep(50);
check('saving with the engine off hides the Tracearr tab and moves off it',
  id('tabTracearr').hidden
  && [...doc.querySelectorAll('.gy-tab.is-active')].map(b => b.dataset.tab).join(',') === 'settings'
  && shown('settingsContainer'),
  `hidden=${id('tabTracearr').hidden} active=${[...doc.querySelectorAll('.gy-tab.is-active')].map(b => b.dataset.tab).join(',')}`);
check('and it confirms the save', shown('saveConfirmation') && !shown('saveFailure'), '');

finish();
