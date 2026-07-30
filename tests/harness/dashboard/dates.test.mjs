// The browser half of finding 30's round trip.
//
// The .NET half (tests/harness/dotnet/repository, probes B3b and G1-G3) proves the server now
// ships `"LastPlayed":"2026-03-04T11:30:00Z"` where it used to ship the same clock time with no
// zone. This drives the *real* renderMediaTable with both strings and shows what that costs the
// admin: a whole-day error in the Last Breath column, and an item wrongly classified against the
// twelve-month staleness cut.
//
// It re-execs itself under a fixed non-UTC zone. Without that the test would pass vacuously on a
// UTC machine — which is exactly the configuration this bug hides on, and plenty of servers run
// that way.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const ZONE = 'America/Los_Angeles';   // UTC-8/-7: late-UTC instants fall on the previous local day

if (process.env.TZ !== ZONE) {
  const child = spawnSync(
    process.execPath,
    [fileURLToPath(import.meta.url), ...process.argv.slice(2)],
    { stdio: 'inherit', env: { ...process.env, TZ: ZONE } });
  process.exit(child.status ?? 1);
}

const { JSDOM } = await import('jsdom');

const HERE = dirname(fileURLToPath(import.meta.url));
const HTML = process.argv[2]
  ? resolve(process.cwd(), process.argv[2])
  : resolve(HERE, '../../../JellyfinGraveyardAnalytics/WebUI/dashboard.html');

const dom = new JSDOM(readFileSync(HTML, 'utf8'), { runScripts: 'dangerously' });
const { window } = dom;
const doc = window.document;

window.ApiClient = {
  getUrl: (u) => '/' + u,
  getJSON: () => Promise.resolve({ Items: [], TotalSize: '0 B' }),
  getPluginConfiguration: () => Promise.resolve({}),
  updatePluginConfiguration: () => Promise.resolve({}),
  ajax: () => Promise.resolve({}),
};

doc.getElementById('GraveyardAnalyticsPage').dispatchEvent(new window.Event('viewshow'));

const results = [];
const check = (name, ok, detail) => results.push({ name, ok, detail });

// 19:30 on 3 March in Los Angeles (UTC-8; before US DST begins on the 8th) — so the correct local
// calendar day is the 3rd, while the zoneless string is read as 03:30 on the 4th. One instant,
// two days. The UTC time-of-day has to be inside the offset for the days to differ at all, which
// is why this is 03:30Z and not some round hour.
const INSTANT_UTC = '2026-03-04T03:30:00Z';
const INSTANT_NO_ZONE = '2026-03-04T03:30:00';

const base = {
  MediaId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', Name: 'Cold Open', Type: 'Movie',
  FormattedSize: '40 GB', PlayCount: 3, UniqueViewers: 1, FormattedDuration: '01:00:00',
  DateAdded: '2024-01-01T00:00:00Z',
};

// Located by its header rather than by a hardcoded index, so moving the column fails loudly
// instead of silently passing on whatever cell now sits there. The colour comes back too: this
// cell is also a verdict, not only a date.
function renderLastBreath(lastPlayed) {
  window.currentTab = 'living';
  window.renderMediaTable([{ ...base, LastPlayed: lastPlayed }]);

  const tbody = doc.getElementById('leastWatchedTableBody');
  const headers = [...tbody.closest('table').querySelectorAll('thead th')]
    .map(th => th.textContent.trim());
  const index = headers.findIndex(h => /last breath/i.test(h));
  const cells = tbody.querySelectorAll('td');

  return index >= 0
    ? { index, text: cells[index].textContent.trim(), color: cells[index].style.color }
    : { index, text: '(no Last Breath column)', color: '' };
}

const fixed = renderLastBreath(INSTANT_UTC);
const broken = renderLastBreath(INSTANT_NO_ZONE);

check('the Last Breath column is where this test thinks it is', fixed.index >= 0,
  `index=${fixed.index}`);

check(`TZ is pinned to ${ZONE}, so the assertion is not vacuous`,
  new Date(INSTANT_UTC).getTimezoneOffset() !== 0,
  `offset=${new Date(INSTANT_UTC).getTimezoneOffset()}`);

// Expectations built from explicit calendar components and formatted the same way the dashboard
// formats, so the check does not depend on the runner's locale — only on which *day* it lands on,
// which is the thing under test.
const localDay = (y, m, d) => new Date(y, m, d)
  .toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });

check('a UTC instant renders on the correct local day (3 March in Los Angeles)',
  fixed.text === localDay(2026, 2, 3), `${fixed.text} <- from ${INSTANT_UTC}, wanted ${localDay(2026, 2, 3)}`);

check('the pre-fix zoneless value renders a day later — the whole bug, in one cell',
  broken.text === localDay(2026, 2, 4) && broken.text !== fixed.text,
  `fixed=${fixed.text} broken=${broken.text}`);

// The cell is a verdict as well as a date: renderMediaTable colours it against a twelve-month
// cut. So the offset does not merely misprint a day — it can move an item across that line and
// dress a long-dead title as a live one. Straddling the cut by an hour proves the line is real
// and that an offset-sized error is enough to cross it.
const iso = (d) => d.toISOString().replace(/\.\d{3}Z$/, 'Z');
const cut = new Date();
cut.setMonth(cut.getMonth() - 12);

const live = renderLastBreath(iso(new Date(cut.getTime() + 36e5)));   // an hour inside the cut
const dead = renderLastBreath(iso(new Date(cut.getTime() - 36e5)));   // an hour past it

check('an hour inside twelve months reads as alive, an hour past it reads as dead',
  live.color === 'rgb(170, 170, 170)' && dead.color === 'rgb(207, 102, 121)',
  `live=${live.text}/${live.color} dead=${dead.text}/${dead.color}`);

check('so an offset-sized error is enough to cross that verdict, not just misprint a day',
  Math.abs(new Date(iso(cut)).getTimezoneOffset()) * 60000 > 36e5,
  `offset=${new Date(iso(cut)).getTimezoneOffset()}min vs the 60min margin tested`);

let failed = 0;
for (const r of results) {
  if (!r.ok) failed++;
  console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '   <-- ' + r.detail}`);
}
console.log(`\n${results.length - failed}/${results.length} passed  (TZ=${process.env.TZ})`);
process.exit(failed ? 1 : 0);
