# Graveyard Analytics — Improvement Plan (LOCKED)

Baseline commit: `71a01f7`
Locked: 2026-07-30

All findings below come from **static reading** of the source, not from a runtime
run. Phase 0 exists to confirm the build and to verify the P0 claims against a
live server before anything is changed.

---

## Locked decisions

### D1 — The Morgue means "strict zero-play, aged past a grace period"

```
item is in the Morgue  <=>  PlayCount == 0
                        AND DateCreated <= UtcNow - effectiveGrace
```

- Gate on `DateCreated` (Summoned), **not** `LastPlayed` — zero-play items have
  `LastPlayed == null`, so a last-played rule cannot express "old".
- New config `MorgueGraceDays`, default **180**, range 30–365.
  Rationale: 365 days means a full year of wasted storage before anything
  surfaces; 180 is already well past any seasonal-rewatch pattern.
- `Total Dead Weight` becomes the sum of the rows actually displayed. Today the
  header sums `PlayCount == 0` across the whole library while the table shows a
  different, partly-played set.

**Mandatory clamp — history coverage.** Playback Reporting only knows history
since it was installed. On a young database every item reads as zero-play, and a
long grace period would flood the Morgue with the entire pre-install library as
false positives.

```sql
SELECT MIN(DateCreated) FROM PlaybackActivity   -- history floor
```

`effectiveGrace = min(MorgueGraceDays, coverageDays)`. Surface `coverageDays` in
the response and render a banner: *"History covers N days — items added before
that cannot be verified as unwatched."* The Tracearr path needs the same clamp,
bounded there by `weeksBack`.

**Least-watched is not lost.** Sanctuary sorts vitality *descending*, so
"1 play, 1 viewer, 40 GB" would otherwise be visible nowhere. Add a checkbox to
the Morgue filter bar — *"Include barely-touched (< N plays)"*, default off. No
new tab, no new endpoint.

**Known gap, deferred:** episode play counts are summed into the series, so one
2-minute sample of episode 1 lifts a 60-episode show out of the Morgue. Needs a
separate aggregate rule (engaged plays relative to episode count). Not in scope.

### D2 — One play threshold: `MinPlayDurationSeconds`, default 120

120s is a sound floor for "not an accidental play" — nobody accidentally streams
two minutes. The number is not the bug.

The bug is that the threshold is **applied inconsistently**, which is what
produces `Plays 3 / Reach 0` and `Last Breath: yesterday / Plays 0`:

| `Repository` method | current filter | after |
| --- | --- | --- |
| `GetItemPlayCounts` (`:87`) | `PlayDuration >= 120` | `>= MinPlayDurationSeconds` |
| `GetItemViewers` (`:112`) | `PlayDuration > 300` | `>= MinPlayDurationSeconds` |
| `GetItemLastPlayedDates` (`:143`) | none | `>= MinPlayDurationSeconds` |
| `GetItemPlayDurations` (`:171`) | none | `>= MinPlayDurationSeconds` |

There is no defensible reason for the 120/300 split. Last-played in particular
**must** be filtered — otherwise a 10-second check bumps "Last Breath" and
shields the item from every time-based rule.

**Deferred refinement:** 120s is absolute, so it is 9% of a sitcom episode but
1.7% of a 3-hour film. Playback Reporting has no runtime column, but Jellyfin
does (`item.RunTimeTicks`), so `threshold = max(MinPlayDurationSeconds,
RunTimeSeconds * MinPlayFraction)` with `MinPlayFraction = 0.05` is available
later. Flat 120 first — it is the one-line-per-query change that makes the
numbers reconcile; runtime-relative needs runtime joined into the aggregate path.

---

## Findings

### P0 — broken now

| # | Finding | Location |
| --- | --- | --- |
| 1 | **Tracearr Morgue path double-prefixed.** Base builds `.../api/v1/public/{endpoint}`; this caller passes `"public/history?..."` → `/api/v1/public/public/history` → 404. The other two callers pass bare paths. With Tracearr enabled all three media tabs throw → `BadRequest` → UI shows an empty table with no message (no `.catch` anywhere). | `TracearrService.cs:31`, `:213` vs `:82`, `:96` |
| 2 | **N+1 SQL inside a LINQ projection.** `GetItemPlayDurations()` sits inside the `Select` lambda — a full-table `SUM…GROUP BY` re-runs once per Chapel item. Correctly hoisted in `GetLivingItems`. | `AnalyticsService.cs:223` vs `:310` |
| 3 | **Playback Reporting DB opened read-write.** `Mode=ReadOnly` is set, then overwritten without it. Writable handle on another plugin's SQLite file → lock contention; SQLite will *create* an empty `playback_reporting.db` if absent, masking "plugin not installed". | `Repository.cs:20-21`, `:27-28` |
| 4 | **Stored XSS in the admin dashboard.** Rows built by string-concatenating `item.Name`, Tracearr `username`, `mediaTitle`, `device` into `innerHTML`. All are attacker-influenced (a filename; any Tracearr-side user). Executes with the **admin's** session. `:569` is worse — name is escaped for `'` only, then embedded in an `onclick="..."` attribute, so a `"` breaks out. | `dashboard.html:481`, `:519`, `:569`, `:574` |
| 5 | ~~**Webhook auth bypass when the key is unset.**~~ **Bypass struck by Phase 0 — see Phase 0 results.** Empty and whitespace query values bind to `null`, so `token` can never equal `string.Empty`; a fresh install fails closed. What remains (severity P1, still fixed in Phase 1): endpoint is `[AllowAnonymous]`; token in a query string lands in access logs; comparison is not fixed-time; handler is a stub that returns `{status:"Condemned"}` and does nothing. | `TracearrController.cs:44-49`, `:57` |
| 6 | **Metric thresholds mutually inconsistent.** See D2. Almost certainly what `71a01f7` was chasing. | `Repository.cs:87`, `:112`, `:143`, `:171` |
| 7 | **Duplicate-listener accumulation.** Every `addEventListener` is registered *inside* the `viewshow` handler. Jellyfin fires `viewshow` on each return to the page → Nth visit fires N fetches per keystroke. | `dashboard.html:411`, `:635-643` |

