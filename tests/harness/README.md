# Verification harnesses

Throwaway-grade checks that were written to *verify claims*, not to be a test
suite. They are committed because each one is the evidence behind a `PLAN.md`
finding, and because re-deriving them costs more than keeping them.

**These are not the project's test suite.** Phase 6 adds a real xUnit project.
Nothing here runs in CI yet and there is no runner tying them together.

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
node actions.test.mjs    #  9 checks
```

Loads `WebUI/dashboard.html`, dispatches `viewshow`, then calls the real
`renderMediaTable` / `renderVisitorTable` / `renderCoverageBanner`.

Covers: media titles and visitor `Visitor` / `Subject` / `Device` / `Player`
rendering as literal text; no injected `<img>`; action buttons carrying no inline
handler; per-tab column counts (morgue 6, others 9); a `0` value rendering as
`"0"` rather than blank; empty states; the coverage banner in all three states.

Since Phase 2 item 7 both engines return the same `VisitorResponse`, so the
visitor checks drive one renderer with two payloads — Tracearr-shaped rows
(`ProgressPercent` set → a Fate verdict) and local ones (`ProgressPercent` null →
a dash, never a guessed verdict) — plus the truncation notice's `colSpan`.

Both accept a path argument, which is how the checks were shown to be
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

## dotnet/probes/ — SQLite and webhook behavior

```bash
cd dotnet/probes && dotnet run
```

- **Probe A** — that `Data Source=<path>` without `Mode=ReadOnly` opens
  read-write *and creates* a missing `playback_reporting.db` (finding 3).
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
