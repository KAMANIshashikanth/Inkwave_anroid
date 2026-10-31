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

## Task 2: Torpedo kit sub (closes subs) - 2026-10-31
- Files: created `Assets/_Game/Scripts/Game/Kits/Torpedo.cs`, `Assets/_Game/Shaders/Fx/LockRing.shader`, `Assets/_Game/Resources/ShaderRefs/Inkwave_Fx_LockRing.mat`; modified `Game/Kits/Kits.cs` (register `torpedo`, rosters), `Game/Subs.cs` (`blastNow` for lethal-contact detonation), `Game/Weapons.cs` (`explodeBombNow` for the same), `Tools/bake/weapons.mjs` (bake the four runtime world parts), `README.md` (M3 status + subs list), `docs/PORT_LOG.md`; new baked `Assets/_Game/Resources/KidWeapons/sub_torpedo/{hullBody,hullInk,finLocal,prop}.bytes` (+ `.meta`)
- JS sources ported: `src/game/kits/torpedo.js` complete except the offline-irrelevant net ghosts: throw arc, feet ink, mid-air lock (`armTime`, `lockRange`, LOS; revealed/squid exceptions), dashed ring on the target's feet, fin unfold/hover, homing swim, locked burst (60 core / 35 rim, `paintRadius 3.6`, 10 droplets x 12 dmg, 3 hits per foe) vs unlocked bomb burst, shoot-down at 20 hp, one live per player, lethal-blast contact, bot fight/paint hooks.
- Compile: `Tools/cc.ps1` -> exit 0
- Builds: Windows Succeeded (`[Inkwave] StandaloneWindows64 build Succeeded`, 178.4 MB, 0 errors; first build exposed a stripped `Shader.Find("Inkwave/Fx/LockRing")`, fixed by adding the shader-reference material under `Resources/ShaderRefs`, the existing pattern)
- Smoke: `t2-torpedo` -> PASS (fps avg 52.4, min 5.9, errors 0; baseline t0 45.6, so no Review Focus 2 regression), `t2r-*` 15/15 PASS (bomb, sticky, burst, shaker, seeker, waddle, tracer, boomerang, scan, curtain, sprinkler, mine, beacon, mist, torpedo), `t2x` + `t2x2` + `t2x3` -> 3/3 PASS (seeds 2114915235, 614305873, 614666115)
- Captures: `Builds/Smoke/t2-torpedo/sub_1a_torpedo.png`, `sub_1b_torpedo.png` show the torpedo; `sub_1b` also shows the dashed lock ring on the target's feet. Full match frames in `Builds/Smoke/t2-torpedo/`
- Phone: not required for this task
- Known gaps / deviations from the JS: the C# loads the four world parts directly with `WeaponModel.loadMesh("sub_torpedo/...")` and keeps its own `YC = 0.034` constant instead of extending `WeaponModel.SubDef`; this avoids global-sub loader side effects. Net ghosts omitted (offline only). No `sub:land` / `sub:end` events (JS emits none). The smoke screenshots show the torpedo small at its 1.9x world scale, as in the web. `Tools/bake/weapons.mjs` re-bakes the four new parts (233 meshes, 7.91 MB total).
- Commit: 8f3f45e
