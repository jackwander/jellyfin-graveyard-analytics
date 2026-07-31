# Verification harnesses

Throwaway-grade checks that were written to *verify claims*, not to be a test
suite. They are committed because each one is the evidence behind a `PLAN.md`
finding, and because re-deriving them costs more than keeping them.

**These are not the project's test suite.** That is
`tests/GraveyardAnalytics.Tests` (91 tests), added in Phase 6 and run in CI by
`.github/workflows/build.yml`.

What went there and what stayed here. The suite took the claims that are about
*shipped behaviour* and can be stated as an assertion: `FormatBytes`, D2's play
threshold across all four aggregates, D1's floor gate driven through the real
`GetLeastWatchedItems`, the configuration clamps, finding 30's parse and its
serialized form and its `JellyfinTimestamps.AsUtc` boundary, the missing-table guard, the
`TtlCache`, and that the embedded Chapel artwork is present
under the names the controller asks for. Several of those overlap a harness here
on purpose — the suite is the version that runs on every push, and the harness is
the version that can be pointed at an *old* assembly with `GRAVEYARD_DLL` to show
the check was not vacuous.

What stayed: everything that is a statement about SQLite or about the environment
rather than about this plugin (`probes` A, the four WAL arrangements), the live
Tracearr probe, the side-by-side old/new comparisons, and the webhook mirror.

The dashboard harnesses **do** run in CI now — they are still the only thing that
executes the shipped `dashboard.html` at all.

## Why they exist at all

There is no Jellyfin server available in this environment, and only the dotnet
`10.0.x` runtime is installed while the plugin targets `net9.0` — so the plugin
can be **built but never loaded** here. Every runtime claim therefore had to be
checked one of three ways:

1. against the **real** `dashboard.html`, driven in a DOM (the JS harnesses),
2. against the **built plugin assembly** by reflection (`FormatBytes`, the DTO),
3. against a **faithful replica** of the controller logic (the webhook probes).

Only 1 and 2 exercise shipped code. 3 mirrors it, so it can drift — if the
webhook logic changes, the mirror in `dotnet/probes/Program.cs` must change too.

## dashboard/ — jsdom over the real UI

```bash
cd dashboard && npm install
node xss.test.mjs        # 32 checks
node actions.test.mjs    # 24 checks
node dates.test.mjs      #  6 checks
node tabs.test.mjs       # 32 checks
node home.test.mjs       # 27 checks
```

Loads `WebUI/dashboard.html`, dispatches `viewshow`, then calls the real
`renderMediaTable` / `renderVisitorTable` / `renderCoverageBanner`.

Covers: media titles and visitor `Visitor` / `Subject` / `Device` / `Player`
rendering as literal text; no injected `<img>`; action buttons carrying no inline
handler; per-tab column counts (morgue 6, others 9); a `0` value rendering as
`"0"` rather than blank; empty states; the coverage banner in all three states.

`support.mjs` is shared plumbing, not a test. Phase 7 rewrote the page — one table
driven by a per-tab column descriptor, module-scoped state behind a single
`window.GraveyardDashboard` seam, and the `hidden` attribute instead of inline
`style.display` — so the differences between revisions live in one adapter rather
than three times over. Two things it records:

- The old file wrote the total card with `innerText`, which **jsdom does not
  implement**. The assignment landed on an expando, so the pre-Phase-7 card
  assertions were reading back their own input and never touched the DOM. The page
  uses `textContent` now and the adapter prefers the expando only when one exists,
  so older revisions still read.
- The Last Breath verdict was an inline colour and is a class (`gy-dead` /
  `gy-alive`) now, so `dates.test.mjs` asserts the class and does not need jsdom to
  resolve the cascade. It still accepts the old colour.

`tabs.test.mjs` is the fourth, added by Phase 7. It is the only one that drives the
page the way an admin does — clicking the tab bar rather than calling a renderer —
and it asserts, per tab, the endpoint requested with its filter values, the column
count of the one table (9 / 6 / 9 / 7, and no request at all from the two
configuration tabs), the heading, that exactly one tab reads as active, and the
**complete** panel visibility map: anything a tab does not list is asserted hidden,
so a new panel that someone forgets to hide on the other five fails rather than
lingering. Plus that saving with the engine switched off takes the Tracearr tab away
*and* moves off it. It skips entirely on a pre-Phase-7 file: none of the panel ids
it names existed.

`home.test.mjs` drives `WebUI/home.js`, the client half of the home screen row. That script
is unsupported by construction — Jellyfin has no API for adding a home section, so it reads
the DOM the web client produced — which makes two properties worth pinning. It **renders
nothing when the Chapel is empty** (an empty row announcing that nothing is leaving is worse
than no row), and **a failure costs the row and nothing else**: no ApiClient, no container, and
a rejecting API each leave the page untouched without throwing into it. Media titles are
filenames, so the same textContent discipline as the dashboard is checked here too.
Non-vacuity: dropping the `items.length` guard makes the two empty-Chapel checks fail.

