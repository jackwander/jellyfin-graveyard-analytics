// Drives the REAL WebUI/home.js — the client half of the home screen row — in a DOM.
//
// This is the only piece of the plugin that runs on every user's home screen rather than on an
// admin page, and it is unsupported by construction: Jellyfin has no API for adding a home
// section, so the script reads the DOM the web client produced. That makes two things worth
// pinning: that it renders nothing at all when there is nothing to show, and that a failure
// costs the row rather than throwing into the page.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { JSDOM } from 'jsdom';
import { reporter, sleep } from './support.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const SCRIPT = process.argv[2]
  ? resolve(process.cwd(), process.argv[2])
  : resolve(HERE, '../../../JellyfinGraveyardAnalytics/WebUI/home.js');

const SOURCE = readFileSync(SCRIPT, 'utf8');
const COLLECTION = { Id: 'col-1', Name: 'Leaving Soon: The Chapel' };
const SECTION = '#graveyardLeavingSoonSection';

const { check, finish } = reporter();

/**
 * A page with a home sections container, a mocked ApiClient, and home.js evaluated into it.
 * `items` is what the Chapel contains; `collection` false means it was never created.
 */
async function run({ items = [], collection = true, noApiClient = false, container = true } = {}) {
  const dom = new JSDOM(
    `<!doctype html><html><head></head><body>${container ? '<div class="homeSectionsContainer"></div>' : ''}</body></html>`,
    { runScripts: 'dangerously', url: 'http://localhost/web/index.html' });
  const { window } = dom;
  const calls = [];

  if (!noApiClient) {
    window.ApiClient = {
      getCurrentUserId: () => 'user-1',
      getImageUrl: (id) => `/Items/${id}/Images/Primary`,
      getItems: (userId, query) => {
        calls.push(query);
        if (query.IncludeItemTypes === 'BoxSet') {
          return Promise.resolve({ Items: collection ? [COLLECTION] : [] });
        }
        return Promise.resolve({ Items: items });
      },
    };
  }

  let threw = null;
  try {
    window.eval(SOURCE);
  } catch (err) {
    threw = err;
  }
  await sleep(80);

  return { doc: window.document, window, calls, threw };
}

// ---- 1. The rule that was asked for: an empty Chapel renders nothing ----------------------
let r = await run({ items: [] });
check('empty Chapel: no section is added at all',
  r.doc.querySelector(SECTION) === null && r.threw === null,
  r.threw ? String(r.threw) : 'a section was rendered');
check('empty Chapel: no stray heading or styles left behind',
  !/Leaving Soon/.test(r.doc.body.textContent) && r.doc.getElementById('graveyardLeavingStyles') === null,
  JSON.stringify(r.doc.body.textContent.slice(0, 60)));

// The collection only exists once something has been condemned.
r = await run({ collection: false });
check('no Chapel collection at all: nothing rendered, no crash',
  r.doc.querySelector(SECTION) === null && r.threw === null, String(r.threw));

// ---- 2. With items, the row appears --------------------------------------------------------
r = await run({ items: [{ Id: 'a', Name: 'Cold Open' }, { Id: 'b', Name: 'The Quiet Ones' }] });
const section = r.doc.querySelector(SECTION);
check('populated Chapel: the section is rendered', section !== null, 'no section');
check('one card per condemned item',
  section && section.querySelectorAll('.graveyard-leaving-card').length === 2,
  `cards=${section ? section.querySelectorAll('.graveyard-leaving-card').length : 0}`);
check('the heading says Leaving Soon',
  section && /Leaving Soon/.test(section.querySelector('.graveyard-leaving-title').textContent), '');
check('cards link to the item detail page',
  section && section.querySelector('.graveyard-leaving-card').getAttribute('href') === '#/details?id=a',
  section ? section.querySelector('.graveyard-leaving-card').getAttribute('href') : '');
check('and there is a way through to the whole collection',
  section && section.querySelector('.graveyard-leaving-more').getAttribute('href') === '#/details?id=col-1', '');

// It asks the server for the collection's children, not for the whole library.
check('the item query is scoped to the collection',
  r.calls.some(c => c.ParentId === 'col-1'), JSON.stringify(r.calls));

// ---- 2b. It borrows the web client's own classes ------------------------------------------
// The first version hand-rolled its markup and looked bolted on: flush against the page edge
// while every native row was indented, with half-size posters. These are the classes that make
// it inherit the real layout — dropping padded-left in particular is what caused that.
{
  const s = r.doc.querySelector(SECTION);
  const want = [
    ['section is a vertical section', s, 'verticalSection'],
    ['title row is indented like every other row', s.querySelector('.sectionTitleContainer'), 'padded-left'],
    ['heading uses the native section title', s.querySelector('.graveyard-leaving-title'), 'sectionTitle'],
    ['strip is an items container', s.querySelector('.graveyard-leaving-strip'), 'itemsContainer'],
    ['cards are real cards', s.querySelector('.graveyard-leaving-item'), 'overflowPortraitCard'],
    ['artwork uses the native image container', s.querySelector('.graveyard-leaving-card'), 'cardImageContainer'],
    ['captions use the native card text', s.querySelector('.graveyard-leaving-name'), 'cardText'],
  ];
  for (const [name, el, cls] of want) {
    check(`native styling: ${name} (.${cls})`,
      !!el && el.classList.contains(cls),
      el ? `classes=${el.className}` : 'element missing');
  }
}

