# Graveyard Analytics — project instructions

Jellyfin plugin (C#, `net9.0`, targets Jellyfin 10.11+). Project lives in
`JellyfinGraveyardAnalytics/`; the admin UI is a single embedded resource,
`WebUI/dashboard.html`.

## Active work

**Read `PLAN.md` before making changes.** It is the locked improvement plan off
commit `71a01f7` — 23 findings with `file:line` refs, two locked decisions (D1
Morgue definition, D2 play threshold), and eight phases with done-when criteria.
Do not re-derive the findings or re-litigate D1/D2.

Current position: **all phases done — 0-7, including the Phase 5 addendum.** The
plan is complete; there is no next phase. Results and evidence for each phase are
recorded in `PLAN.md` under a "Phase N results" heading — read those before
reopening a finding.

The three items that Phase 6 left open were **all closed on 2026-07-30**, in a pass
after Phase 7. Written up in `PLAN.md` under "Post-plan: the three carried items".
Do not reopen them from the older "Phase 6 results" text, which still describes them
as open:

- **Finding 30's `DateAdded` half is answered: there was nothing to fix.**
  `BaseItem.DateCreated` arrives as `DateTimeKind.Utc`, guaranteed by Jellyfin's
  SQLite provider installing a value converter whose read direction is
  `DateTime.SpecifyKind(v, Utc)` over every `DateTime`/`DateTime?`. Verified at tag
  `v10.11.6`: `SqliteDatabaseProvider.cs:113-115`, `ModelBuilderExtensions.cs:42-45`,
  `ValueConverters/DateTimeKindValueConverter.cs:17`. The stored instant is UTC too.
  A `Services/JellyfinTimestamps.AsUtc` boundary was added anyway — a no-op on any
  stock server — because that guarantee is the *provider's* and 10.11 admits
  plugin-supplied providers. Read its remarks before touching it.
- **`PlaybackDatabaseExists` is still `File.Exists`, and that is now correct**: the
  guard callers use is `Repository.PlaybackDataUnavailableReason()`, which also
  checks `sqlite_master` for the `PlaybackActivity` table. Deliberately narrow — it
  does not swallow lock or not-a-database errors, because reporting `SQLITE_BUSY` as
  "Playback Reporting is not installed" is worse than an error.
- **The pin stays at 10.11.6, for a different reason than before.** It is now the
  *oldest supported* ABI, which is what keeps one artifact loadable across 10.11.x —
  not a workaround. `IUserManager.Users` was removed in **10.11.9**, and the plugin
  shipped IL calling it, so the Guestbook died on every current server;
  `Services/UserManagerCompat.cs` resolves that accessor by name. **That file must
  not name either member in code.** `tests/harness/dotnet/abi` guards it.

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

**The test suite is `tests/GraveyardAnalytics.Tests` (91 tests, `net10.0`).** Added
in Phase 6, run in CI on every push. It covers `FormatBytes`, D2's play threshold
over a real SQLite file, D1's floor gate through the real `GetLeastWatchedItems`,
the configuration clamps, finding 30's parse and its serialized form and the
`JellyfinTimestamps.AsUtc` boundary, the missing-`PlaybackActivity`-table guard, the
`TtlCache`, and the embedded Chapel artwork. Its non-vacuity was checked by
mutating the source, not asserted — if you add to it, do the same.

`tests/harness/` is **separate and still current**: throwaway-grade evidence for
`PLAN.md` findings, kept because several can be pointed at an *old* assembly
(`GRAVEYARD_DLL=…`) to prove a check catches the bug it names. Read
`tests/harness/README.md` before trusting or extending them — the webhook probes
are a *mirror* of controller logic that must be updated alongside it.

Expected results, so a regression is obvious: suite **91**; dashboard `xss` 32,
`actions` 24, `dates` 6, `tabs` 32, `home` 27; dotnet `abi` 5 (per ABI), `repository` 27,
`di` 19, `ttlcache` 12, `visitormap` 21; `formatbytes` and `probes` print tables
rather than counts.

The `Jellyfin.Controller` / `Jellyfin.Model` references are **pinned to 10.11.6
because that is the oldest server supported** — compiling against the oldest ABI is
what keeps one artifact loadable across the whole 10.11.x line. Do not restore the
floating `10.11.*-*`: it resolves to the newest, which both breaks a clean checkout
and would silently raise the floor. Any Jellyfin API added after 10.11.6 has to be
reached the way `Services/UserManagerCompat.cs` reaches `GetUsers()` — by name at
runtime, never a compile-time reference — and `tests/harness/dotnet/abi` is what
proves the built assembly still resolves everywhere.

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
- `WebUI/dashboard.html` was rewritten in Phase 7 and has rules that are easy to
  undo by accident. It holds **zero `style="…"` attributes and zero
  `element.style.…` writes** — styling is classes in the page's own `<style>`
  block, every rule scoped to `#GraveyardAnalyticsPage`, and show/hide is the
  `hidden` attribute. There is **one table**: a column exists once, in the per-tab
  descriptor, and full-width rows take their `colSpan` from it — do not reintroduce
  a column-count constant. State lives in the module closure;
  `window.GraveyardDashboard` is the only global, exists for the jsdom harnesses,
  and the page never reads it — do not add a second. Listeners register once at
  script evaluation, **never inside `viewshow`** (finding 7); Jellyfin fires it on
  every return to the page. Every fetch needs a `.catch` that renders the failure,
  and a response is only rendered if its `state.request` generation is still
  current.
- The **home screen row** (`EnableHomeSection`, off by default) is the one piece of
  this plugin that runs outside the admin UI, and it is unsupported by
  construction: Jellyfin has no home-section API (`HomeSectionType` is a closed
  enum, `BrandingOptions` has `CustomCss` and no JS equivalent), so
  `Services/HomeSectionStartupFilter.cs` registers an `IStartupFilter` and injects
  one `<script>` into `index.html`. Rules that are not stylistic: it must
  **short-circuit rather than wrap the response body** (Jellyfin's
  `UseResponseCompression` sits *inside* it, so a wrapping filter is handed gzip and
  silently no-ops); it must match the **end** of the path, since it runs outside
  `app.Map(BaseUrl, …)`; and **every failure path must fall through to the
  untouched page**. `WebUI/home.js` must never throw into the client and must render
  nothing when the Chapel is empty. Do not adopt the community plugins' approach of
  Harmony-patching `Startup.Configure` or splicing the minified bundle — that is why
  they need one build per Jellyfin patch and why a mismatch has taken servers down.
- Release flow is `./release.sh vX.X.X.X --changelog "…" --publish`. It runs the
  suite, stamps the version into the csproj and `build.yaml`, publishes, zips,
  patches `manifest.json` (checksum + UTC timestamp), then commits those three
  files, tags, and pushes branch and tag — no hand-editing, no manual upload. The
  tag push is what publishes: `.github/workflows/release.yml` rebuilds the same
  zip, refuses a tag disagreeing with the csproj or a checksum disagreeing with
  the committed manifest, and attaches it. `--dry-run` rehearses; omitting
  `--publish` stops after the manifest patch and prints the git commands.
  `--publish` refuses unless HEAD is on `master`, the tree is clean apart from
  those three files, and the tag is unused locally and on the remote, then asks
  you to type the version.
- **The release artifact is built once and uploaded, never rebuilt.** `release.sh`
  publishes the exact bytes it checksummed into the manifest, so the two agree by
  construction. `release.yml` fires on `release: published` and *verifies* —
  version consistency, then it downloads what `sourceUrl` points at and checks the
  MD5 and the shipped set. Do not restore the old shape, where CI rebuilt the zip
  and compared: that gate never once passed. Four separate environment
  differences broke it in turn — zip entry mtimes, the commit SourceLink embeds in
  the assembly, the floating SDK version, and the compilation-options blob inside
  the embedded PDB, which records the runtime `csc` itself ran on. Each fix only
  revealed the next.
  The determinism settings in the csproj and the zip mtime flattening in
  `release.sh` are **kept** — they make rebuilds comparable and are cheap — but
  nothing depends on them any more. Two builds of the same source are identical
  on the same SDK (verified macOS vs Linux at 10.0.200) and differ across SDK
  patches, which is why `global.json` pins it.
