# Graveyard Analytics — project instructions

Jellyfin plugin (C#, `net9.0`, targets Jellyfin 10.11+). Project lives in
`JellyfinGraveyardAnalytics/`; the admin UI is a single embedded resource,
`WebUI/dashboard.html`.

## Active work

**Read `PLAN.md` before making changes.** It is the locked improvement plan off
commit `71a01f7` — 23 findings with `file:line` refs, two locked decisions (D1
Morgue definition, D2 play threshold), and eight phases with done-when criteria.
Do not re-derive the findings or re-litigate D1/D2.

Current position: **Phases 0-5 done, including the Phase 5 addendum. Phase 6 is
next** — items 21-25: csproj cleanup, `buiild.yaml` → `build.yaml`, a portable
`release.sh` that patches `manifest.json`, GitHub Actions, the first real xUnit
project, and untracking the committed `Releases/*.dll` / `.DS_Store` /
`.idea/workspace.xml`. Phases run in order. Results and evidence for each finished
phase are recorded in `PLAN.md` under a "Phase N results" heading — read those
before reopening a finding.

Two things Phase 6 inherits, both written up in `PLAN.md`: **finding 30's
unresolved half** (`DateAdded` comes straight from Jellyfin and its `DateTimeKind`
cannot be observed here — do not normalize it on a guess) and the note that
`PlaybackDatabaseExists` is `File.Exists` only, so a database without the
`PlaybackActivity` table still gives a 500 rather than the actionable 400.

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
installed, so the plugin can be built but **not loaded** here. Runtime claims
were verified with the harnesses in `tests/harness/` — jsdom driving the real
`dashboard.html`, reflection over the built assembly, the real `Repository` over a
real SQLite file, the real service registrator in a real DI container, and an
ASP.NET Core app mirroring the webhook. Read `tests/harness/README.md` before
trusting or extending them: they are evidence for `PLAN.md` findings, not a test
suite, and the webhook probes are a *mirror* of controller logic that must be
updated alongside it. Phase 6 still adds the real xUnit project — and the harnesses
are where to look first for what it should assert.

Current expected results, so a regression is obvious: dashboard `xss` 31, `actions`
17, `dates` 6; dotnet `repository` 27, `di` 19, `ttlcache` 12, `visitormap` 21;
`formatbytes` and `probes` print tables rather than counts.

The `Jellyfin.Controller` / `Jellyfin.Model` references are **pinned to
10.11.6**. Do not restore the floating `10.11.*-*`: it resolves to 10.11.11,
which removed `IUserManager.Users` and breaks the build from a clean checkout
(`AnalyticsService.cs:429`).

## Build

```bash
cd JellyfinGraveyardAnalytics && dotnet publish -c Release
```

No `.sln`. Local SDK is dotnet 10.x; the csproj targets `net9.0`. There is no
test project and no CI yet — both are Phase 6.

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
  `"Leaving Soon: The Chapel"`.
- Release flow is `release.sh vX.X.X.X`, then hand-edit `manifest.json`
  (checksum + timestamp). Phase 6 automates this.