// ---- 3. Titles are filenames, so they are attacker-influenced ------------------------------
const PAYLOAD = '<img src=x onerror=alert(1)>';
r = await run({ items: [{ Id: 'a', Name: PAYLOAD }] });
check('a media title is rendered as literal text, not markup',
  r.doc.querySelector('.graveyard-leaving-name').textContent === PAYLOAD
    && r.doc.querySelectorAll('img').length === 0,
  `imgs=${r.doc.querySelectorAll('img').length}`);

// ---- 4. It must not duplicate itself -------------------------------------------------------
// The observer fires on every DOM change, and the home screen mutates constantly.
r = await run({ items: [{ Id: 'a', Name: 'Cold Open' }] });
r.doc.body.appendChild(r.doc.createElement('div'));
r.doc.body.appendChild(r.doc.createElement('div'));
await sleep(80);
check('repeated DOM mutations do not add the section twice',
  r.doc.querySelectorAll(SECTION).length === 1,
  `sections=${r.doc.querySelectorAll(SECTION).length}`);

// ---- 5. Failure costs the row and nothing else ---------------------------------------------
r = await run({ noApiClient: true });
check('no ApiClient: no throw, no section',
  r.threw === null && r.doc.querySelector(SECTION) === null, String(r.threw));

r = await run({ items: [{ Id: 'a', Name: 'Cold Open' }], container: false });
check('no home sections container: no throw, nothing appended',
  r.threw === null && r.doc.querySelector(SECTION) === null, String(r.threw));

// A rejecting API is the likeliest real failure — an expired session, or a 500.
{
  const dom = new JSDOM('<!doctype html><html><body><div class="homeSectionsContainer"></div></body></html>',
    { runScripts: 'dangerously', url: 'http://localhost/web/index.html' });
  const { window } = dom;
  window.ApiClient = {
    getCurrentUserId: () => 'user-1',
    getImageUrl: () => '',
    getItems: () => Promise.reject(new Error('401')),
  };
  let threw = null;
  try { window.eval(SOURCE); } catch (err) { threw = err; }
  await sleep(80);
  check('a failing API leaves the page alone rather than throwing into it',
    threw === null && window.document.querySelector(SECTION) === null, String(threw));
}

// ---- 6. The bug that shipped: a throw before the observer was installed ---------------------
// On a real 10.11 server everything worked — the script was served, the container existed, the
// collection resolved — and the row never appeared. watch() rendered before observing, and
// getCurrentUserId() throws while the page has no session yet, so the exception escaped and the
// observer was never installed. The client then did nothing for the rest of the session.
{
  const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>',
    { runScripts: 'dangerously', url: 'http://localhost/web/index.html' });
  const { window } = dom;
  let sessionReady = false;

  window.ApiClient = {
    // Exactly the real failure: it throws rather than returning nothing.
    getCurrentUserId: () => {
      if (!sessionReady) throw new Error('no session yet');
      return 'user-1';
    },
    getImageUrl: (id) => `/Items/${id}/Images/Primary`,
    getItems: (userId, query) => Promise.resolve({
      Items: query.IncludeItemTypes === 'BoxSet' ? [COLLECTION] : [{ Id: 'a', Name: 'Cold Open' }],
    }),
  };

  let threw = null;
  try { window.eval(SOURCE); } catch (err) { threw = err; }
  await sleep(40);

  check('a throwing session at load does not take the script down',
    threw === null, String(threw));
  check('and nothing is rendered while there is no session',
    window.document.querySelector(SECTION) === null, 'rendered too early');

  // The home screen arrives later, as it does behind a login.
  sessionReady = true;
  const container = window.document.createElement('div');
  container.className = 'homeSectionsContainer';
  window.document.body.appendChild(container);
  await sleep(250);

  check('once the session and the home screen arrive, the row still appears',
    window.document.querySelector(SECTION) !== null,
    'the observer was never installed — this is the shipped bug');
}

// A synchronous throw must not latch `running` and disable every later attempt.
{
  const dom = new JSDOM('<!doctype html><html><head></head><body><div class="homeSectionsContainer"></div></body></html>',
    { runScripts: 'dangerously', url: 'http://localhost/web/index.html' });
  const { window } = dom;
  let broken = true;

  window.ApiClient = {
    getCurrentUserId: () => 'user-1',
    getImageUrl: (id) => `/Items/${id}/Images/Primary`,
    getItems: (userId, query) => {
      if (broken) throw new Error('transient');
      return Promise.resolve({
        Items: query.IncludeItemTypes === 'BoxSet' ? [COLLECTION] : [{ Id: 'a', Name: 'Cold Open' }],
      });
    },
  };

  window.eval(SOURCE);
  await sleep(40);
  check('a synchronous API throw renders nothing, as expected',
    window.document.querySelector(SECTION) === null, 'rendered despite throwing');

  broken = false;
  window.document.body.appendChild(window.document.createElement('div'));
  await sleep(250);
  check('and it recovers once the API works — the failure is not latched',
    window.document.querySelector(SECTION) !== null,
    '`running` stayed true after the throw');
}

// ---- 7. No innerHTML anywhere in the shipped file ------------------------------------------
check('the script never assigns innerHTML',
  !/\.innerHTML\s*=/.test(SOURCE), 'innerHTML assignment found');

finish();