Phase 7 also added, in `actions.test.mjs`: **finding 7 measured** — three
`viewshow` dispatches, then one keystroke, and a count of the fetches it caused;
the loading row; the error row and that it names the status and prefers the
server's own `{ message }`; that a failed fetch clears the total card; and that a
response arriving after its tab was left is dropped, which matters now that one
table serves every tab.

Since Phase 5 item 20, `actions.test.mjs` also drives `renderTotals` over the
split response fields: the per-tab label and its "(listed rows)" qualifier when
`TotalCoversAllMatches` is false, `TotalSize` as the headline figure, and the
"Never played" sub-line appearing **only** when `TotalWasted` differs from it (so
the Morgue's default zero-play state does not print the same number twice) and
never for a null, which means "this view has nothing it can claim" rather than a
claim of zero. Plus that leaving a tab clears the sub-line, since it is a claim
about one tab's rows and `fetchAndRenderTable` still has no `.catch`.

Since Phase 2 item 7 both engines return the same `VisitorResponse`, so the
visitor checks drive one renderer with two payloads — Tracearr-shaped rows
(`ProgressPercent` set → a Fate verdict) and local ones (`ProgressPercent` null →
a dash, never a guessed verdict) — plus the truncation notice's `colSpan`.

`dates.test.mjs` is the browser half of finding 30's round trip: it drives
`renderMediaTable` with `"2026-03-04T03:30:00Z"` and with the pre-fix zoneless
`"2026-03-04T03:30:00"`, and shows the Last Breath cell reading **3 March** against
**4 March** for one instant. It **re-execs itself under `TZ=America/Los_Angeles`** —
on a UTC machine the bug is invisible and the test would pass vacuously, and plenty
of servers run UTC. It also pins the twelve-month colour cut, because the cell is a
verdict as well as a date and an offset-sized error can cross it. Expectations are
built from explicit calendar components and formatted the same way the page formats,
so the runner's *locale* does not matter — only which day it lands on.

All three accept a path argument, which is how the checks were shown to be
non-vacuous:

```bash
git show 71a01f7:JellyfinGraveyardAnalytics/WebUI/dashboard.html > /tmp/old.html
node xss.test.mjs /tmp/old.html         # 6/13, 2 skipped — expect failures

git show 55b2a2a:JellyfinGraveyardAnalytics/WebUI/dashboard.html > /tmp/pre7.html
node actions.test.mjs /tmp/pre7.html    # 17/19, 1 skipped — finding 7 fails at 3 fetches
```

Against `71a01f7`, `xss.test.mjs` **fails 7** — including
`attrs=onclick,onmouseover,class,style` on an action button, i.e. the attribute
breakout at old `:569` demonstrably injects a live event handler.

That run had **silently stopped working**. This README claimed "fails 12", but
Phase 2 item 7 unified the two visitor tables and renamed the tbody, so from then
on the harness died on a null lookup partway through instead of reporting; the
claim went unchecked because nothing re-ran it. The adapter now reports what a given
revision *has* (`supports`) and skips the rest with a printed `SKIP`, so a
degraded run cannot read as a complete one. Seven failures, two skipped sections —
the visitor tables and the coverage banner, neither of which existed at `71a01f7`.

Against `55b2a2a` (the page as Phase 6 left it), `actions.test.mjs` fails the
finding 7 pair at **`fetches=3`** — one keystroke, three viewshows' worth of
duplicate listeners, three requests. That is the measurement Phase 7 is judged on.

Caveat: jsdom does not fetch `img src=x`, so `onerror` never fires there. The
proof of execution is the injected `onmouseover` attribute and the `<img>` nodes
themselves, not an `alert()` count.

## dotnet/abi/ — would the built plugin run on this Jellyfin?

```bash
cd dotnet/abi && dotnet run                       # 5 checks, default 10.11.11
dotnet run -p:JfVersion=10.11.6                   # any 10.11.x
GRAVEYARD_DLL=/path/to/old/plugin.dll dotnet run -p:JfVersion=10.11.11   # non-vacuity
```

The harness that found the worst bug in this repo. It reads the **MemberRef table of the
built assembly's IL** — every member the plugin actually calls out of a `Jellyfin.*` /
`MediaBrowser.*` assembly, 46 of them — and asks the reference assemblies of a given
10.11.x whether each one still exists. Then it drives `UserManagerCompat.AllUsers`
through a `DispatchProxy` stub, because that shim binds by name at runtime and nothing at
compile time can tell you it works.

Why this class of bug needs a harness at all: a removed member is **not a build error**.
The plugin compiles against its pinned reference assemblies, `manifest.json`'s `targetAbi`
is a *minimum* so the server loads it regardless, and the failure only arrives when the
code path is first executed. Nothing else here would have caught it.

What it found: `IUserManager.Users` existed through **10.11.8** and was replaced by
`GetUsers()` in **10.11.9**. The two never coexist, so no single compiled call reaches
both, and the shipped plugin — compiled against 10.11.6 — carried IL referencing
`get_Users`. On any 10.11.9+ server it loaded and then died the moment the Guestbook was
opened. Measured in both directions: pointed at the pre-shim assembly it reports 47 refs
with `get_Users` **missing on 10.11.9 and 10.11.11 and present on 10.11.8**; pointed at the
current one, 46 refs and **0 missing on all four**, with the shim returning users on each.

Caveat worth keeping straight, in the spirit of the rest of this file: this proves the
member is *absent*, not that the runtime throws — the plugin still cannot be loaded here.
The failure mode is the ordinary .NET consequence of a missing member, not something
observed. It also says nothing about types the reference packages do not ship, and it
reports how many of those it skipped rather than counting them as passes.

## dotnet/formatbytes/ — old vs new, side by side

```bash
cd dotnet/formatbytes && dotnet run
```

Runs the pre-fix `FormatBytes` (copied from `71a01f7`) next to the current one
loaded from the built DLL. Shows identical output from 0 B to 5 TB, and that
1 PB / 1 EB / `long.MaxValue` threw `IndexOutOfRangeException` before and do not
now. Build the plugin first, or set `GRAVEYARD_DLL`.

## dotnet/visitormap/ — the Tracearr → VisitorResponse mapping (Phase 2 item 7)

```bash
cd dotnet/visitormap && dotnet run                      # 21 checks, live probe skipped
TRACEARR_URL=http://10.10.1.201:3000 TRACEARR_KEY=trr_pub_… dotnet run   # + 5 live
```

Reflects `TracearrService.MapSession` out of the built DLL and drives it with a
**verbatim history row** captured from the live server (recorded in `PLAN.md`).
The row is the point: `durationMs` is a JSON number while `progressMs` and
`totalDurationMs` are JSON *strings* on the same row, so a mapper calling
`GetInt64()` on all three throws on two of them. Also covers `{}`, a null
`showTitle` (movies), a zero runtime (divide-by-zero), unparseable numbers, and
progress past 100%.

Then reflects `BuildHistoryEndpoint` and asserts the query string carries
`startDate`/`endDate`/`timezone`/`pageSize=100` and **no `weeksBack`**
(finding 27). With the two env vars set it fires that exact query at a real
Tracearr and checks the server accepts it and that every returned row falls
inside the window — the one harness here that exercises shipped code against the
real service rather than a mirror. The key is read from the environment and is
never written to the repo.

## dotnet/ttlcache/ — the aggregate cache (Phase 4 item 15)

```bash
cd dotnet/ttlcache && dotnet run     # 12 checks
```

Reflects `TtlCache<T>` out of the built DLL and drives it with an **injected
clock**, so the TTL is crossed without sleeping, and a factory that counts its
own invocations — one invocation is one full set of aggregate queries.

This is the harness behind Phase 4's done-when ("a debounced keystroke issues no
new SQL inside the TTL window"): ten reads inside the window load once, 59s is
still cached and 61s is not, a signature change (engine or play threshold) is a
miss rather than stale data, `Invalidate` forces a reload inside the window, and
eight concurrent readers collapse into a single load.

Caveat worth keeping straight: this verifies the **cache**, not the database.
Nothing here observes SQLite. The done-when holds because the factory runs once
per window *and* is the only remaining caller of the repository aggregates — the
second half of that is read from the code, not measured.

Phase 5 removed the reason `PlaybackStatsProvider` itself could not be driven
here — it takes its configuration and repository through the constructor now
instead of reading `Plugin.Instance`. Nothing exercises it yet; that belongs to
Phase 6's xUnit project, which can construct it outright.

## dotnet/di/ — container resolution and lifetimes (Phase 5 item 18)

```bash
cd dotnet/di && dotnet run     # 19 checks
```

Runs the real `GraveyardServiceRegistrator` from the built DLL into a real
`ServiceCollection` and builds the provider with
`ValidateOnBuild + ValidateScopes`, mirroring how Jellyfin activates things:
registrator via a parameterless ctor, controllers from a request scope *and* via
`ActivatorUtilities` (what `DefaultControllerActivator` uses), plugin via
`ActivatorUtilities` on the root provider. Jellyfin's own services are
`DispatchProxy` stubs; every plugin registration is real.

This is the harness for the part of item 18 that reading the code cannot settle —
lifetimes: `Repository` and `TtlCache` one instance server-wide, `AnalyticsService`
one per request (its episode index must not outlive the request),
`TracearrService` transient so no `HttpClient` is pinned, and no captive
dependency anywhere. Two checks are about the remaining static: resolving the
whole graph leaves `Plugin.Instance` **null**, so nothing depends on
plugin-construction order — and `PluginConfigurationSource.Current` throws an NRE
if read before Jellyfin has constructed the plugin, which every media path has
inside a `try` but `TracearrController`'s two actions do not.

Not covered: that Jellyfin's own container registers `IApplicationPaths`, which
`Repository` needs. It does — the released pre-Phase-5 `Plugin` ctor took it and
loaded — but that is read from evidence, not measured here.

## dotnet/repository/ — the real Repository over a real SQLite file (Phase 5 item 19)

```bash
cd dotnet/repository && dotnet run     # 27 checks
GRAVEYARD_DLL=/path/to/old/plugin.dll dotnet run   # non-vacuity check, expect B3b + G1 to fail
```

Constructs the **real** `Repository` from the built DLL (a stub `IApplicationPaths`
pointed at a temp directory) and queries a SQLite database seeded through
Playback Reporting's column declarations. Two claims need this:

- **The typed row DTOs** that replaced Dapper's `dynamic`. Mapping is decided at
  runtime from SQLite's storage classes, so a clean build proves nothing: the
  checks cover all four aggregates, the history floor, the Guestbook row shape,
  the row cap's truncation flag, the UTC window, dash-stripped id keys, and that
  the play threshold is a real query parameter (3 plays at a 1s floor, 2 at 120s).
  Two probes cover the states a typed mapper is likeliest to break on where
  `dynamic` did not: an **empty `PlaybackActivity` table** — a fresh Playback
  Reporting install, the most common state there is — and a **NULL-heavy newest
  row**, which matters because Dapper builds one deserializer per query from the
  *first* row's storage classes.
- **Finding 3's read-only handle.** It reads `_playbackDbConn` off the instance by
  reflection and asserts the string the repository *actually uses* says
  `Mode=ReadOnly`, that a write through it is refused, that a missing database
  throws instead of being invented, and that **no file is created** by the
  attempt — the inverse of `probes` Probe A, which recorded the old behavior.
- Four WAL arrangements, because Playback Reporting chooses the journal mode: a
  cleanly closed WAL database, a stale `-wal` copied out from under a live writer
  (what a killed server leaves), that same stale `-wal` with its `-shm` deleted,
  and that case again with the directory itself made read-only. The first three
  read fine; the fourth fails with SQLite error 14.

- **Finding 30's round trip** (probes B3b, G1-G3): stored string → `Repository` →
  the real `LeastWatchedItem` → JSON, asserting the `DateTimeKind` and then the
  serialized `"LastPlayed":"2026-03-04T11:30:00Z"`. Pointed at a pre-fix assembly
  via `GRAVEYARD_DLL` these two fail and the other 25 pass, which is what makes them
  a test of the fix rather than of the harness. `dates.test.mjs` carries it into the
  browser.

  The third WAL probe is the informative one, and an earlier version of it was
  **wrong**: it was credited with showing that a read-only connection copes with a
  missing `-shm`, when in fact SQLite *creates* the `-shm` — a read-only connection
  writes that sidecar into another plugin's directory. It now asserts the file
  list before and after to say so, and probe E4 pins the one arrangement that
  genuinely fails, by removing write access to the directory. Jellyfin writes to
  its data path constantly, so E4 is not a state a real server is in; it is here so
  the read-only claim is no stronger than the evidence.

The table DDL is a **replica** of Playback Reporting's, so it can drift — that
plugin is not installed here. The declared types are the part that matters, and
`DateCreated DATETIME` holding a naive UTC string is precisely the mismatch the
string-typed DTOs exist for.

## dotnet/probes/ — SQLite and webhook behavior

```bash
cd dotnet/probes && dotnet run
```

- **Probe A** — that `Data Source=<path>` without `Mode=ReadOnly` opens
  read-write *and creates* a missing `playback_reporting.db` (finding 3). This is
  a statement about SQLite, so it still holds; Phase 5 fixed the plugin side, and
  `dotnet/repository` checks the shipped connection string against it.
- **Probe D** — reflects over the built assembly to confirm `LeastWatchedItem`
  serializes no filesystem path (the success-body leak found in review).
- **Probe B** — the *old* query-string token binding, run under both settings of
  `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes`. This is what
  struck finding 5's claimed bypass: empty and whitespace values bind to `null`,
  so the token could never equal `string.Empty`.
- **Probe C** — the *current* logic: engine-disabled and empty-key rejection,
  header token, fixed-time compare (a prefix of the key fails), honest 501, and
  the correct key in `?token=` no longer authenticating.

B and C are mirrors of controller code, not the controller itself — see the
warning above.