### P0 — added during Phase 2, from the live Tracearr server at `10.10.1.201:3000`

| # | Finding | Location |
| --- | --- | --- |
| 24 | **`TestConnectionAsync` calls an endpoint that does not exist.** `GET /api/v1/public/system/status` → **404** on a live, healthy Tracearr. So the Settings tab's connection test can *never* succeed, and `PingTracearr` reports "Could not connect to Tracearr. Check your URL and API Key." even when the URL and key are perfect. Same class of bug as finding 1, missed by static reading because the endpoint name looks plausible. Confirmed real endpoints under `/api/v1/public/`: `history`, `users`, `stats`, `docs` (all 401 unauthenticated). Unauthenticated `GET /health` at the server root returns `{"status":"ok",...}`. | `TracearrService.cs:66` |
| 25 | **`media/stale` does not exist either** — `GET /api/v1/public/media/stale` → 404. Both dead methods that Phase 2 deletes were built against an endpoint Tracearr does not serve, so they could never have worked. Strengthens item 8 from "dead code" to "dead *and* wrong". | `TracearrService.cs` (deleted methods) |
| 26 | ~~Morgue aggregate truncated far worse than the cap suggests.~~ **Withdrawn** — the 847-sessions-per-week measurement it rested on was finding 27's ignored parameter returning all-time totals. A real week is 16. Its open question ("try `stats`/`users` before enlarging the cap") was answered: neither carries item identity, so `history` is the only possible source. Full write-up below. | — |
| 27 | **`weeksBack` is not a Tracearr parameter.** `history` takes `startDate`/`endDate`/`timezone`; unknown keys are ignored, not rejected. So the Morgue aggregate walked *all* history while asking for 52 weeks, and the Guestbook's timeframe dropdown never narrowed anything. Fails **open** with a 200, unlike findings 1/24/25. | `TracearrService.cs:135`, `:158` (pre-fix) |

### P1 — correctness / semantics

| # | Finding | Location |
| --- | --- | --- |
| 8 | Morgue is not zero-play (sorts viewers→plays→size, takes `limit`), and the morgue table omits the Plays column, so a played item is indistinguishable. Header total and table contents describe different sets. Resolved by **D1**. | `AnalyticsService.cs:156`, `:161-165`; `dashboard.html:139-152` |
| 9 | **Title-collision dedupe destroys items.** `GroupBy(name.ToLower()).Select(g => g.First())` — "The Thing" (1982) and (2011) collapse to one; size undercounted. Applied in Morgue only, so tabs report different library totals. | `AnalyticsService.cs:73-76` |
| 10 | **Two divergent `FormatBytes`.** 5 suffixes / `0.##` / integer-divides mid-loop losing precision / **`IndexOutOfRangeException` at ≥1 PB** (loop guard tests `i < Length` pre-increment) — vs 7 suffixes / 1 decimal. Same number renders differently per tab. | `AnalyticsService.cs:171-181`; `TracearrService.cs:191-198` |
| 11 | **Guestbook has no row cap.** No `LIMIT`; 12 weeks on a busy server serializes tens of thousands of sessions into one JSON blob. Also mixes locally-parsed dates with a `DateTime.UtcNow` fallback. | `Repository.cs:191-209`; `AnalyticsService.cs:425-430` |
| 12 | **Tracearr paging unbounded and uncached.** Walks every history page sequentially, 52 weeks, on every tab switch and every debounced keystroke. No cap, no cache, no `CancellationToken`. | `TracearrService.cs:208-273` |
| 13 | **Tracearr path loses Guestbook features.** UI hides `visitorSummary`, so leaderboard and Ghosts vanish when Tracearr is on. Controller returns raw Tracearr JSON as `object`, so the UI sniffs `!!response.data` to pick a renderer. | `dashboard.html:418-430`; `GraveyardAnalyticsController.cs:325` |
| 14 | **`Take(limit)` applied after full mapping.** `limit=10` still maps the entire library and runs `GetRecursiveChildren` on every series. | `AnalyticsService.cs:164`, `:297`, `:416` |
| 15 | **Weakest validation on the most destructive endpoint.** `LastRites` uses `GetItemById(string)` while Condemn/Pardon both `Guid.TryParse`. Deletes files from disk; only guard is a browser `confirm()`. | `GraveyardAnalyticsController.cs:129` |
| 16 | `UpdateItemAsync(item, parentItem!, …)` with a possibly-null parent. Use `item.GetParent() ?? RootFolder`. | `GraveyardAnalyticsController.cs:168`, `:294` |

### P2 — architecture / build / hygiene

- **Service-locator anti-pattern.** `new AnalyticsService(Plugin.Instance.Repository, …, Plugin.UserDataManager, …)` repeated 4×; the registrator registers only `HttpClient`. Untestable. `IUserDataManager` is injected and never used. — `controller:85,100,115,340`; `Plugin.cs:67-73`
- **`Plugin.Repository` lazy getter is not thread-safe** — concurrent requests double-run `DatabaseInitializer` and `Directory.CreateDirectory`. — `Plugin.cs:30-40`
- **Dead code:** `AdvancedAnalytics.db` + both its tables + `PlaybackEvent` (never read or written, and written to `plugins/configurations` rather than a data path); `Repository.GetWatchedMediaIds` / `GetOverallStats` / `GetActivityTimeline` / `GetAllActiveUserIds` / `GetWatchedMediaIdsByUser`; `TracearrService.GetStaleMediaAsync` / `GetStaleMediaAlignedAsync`; `VisitorSession.Client`.
- **`ex.Message` returned to the client in 8 places** — leaks filesystem paths. Several `catch` blocks never log. — `controller:90,105,120,146,272,312,330,346`
- **csproj:** `Microsoft.AspNetCore.Mvc.Core 2.2.5` is EOL and redundant (Jellyfin.Controller brings the framework reference); `Microsoft.Data.Sqlite 8.0.8` on `net9.0` conflicts with Jellyfin's own SQLitePCLRaw and should be `<Private>false</Private>`. No `AssemblyVersion`/`FileVersion`, no analyzers, no `.editorconfig`, no `.sln`.
- **`buiild.yaml`** — filename typo (tooling expects `build.yaml`), and `artifacts` lists the stale `JellyfinAnalyticsPlugin.dll` while `release.sh` ships `JellyfinGraveyardAnalyticsPlugin.dll`.
- **`release.sh` is macOS-only** (`md5 -q`; Linux needs `md5sum`). Manifest checksum and timestamp are hand-edited → drift. — `release.sh:36`
- **No CI, no tests.** `.github/` absent.
- **Repo bloat:** 4 committed `.dll` under `Releases/`, tracked `.DS_Store`, tracked `.idea/workspace.xml`.
- **Condemn downloads collection art from `raw.githubusercontent.com` at runtime** — hardcoded URL, no timeout, breaks offline. — `controller:206`, `:237`
- **`dashboard.html`: 651 lines, nearly every element inline-styled, three hand-duplicated `<thead>` blocks, all state on `window.*`.** Any column change means editing three places. — `dashboard.html:120-186`
- `LeastWatchedResponse.TotalWastedSize` is reused as "living total size"; the UI relabels it.
- README build instructions are wrong (no root project or sln). — `README.md:87-90`

