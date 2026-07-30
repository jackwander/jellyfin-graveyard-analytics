# Verification harnesses

Throwaway-grade checks that were written to *verify claims*, not to be a test
suite. They are committed because each one is the evidence behind a `PLAN.md`
finding, and because re-deriving them costs more than keeping them.

**These are not the project's test suite.** That is
`tests/GraveyardAnalytics.Tests` (85 tests), added in Phase 6 and run in CI by
`.github/workflows/build.yml`.

What went there and what stayed here. The suite took the claims that are about
*shipped behaviour* and can be stated as an assertion: `FormatBytes`, D2's play
threshold across all four aggregates, D1's floor gate driven through the real
`GetLeastWatchedItems`, the configuration clamps, finding 30's parse and its
serialized form, the `TtlCache`, and that the embedded Chapel artwork is present
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
node xss.test.mjs        # 31 checks
node actions.test.mjs    # 17 checks
node dates.test.mjs      #  6 checks
```

Loads `WebUI/dashboard.html`, dispatches `viewshow`, then calls the real
`renderMediaTable` / `renderVisitorTable` / `renderCoverageBanner`.

Covers: media titles and visitor `Visitor` / `Subject` / `Device` / `Player`
rendering as literal text; no injected `<img>`; action buttons carrying no inline
handler; per-tab column counts (morgue 6, others 9); a `0` value rendering as
`"0"` rather than blank; empty states; the coverage banner in all three states.

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
non-vacuous — against the pre-Phase-1 file, `xss.test.mjs` **fails 12**,
including an injected live `onmouseover` handler:

```bash
git show 71a01f7:JellyfinGraveyardAnalytics/WebUI/dashboard.html > /tmp/old.html
node xss.test.mjs /tmp/old.html    # expect failures
```

Caveat: jsdom does not fetch `img src=x`, so `onerror` never fires there. The
proof of execution is the injected `onmouseover` attribute and the `<img>` nodes
themselves, not an `alert()` count.

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
