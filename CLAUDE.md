# Graveyard Analytics — project instructions

Jellyfin plugin (C#, `net9.0`, targets Jellyfin 10.11+). Project lives in
`JellyfinGraveyardAnalytics/`; the admin UI is a single embedded resource,
`WebUI/dashboard.html`.

## Active work

**Read `PLAN.md` before making changes.** It is the locked improvement plan off
commit `71a01f7` — 23 findings with `file:line` refs, two locked decisions (D1
Morgue definition, D2 play threshold), and eight phases with done-when criteria.
Do not re-derive the findings or re-litigate D1/D2.

Current position: **Phases 0-6 done, including the Phase 5 addendum. Only Phase 7
remains** — the optional dashboard rewrite (item 26). Phases run in order. Results
and evidence for each finished phase are recorded in `PLAN.md` under a "Phase N
results" heading — read those before reopening a finding.

Three things carried past Phase 6, all written up in "Phase 6 results":

- **Finding 30's `DateAdded` half is still open.** It comes straight from Jellyfin
  and its `DateTimeKind` cannot be observed here — do not normalize it on a guess.
  The xUnit suite cannot settle it either: it constructs `Movie` objects itself, so
  the `Kind` it sees is the one the test wrote. This needs a running server.
- **`PlaybackDatabaseExists` is `File.Exists` only**, so a database present but
  missing the `PlaybackActivity` table still gives a 500 rather than the actionable
  400. The suite covers the missing-file and empty-table cases, not this one.
- **The `Jellyfin.Controller` pin stays at 10.11.6.** Phase 6 was to decide; the
  decision is no. See the pin note below.

Some findings changed on contact with reality. **Finding 3 belonged to no phase**
and was fixed in Phase 5, since item 19 rewrote the same lines. **Finding 30 was
fixed in the Phase 5 addendum**, at two sites — the review found the local one, and
the Tracearr engine had the same bug in the other direction.
**Finding 5's webhook auth bypass was struck in Phase 0** (empty query values bind to `null`, so the endpoint
failed closed); the rest of finding 5 was real and is fixed. **D1's grace clamp
was replaced by a floor gate** (decided 2026-07-30) after the clamp turned out to
admit *more* unverifiable items the less history existed — see "D1 — RESOLVED" in
`PLAN.md`. Findings 24-27 were added from live measurement against a real
Tracearr server. **Finding 26 was withdrawn on 2026-07-30** — the volume it
described was an artifact of finding 27 (`weeksBack` is not a Tracearr parameter,
so it was ignored and every history request returned all-time totals). Nothing
blocks the Tracearr engine for the Morgue now. Read finding 26's write-up before
re-measuring anything against Tracearr: `GET /api/v1/public/docs` returns the
full OpenAPI document and is the source of truth for that API.

`test-release.sh` at the repo root is **intentionally untracked** — it predates
this work. Do not `git add -A`; stage named paths (it has been swept into a commit
twice that way and removed again).

A live Tracearr is reachable at `http://10.10.1.201:3000` (public API under
`/api/v1/public`, Bearer key). Ask the user for a key when one is needed — do not
store it in the repo.

No Jellyfin server is available locally, and only the dotnet 10.0.x runtime is
installed, so the plugin can be built but **not loaded** here.

**The test suite is `tests/GraveyardAnalytics.Tests` (85 tests, `net10.0`).** Added
in Phase 6, run in CI on every push. It covers `FormatBytes`, D2's play threshold
over a real SQLite file, D1's floor gate through the real `GetLeastWatchedItems`,
the configuration clamps, finding 30's parse and its serialized form, the
`TtlCache`, and the embedded Chapel artwork. Its non-vacuity was checked by
mutating the source, not asserted — if you add to it, do the same.