---

## Phases

Each phase is independently shippable. Stop anywhere.

### Phase 0 — Build baseline *(prerequisite)* — **DONE 2026-07-30**

`dotnet build` and confirm restore of `Jellyfin.Controller 10.11.*-*`. Local SDK
is dotnet 10.0.200; the csproj targets `net9.0`. Verify P0 items 1, 3, 5, 6
against a live server where practical. No behavior change.

**Done when:** clean build, and the P0 claims are either confirmed or struck
from this plan.

#### Phase 0 results

**Build:** `dotnet build -c Release` → **succeeded, 0 warnings / 0 errors**.
`Jellyfin.Controller 10.11.*-*` and `Jellyfin.Model` restored; `net9.0` output
at `bin/Release/net9.0/JellyfinGraveyardAnalyticsPlugin.dll`.

> **Correction (Phase 3).** That baseline was **not reproducible from a clean
> checkout** and the claim above was too strong. `git archive HEAD` into an empty
> directory plus `dotnet build -c Release` failed:
> `AnalyticsService.cs(429,41): error CS1061: 'IUserManager' does not contain a
> definition for 'Users'`. The floating `10.11.*-*` now resolves to **10.11.11**,
> which removed that member; every local build passed only because
> `obj/project.assets.json` still pinned **10.11.6**. The reference is now pinned
> to `10.11.6` in the csproj (pulled forward from Phase 6) and a clean checkout
> builds `0 warnings / 0 errors`. Phase 6 decides whether to move to the newer
> API. `AnalyticsService.cs:429` is pre-existing code, untouched by any phase
> here.

**No live server.** No Jellyfin process, no listener on 8096/8920, no Jellyfin
data dir, and only the `10.0.4` runtime is installed (no `net9.0` runtime), so
the plugin cannot be *loaded* locally — only built. Claims 1 and 6 were
therefore verified by reading the cited lines; claims 3 and 5 were reproduced
with a standalone probe (`Microsoft.Data.Sqlite 8.0.8`, ASP.NET Core MVC), since
both turn on library behavior rather than on plugin logic.

| # | Verdict | Evidence |
| --- | --- | --- |
| 1 | **CONFIRMED** | `:31` builds `/api/v1/public/{endpoint}`; `:213` passes `"public/history?..."` → `/api/v1/public/public/history`. The other callers pass bare paths (`:68` `system/status`, `:82` `history?...`, `:96` `media/stale?...`). All three media tabs route through `GetPlaybackStatsAsync` → `BadRequest`; no `.catch` in `fetchAndRenderTable`. |
| 3 | **CONFIRMED** | Probe: `Mode=ReadOnly` on a missing file throws SQLite error 14 and creates nothing; the string actually used at `:28` (no `Mode=`) **opened, created an 8192-byte `playback_reporting.db`, and accepted `CREATE TABLE` + `INSERT`**. So the live handle is read-write and a missing Playback Reporting DB is silently manufactured. |
| 5 | **BYPASS STRUCK; rest confirmed** | Probe replicated `[FromBody] payload, [FromQuery] string token` against a configured key of `string.Empty` under both `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` settings. `?token=`, `?token=%20`, and no query → **400** (implicit-required) or **401** (`token` bound to `null`, suppressed) — never 200. `ConvertEmptyStringToNull` is on by default, so no request can make `token` equal `""`; the endpoint fails closed. The `[AllowAnonymous]` stub returning a lying `200`, the query-string token, and the non-fixed-time compare all stand. |
| 6 | **CONFIRMED** | `:87` `PlayDuration >= 120`; `:112` `PlayDuration > 300`; `:143` and `:171` no duration filter at all. Exactly the split described in D2. |

Probe caveat: it ran on ASP.NET Core 10.0.4, not the 9.x Jellyfin 10.11 ships
on. The two behaviors it relies on (`ConvertEmptyStringToNull`, implicit-required
for non-nullable reference types) are unchanged across 9 and 10, and the strike
holds under both branches of the one MVC option that could differ.

### Phase 1 — Security — **DONE 2026-07-30**

1. `dashboard.html` — replace all `innerHTML` row construction with
   `createElement` + `textContent`; move `onclick=` to `addEventListener` +
   `dataset.id`. *(fixes 4)*
2. `TracearrController` — reject when `TracearrApiKey` is empty; move the token
   to a header; `CryptographicOperations.FixedTimeEquals`. Either implement the
   condemn call or return `501` instead of a lying `200`. *(fixes 5)*
3. Controllers — generic client messages, log `ex` server-side.
4. `LastRites` — `Guid.TryParse`, and require the item to carry `[Chapel]`
   before deleting. *(narrows 15)*

**Done when:** a media title containing `<img onerror=alert(1)>` renders as
literal text; `?token=` on a fresh install returns 401; no response body
contains a filesystem path.

#### Phase 1 results

Clean build (0 warnings). All three done-when criteria were exercised, not just
read:

