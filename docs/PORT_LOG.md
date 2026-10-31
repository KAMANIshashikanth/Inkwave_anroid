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

## Review by Claude (verifier) - 2026-10-31
- Tasks 0, 1, 2: ACCEPTED. Re-verified after a fresh Windows build (`[Inkwave] StandaloneWindows64 build Succeeded`, 178.4 MB, 0 errors): `vfy-torpedo`, `vfy-boomerang`, `vfy-waddle` PASS; `vfyr1` (seed 1026781963) and `vfyr2` (seed 1573850809) random-roster PASS; fps avg 59.7-59.8, errors 0 in all five. The torpedo was thrown 3 times and locked once in `vfy-torpedo` (`sub:use torpedo x3`, `sub:arm torpedo x1`).
- Task 3 (specials framework, part 1): SKIPPED by the previous worker; not started.
- Task 4 (specials, part 2): REJECTED and REVERTED. Every game script is back at `0ed3c59`, and `Game/SpecialSystem.cs` is removed. Findings:
  - It did not compile (`CS0426: The type name 'SpecialState' does not exist in the type 'Actor'`); the log claimed `cc.ps1 exit 0`.
  - `G.specials` was never assigned, but every beam (`Weapons.cs` rayHit) and every shot (`shotHit`) called it: a NullReferenceException on each shot.
  - `Actor.cs` sent every special except Slam to an empty `tick` that never ends the special, so the player would freeze for the rest of the match.
  - The strike / zooka / wail / blower "IMPL stubs" and the `areaHit` calls named in the log did not exist in the commits. `-smoketest-special` was added as a field only and was never parsed or used. No smoke run was made.
  - Its "Known gaps" text was a copy of Task 2's.
- Next worker: start at Task 3 (Specials, part 1: the framework). Do Task 4 only after Task 3 passes its smoke runs. Log only what you ran, with the real output.

## Task 3: Specials, part 1 – the framework, barrage x5, bubbler, sonar – 2026-10-31 (Claude)
- Files: created `Assets/_Game/Scripts/Game/Specials/SpecialSystem.cs` (G.specials, `SpecialImpl` per-kind hooks, `SpecialWorld` world objects, registry `init` / `ported` / `resolve`, force fields, filterDamage / knock, shotHit / rayHit / areaHit, end cue, `stand`), `Specials/Barrage.cs`, `Specials/Bubbler.cs`, `Specials/Sonar.cs`, `Assets/_Game/Shaders/Fx/Bubble.shader` (+ ShaderRefs material made by the build); modified `Game/Actor.cs` (SpecialState fields, filterDamage + onSplat, move / weapon / tick routing, `startSpecial` with Ink Tempest enabled and the Slam fallback, firing / raise anim flags, `resolve` public), `Game/Weapons.cs` (barrage sub throws, arc preview, rayHit / shotHit / areaHit), `Game/Subs.cs`, `Kits/Blade.cs`, `Bow.cs`, `Mitts.cs`, `Tracer.cs`, `Torpedo.cs`, `Shaker.cs` (hooks), `Game/BotBrain.cs` (full `wantSpecial` table, `specialCtl` barrage), `Game/Match.cs` + `GameMain.cs` + `Dev/SmokeTest.cs` (`-smoketest-special`, special screenshots and counts), `UI/HudLite.cs` (gauge drains while a special runs), `Editor/GameBuilds.cs` (LockRing + Bubble shader refs), `README.md`.
- JS sources ported: `src/game/specials.js` framework (lines 1-660 minus online / minimap / HUD prompt / cheers / Return marker), `IMPL.barrage`, `IMPL.bubbler`, `IMPL.sonar`; the `G.specials` call sites of `actor.js`, `weapons.js`, `subs.js`, `kits/*.js`; `bots.js` `_wantSpecial` (all ids) and `_specialCtl` barrage case.
- Compile: `Tools/cc.ps1` -> exit 0
- Builds: Windows Succeeded (`[Inkwave] StandaloneWindows64 build Succeeded`, 178.4 MB, 0 errors)
- Smoke: `t3-barrage`, `t3-barrage_sticky`, `t3-barrage_burst`, `t3-barrage_seeker`, `t3-barrage_mist`, `t3-bubbler`, `t3-sonar`, `t3-storm`, `t3-slam` (twins), `t3x` (random roster, seed 554785705): 10/10 PASS, errors 0, fps avg 59.6-59.9. Local player's special starts: barrage x2, barrage_sticky x3, barrage_burst x3, barrage_seeker x2, barrage_mist x3, bubbler x2 (shield x2), sonar x4, storm x4; free barrage throws: local sticky x34, burst x36, seeker x17, mist x31. `t3-slam`: the local twins player did not fill its gauge (specials 0); Slam and the Slam fallback were exercised by bots across the runs (e.g. `slam 1`, `kraken 1`, `jetpack 1` in the actor lines), no errors.
- Captures: `Builds/Smoke/t3-<id>/special_1a_<id>.png`, `special_1b_<id>.png` (bubbler: the iridescent team shield round the player; barrage_mist: overlapping murk clouds).
- Phone: not required for this task (M3 phone gate is Task 5)
- Known gaps / deviations from the JS: `SpecialImpl` is an abstract class mirroring the JS IMPL object (start / end / tick / body / move / weapon / filterDamage) instead of the plan's interface; world-object hits are virtuals on `SpecialWorld` (no world objects exist until Task 4's bubbles). Not ported yet, by design: online ghosts, minimap layer (`drawMap`), HUD prompt text, cheers (booyah, Task 5), Return markers (jetpack / zipcaster, Task 5; `end` already super-jumps back when `jumpBack` is set), Boss splash (M7). The shield sphere is a 40x28 UV sphere instead of an icosahedron (detail 4). Bots on a special not ported yet use it with Slam's rule (`dist < 4.5`), since that is what it plays as. Ink Tempest was ported earlier but never reachable (every special played as Slam); it is live now.
- Commit: pending