`tests/harness/` is **separate and still current**: throwaway-grade evidence for
`PLAN.md` findings, kept because several can be pointed at an *old* assembly
(`GRAVEYARD_DLL=…`) to prove a check catches the bug it names. Read
`tests/harness/README.md` before trusting or extending them — the webhook probes
are a *mirror* of controller logic that must be updated alongside it.

Expected results, so a regression is obvious: suite **85**; dashboard `xss` 31,
`actions` 17, `dates` 6; dotnet `repository` 27, `di` 19, `ttlcache` 12,
`visitormap` 21; `formatbytes` and `probes` print tables rather than counts.

The `Jellyfin.Controller` / `Jellyfin.Model` references are **pinned to
10.11.6**. Do not restore the floating `10.11.*-*`: it resolves to 10.11.11,
which removed `IUserManager.Users` and breaks the build from a clean checkout
(`AnalyticsService.cs:429`). Phase 6 considered moving and declined — it is a code
change, not a build one.

## Build and test

```bash
dotnet build JellyfinGraveyardAnalytics/JellyfinGraveyardAnalyticsPlugin.csproj -c Release
dotnet test tests/GraveyardAnalytics.Tests
```

No `.sln`, and the repo root holds no project — `dotnet publish` from the root
fails with `MSB1003`. Local SDK is dotnet 10.x; the plugin targets `net9.0` and the
test project targets `net10.0` (only that runtime is installed here).

**CI builds with `-p:TreatWarningsAsErrors=true`, so the tree must stay at zero
warnings.** Analyzers are on (`AnalysisMode=Recommended`); CA1848 and CA1873
(LoggerMessage delegates) are suppressed in `.editorconfig` with the reasoning
written down. Do not suppress anything else without adding a comment saying why.

`.github/workflows/build.yml` also asserts the **shipped set**: the publish
directory must contain exactly `JellyfinGraveyardAnalyticsPlugin.dll` and
`Dapper.dll`, and no SQLite runtime assets. `Microsoft.Data.Sqlite` is referenced
with `ExcludeAssets="runtime;native"` — both, because `runtime` alone still emits
the native `runtimes/` tree.

## Conventions

- Playback data is read from the **Playback Reporting** plugin's SQLite database.
  `Mode=ReadOnly` since Phase 5 and it must stay that way (finding 3): a writable
  open *creates* a missing file, which then reads as "installed, no activity".
  Check `Repository.PlaybackDatabaseExists` before any query — a read-only open of
  a missing file throws SQLite error 14. `PlayDuration` is in seconds.
- Services take their dependencies through the constructor and are registered in
  `GraveyardServiceRegistrator`. `Plugin.Instance` is read in exactly one place,
  `PluginConfigurationSource`; take `IPluginConfigurationSource` instead of adding
  a second reader, and read `.Current` per use so a saved setting takes effect.
- Any timestamp crossing the wire must be `DateTimeKind.Utc` (finding 30). Parse
  stored and third-party strings through the existing helpers —
  `Repository.TryParseStoredUtc`, `TracearrService.TryParseUtc` — never a bare
  `DateTime.TryParse`: it yields `Unspecified` for a zoneless string and `Local` for
  one with an offset, and both serialize into a date the browser reads wrongly.
- `[Chapel]` is the tag marking condemned items; the paired public collection is
  `"Leaving Soon: The Chapel"` — the constant `ChapelCollectionName`, and the
  lookup is `FindChapelCollection()`. Do not spell either out again. Its artwork is
  **embedded** (`Resources/*.jpg`); the plugin makes no outbound HTTP of its own.
- Release flow is `./release.sh vX.X.X.X --changelog "…"`. It stamps the version
  into the csproj and `build.yaml`, publishes, zips, and patches `manifest.json`
  (checksum + UTC timestamp) itself — no hand-editing. `--dry-run` builds and
  checksums without rewriting anything. Then commit, tag, and let
  `.github/workflows/release.yml` attach the zip; it refuses a tag that disagrees
  with the csproj or whose checksum does not match the committed manifest.