1. **XSS.** `dashboard.html` builds every row with `createElement` +
   `textContent` (helpers `td` / `span` / `subLine` / `emptyRow` / `clear` /
   `actionButton`); zero `innerHTML` remains in the file. Row action buttons
   carry `dataset.id` / `dataset.name` and bind via `addEventListener`.
   A jsdom harness drives the *real* render functions with
   `<img src=x onerror=alert(1)>` and `Evil" onmouseover="alert(2)` as the media
   title, Tracearr `username` / `mediaTitle` / `device` / `player`, and the local
   leaderboard / ghosts / session fields: **17/17 pass**. The same harness
   against the `71a01f7` file **fails 12** — including
   `attrs=onclick,onmouseover,class,style` on an action button, i.e. the
   attribute breakout at old `:569` demonstrably injects a live event handler.
   (Caveat: jsdom does not load `img src=x`, so `onerror` never fires there; the
   proof of execution is the injected `onmouseover`, and the injected `<img>`
   nodes themselves.)
   A second harness clicks the rebuilt buttons — **9/9 pass**: Condemn / Pardon /
   Exorcise reach `POST GraveyardAnalytics/{Condemn,Pardon,LastRites}/{id}`,
   `confirm()` still shows the raw title, column counts hold (morgue 5, others 9),
   and a `0` value renders as `"0"` rather than blank (the helper tests
   `!== null && !== undefined`, not truthiness).
2. **Webhook.** Token moved to an `X-Tracearr-Token` header, empty/whitespace
   `TracearrApiKey` now rejects outright, comparison is
   `CryptographicOperations.FixedTimeEquals`, and the stub answers **501** with
   `status: "NotImplemented"` instead of a lying `200 Condemned`. Probe (same
   harness as Phase 0): fresh install → **401** with no header, an empty header,
   or any header; key set → 401 for no header / wrong header / a *prefix* of the
   key; correct header → 501; and the correct key passed only as `?token=` →
   **401**, so the old query-string vector is closed. `payload` is now nullable,
   so a missing or empty body reaches the auth check first — but malformed JSON
   still returns 400 and a wrong content-type 415 from the framework *before* the
   action runs. Neither leaks anything; the earlier blanket claim that
   "authentication runs before model validation" was too broad.
3. **Error bodies.** All eight `ex.Message` returns are gone. Every failure path
   logs the exception server-side and returns a literal
   (`GenericFailure = "The request failed. Check the Jellyfin server log for
   details."`). The one message still forwarded to the client is
   `PlaybackDataUnavailableException` — a new typed exception whose text is
   written for the admin UI and carries no path, introduced so that
   "Playback Reporting is not installed" survives the generic-message change.
   Error bodies are now uniformly `{ message }` objects rather than bare strings.
4. **LastRites** now `Guid.TryParse`s its id (it was the last endpoint taking a
   raw string) and refuses any item that does not carry `[Chapel]`, so a delete
   from disk requires a prior Condemn.

Not done here, deliberately: the six static `onclick="window.switchTab(...)"` /
`savePluginConfig()` attributes in the markup interpolate nothing and are left
for Phase 7's rewrite. `[AllowAnonymous]` stays on the webhook by design — that
is what the key authenticates.

#### Phase 1 review round

An independent review of the diff caught a real miss and five smaller items. All
fixed in-phase:

- **`LeastWatchedItem.Path` was serialized on every media row** —
  `/LeastWatched`, `/Living` and `/Purgatory` each shipped absolute media paths
  in their **success** bodies, so done-when criterion 3 was not actually met.
  This phase had only checked error bodies. The property and its four
  assignments (`AnalyticsService.cs` ×3, `TracearrService.cs` ×1) are deleted —
  the dashboard never read it. Verified by reflecting over the built assembly:
  the DTO now serializes 11 properties, no `Path`, no path text anywhere.
  **A new finding worth carrying forward: success bodies leak too, not just
  error bodies. Audit any DTO added in later phases.**
- **`GetVisitors` had an unguarded prologue** — the config read and the
  `Plugin.Instance.Repository` construction (which does `CreateDirectory` +
  `DatabaseInitializer.Initialize`) sat outside the `try`, so a read-only or
  broken data path escaped to Jellyfin's exception middleware. Whole body is
  inside the `try` now.
- **`ex.Message` could still reach the client one indirection away** — the three
  `PlaybackDataUnavailableException` catches echoed `ex.Message`, safe only by
  convention. They now return a `PlaybackUnavailableMessage` literal; the
  exception is `sealed` and its text is log-only.
- **The webhook ignored `EnableTracearr`** — `savePluginConfig` persists the key
  even when the engine is switched off, so an admin-invisible authenticated path
  survived the toggle. Now rejected first, before the key check. Probe:
  engine off + correct header → **401**.
- Added a class-level `[Authorize(Policy = "RequiresElevation")]` to
  `TracearrController`, making the webhook's `[AllowAnonymous]` an explicit
  exception rather than the ambient default for any action added later.
- Documented the `X-Tracearr-Token` contract in `README.md` — the move off
  `?token=` is a breaking change with no other discoverable record.

Reviewed and **not** acted on: header duplication returning 401 (correct
fail-closed posture); no rate limit on the anonymous webhook (moot while it is a
501 stub); and Condemn assigning `item.Tags` before `UpdateItemAsync` succeeds,
which can leave a tagged in-memory item after a failed persist — that is
**finding 16**'s `parentItem!` null bug and belongs to its own fix, not here.

### Phase 2 — Fix the broken Tracearr path

5. Drop the `public/` prefix at `TracearrService.cs:213`. *(fixes 1)*
6. Page cap + total-page sanity check on the paging loop; thread
   `CancellationToken` through `SendTracearrRequestAsync` and both controllers.
   *(fixes 12)*
7. Normalize `GetVisitorHistoryAsync` into `VisitorResponse`, building
   leaderboard and ghosts from Tracearr rows. Removes the `!!response.data`
   sniff and restores the two summary cards. *(fixes 13)*
8. Delete `GetStaleMediaAsync` and `GetStaleMediaAlignedAsync`.

