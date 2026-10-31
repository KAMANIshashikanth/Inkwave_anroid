# Port log — Color Splash

## Task 0: Baseline, tools, log — 2026-10-31
- Files: created `Tools/cc.ps1`, `Tools/smoke.ps1`, `docs/PORT_LOG.md`
- JS sources ported: none (tooling task)
- Compile: `Tools/cc.ps1` → exit 0
- Builds: Windows Succeeded (`[Inkwave] StandaloneWindows64 build Succeeded: Builds/Windows/ColorSplash.exe`, 178.2 MB, 0 errors)
- Smoke: `t0` → PASS (`Builds/Smoke/t0/summary.txt`), fps avg 45.6 (baseline)
- Captures: none (smoke screenshots in `Builds/Smoke/t0/`)
- Phone: not required for this task
- Known gaps / deviations from the JS: the first two baseline runs failed on the then-open boomerang crash (bots drew `boomerang`); after Task 1's fix the baseline was re-run and PASSed. Tools accept the plan's argument forms; `-Subs` must be called in-session (`& .\Tools\smoke.ps1 -Subs a,b`), because `powershell -File` flattens the array into one string.
- Commit: b855257

## Task 1: Fix the boomerang crash (open bug) — 2026-10-31
- Files: modified `Assets/_Game/Scripts/Game/CharacterGeo.cs`, `Game/WeaponModel.cs`, `Game/Match.cs`, `GameMain.cs`, `Dev/SmokeTest.cs`; updated `docs/PORT_LOG.md`; new evidence copies `Builds/Smoke/t1x-run{1,2,3}.summary.txt` (git-ignored)
- JS sources ported: none (bug fix); the roster flag mirrors tools/smoke.sh's random-roster mode
- Compile: `Tools/cc.ps1` → exit 0
- Builds: Windows Succeeded (`[Inkwave] StandaloneWindows64 build Succeeded`, 178.2 MB)
- Smoke: `t1-boomerang` → PASS, `t1-waddle` → PASS, `t1r-*` 14/14 PASS (bomb, sticky, burst, shaker, seeker, waddle, tracer, boomerang, scan, curtain, sprinkler, mine, beacon, mist), `t1x` 3/3 PASS (seeds 314820193, 916006833, 1550171285). Baseline re-run `t0` → PASS, fps avg 45.6
- Captures: `Builds/Smoke/t1-boomerang/sub_1a_boomerang.png`, `sub_1b_boomerang.png` (and 2a/2b, 3a/3b) show the boomerang
- Phone: not required for this task
- Known gaps / deviations from the JS: the reproduction ran on the pre-fix build as `t0` (two FAILs, errors in `Builds/Smoke/t0/summary.txt` at the time); the fix keeps a CPU copy only for `sub_boomerang/body` via `loadRigid(..., readable: true)`, every other baked mesh still calls `UploadMeshData(true)`. Roster RNG is mulberry32 (`Ctx.rng`) seeded per run and logged as `roster: random seed <n>`; `-smoketest-seed N` replays a seed. Random specials are assigned and logged but still play as Tidal Slam until Task 3.
- Commit: 7140039
