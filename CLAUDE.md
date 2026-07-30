# Graveyard Analytics — project instructions

Jellyfin plugin (C#, `net9.0`, targets Jellyfin 10.11+). Project lives in
`JellyfinGraveyardAnalytics/`; the admin UI is a single embedded resource,
`WebUI/dashboard.html`.

## Active work

**Read `PLAN.md` before making changes.** It is the locked improvement plan off
commit `71a01f7` — 23 findings with `file:line` refs, two locked decisions (D1
Morgue definition, D2 play threshold), and eight phases with done-when criteria.
Do not re-derive the findings or re-litigate D1/D2.

Current position: **Phases 0, 1, 3 done; Phase 2 all but item 7 (needs a Tracearr API key). Phase 4 is next.** Phases
run in order. Results and evidence for each finished phase are recorded in
`PLAN.md` under a "Phase N results" heading — read those before reopening a
finding.

One finding changed on contact with reality: **finding 5's webhook auth bypass
was struck in Phase 0** (empty query values bind to `null`, so the endpoint
failed closed). The rest of finding 5 was real and is fixed.

No Jellyfin server is available locally, and only the dotnet 10.0.x runtime is
installed, so the plugin can be built but **not loaded** here. Runtime claims
were verified with the harnesses in `tests/harness/` — jsdom driving the real
`dashboard.html`, reflection over the built assembly, and an ASP.NET Core app
mirroring the webhook. Read `tests/harness/README.md` before trusting or
extending them: they are evidence for `PLAN.md` findings, not a test suite, and
the webhook probes are a *mirror* of controller logic that must be updated
alongside it. Phase 6 still adds the real xUnit project.

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

- Playback data is read from the **Playback Reporting** plugin's SQLite database
  (read-only; see finding 3). Its `PlayDuration` column is in seconds.
- `[Chapel]` is the tag marking condemned items; the paired public collection is
  `"Leaving Soon: The Chapel"`.
- Release flow is `release.sh vX.X.X.X`, then hand-edit `manifest.json`
  (checksum + timestamp). Phase 6 automates this.