**Done when:** with Tracearr enabled, all three media tabs populate, and the
Guestbook shows leaderboard + Ghosts.

Also fix **finding 24** here — it is the same bug as item 5 wearing a different
endpoint name, and it is what makes the Settings page lie about the connection.

#### Phase 2 results — **complete (2026-07-30)**

All four items done, plus findings 24, 25 and 27. Finding 26 was raised here and
withdrawn here. The done-when criterion is met in code and verified as far as it
can be without a Jellyfin server: the response shape, the mapping, and the live
query are all covered by harnesses, but "all three media tabs populate" itself
has never been observed in a running Jellyfin — that gap is Phase 6's.

A live Tracearr is available at `http://10.10.1.201:3000`, which turns most of
this phase from inference into measurement.

**Finding 1 confirmed against the real server:**

```
GET /api/v1/public/public/history  -> 404      <- what the plugin requests today
GET /api/v1/public/history         -> 401      <- endpoint exists, wants a key
    {"statusCode":401,"error":"Unauthorized","message":"Missing or invalid Authorization header"}
```

A 404 on the doubled path and a 401 on the corrected one is exactly the
signature finding 1 predicted.

- **Item 5 done.** The paging loop now requests the bare `history?...` path.
- **Item 6 done.** `MaxHistoryPages = 40` cap; hitting it logs a warning naming
  the pages available and the weeks requested, so truncation is never silent.
  Page-count parsing moved into `ReadTotalPages`, which prefers an explicit
  `totalPages`, tolerates a missing/zero/non-numeric `pageSize` instead of
  dividing by it, and never returns less than 1. `CancellationToken` is threaded
  through `SendTracearrRequestAsync`, `TestConnectionAsync`,
  `GetVisitorHistoryAsync`, `GetTracearrPlaybackStatsAsync` (checked once per
  page) and all five controller actions.
- **Item 8 done.** `GetStaleMediaAsync` and `GetStaleMediaAlignedAsync` deleted
  — both callerless, and both aimed at the nonexistent `media/stale`
  (finding 25). Their removal orphaned `TracearrService.FormatBytes`, which is
  gone too, so **half of finding 10 is already resolved**: only
  `AnalyticsService.FormatBytes` remains, and Phase 3 item 11 now has one
  implementation to fix rather than two to reconcile.
- **Finding 24 fixed.** The connection test now probes
  `history?weeksBack=1&page=1` — the endpoint the plugin actually depends on —
  and reports *why* it failed instead of always blaming the API key:
  `NotConfigured` / `Unreachable` / `Unauthorized` / `UnexpectedResponse`.
  Request building moved into `BuildRequest` so the probe and the data path
  cannot drift apart again. Verified live: the new endpoint returns **401** with
  a bogus key (→ "reachable but rejected the API key"), while `system/status`
  returns **404** with or without a key (→ `UnexpectedResponse`), which is why
  the old test could never report success.
##### Tracearr live payload reference *(measured 2026-07-30)*

`GET {TracearrUrl}/api/v1/public/history?...`, header
`Authorization: Bearer <trr_pub_...>`. Ask the user for a key; it is not stored.

```
meta : {"total": 847, "page": 1, "pageSize": 25}      <- no totalPages field
```

**`GET /api/v1/public/docs` returns the full OpenAPI document** (200 with a key,
26 KB). That is the source of truth for this section and should be re-read before
any further endpoint guessing. The entire public surface is:

| endpoint | parameters | use to us |
| --- | --- | --- |
| `health` | — | unauthenticated liveness |
| `stats` | `serverId` | `activeStreams` / `totalUsers` / `totalSessions` (last 30d) / `recentViolations` — **server totals, no per-item anything** |
| `stats/today` | `serverId`, `timezone` | today's plays / watch hours — same shape of no use |
| `streams` | `serverId`, `summary` | *active* sessions only |
| `streams/{id}/terminate` | — | POST |
| `users` | `page`, `pageSize`, `serverId` | per-user `sessionCount`, `lastActivityAt`, `trustScore` |
| `violations` | `page`, `pageSize`, `serverId`, `severity`, `acknowledged` | — |
| `history` | `page`, `pageSize`, `serverId`, `state`, `mediaType`, **`startDate`**, **`endDate`**, **`timezone`** | the only per-session source |

**There is no `weeksBack` parameter anywhere — see finding 27.** The window is
`startDate` / `endDate` (day-granular, expanded to start/end of day in
`timezone`), and an unknown query key is ignored rather than rejected.

`pageSize` maxes at **100** — the documented ceiling. 101 and above return
**400**, which is what the earlier "500 and 1000 return something unparseable"
note was actually seeing. Paging verified: 9 pages × 100 = the 847 total.
Inverted ranges (`startDate > endDate`) and unknown IANA zones are both 400.

Row fields relevant to mapping (the guesses in the existing code were all
**correct**, with one type surprise):

| field | example | note |
| --- | --- | --- |
| `user` | `{id, username, thumbUrl, avatarUrl}` | `username` → `Visitor` / leaderboard key |
| `mediaTitle` | `"The Conjugal Conjecture"` | episode title |
| `showTitle` | `"The Big Bang Theory"` | null for movies |
| `mediaType` | `"episode"` | lowercase; `"movie"` also occurs |
| `thumbPath` | `"/Items/426b0f74a4a4f19e65783d9e7b5ff4ea/Images/Primary"` | `Split('/')[2]` is the **dash-less** Jellyfin id — exactly the `ToString("N")` form the aggregates key on |
| `startedAt` / `stoppedAt` | `"2026-07-29T22:33:30.278Z"` | ISO 8601 UTC |
| `durationMs` | `689932` | **number** |
| `progressMs` | `"674554"` | **string** |
| `totalDurationMs` | `"1312416"` | **string** |
| `watched` | `false` | bool |
| `isTranscode` | `false` | bool |
| `videoDecision` | `"directplay"` | lowercase |
| `device` / `player` / `product` / `platform` | `"Android TV"` / `"Living Room TV"` | `device`+`player` fill the Vessel cell |
| `state` | `"stopped"` | |

The two `*Ms` strings are why any C# mapping must tolerate string-or-number
rather than calling `GetInt64()` directly; the dashboard already `parseInt`s them.

**Ghosts — RESOLVED (2026-07-30): Jellyfin's user list, not Tracearr's.**
`users` was probed to settle it. Tracearr's usernames are Jellyfin's, verbatim —
each row carries the Jellyfin user id inside `thumbPath`
(`/Users/aaa36…/Images/Primary`), so the namespaces are one namespace and
matching by username is sound. Tracearr's own list is still the wrong source:
`sessionCount` is **0 for all 17 users** while ten of them have a non-null
`lastActivityAt`, so the field that looks like the ghost test is not populated.
More importantly a ghost means "has an account on *this* Jellyfin server and
watched nothing" — Tracearr is multi-server (every row carries `serverId` /
`serverName`; this instance tracks one, `Entertainment`), so its user list
answers a different question. Ghosts are therefore derived in the controller as
Jellyfin users minus the usernames present in the returned sessions. Known
limitation, recorded in the code: a Tracearr-side rename shows the old name as a
ghost.

- **Item 7 done.** `GetVisitorHistoryAsync` returns `VisitorResponse` instead of
  `object?`, so the dashboard's `!!response.data` sniff is gone along with the
  duplicate table it selected — one table now serves both engines and the two
  summary cards are back for Tracearr. **Every field guess in the old code was
  correct**; the type guesses were not, which is the whole reason this needed
  live payloads: `durationMs` is a number while `progressMs` and
  `totalDurationMs` are strings *on the same row*, so all integer reads go
  through a string-or-number `ReadInt64`. The Fate column survived as an optional
  per-row cell: `VisitorSession` gained nullable `ProgressPercent` / `Watched`,
  which Tracearr fills and the local engine leaves null (Playback Reporting
  records elapsed time but not item runtime), and a null renders as a dash rather
  than a guessed verdict. Paging is bounded by `GuestbookRowLimit` as well as the
  page cap, and `Truncated` now means the same thing on both paths.
- Evidence: `tests/harness/dotnet/visitormap` — reflection over the built
  assembly, driving `MapSession` with the verbatim live row above, then firing
  the generated query at the real server (21 checks + 5 live). The jsdom
  `xss.test.mjs` was rewritten to the single response shape, 31/31.

### Phase 3 — Make the numbers correct

9.  `MinPlayDurationSeconds` (default 120) applied to **all four** aggregates.
    *(D2, fixes 6)*
10. Hoist `GetItemPlayDurations()` out of the `Select` in `GetPurgatoryItems`;
    remove the duplicate episode fetch at `:227-238` (query *or*
    `GetRecursiveChildren`, not both). *(fixes 2)*
11. One shared `FormatBytes`; fix the `i < Length` overflow; drop the mid-loop
    integer division. *(fixes 10)*
12. Morgue = `PlayCount == 0 && DateCreated <= UtcNow - effectiveGrace`. Add
    `MorgueGraceDays` (default 180) and `Repository.GetHistoryFloorDate()`;
    clamp grace to coverage; return `coverageDays`. Dashboard gets the coverage
    banner and the "include barely-touched" toggle. *(D1, fixes 8)*
13. Remove the title `GroupBy`, or key it on `(Name, ProductionYear, Type)`.
    Apply one search + dedupe rule across all three methods. *(fixes 9)*
14. `GetRawPlaybackActivity` — config-backed `LIMIT` (~5000) plus a `truncated`
    flag; UTC end-to-end. *(fixes 11)*

**Done when:** no row can show `Plays > 0 / Reach 0` or `Last Breath` set with
`Plays 0`; `Total Dead Weight` equals the sum of displayed rows; a fresh
Playback Reporting install shows the coverage banner instead of the whole
library.

#### Phase 3 results

Clean build, 0 warnings, and reproducible from a clean checkout for the first
time (see the Phase 0 correction above).

- **Item 9 done (D2).** `MinPlayDurationSeconds` is a parameter on all four
  aggregates, passed as a Dapper parameter rather than interpolated. The 120/300
  split is gone and the two unfiltered queries — last-played and durations — now
  filter too, which is what allowed `Plays 3 / Reach 0` and
  `Last Breath: yesterday / Plays 0`.
- **Item 10 done.** `GetItemPlayDurations()` is hoisted out of the `Select`
  lambda in `GetPurgatoryItems` (it was a full-table `SUM…GROUP BY` re-running
  once per Chapel item). The duplicate episode fetch is gone: the method ran
  *both* an `InternalItemsQuery` and `GetRecursiveChildren` and then mixed them —
  size and plays from one list, durations from the other. One list now, filtered
  on `BaseItemKind.Episode`.
- **Item 11 done.** One `FormatBytes`, now `public static`. Verified against the
  built assembly next to the old implementation copied from `71a01f7`:

  | input | old | new |
  | --- | --- | --- |
  | 1 TB | `1 TB` | `1 TB` |
  | **1 PB** | **`IndexOutOfRangeException`** | `1 PB` |
  | 1 EB | `IndexOutOfRangeException` | `1 EB` |
  | `long.MaxValue` | `IndexOutOfRangeException` | `8 EB` |
  | -2048 | `-2048 B` | `-2 KB` |

  Identical output from 0 B through 5 TB, so the fix is not a reformat. Scaling
  happens in `double` (no mid-loop integer division) and the suffix index can no
  longer leave the array.
- **Item 12 done (D1).** Morgue is `PlayCount == 0 && DateAdded <= UtcNow -
  effectiveGrace`, with `MorgueGraceDays` (default 180) clamped by
  `Repository.GetHistoryFloorDate()`. `Total Dead Weight` is the sum of the rows
  actually returned, so header and table finally describe one set. The response
  carries `CoverageDays`, `EffectiveGraceDays`, `ConfiguredGraceDays` and
  `UnverifiableItemCount`; the dashboard renders a banner from them and gained
  the "Include barely-touched (≤ 2 plays)" toggle, plus a **Plays column in the
  Morgue table** — with the toggle on, a played row would otherwise be
  indistinguishable from a zero-play one.
- **Item 13 done.** The `GroupBy(name.ToLower())` that collapsed
  The Thing (1982) into The Thing (2011) is replaced by a key of
  `(Name, ProductionYear, BaseItemKind)`. Query construction and
  search+dedupe are now two shared helpers (`BuildMediaQuery`,
  `ApplySearchAndDedupe`) used by all three media views, which also fixes the
  Sanctuary silently not applying `SearchTerm` to its query.
- **Item 14 done.** `GetRawPlaybackActivity` takes a `GuestbookRowLimit`
  (default 5000, clamped 100–50000), fetches one row past the cap to detect
  truncation, and returns a `Truncated` flag that the Guestbook surfaces as a
  notice saying the leaderboard and ghosts cover only the returned rows. The
  window is UTC end to end — bounds were being formatted from local time against
  naive-UTC storage, shifting every query by the server's offset.

Config keys added: `MinPlayDurationSeconds`, `MorgueGraceDays`,
`GuestbookRowLimit`, each clamped in its setter. **Deviation from the config
table:** `MinPlayDurationSeconds` has an effective range of 1–3600, not 0–3600.
A config written before the key existed deserializes as `0`, indistinguishable
from a deliberate zero, and honouring it as "no floor" would silently restore
the unfiltered aggregates this setting exists to fix.

Dashboard suites: **22/22** and **9/9**, now covering the coverage banner in all
three states and the widened Morgue row.

#### D1 — RESOLVED: floor gate replaces the clamp *(decided 2026-07-30)*

The clamp is **removed**. `MorgueGraceDays` is applied as configured, and a
candidate must additionally have been added *after* playback history begins:

```
item is in the Morgue  <=>  PlayCount == 0
                        AND DateCreated <= UtcNow - MorgueGraceDays
                        AND DateCreated >= historyFloor      // unless opted in
```

Rationale: under a floor gate the clamp is redundant — an item added after the
floor is by construction younger than coverage, so `min(grace, coverage)` almost
never binds — and the clamp was actively harmful, admitting *more* unverifiable
items the less history existed. The list feeds Condemn → Exorcise, which deletes
files, so a false positive risks someone's favourite film while a false negative
only costs disk. That asymmetry sets the default.

The cost is explicit rather than hidden: on a young Playback Reporting database
the default Morgue is sparse, because nothing added after the floor is yet
`MorgueGraceDays` old. A second checkbox — *"Include unverifiable (older than
history)"*, default off — shows the withheld set, and those rows are marked
**"unverified — predates history"** individually, not just in the banner. The
banner always states coverage, the grace period in force, and how many items are
withheld or included.

Response fields: `CoverageDays`, `GraceDays`, `UnverifiableCandidateCount`,
`IncludingUnverifiable`, `HistoryFloorUtc` (the last so the UI can mark rows).
`EffectiveGraceDays` / `ConfiguredGraceDays` / `UnverifiableItemCount` are gone
with the clamp. Suites: **26/26** and **9/9**.

<details>
<summary>The contradiction that prompted the change (kept for the record)</summary>

`effectiveGrace = min(MorgueGraceDays, coverageDays)` is implemented as locked,
but **the formula works against its own stated purpose** and I did not want to
quietly "fix" a locked decision.

D1 introduces the clamp to stop a young database flooding the Morgue with the
pre-install library. But shrinking the grace period *loosens* the age test, so
less history admits **more** items, not fewer:

| history coverage | effective grace | items needing to be older than | effect |
| --- | --- | --- | --- |
| 400 days | 180 | 180 days | intended |
| 20 days | 20 | **20 days** | nearly the whole library qualifies |
| 0 days | 0 | **now** | *everything* qualifies |

Two mitigations are in place, neither of which is the missing rule:

1. Zero coverage returns an **empty** Morgue with an explanatory banner, because
   returning the entire library there is indefensible.
2. `UnverifiableItemCount` counts returned rows added before the history floor,
   and the banner discloses it.

The rule that would actually match D1's rationale is a floor gate —
`DateCreated >= historyFloor`, i.e. only judge items the history could have
observed — either replacing the clamp or alongside it. That inverts which items
appear on a young database, so it is a product call, not a refactor.

</details>

### Finding 26 (P0) — ~~the Tracearr Morgue aggregate is truncated far worse than the cap suggests~~ **WITHDRAWN 2026-07-30 — the volume was an artifact of finding 27**

**This finding was wrong, and the way it was wrong is the useful part.** It was
built on one measurement, `history?weeksBack=1 -> total: 847`, read as "847
sessions in a single week". `weeksBack` is not a Tracearr parameter (finding 27);
it was ignored, so 847 was the server's **entire history**, not one week's.
Re-measured with the real parameters:

```
history                                          -> total 847   <- all-time
history?weeksBack=1                              -> total 847   <- parameter ignored
history?weeksBack=52                             -> total 847   <- same, proving it
history?startDate=2025-07-30&endDate=2026-07-30  -> total 847   <- a real year
history?startDate=2026-07-23&endDate=2026-07-30  -> total  16   <- a real week
history?startDate=2026-07-29&endDate=2026-07-30  -> total   2
```

A week is **16 sessions, not 847** — off by 53×. The corrected arithmetic: a year
is 847 rows, **9 pages** at the maximum page size of 100, against a 40-page cap
worth 4,000 rows. Nothing was being truncated on this server; the "cap covers
about ten days" conclusion does not survive, and neither does the claim that
items were reading as zero-play because of it.

What *is* real, and is what the bad measurement was standing in front of: the
aggregate never applied a window at all, so it walked the whole history every
time regardless of the 52 weeks it thought it had asked for. That is finding 27,
and fixing it fixes the volume problem by construction — the cap is now a
backstop rather than the binding constraint.

**The recommendation this finding carried — "try `stats`/`users` before enlarging
the cap" — was executed, and the answer is no.** Both endpoints were probed with
a working key and neither can feed the Morgue:

```
stats -> {"activeStreams":0,"totalUsers":17,"totalSessions":130,"recentViolations":0,...}
users -> per-user {sessionCount, lastActivityAt, trustScore, serverId, ...} x 17
```

`stats` is a dashboard summary — four server-wide counters, no per-item breakdown
and no date window beyond its fixed "last 30 days". `users` aggregates by *user*,
not by media item. The Morgue needs plays-per-item, and **`history` is the only
endpoint in the entire public API that carries item identity**. There is no
aggregate-side query to move to; paging raw history is not a workaround, it is
the only option the API offers. Phase 4's TTL cache is therefore the right lever
after all, and it is now sitting on ~9 pages rather than an unbounded walk.

**Superseded by finding 27. Nothing here blocks the Tracearr engine for the
Morgue any more** — that gate was raised on measurements that turned out to be an
artifact of the very bug it was measuring.

### Finding 27 (P0) — `weeksBack` is not a Tracearr parameter, so no history request has ever been windowed

Every `history` call the plugin makes sends `weeksBack`. Tracearr's history
endpoint takes `startDate` / `endDate` / `timezone` and **has no `weeksBack`**;
unknown query keys are dropped rather than rejected, so each request returned a
healthy 200 while the parameter meaning "how far back to look" fell on the floor.

Two consequences, both silent:

1. **The Morgue aggregate** asked for 52 weeks and got *everything*, on every tab
   switch and every debounced keystroke. On a young server that is
   indistinguishable from working; on an old one it is an unbounded walk that the
   page cap then truncates arbitrarily — which is exactly what finding 26 mistook
   for a volume problem.
2. **The Guestbook** sent `endDate` (which *is* real) with no `startDate`, so the
   timeframe dropdown never narrowed anything — 1 week and 12 weeks returned the
   same rows, everything up to the end date. Confirmed:
   `history?endDate=2026-07-30 -> total 847`, versus 16 for the same week with a
   `startDate`.

This is the third endpoint-contract bug in this family (findings 1, 24, 25) and
the first one that fails *open* — the others 404'd loudly, this one returns 200
and quietly answers a different question. Static reading could not have caught
it: the parameter name is plausible and the response is well-formed.

**Fixed.** Both callers now go through one `BuildHistoryEndpoint(start, end, page)`
that emits `startDate` / `endDate` / `timezone=UTC` / `pageSize=100`, so the two
paths cannot drift apart again. `timezone` is pinned to UTC because the local
engine bounds its window in UTC and the same timeframe must not return different
sessions depending on which engine is enabled. `TestConnectionAsync` also stopped
sending the phantom parameter — it now probes `history?page=1&pageSize=1`, which
is one row instead of a page, and still gives 200 with a good key / 401 with a
bad one. Verified end to end: the query string produced by the built assembly was
fired at the live server and returns 16 rows, all inside the requested window
(`tests/harness/dotnet/visitormap`).

### Phase 4 — Performance

15. Extract a `PlaybackStatsProvider` with a short TTL cache (~60s) over the
    four aggregates and the Tracearr paging; invalidate on Condemn / Pardon /
    LastRites. *(12, 14)*
16. Filter and sort *before* the expensive `GetRecursiveChildren` mapping so
    `limit=10` maps 10 items, not the library. Cache per-series size per
    request. *(fixes 14)*
17. Filter series children to `BaseItemKind.Episode` instead of `Path != null`.

**Done when:** a debounced keystroke issues no new SQL inside the TTL window,
and `limit=10` no longer walks every series.

### Phase 5 — Structure

18. Register `Repository` and `AnalyticsService` in
    `GraveyardServiceRegistrator`; inject into the controller. Drop
    `Plugin.Instance` static access and the unused `IUserDataManager`. If any
    static access must remain, make `Plugin.Repository` a `Lazy<Repository>`.
19. Delete `AdvancedAnalytics.db`, `DatabaseInitializer`, `PlaybackEvent`, the
    five unused `Repository` methods, and `VisitorSession.Client`. Replace
    Dapper `dynamic` with typed DTOs.
20. Rename `TotalWastedSize` → `TotalSize`; add a distinct `TotalWasted`.

### Phase 6 — Build & release

21. csproj: drop `Mvc.Core 2.2.5`; mark `Microsoft.Data.Sqlite` non-private;
    add `AssemblyVersion` / `FileVersion`; add `.editorconfig` and analyzers.
22. Rename `buiild.yaml` → `build.yaml`; correct its `artifacts` DLL name.
23. `release.sh` — portable checksum (`md5sum || md5 -q`); auto-patch
    `manifest.json` (version, checksum, timestamp) instead of hand-editing.
24. Add `.github/workflows/build.yml` (restore + build, warnings as errors) and
    a release workflow. Add an xUnit project — a suite over `FormatBytes`, the
    threshold filter, and the grace clamp is exactly what would have caught
    findings 6 and 10.
25. `git rm --cached` the four `Releases/*.dll`, `.DS_Store`, and
    `.idea/workspace.xml`; extend `.gitignore`. Fix README build instructions.
    Embed the two Chapel PNGs as resources, removing the runtime GitHub
    dependency.

### Phase 7 *(optional)* — Dashboard rewrite

26. Extract inline styles into the existing `<style>` block as classes; replace
    the three duplicated `<thead>` blocks with one table driven by a per-tab
    column descriptor; module-scoped state instead of `window.*`; register
    listeners once, outside `viewshow` *(fixes 7)*; loading / empty / error
    states with `.catch` on every fetch.

---

## Config surface after Phase 3

| Key | Default | Range | Introduced |
| --- | --- | --- | --- |
| `EnableTracearr` | `false` | — | existing |
| `TracearrUrl` | `""` | — | existing |
| `TracearrApiKey` | `""` | — | existing |
| `MinPlayDurationSeconds` | `120` | 0–3600 | Phase 3 |
| `MorgueGraceDays` | `180` | 30–365 | Phase 3 |
| `GuestbookRowLimit` | `5000` | 100–50000 | Phase 3 |

## Deferred — explicitly out of scope

- Runtime-relative play threshold (`MinPlayFraction`, see D2).
- Series-level engagement rule (one sampled episode currently revives a whole
  show, see D1).
- Implementing the Tracearr condemn webhook body (Phase 1 makes it honest by
  returning `501`; wiring it up is separate work).
- Tracearr automation rules UI — the tab is a placeholder today.
