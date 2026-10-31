# Color Splash (INKWAVE → Unity C# Android) — Port Completion Plan

> **For agentic workers:** this is the handoff for finishing the port. Work task by task, in order. Steps use checkbox (`- [ ]`) syntax for tracking. **The user forbids sub-agents**: do every read, search, plan and edit yourself (no Agent / Explore / Plan / Workflow tools or equivalents). After every task, append a record to `docs/PORT_LOG.md` (format in Part C). The user will have Claude verify each task from that record, so a task without a record counts as not done.

**Goal:** finish translating the open-source three.js game INKWAVE (MIT, © Jayden Davis) into the Unity 6 C# project `D:\color_splash\Inkwave_anroid`. The result is "Color Splash": an offline-only Android game against bots, faithful to the web original, that runs well on a phone.

**Architecture:** gameplay code is translated module by module from `reference/inkwave/src/**.js` to C#. JS names, numbers and coordinate system ("three space") stay unchanged. Procedural content that never changes at runtime (meshes, textures, murals, sounds, icons) is **baked at edit time by running INKWAVE's own JS**. Pure-geometry generators run under Node. Generators that need canvas, WebGL or WebAudio run in headless Chrome. Unity only loads the baked results. Each milestone ends with a passing Windows smoke test and a phone test by the user.

**Tech Stack:** Unity 6000.5.9f1, URP 17.5.0, Input System 1.20.0, uGUI 2.5.0 (incl. TextMeshPro), C# 9, HLSL; Node 24 (bakers); system Chrome (headless bakers); PowerShell tooling on Windows 11.

**Spec:** the original approved port plan is `C:\Users\kaman\.claude\plans\now-i-wanna-reverse-valiant-prism.md`. Its decisions are copied into Part A below, so this file stands on its own. Where Part A and the spec differ, Part A wins (it records later decisions).

---

## Part A — Context you need before touching anything

### A1. What exists and where

| Thing | Location |
|---|---|
| Unity project (the user's folder name, misspelling intended) | `D:\color_splash\Inkwave_anroid` |
| Translation source (git clone, **read-only**, git-ignored) | `D:\color_splash\Inkwave_anroid\reference\inkwave` at upstream commit `7c970e6`. Restore with `git clone https://github.com/zgjzgj1985/inkwave reference/inkwave` (needs network; the original `jaydendavisnc/inkwave` returns 404). |
| Live web original (visual reference) | https://inkwave-aah.pages.dev |
| Unity editor | `C:\Program Files\Unity\Hub\Editor\6000.5.9f1\Editor\Unity.exe` |
| Node | `node` on PATH, v24 (built-in `fetch`, `WebSocket`, `zlib.brotli*`) |
| Chrome | `C:\Program Files\Google\Chrome\Application\chrome.exe` (Edge also present) |
| Python | **not installed**; use PowerShell or Node instead |
| Git repo | `D:\color_splash\Inkwave_anroid` is its **own** repo, branch `main`, remote `origin` = https://github.com/KAMANIshashikanth/Inkwave_anroid.git (baseline commit `1125009`). It sits inside the folder of a different repo, `D:\color_splash` (the user's `PaintBattle` project, remote `color_splash.git`). Always run git from inside `Inkwave_anroid`; never run git commands for this project from `D:\color_splash`. |
| Builds (git-ignored) | `Builds/Windows/ColorSplash.exe`, `Builds/Android/ColorSplash.apk`, `Builds/Smoke/`, `Builds/Captures/` |

### A2. User decisions (do not re-open these)

- The user rejected a WebView/Capacitor wrap. Their reasons: the web build was not playable by touch, and three.js is slow on phones. Hence a native Unity C# port.
- Branding is **Color Splash**, company **Zoiko**, package **`com.zoiko.colorsplash`**. Never show "INKWAVE" as the title. The credits must say "Original game: INKWAVE by Jayden Davis" and include `LICENSE-INKWAVE.txt` (MIT).
- **Offline only.** Bots only, no online play.
- **Squid form is part of this game.** The user's other project PaintBattle has a "no squid" rule; it does **not** apply here.
- **Never modify, delete or stage `D:\color_splash\PaintBattle`.** The user has uncommitted work there. Never run git in `D:\color_splash` itself: that is the PaintBattle repo, and `git add` there would embed this repo in it.
- Gate rule: each milestone ends with a phone test by the user. Do not start the next milestone until the user reports it plays well. So far they reported: M1 "without lag", M2 "too good", M3 mains "working good", M3 kits "good all working".
- The user said to defer the remaining M2 material polish (Task 18) and keep moving.
- Not ported, ever:
  - `src/net/`, `server/`, `electron/`, `src/dev/`, `tools/*-lab*`;
  - the `cargo` stage (`onlineOnly` in `config.js`) and `lobbySet*.js` (the online lobby set);
  - `ui/news.js` (it advertises multiplayer);
  - every online hook: `netState`, `netApply`, ghosts, `netHurt`, `KIT_GHOSTS`;
  - the legacy weapons `dualies`, `slosher` and `splatling`. `WEAPON_SUCCESSOR` maps them to twins, bucket and spinner.

### A3. Technical decisions already in force

1. **Three space.** Gameplay keeps INKWAVE's numbers: metres, Y up, the kid faces +Z at yaw 0, right-handed.
   - Every world object is parented under `ThreeSpace.root`, which is mirrored with scale `(1,1,-1)`. three's CCW front faces then render correctly; the M0 spike verified this.
   - Copy positions and quaternions unchanged. Convert a three Euler with `ThreeSpace.eulerXYZ`: three's order is XYZ, Unity's `Quaternion.Euler` is ZXY.
   - Place the camera with `ThreeSpace.placeCamera` / `ThreeSpace.cameraLookAt`.
2. **Own physics.** `Game/Physics.cs` ports `physics.js` (oriented boxes, capsule resolve, ground and camera probes). Never use PhysX for gameplay.
3. **Names follow the JS.** Fields and methods keep their camelCase JS names (`actor.addTurf()`, `PLAYER.runSpeed`).
   - Every C# file starts with `// Port of src/<path>.js — <what it is>` and names what is not ported yet.
   - Folders mirror the JS tree under `Assets/_Game/Scripts/`.
   - Namespaces: `Inkwave`; `Inkwave.EditorTools` for editor code; `Inkwave.Dev` for test helpers.
4. **Bake, don't generate on the phone.**
   - Static geometry generators run under Node through `Tools/bake/register.mjs` + `hooks.mjs`. The hooks map `three` to the vendored copy and can expose module-private functions through the `EXPOSE` table without editing the reference on disk.
   - Bakers write IWK1 mesh files (`Tools/bake/iwk.mjs`), which `Game/CharacterGeo.cs` loads.
   - Runtime code (behaviour, animation, ink) is translated to C#.
   - The phone asset set is quality `low`, LOD tiers `game` + `far`.
5. **Events.** `Ctx.on/emit` with one `Ev` payload class carry the events named in `reference/inkwave/docs/EVENTS.md`.
6. **Ink.** `World/PaintSystem.cs` ports `paint.js`: a GPU atlas splat shader plus a CPU 0.25 m turf grid. Flood fills are not ported yet; port them when a caller needs them.
7. **Rendering.**
   - URP forward with one directional sun.
   - SSAO is off (`PC_Renderer`), because INKWAVE uses baked AO lightmaps (`reference/inkwave/assets/lightmaps/<id>.png/.json`).
   - Quality tiers come from `Config.QUALITY` (low / medium / high). Touch devices default to `low`.
8. **UI is uGUI + TextMeshPro.** This changes the spec, which said UI Toolkit. `HudLite` and `TouchControls` are already uGUI, and one UI system is simpler on mobile.
9. **Kits.** Kit weapons register in `Kits.init()` (`Game/Kits/Kits.cs`, port of `kits/registry.js`); `GameMain.Awake` calls it.
   - Main kits go in `Kits.MAIN[kind]` and sub kits in `Kits.SUB[kind]`.
   - A sub not ported yet falls back to the Splat Bomb (`SubSystem.ported`).
   - Every special except slam and storm currently plays as Tidal Slam.

### A4. Pitfalls already paid for (read all of them)

- `Mesh.SetVertices(arr, start, len)` resizes the mesh and breaks a fixed index buffer. For partial uploads use `SetVertexBufferParams` + `SetVertexBufferData`.
- Shaders that draw raw clip coordinates into a RenderTexture must flip y under `UNITY_UV_STARTS_AT_TOP`.
- A custom `Bounds` class shadows `UnityEngine.Bounds`; the stage one is therefore named `StageBounds`. Avoid other Unity type names.
- **Skinned meshes:** Unity passes the vertex shader the *skinned* position, without bone matrices.
  - three's `vBindPos` therefore comes from a baked TEXCOORD5 rest-position channel.
  - Vertex-shader rigs (lids, mouth, eye turn) need another approach (Task 18).
- `JsonUtility` turns missing arrays and objects into empty ones, never null. Check `.Length`.
- **Partial-class static init order is undefined across files.** Index tables built by counters (pose layout, springs, timers) must live in one file.
- Editor tools that build characters or kits (captures) must call `Kits.init()`. Otherwise kit models fall back to generic part animation.
- **Affine part poses:** a three matrix T·R·F (F = rotation × non-uniform scale) maps to a Unity Transform only if the bake puts the geometry in the part's own frame. Undo the authoring matrix in the bake.
- `CharacterGeo` meshes are uploaded with `UploadMeshData(true)` and are **not CPU-readable**. Never read `.vertices` from them (this caused the open bug in Task 1).
- Shaders reached only through `Shader.Find` must be added to the `Shaders` array in `Editor/GameBuilds.cs`. Otherwise they are stripped from players.
- Kit model geometry is baked by importing `kits/<kind>-model.js`. Do not import the kit logic module for mains: it pulls in bots and audio. Sub-kit modules do import cleanly.
- Building a scene with `EditorSceneManager.NewScene(Single)` unloads unused assets. Load ScriptableObjects and prefabs **after** creating the scene.
- **Batch mode:**
  - It aborts with "another Unity instance is running" when the user has the editor open. Ask them to close it; never kill it.
  - "No valid Unity Editor license" means the licence file `%LOCALAPPDATA%\Unity\licenses\UnityEntitlementLicense.xml` expired. Ask the user to refresh it in Unity Hub (Settings → Licenses).
  - Captures need a GPU: never pass `-nographics`. The first `Camera.Render` of a batch session can miss skinned meshes, so render once and discard.
- **Network:**
  - Sandboxed shells may have no internet.
  - An Android build has succeeded before (`Builds/Android/ColorSplash.apk`), so the Gradle cache is warm. If a build hangs at "Building Gradle project", it needs network: ask the user to allow it, or to build from the editor.
  - No new Unity packages and no npm packages: neither can be downloaded.

### A5. Current state (2026-10-31)

Milestones (README table, corrected):

| # | Scope | State |
|---|---|---|
| M0 | Setup, config, event bus, input + touch, three-space bridge | done |
| M1 | Playable Turf War on Tidewater Plaza vs 7 bots | done, phone PASS |
| M2 | Procedural squidkid (baked meshes, full `character.js` animation) | done; material polish deferred (Task 18) |
| M3 | Weapons, subs, specials | 12/12 mains done; 14/15 subs (torpedo missing; boomerang has an open bug); specials **not started** |
| M4 | All 7 offline stages, props, environment, texlib, baked AO | not started (Tidewater layout only, flat-coloured) |
| M5 | FX + audio | not started (`Fx/FxStubs.cs` no-ops) |
| M6 | Menus, full HUD, minimap, diorama overlay, locker, showcase | not started (`UI/HudLite.cs` stand-in) |
| M7 | Zone Control, Boss mode | not started |
| M8 | Mobile polish, release AAB | not started |

`README.md` still says "subs, specials next". In fact `subs.js` is ported, plus the shaker, tracer, waddle and boomerang kits. Fix the README in Task 2.

JS module → C# status (anything not listed under src/ is in A2's never-port list):

| JS (src/) | C# | Status |
|---|---|---|
| config.js | Config.cs | ported (all tables incl. SPECIALS, MAPS, QUALITY, ZONES, TOUCH) |
| core/ctx, input, touch | Core/Ctx.cs, InputState.cs, TouchControls.cs | ported |
| core/renderer.js | GameMain.applyRenderQuality | partial: post stack (bloom 0.9, neutral tonemap, grade LUT) is Task 7 |
| main.js | GameMain.cs | M1 subset (boot, Turf War on Tidewater, loop) |
| game/actor, player, cameraRig, nav, physics, match | Actor, PlayerController, CameraRig, NavGraph, Physics, Match | ported (match: Turf War only) |
| game/bots.js | BotBrain.cs | Turf War brain + kit hooks; specials, Zone and Boss layers missing |
| game/character.js, character-style.js | Character*.cs, CharacterStyle.cs | ported |
| game/character-geo/face/hair/outfit/lod.js | baked by `Tools/bake/character.mjs` → Resources/Kid; loader CharacterGeo.cs | baked |
| game/character-mats.js | CharacterMats.cs + Shaders/Kid/*.shader | first pass (Task 18) |
| game/character-weapons.js | baked by `Tools/bake/weapons.mjs` → Resources/KidWeapons; WeaponModel.cs | baked (incl. sub_* and sp_blower/jetgun/stamp/zooka props) |
| game/weapons.js | Weapons.cs | ported (8 built-in mains, bomb, Ink Tempest storm) |
| game/subs.js | Subs.cs | ported (10 built-in kinds) |
| game/kits/brolly, bow, blade, mitts (+ -model, blade-fx) | Game/Kits/*.cs | ported |
| game/kits/shaker, tracer, waddle, boomerang | Game/Kits/*.cs | ported (boomerang: Task 1 bug) |
| game/kits/torpedo.js | — | **Task 2** |
| game/specials.js, special-props.js | — | **Tasks 3–5** |
| world/level, mapkit, paint | Level.cs, MapKit.cs, PaintSystem.cs | ported |
| world/inkShading, levelMaterial | Shaders/Level/Level.shader | ink layer only; texlib patterns + AO are Task 6 |
| world/stages/tidewater/layout.js | World/Stages/Tidewater/TidewaterLayout.cs | ported |
| world/texlib, environment, props*, decor, dressing, murals, maps (halyard), stages/* (rest) | — | **Tasks 6–9** |
| fx/fx, fxHooks, screenfx, swimWake | Fx/FxStubs.cs (call surface only) | **Task 10** |
| audio/audio, music | Fx/FxStubs.cs (AudioSys call surface only) | **Task 11** |
| ui/hud, menus, ui-icons, menu-art, ui-util, diorama, game/minimap, game/showcase, world/mapThumb | UI/HudLite.cs stand-in | **Tasks 12–15** |
| game/zones, world/zones-data, world/variants, fx/zoneMarks | — | **Task 16** |
| boss/*, ui/hud-boss, ui/boss-art, audio/bossAudio | — | **Task 17** (optional, ask first) |

### A6. Orientation reading list (read in this order before Task 0)

1. `Inkwave_anroid/README.md`
2. `reference/inkwave/README.md`, `docs/CONTRACTS.md`, `docs/EVENTS.md`, `docs/RIG.md`
3. `Assets/_Game/Scripts/GameMain.cs`, `Core/Ctx.cs`, `Core/Three/ThreeSpace.cs`, `Fx/FxStubs.cs`, `Dev/SmokeTest.cs`, `Editor/GameBuilds.cs`
4. The pattern files for a module port: `Game/Subs.cs` (a whole module), `Game/Kits/Kits.cs` (hook surface), `Game/Kits/Waddle.cs` (a kit with a baked model, bot hooks and events)
5. `Tools/bake/hooks.mjs`, `iwk.mjs`, `weapons.mjs` (the bake pattern)

### A7. The C# runtime in one paragraph

- `G` (static partial class) is the global context, the port of `ctx.js`'s `G`: `G.level`, `G.paint`, `G.fx`, `G.audio`, `G.hud`, `G.settings`, `G.quality`, `G.teamColors`.
- `GameMain` (MonoBehaviour) boots and runs one `Match`.
- `Match` owns 8 `Actor`s. Each has a `WeaponRunner` (Weapons.cs), an `ICharacter` (Character.cs) and either a `PlayerController` or a `BotBrain`.
- Projectiles, subs (`SubSystem`) and kit objects step from `Projectiles.update`.
- `Level` + `LevelView` build collision blocks, paintable faces and render meshes from a layout, built with `MapKit`.
- `PaintSystem` owns the ink atlas and turf grid. `NavGraph` serves the bots.
- `HudLite` and `TouchControls` are uGUI canvases.
- `SmokeTest` (dev builds only) hands the local player to a bot and writes `summary.txt`.

---

## Global Constraints

- Unity **6000.5.9f1**, URP **17.5.0**, Input System **1.20.0**, uGUI **2.5.0**. Add no packages and no npm modules: nothing can be downloaded.
- Player settings:
  - product **Color Splash**, company **Zoiko**, id **com.zoiko.colorsplash**;
  - landscape only, IL2CPP **ARM64**, minSdk **26**, targetSdk **36** for release.
- Offline only: nothing from the never-port list in A2. The release build must not request the INTERNET permission.
- MIT credit stays: `LICENSE-INKWAVE.txt` plus "Original game: INKWAVE by Jayden Davis" in the in-game credits.
- Gameplay numbers come from `Config.cs` (= `config.js`). Never invent tuning values. Where the JS hard-codes a number, copy it.
- Follow the naming, header-comment, folder and three-space rules in A3. Every new `Shader.Find` shader goes in `GameBuilds.Shaders`.
- `reference/inkwave` is read-only. Bakers expose private functions through `hooks.mjs` `EXPOSE`, never by editing files.
- Git:
  - run git only inside `D:\color_splash\Inkwave_anroid` (its own repo);
  - commit only after the user has approved commits (Task 0), with messages ending in the attribution lines the user's tooling requires;
  - push to `origin main` only when the user asks;
  - never commit keystores or passwords.
- No sub-agents. Never kill the user's Unity editor.
- A task is done only when all of these hold:
  1. `Tools/cc.ps1` prints `exit 0` with no `error CS` lines;
  2. the task's smoke runs print `PASS`;
  3. the README milestone table is updated;
  4. a `docs/PORT_LOG.md` record exists.

## Review Focus

Each line below names an input or condition, then what a reasonable player expects. Each one has a test step in the owning task.

1. **Touch-only play.** Every feature (sub throw, special activation and aiming, map hold, Super Jump, every menu) is usable on a phone with no keyboard, on 16:9 and 20:9 screens, without overlapping the touch buttons. Tests: Task 5 step 6, Task 13 step 5, Task 14 step 7.
2. **Low-tier frame rate.** New FX, props and environment keep the phone where the user called M1 "without lag". Tests:
   - each smoke `summary.txt` reports `fps: avg`; a task must not drop the Windows avg by more than 10 % against the previous task's record on the same map and weapon;
   - phone tests at Tasks 7, 10 and 15.
3. **App backgrounded mid-match** (home button, call, screen off). Expect the match to pause, then resume with no errors, and audio to resume. Test: Task 14 step 6 (`OnApplicationPause` / `OnApplicationFocus`).
4. **Bots carrying content not yet ported, or every loadout combination.** Expect no exception, ever. A missing sub falls back to the Splat Bomb, a missing special to Tidal Slam. The open boomerang bug is exactly this class. Test: the `-smoketest-roster random` run in Tasks 1, 2 and 5.
5. **Stale or missing baked data** (lightmap hash mismatch, missing clip, missing icon). Expect a single warning and a graceful fallback (no AO, silence, blank sprite), never an exception or a pink material. Tests: Task 6 step 6, Task 11 step 3.

---

## Part B — Tasks

Verification commands used by every task (paths relative to `D:\color_splash\Inkwave_anroid`):

- **Compile check** (works with the editor open): `powershell -ExecutionPolicy Bypass -File Tools/cc.ps1`. Expected: last line `exit 0`, no `error CS`.
- **Windows build** (editor closed):
  `& "C:\Program Files\Unity\Hub\Editor\6000.5.9f1\Editor\Unity.exe" -batchmode -quit -projectPath D:\color_splash\Inkwave_anroid -executeMethod Inkwave.EditorTools.GameBuilds.BuildWindowsDevelopment -logFile Builds\build_win.log`
  Expected: `Builds\build_win.log` contains `[Inkwave] StandaloneWindows64 build Succeeded`.
- **Android build:** the same command with `BuildAndroidDevelopment` and `-logFile Builds\build_android.log`. Expected: `[Inkwave] Android build Succeeded`.
- **Smoke:** `powershell -ExecutionPolicy Bypass -File Tools/smoke.ps1 -Weapon <id> -Subs <id,...> [-Special <id>] [-Map <id>] [-Mode <m>] [-Roster random] -Tag <tag>`.
  Expected: one line per run, `<tag>-<sub> exit 0 PASS`. Output goes to `Builds/Smoke/<tag>-<sub>/` (`summary.txt`, `player.log`, PNGs).
- **Phone test:** build the APK, tell the user its path, and ask them to install it and play one full match. Record their verdict verbatim in the log. Do not continue past a milestone gate without a positive verdict.

### Task 0: Baseline, tools, log

**Files:**
- Create: `Tools/cc.ps1`, `Tools/smoke.ps1`, `docs/PORT_LOG.md`

**Interfaces:**
- Produces: the compile check, the smoke runner and the log every later task uses.

- [ ] **Step 1: Read the A6 list.**
- [ ] **Step 2: Check the baseline and ask for commit approval.**
  1. Run `git -C D:\color_splash\Inkwave_anroid status --short` and `git -C D:\color_splash\Inkwave_anroid log --oneline -3`. Expected: `1125009 first commit` (or later commits) and a clean tree. `.gitignore` excludes Library, Builds, build, .utmp, reference and the csproj files.
  2. Ask the user in these words: "May I commit to the Inkwave_anroid repo after each task? (I will push only when you ask.)"
  3. Record the answer in the log.
- [ ] **Step 3: Create `Tools/cc.ps1`** with exactly:

```powershell
# Offline compile check of every game + editor script (Roslyn from the Unity editor's SDK, refs from the editor csproj).
# Works while the editor is open. The csproj files are generated by Unity (open the project once if they are missing).
$proj = 'D:\color_splash\Inkwave_anroid'
$ed = 'C:\Program Files\Unity\Hub\Editor\6000.5.9f1\Editor\Data'
$x = [xml](Get-Content "$proj\Assembly-CSharp-Editor.csproj" -Raw)
$refs = @()
foreach ($h in $x.SelectNodes('//*[local-name()="HintPath"]')) {
  $p = $h.InnerText
  if (-not [System.IO.Path]::IsPathRooted($p)) { $p = Join-Path $proj $p }
  if ($p -match 'Assembly-CSharp\.dll$') { continue }
  if (Test-Path $p) { $refs += $p }
}
$defs = ($x.SelectNodes('//*[local-name()="DefineConstants"]') | Select-Object -First 1).InnerText
$out = Join-Path $env:TEMP 'iw_cc.dll'
$rsp = Join-Path $env:TEMP 'iw_cc.rsp'
$lines = @('-target:library', '-nologo', '-langversion:9.0', '-nowarn:0169,0649,0414,0219,0168,0162,0618,0067', "-out:`"$out`"", "-define:$defs", '-unsafe+')
foreach ($r in $refs) { $lines += "-r:`"$r`"" }
Get-ChildItem "$proj\Assets\_Game\Scripts" -Recurse -Filter *.cs | ForEach-Object { $lines += "`"$($_.FullName)`"" }
Set-Content -Path $rsp -Value $lines -Encoding utf8
& "$ed\DotNetSdk\dotnet.exe" "$ed\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll" "@$rsp" 2>&1 | Where-Object { $_ -match 'error|warning CS' } | Select-Object -First 60
"exit $LASTEXITCODE"
```

- [ ] **Step 4: Run it.** Expected: `exit 0`.
- [ ] **Step 5: Create `Tools/smoke.ps1`** with exactly:

```powershell
# Smoke runner: one Windows player run per sub; the verdict is the last PASS/FAIL line of summary.txt.
param([string]$Weapon = 'shooter', [string[]]$Subs = @(''), [string]$Special = '', [string]$Map = '', [string]$Mode = '',
      [string]$Roster = '', [string]$Tag = 'run', [int]$Seconds = 90)
$proj = 'D:\color_splash\Inkwave_anroid'
$exe = "$proj\Builds\Windows\ColorSplash.exe"
foreach ($s in $Subs) {
  $name = if ($s) { "$Tag-$s" } else { $Tag }
  $out = "$proj\Builds\Smoke\$name"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  New-Item -ItemType Directory -Force $out | Out-Null
  $a = @('-smoketest', '-smoketest-out', $out, '-smoketest-seconds', "$Seconds", '-smoketest-timescale', '2', '-smoketest-weapon', $Weapon,
         '-screen-width', '1280', '-screen-height', '720', '-screen-fullscreen', '0', '-logFile', "$out\player.log")
  if ($s) { $a += @('-smoketest-sub', $s) }
  if ($Special) { $a += @('-smoketest-special', $Special) }
  if ($Map) { $a += @('-smoketest-map', $Map) }
  if ($Mode) { $a += @('-smoketest-mode', $Mode) }
  if ($Roster) { $a += @('-smoketest-roster', $Roster) }
  $p = Start-Process -FilePath $exe -ArgumentList $a -PassThru
  if (-not $p.WaitForExit(240000)) { $p.Kill(); "$name TIMEOUT"; continue }
  $sum = "$out\summary.txt"
  $verdict = if (Test-Path $sum) { (Get-Content $sum | Select-String -Pattern '^(PASS|FAIL)' | Select-Object -Last 1).Line } else { 'NO SUMMARY' }
  "$name exit $($p.ExitCode) $verdict"
}
```

- [ ] **Step 6: Build and run the baseline.** Build Windows, then run `Tools/smoke.ps1 -Weapon shooter -Tag t0`. Expected: `t0 exit 0 PASS`. Note the `fps: avg` value from `Builds/Smoke/t0/summary.txt`; it is the baseline.
- [ ] **Step 7: Create `docs/PORT_LOG.md`** with the Part C header and the Task 0 record. Commit.

### Task 1: Fix the boomerang crash (open bug)

**Symptom:** the last session's smoke runs `v3-boomerang` and `v3-waddle` FAILED. The waddle run failed because a bot threw a boomerang. The error:
`Not allowed to access vertices on mesh 'kid:sub_boomerang/body' (isReadable is false …)` at `Boomerang.proto()`, [Boomerang.cs:56](../../../Assets/_Game/Scripts/Game/Kits/Boomerang.cs#L56). The mesh loader ends with `m.UploadMeshData(true)`, [CharacterGeo.cs:240](../../../Assets/_Game/Scripts/Game/CharacterGeo.cs#L240), which frees the CPU copy.

**Files:**
- Modify: `Assets/_Game/Scripts/Game/CharacterGeo.cs` (the IWK mesh loader that ends at line 242)
- Modify: `Assets/_Game/Scripts/Game/WeaponModel.cs` (`getSubDef`, ~line 128)
- Modify: `Assets/_Game/Scripts/Dev/SmokeTest.cs`, `Assets/_Game/Scripts/GameMain.cs` (roster flag)

**Interfaces:**
- Produces:
  - an opt-in `bool readable = false` parameter on the IWK loader; when true, call `UploadMeshData(false)`;
  - `GameMain.randomRoster` (bool), set by `-smoketest-roster random`. Every bot gets a random weapon from `WEAPON_ORDER`, a random sub from `SUB_ORDER` and a random special from `SPECIAL_ORDER`, from a seeded RNG whose seed is logged in `summary.txt`.

- [ ] **Step 1: Reproduce.** Run `Tools/smoke.ps1 -Weapon shooter -Subs boomerang,waddle -Tag t1`. Expected now: FAIL, with the error above in `Builds/Smoke/t1-boomerang/summary.txt`.
- [ ] **Step 2: Fix the loader.** Pass `readable: true` only for `sub_boomerang` `body`. Do not make all meshes readable: it doubles their memory.
- [ ] **Step 3: Add the random roster flag** (`-smoketest-roster random`).
- [ ] **Step 4: Verify.**
  1. Compile check.
  2. Build Windows.
  3. Run `-Subs boomerang,waddle -Tag t1`. Expected: both PASS, and `sub_1a_boomerang.png` shows the boomerang.
  4. Regression: `-Subs bomb,sticky,burst,shaker,seeker,waddle,tracer,boomerang,scan,curtain,sprinkler,mine,beacon,mist -Tag t1r`. Expected: 14 × PASS.
  5. Run `-Roster random -Tag t1x` three times. Expected: 3 × PASS.
- [ ] **Step 5: Log and commit.**

### Task 2: Torpedo kit sub (closes subs)

**Source:** `src/game/kits/torpedo.js` (45 KB). The model is already baked at `Resources/KidWeapons/sub_torpedo/`. If the runtime needs a module-private generator, add it to `hooks.mjs` `EXPOSE` and re-bake with `node --import ./Tools/bake/register.mjs Tools/bake/weapons.mjs`.

**Files:**
- Create: `Assets/_Game/Scripts/Game/Kits/Torpedo.cs`
- Modify: `Assets/_Game/Scripts/Game/Kits/Kits.cs` (`init()`: `SUB["torpedo"] = new Torpedo(); BotBrain.THROWN.Add("torpedo");`, because torpedo.js sets `THROWN.torpedo = true`), `README.md`

**Interfaces:**
- Consumes the `SubKit` hooks:
  - `use(SubSystem, Actor, SubDef)`, `hold`, `blocked`, `tick`, `clear`;
  - `blockShot`, `blockRay`;
  - `damageArea(Vector3 c, float radius, float dmg, int team)` (torpedoes caught in a blast; SubSystem already calls it), `noArc`, `threats`;
  - `KitBot` for the JS `bot:` object at torpedo.js line 736.
- Produces: `Kits.SUB["torpedo"]`. After this, `SubSystem.ported(k)` is true for all 15 `SUB_ORDER` kinds.

- [ ] **Step 1: Translate.** Port torpedo.js to `Torpedo.cs` following the `Waddle.cs` layout:
  - header lists events and what is not ported (online ghosts; Boss splash = Task 17; sounds = Task 11, called by name through `G.audio`);
  - emit the same events with the same payloads;
  - call `G.fx` / `G.audio` where the JS does. Add a method to `FxStubs.cs` only if the JS calls an fx function that the stubs lack.
- [ ] **Step 2: Register** it in `Kits.init()`.
- [ ] **Step 3: Verify.**
  1. Compile check, then build Windows.
  2. Run `-Subs torpedo -Tag t2`. Expected: PASS, and `sub_1a_torpedo.png` / `sub_1b_torpedo.png` show the torpedo.
  3. Re-run the Task 1 regression list with `torpedo` added: 15 × PASS.
  4. Run `-Roster random -Tag t2x` × 3: PASS.
- [ ] **Step 4: Update the README.** In the M3 row and the "What M3 contains" section, list `subs.js` and all 5 sub kits as ported.
- [ ] **Step 5: Log and commit.**

### Task 3: Specials, part 1 — the framework, barrage ×5, bubbler, sonar

**Source:** `src/game/specials.js` (113 KB).
- The header (lines 1–12) defines the contract. A running special is `actor.specialActive = { id, def, t, dur, … }`.
- Its flags steer the actor: `body`, `speed`, `accel`, `noSquid`, `inkProof`, `faceYaw`, `faceMove`, `aimFace`, `firing`, `hang`, `seatY`.
- Hooks: `start`, `tick`, `body`, `weapon`, `filterDamage`, `onSplat`, `shotHit`, `rayHit`, `areaHit`.
- The `IMPL` table starts at line 1245. Tidal Slam (actor.js) and Ink Tempest (weapons.js) are already ported and stay where they are.

**Files:**
- Create: `Assets/_Game/Scripts/Game/Specials/SpecialSystem.cs`. It holds `G.specials`, the helpers (`groundBelow`, `paint`, `blast`, `tickDamage`, `stampFwd`, `stampFront`), the hook dispatch, the world-object list and `ReturnMarker`.
- Create: one file per IMPL entry, holding that special's world-object classes: `Specials/Barrage.cs` (covers `barrage`, `barrage_sticky`, `barrage_burst`, `barrage_seeker`, `barrage_mist`; they throw subs through `SubSystem`), `Specials/Bubbler.cs` (with `Bubble`), `Specials/Sonar.cs`.
- Modify:
  - `Game/Actor.cs`: route every special except slam to `G.specials`; honour the flags above in the movement update; call `filterDamage` and `onSplat`.
  - `Game/Weapons.cs`: call the `shotHit` / `rayHit` / `areaHit` hooks at the three places already marked with comments (~lines 970, 1370, 1438).
  - `Game/BotBrain.cs`: the `wantSpecial` call sites at ~lines 305 and 370, plus bots.js's per-special use logic for these ids.
  - `Dev/SmokeTest.cs` and `GameMain.cs`: add `-smoketest-special ID` → `GameMain.startSpecial`. Save `special_1a_<id>.png` / `special_1b_<id>.png` 0.3 s and 1.5 s after the local player starts a special, the same way the sub screenshots are taken.

**Interfaces:**
- Produces the `ISpecialImpl` interface. Every member defaults to a no-op:

```csharp
public interface ISpecialImpl {
  void start(Actor a, SpecialState s); void tick(Actor a, SpecialState s, float dt);
  bool body(Actor a, SpecialState s, float dt);                 // true = owns movement this frame
  bool weapon(Actor a, SpecialState s, float dt, WeaponInput inp); // true = owns the trigger this frame
  float filterDamage(Actor a, SpecialState s, float amount, Actor attacker, string source);
  void onSplat(Actor a, SpecialState s);
  bool shotHit(Vector3 prev, Vector3 pos, int team, float dmg);
  float rayHit(Vector3 from, Vector3 dir, float len, int team, float dmg);
  void areaHit(Vector3 c, float radius, float dmg, int team);
}
```

- Produces `SpecialSystem.register(string id, ISpecialImpl impl)` and `SpecialSystem.ported(string id)`. An unported id falls back to slam with a single warning.
- Icon badges (`iconBadge`, canvas + `SPECIAL_ICONS`) are a plain team-coloured disc until Task 12 provides icon sprites.

- [ ] **Step 1: Port the framework** and `SpecialState` (fields from the header contract). Keep the C# field names identical to the JS.
- [ ] **Step 2: Port barrage (5 ids), bubbler and sonar**, with their world objects.
- [ ] **Step 3: Wire the hooks** into Actor, Weapons and BotBrain, and add the smoke flag.
- [ ] **Step 4: Verify.**
  1. Compile check, then build Windows.
  2. For each id in `barrage barrage_sticky barrage_burst barrage_seeker barrage_mist bubbler sonar`, run `-Special <id> -Tag t3-<id>`. Expected: PASS. The special screenshots show the effect, and `summary.txt` has a `specials` count ≥ 1 for the player.
  3. Run `-Weapon twins -Special slam -Tag t3-slam`: PASS (no regression).
- [ ] **Step 5: Log and commit.**

### Task 4: Specials, part 2 — strike, zooka, wail, blower

**Source:** `IMPL.strike` (with `Missile`), `IMPL.zooka`, `IMPL.wail` (with `Speaker`), `IMPL.blower` (with `Tornado` and `Twister`). Prop models: `sp_zooka` and `sp_blower` are already baked. The missile and speaker come from `special-props.js` (see Task 5 step 1, which can be done first).

**Files:**
- Create: `Specials/Strike.cs`, `Specials/Zooka.cs`, `Specials/Wail.cs`, `Specials/Blower.cs`
- Modify: `BotBrain.cs` (use logic for these ids)

**Interfaces:**
- Consumes: `ISpecialImpl` and `SpecialSystem.register` (Task 3); `Projectiles.fireCustom` options (Weapons.cs ~line 559) for the zooka shots.

- [ ] **Step 1: Port the four specials.**
- [ ] **Step 2: Verify.** Compile check, build, then `-Special <id> -Tag t4-<id>` for `strike zooka wail blower`. Expected: 4 × PASS, with screenshots showing the effect.
- [ ] **Step 3: Log and commit.**

### Task 5: Specials, part 3 — kraken, jetpack, stamp, booyah, zipcaster, crab + special props (closes M3)

**Source:** `IMPL.kraken`, `jetpack`, `stamp` (with `ThrownStamp`), `booyah` (with `Orb`), `zipcaster`, `crab` (with `Shell`). These own the body through the `body`, `seatY`, `hang` and `faceYaw` flags. Models: `special-props.js` `getSpecialProp(kind)`; `sp_jetgun` and `sp_stamp` are already baked.

**Files:**
- Create: `Tools/bake/specials.mjs`. It imports `game/special-props.js` and writes every `getSpecialProp` kind as IWK1 meshes plus a JSON of part pivots and grips to `Assets/_Game/Resources/SpecialProps/<kind>/`, using the same writer and manifest style as `weapons.mjs`.
- Create: `Game/SpecialProps.cs` (loader, like `WeaponModel.getSubDef`), `Specials/Kraken.cs`, `Jetpack.cs`, `Stamp.cs`, `Booyah.cs`, `Zipcaster.cs`, `Crab.cs`
- Modify: `Character*.cs` if a special needs a pose layer the JS adds (follow `character.js`), `BotBrain.cs`, `README.md`

**Interfaces:**
- Produces: `SpecialProps.get(string kind)`, returning meshes and pivots. After this task, `SpecialSystem.ported(id)` is true for all 19 `SPECIAL_ORDER` ids.

- [ ] **Step 1: Write and run the bake.** Write `specials.mjs` and run `node --import ./Tools/bake/register.mjs Tools/bake/specials.mjs`. Expected: one folder per kind and no `unmapped attribute` errors.
- [ ] **Step 2: Port the six specials.**
- [ ] **Step 3: Verify each.** Compile check, build, then `-Special <id> -Tag t5-<id>` for the 6 ids. Expected: 6 × PASS.
- [ ] **Step 4: Run the full M3 regression.**
  1. All 12 mains with `-Weapon <id> -Tag t5w-<id>`.
  2. All 19 specials.
  3. `-Roster random -Tag t5x` × 5.
  Expected: every run PASS. Compare the avg fps against the Task 0 baseline: no drop over 10 %.
- [ ] **Step 5: Update the README.** Set the M3 row to done and add a "What M3 contains" entry for specials.
- [ ] **Step 6: Phone gate M3.** Build the APK and ask the user to:
  1. play a match;
  2. trigger a special from the touch button (and aim zooka / strike by touch);
  3. try every special in the touch layout (Review Focus 1).
  Record the verdict. Log and commit.

### Task 6: Stage pipeline — browser baker, texlib, baked AO on Tidewater (M4 start)

**Why a browser:**
- `texlib.js` generates its textures on the GPU (WebGL array render targets).
- `props.js`, `decor.js` and `murals.js` draw on 2D canvas with web fonts.
- Node cannot run any of these. Headless system Chrome can.
- puppeteer is not installed and cannot be downloaded, so drive Chrome through the DevTools protocol with Node 24's built-in `WebSocket`.

**Files:**
- Create: `Tools/bake/browser.mjs`, the browser bake driver. It:
  1. starts a `node:http` static server on 127.0.0.1, rooted at the project folder (so `/reference/inkwave/...` and `/Tools/bake/pages/...` are served);
  2. accepts `POST /save?path=<rel>` and writes the body under `Assets/_Game/` (e.g. `Baked/…`, `Audio/…`), rejecting any path containing `..` or a drive letter;
  3. launches Chrome with `--headless=new --remote-debugging-port=<port> --user-data-dir=<temp dir> --no-first-run`;
  4. reads `http://127.0.0.1:<port>/json/version`, connects to the page target's WebSocket, `Page.navigate`s to `/Tools/bake/pages/<page>.html?<args>`, and waits until the page sets `window.__bakeDone` (poll with `Runtime.evaluate`);
  5. forwards the page's console to stdout, exits non-zero on `window.__bakeError`, and kills Chrome on exit.
  Usage: `node Tools/bake/browser.mjs <page> [query]`. A second mode takes web-reference screenshots: `node Tools/bake/browser.mjs --url <served path> --shot <png> [--wait <s>] [--size 1280x720]`. It navigates, waits, and writes `Page.captureScreenshot` from Node.
- Create: `Tools/bake/pages/importmap.json`, mirroring the import map in `reference/inkwave/index.html` with paths rewritten to `/reference/inkwave/vendor/three/...`.
- Create: `Tools/bake/pages/texlib.html` + `texlib.js`. They call `createTextureLibrary(renderer, { size })` for size 512 (medium/high) and 256 (low). For every layer, read albedo / normal / orm (`setRenderTarget(target, layer)` + `readRenderTargetPixels`) and save PNGs to `Baked/Texlib/<size>/<map>_<layer:00>.png`, plus `Baked/Texlib/layers.json` (name → index, from `lib.layers`).
- Make `Tools/bake/iwk.mjs` isomorphic: `writeMesh` builds an `ArrayBuffer`, and only the Node entry point touches `fs`. The browser pages then POST IWK1 files through `/save`.
- Create: `Assets/_Game/Scripts/Editor/TexlibImporter.cs`. It builds `Texture2DArray` assets `Baked/Texlib/texlib_{albedo,normal,orm}_{256,512}.asset`: albedo sRGB, the others linear, full mips, Repeat, aniso 4.
- Create: `Assets/_Game/Scripts/Editor/LightmapImporter.cs`. It copies `reference/inkwave/assets/lightmaps/<id>.{png,json}` (and `<id>.zones.*`) into `Baked/Lightmaps/` with import settings: linear, mipmaps, no compression artefacts (R8 or RGBA32).
- Modify:
  - `Shaders/Level/Level.shader`: port the texlib sampling (`TEXLIB_GLSL`) and the remaining `levelMaterial.js` / `inkShading.js` surface features. The surface layer per face comes from the stage's SURF ids. Also sample the AO lightmap through the face's `light` atlas rect.
  - `World/LevelView.cs`: load the AO, then call `level.layoutLightmap(meta.ppm, meta.size)` and compare `level.layoutHash` to `meta.hash`, as in main.js `_loadLightmap` (lines 405–420). On a mismatch: log one warning, set `lightSize = 0`, clear face lights, render without AO.
  - `GameBuilds.Shaders`, if new shaders appear.

**Interfaces:**
- Produces:
  - the browser bake driver and `save(path, arrayBuffer|blob)` (page side) for Tasks 7–17;
  - `Texlib` runtime access: `Texlib.layer(string name) → int`, plus the three arrays bound as global shader textures `_TexlibAlbedo`, `_TexlibNormal`, `_TexlibOrm`;
  - `Lightmaps.load(string layoutId, Level level) → Texture2D` (null on mismatch or absence).

- [ ] **Step 1: Bake texlib.** Write `browser.mjs` and the texlib page. Run `node Tools/bake/browser.mjs texlib "size=256"` and `"size=512"`. Expected: PNG count = layers × 3 per size, and the console shows no WebGL errors.
- [ ] **Step 2: Import.** Run the importers in batch mode (`-executeMethod Inkwave.EditorTools.TexlibImporter.Run`, then `LightmapImporter.Run`).
- [ ] **Step 3: Check the layout hash.** Load Tidewater and log `level.layoutHash`. Expected: `8b3b5d82`, the value in `reference/inkwave/assets/lightmaps/tidewater.json`. If it differs, fix the C# hash or the layout until it matches. This proves the layout and the hash both match the web game.
- [ ] **Step 4: Port the shader work.** Texlib + AO into `Level.shader`.
- [ ] **Step 5: Compare against the web.**
  1. Unity side: `-executeMethod Inkwave.EditorTools.StageCapture.Tidewater -captureOut Builds/Captures/t6`.
  2. Web side: `node Tools/bake/browser.mjs --url "/reference/inkwave/index.html?map=tidewater&time=day&autostart=60" --shot Builds/Captures/t6/web.png --wait 10`. Match the camera pose of one Unity capture (set the web camera through the page's dev hooks if it has them, otherwise pick the Unity pose to match the web shot).
  3. Expected: the same surface patterns and AO darkening in corners. Put both images in the log.
- [ ] **Step 6: Test a stale lightmap** (Review Focus 5). Temporarily edit the copied `tidewater.json` hash, run the smoke and expect PASS plus exactly one stale-lightmap warning. Restore the file.
- [ ] **Step 7: Smoke and log.** Smoke `-Tag t6`: PASS. Log and commit.

### Task 7: Tidewater fully dressed — props, murals, decor, dressing, environment, post stack

**Source:**
- `stages/tidewater/{props,surfaces,murals}.js`
- shared: `world/props.js`, `decor.js`, `dressing.js`, `murals.js`, `environment.js` (sky dome, sun/hemisphere light, sea, pilings, far scenery, themes `day` / `sunset` / `golden`; `MAPS[].times` maps day→day and dusk→sunset)
- `core/renderer.js` (post: bloom threshold 0.9, neutral tonemap, grade/vignette)
- **How main.js builds a stage:** read main.js from `_loadLightmap` back to the stage-build function (around lines 380–420) and reproduce that call order exactly in the bake page.

**Decision:**
- Bake everything static in the browser: meshes, textures, mural canvases, instance transforms.
- Translate to HLSL only what animates: the sea, sky clouds, flags and swaying props.
- Paintable level faces stay runtime (`Level` / `PaintSystem`), because ink needs them.

**Files:**
- Create: `Tools/bake/pages/stage.html` + `stage.js` (`?stage=<id>&time=<day|dusk>&quality=<low|medium|high>`). It builds the stage dressing as main.js does, then exports:
  - (a) each static mesh as IWK1, merged per material when they share a world transform group;
  - (b) repeated props as instance lists (`{mesh, material, matrices[]}`), not merged copies;
  - (c) a material descriptor JSON per material: three material type, color, maps by baked texture path, roughness, metalness, emissive, transparent, side, vertexColors, and an `onBeforeCompile` tag naming the custom shader chunk if any;
  - (d) textures and mural canvases as PNG;
  - (e) the environment's light/theme numbers as JSON.
  Output: `Baked/Stages/<id>/<time>/…` + `stage.json` (manifest).
- Create: `Editor/StageImporter.cs`. It turns `stage.json` into a prefab `Baked/Stages/<id>/<id>_<time>.prefab`: MeshRenderers for static meshes, a `PropInstances` component drawing the instance lists with `Graphics.RenderMeshInstanced`, and materials mapped by descriptor type:
  - `MeshStandardMaterial` / `MeshPhysicalMaterial` → new `Inkwave/Prop` (URP lit: base colour × vertex colour × map, roughness/metalness, texlib layer option, AO lightmap option);
  - `MeshBasicMaterial` → `Universal Render Pipeline/Unlit`;
  - each `onBeforeCompile` tag → a dedicated HLSL port.
- Create: `Shaders/Env/Sky.shader`, `Shaders/Env/Sea.shader` (ports of environment.js GLSL), `Shaders/Prop/Prop.shader`
- Create: `World/Environment.cs`. It holds the runtime parts of environment.js: theme switch, sun/hemisphere/ambient values, fog/haze, animated sea parameters, gulls/sailboats if they animate.
- Modify: `GameMain.cs` (load the stage prefab for map + time, apply the theme, apply the post stack per `Config.QUALITY`), the URP Volume profile (Bloom threshold 0.9, Neutral tonemapping, colour grade), `GameBuilds.Shaders`

**Interfaces:**
- Produces:
  - `StageLoader.load(string id, string time) → GameObject`;
  - `Environment.setTheme(string name)`;
  - the `stage.html` exporter reused unchanged by Task 8.

- [ ] **Step 1: Bake.** Write `stage.html` / `stage.js` and run `node Tools/bake/browser.mjs stage "stage=tidewater&time=day&quality=low"`, then again with `time=dusk`. Expected: `stage.json` plus files, and no console errors (fonts: load `assets/fonts/*.woff2` through `FontFace` before drawing murals, as murals.js line 39 does).
- [ ] **Step 2: Import** (`-executeMethod Inkwave.EditorTools.StageImporter.Run -stage tidewater`). Port the sky, sea and prop shaders and `Environment.cs`.
- [ ] **Step 3: Compare against the web.** Capture side by side at 3 poses (spawn loggia, the Square at mid, the overview), day and dusk, as in Task 6 step 5. Expected: the same props, murals, sky and sea, with differences explainable only by the renderer. Log all 12 images.
- [ ] **Step 4: Check performance.** Smoke `-Tag t7`: PASS. Record the avg fps (Review Focus 2) and the Android APK size.
- [ ] **Step 5: Phone gate (performance).** Build the APK. Ask the user to play a full Tidewater match on day and on dusk (add a temporary `-map` / time rotation in GameMain if menus are not ready: alternate day and dusk each match). If they report lag: profile with the Unity Profiler over USB, cut the low tier (instance draw distance, texture size, shadow distance) and re-test before Task 8.
- [ ] **Step 6: Log and commit.**

### Task 8: The other six stages

Per stage, in this order (simplest first):

| Stage id | Sources |
|---|---|
| `kelpline` | `stages/kelpline/{layout,props,surfaces,murals}.js` |
| `saltpan` | `stages/saltpan/*.js` |
| `lockgate` | `stages/lockgate/*.js` |
| `terraces` | `stages/terraces/*.js` (layout.js is 67 KB) |
| `crossmarket` | `stages/crossmarket/*.js` (11 modules: buildings, hall, kit, landing, street, tram, scenery …) |
| `halyard` | no stage folder: data in `world/maps.js` (`HALYARD`), `dressing.js`, `murals.js`, `props-marina-dock.js`, `props-marina-vessels.js`; environment "marina water mode" (see environment.js header) |

**Decision for layouts:** bake them, don't hand-translate.
- `Tools/bake/layouts.mjs` (Node: layouts and `mapkit.js` are pure data) imports `world/maps.js` `MAP_LAYOUTS` (and the stage registry) and writes `Assets/_Game/Resources/Stages/<id>/layout.json` with everything `Level.cs` consumes. Read Level.cs's inputs first and serialize exactly those.
- Bake both the Turf War variant and the `zones` variant: `variants.js` `onlyIn` / `notIn` tags. The zones variant is needed by Task 16.
- `World/LayoutData.cs` loads that JSON into the same structures `TidewaterLayout.cs` produces.
- **Oracle:** the loaded layout's `layoutHash` must equal `assets/lightmaps/<id>.json` `hash` (and `<id>.zones.json` for the zones variant).
- Check Tidewater through the JSON path first: same hash as the hand-ported `TidewaterLayout.cs`.

**Files:**
- Create: `Tools/bake/layouts.mjs`, `World/LayoutData.cs`, `Baked/Stages/<id>/…` (via Task 7's exporter), `Shaders/Env/Marina*.shader` if halyard's water differs
- Modify: `GameMain.cs` / `Match.cs` (map id from `GameMain.startMap`), `Dev/SmokeTest.cs` (`-smoketest-map ID`), `Config.cs` only if a map field is missing

**Interfaces:**
- Produces: `LayoutData.load(string id, string mode) → Layout` (mode `"turf"` or `"zones"`) and `GameMain.startMap`.

- [ ] **Step 1: Bake all layouts.** Write `layouts.mjs` and `LayoutData.cs`, then run `node --import ./Tools/bake/register.mjs Tools/bake/layouts.mjs`. Expected: 7 `layout.json` files (+ 5 zones variants: tidewater, crossmarket, halyard, saltpan, terraces, which are the stages with `.zones` lightmaps).
- [ ] **Step 2: Check the hashes.** For each stage and mode, log the hash next to the lightmap JSON hash. Expected: all equal.
- [ ] **Step 3: Per stage** (in table order):
  1. bake day + dusk with `browser.mjs stage …`;
  2. import;
  3. port any stage-specific animated shader;
  4. smoke `-Map <id> -Tag t8-<id>`: PASS (bots must navigate: both teams' turf > 0 is part of PASS);
  5. take 3-pose captures vs the web;
  6. log it.
- [ ] **Step 4: Commit after each stage.**

### Task 9: M4 close — map rotation, size budget, phone gate

- [ ] **Step 1: Add a temporary rotation.** GameMain rotates `OFFLINE_MAPS` × `TIMES` match after match, until menus arrive in Task 14.
- [ ] **Step 2: Check memory and size.** Measure the APK size and, through `Profiler`, the texture memory per stage on the low tier. Expected: APK under 150 MB. Flag anything near Play's 200 MB compressed download limit (Review Focus 2).
- [ ] **Step 3: Phone gate M4.** The user plays at least 3 different stages. Record the verdict. Update the README (M4 done). Log and commit.

### Task 10: FX (M5)

**Source:**
- `fx/fx.js` (124 KB): the particle and decal systems;
- `fx/fxHooks.js`: event bus → fx calls;
- `fx/screenfx.js`: screen overlays such as ink on the lens, damage and low-ink vignettes;
- `fx/swimWake.js`.

**Files:**
- Create: `Fx/FxSystem.cs` (`class FxSystem : Fx`, overriding **every** method of `Fx` in `FxStubs.cs`), `Fx/FxHooks.cs`, `Fx/ScreenFx.cs`, `Fx/SwimWake.cs`, and the shaders under `Shaders/Fx/` they need
- Modify: `GameMain.cs` (`G.fx = new FxSystem(...)`), `GameBuilds.Shaders`

**Decisions:**
- **Particles** are pooled and drawn with `Graphics.RenderMeshInstanced` in batches. This follows the existing InkBlob pattern; do not use the Unity ParticleSystem.
- Pool sizes and caps come from the JS, scaled by the `Config.QUALITY` fields fx.js reads.
- **Screen FX:** a full-screen uGUI `RawImage` overlay with a material, above the 3D view and below the HUD, ported from screenfx.js's shaders.
- **Signatures:** `FxStubs.cs` stays as the contract. Add a method only when a JS caller needs one, and keep its JS name.

- [ ] **Step 1: Record the before state.** Smoke `-Tag t10-before`, and note the avg fps.
- [ ] **Step 2: Port fx.js** into `FxSystem`, then fxHooks, screenfx and swimWake.
- [ ] **Step 3: Verify.**
  1. Compile check, build.
  2. Smoke `-Tag t10` plus `-Weapon roller`, `-Weapon charger`, `-Special booyah`: PASS.
  3. The fire and sub screenshots now show splashes, muzzle flashes and wakes.
  4. Avg fps drop under 10 % against t10-before; otherwise lower the caps on the low tier.
- [ ] **Step 4: Phone check, log and commit.**

### Task 11: Audio (M5 close)

**Source:** `audio/audio.js` (139 KB, procedural WebAudio SFX) and `audio/music.js` (71 KB, procedural soundtrack). Both are written to render through `OfflineAudioContext` (see the audio.js header and `tools/audio-test.mjs`), so bake them in headless Chrome.

**Files:**
- Create: `Tools/bake/pages/audio.html` + `audio.js`. They:
  - enumerate every one-shot and loop name in audio.js;
  - render each through an `OfflineAudioContext` at 48 kHz (the builder's channel count) and encode it as 16-bit PCM WAV;
  - render loops ≥ 4 s with a crossfaded seam;
  - render each `params` mode of a parameterised loop (e.g. `zone_hum { mode, tension }`) as its own clip;
  - render each music.js track or section as WAV.
  Output: `Assets/_Game/Audio/Sfx/<name>.wav`, `Assets/_Game/Audio/Music/<track>.wav`, `Assets/_Game/Audio/audio.json` (name → clip, loop flag, base gain).
- Create: `Audio/AudioSystem.cs` (`class AudioSystem : AudioSys`), `Audio/MusicPlayer.cs`, `Assets/_Game/Audio/Mixer.mixer` (Master / Music / Sfx groups; a duck snapshot for `audio.duck`)
- Modify: `GameMain.cs` (`G.audio = …`), the import settings via an editor postprocessor (SFX: Decompress On Load, Vorbis 0.7; music: Streaming, Vorbis 0.5)

**Decisions:**
- Voice caps as in the JS: `MAX_VOICES = 48`, `MAX_LOOPS = 24`, oldest stolen first.
- Volume taper: `v^1.5`.
- Positional sounds use a 3D AudioSource with a custom rolloff approximating `distCut` + inverse distance. Do not use HRTF.
- Loop pitch and volume follow the JS conventions in the audio.js header.

- [ ] **Step 1: Bake.** Run `node Tools/bake/browser.mjs audio`. Expected: the clip count equals the name count the page printed, with zero failures.
- [ ] **Step 2: Port the runtime.** `AudioSystem`, `MusicPlayer` and the mixer.
- [ ] **Step 3: Verify.** Smoke `-Tag t11`: PASS, and `player.log` has 0 `missing clip` warnings. `AudioSystem` logs each unknown name once; Review Focus 5.
- [ ] **Step 4: Phone check, then M5 gate.** Ask the user to listen on the phone (shots, splats, swim, music, the judge drum roll). Update the README (M5 done). Log and commit.

### Task 12: UI foundation — fonts, icons, art, tokens (M6 start)

**Files:**
- Fonts: the user must supply **TitanOne-Regular.ttf** and **Rubik** TTF (Regular, Bold, ExtraBold; OFL, from Google Fonts). Put them in `Assets/_Game/UI/Fonts/`. Ask the user. Unity cannot import the repo's `.woff2` files, and converting WOFF2 needs a tool that cannot be downloaded here.
  Then create dynamic TMP font assets (`Editor/UiFontSetup.cs`).
- Create: `Tools/bake/pages/ui-art.html` + `ui-art.js`. They render every icon in `ui/ui-icons.js`, the art in `ui/menu-art.js`, and the stage thumbnails from `world/mapThumb.js` (SVG strings → image → canvas) to PNG at 128 and 256 px. Output: `Baked/UI/{icons,art,maps}/<name>.png` + `ui.json`.
- Create: `UI/UiIcons.cs` (sprite lookup by JS name), `UI/UiTheme.cs` (colours, radii and font sizes from `styles/ui.css`, `styles/hud.css` and `docs/CONTRACTS.md` §"Fonts / UI tokens"), `UI/UiUtil.cs` (port of `ui-util.js`)
- Modify: `Specials/SpecialSystem.cs` (icon badges use `UiIcons`)

- [ ] **Step 1: Get the fonts.** Ask the user for the font files, then create the TMP assets.
- [ ] **Step 2: Bake the UI art.** Run `node Tools/bake/browser.mjs ui-art`, then import the sprites (Sprite, no mips, ASTC on Android).
- [ ] **Step 3: Verify.** Compile check. Add an editor capture `UiCapture.Icons` that tiles every icon into one PNG, and log it.
- [ ] **Step 4: Log and commit.**

### Task 13: Full HUD, minimap, diorama overlay, Super Jump

**Source:**
- `ui/hud.js` (131 KB) + `styles/hud.css`, replacing `HudLite`;
- `game/minimap.js`;
- `ui/diorama.js` (the overlay pinned onto the camera diorama `CameraRig` already flies);
- Super Jump to teammates and beacons (check which parts `Actor.cs` already has: `superJumpState`, `_jumpBeacon`).

**Files:**
- Create: `UI/Hud.cs` (+ split files per hud.js section if over ~1500 lines), `UI/Minimap.cs` (the JS draws to 2D canvas: render to a `Texture2D` from the layout, then refresh the paint layer from the turf grid at ~5 Hz on the low tier), `UI/Diorama.cs`
- Delete: `UI/HudLite.cs`, once `Hud.cs` covers all of it
- Modify: `GameMain.cs`, `TouchControls.cs` (map button hold → diorama; Super Jump target taps)

- [ ] **Step 1: Port** hud.js, then minimap, then diorama + Super Jump.
- [ ] **Step 2: Verify.** Compile check, build, smoke `-Tag t13`: PASS. The screenshots show the full HUD (timer, squads, ink, special, kill feed, minimap).
- [ ] **Step 3: Capture aspect ratios.** Add `-screen-width 2400 -screen-height 1080` (20:9) and 1920×1080 smoke runs, and check that nothing overlaps the touch buttons.
- [ ] **Step 4: Run `-Roster random` × 2.** Expected: PASS.
- [ ] **Step 5: Touch check** (Review Focus 1). Build the APK and ask the user to use the map hold and Super Jump by touch. Record the verdict.
- [ ] **Step 6: Log and commit.**

### Task 14: Menus and game flow

**Source:** `ui/menus.js` (271 KB) + `styles/ui.css`, plus the flow parts of `main.js`. The screens:
- title, main, mode (Turf War; Zone Control and Boss added by Tasks 16–17);
- setup (map cards with thumbnails, time, difficulty from `DIFFICULTY`, team palette incl. `COLORBLIND_PALETTE`);
- loadout (weapon, sub, special, in `*_ORDER`), locker (`CharacterStyle` customisation);
- settings (`DEFAULT_SETTINGS`), how-to, credits, pause, results;
- `PROGRESSION`, as the JS uses it.

**Skip:** online lobby entries, news pop-ups and any "Online" button.

**Files:**
- Create: `UI/Menus/*.cs` (one file per screen), `Core/Settings.cs` (persistence: JSON in `PlayerPrefs` key `colorsplash.settings`; keys and defaults exactly `DEFAULT_SETTINGS`), `UI/Menus/Credits.cs` (shows `LICENSE-INKWAVE.txt` text and "Original game: INKWAVE by Jayden Davis")
- Modify:
  - `GameMain.cs`: boot into the title screen, not straight into a match. Smoke flags still skip menus. Remove the Task 9 rotation.
  - title strings: `GAME_TITLE` shows **Color Splash**.
  - Android back button: pause in a match, back in menus.

- [ ] **Step 1: Port the screens** in flow order, title → results.
- [ ] **Step 2: Wire settings.** Load, save and apply settings (quality, volumes, touch layout, sensitivity, colour-blind palette).
- [ ] **Step 3: Verify.** Compile check, build. Smoke still PASSes (`-Tag t14`): smoke bypasses menus.
- [ ] **Step 4: Add a menu walk.** Add `Dev/MenuWalk.cs` (`-menuwalk -menuwalk-out DIR`): it visits every screen, screenshots it, and exits 0 with no errors. Expected: one PNG per screen, logged.
- [ ] **Step 5: Test settings persistence.** Change settings, quit, relaunch: the values persist.
- [ ] **Step 6: Test backgrounding** (Review Focus 3). Implement pause on `OnApplicationPause(true)` / `OnApplicationFocus(false)` in a match. On the phone, press home mid-match, return, and expect the pause menu, then resume with audio and no errors (check logcat with `adb logcat -s Unity` if a device is attached).
- [ ] **Step 7: Touch check.** The user navigates every menu by touch on the phone. Log and commit.

### Task 15: Showcase — loadout pedestal and results podium (M6 close)

**Source:** `game/showcase.js` (161 KB). It is a studio-lit overlay scene: one kid on a pedestal for the loadout screen, and the team podium with confetti, ink bursts or a defeat drizzle for the results.

**Files:**
- Create: `Game/Showcase.cs` (its own layer and camera rendering into an RT composited over the frame, as the JS composites over the finished frame), any baked podium meshes via a Node or browser bake (whichever its generators need)
- Modify: `UI/Menus/Loadout.cs`, `UI/Menus/Results.cs`

- [ ] **Step 1: Port the showcase**, bake its static meshes, and wire it to the loadout and results screens.
- [ ] **Step 2: Verify.** The menu walk shows the pedestal and the podium (win and lose), and smoke PASSes.
- [ ] **Step 3: Phone gate M6.** The full flow on the phone: title → setup → loadout → match → results → again. Check fps (Review Focus 2). Update the README (M6 done). Log and commit.

### Task 16: Zone Control (M7)

**Source:** `game/zones.js`, `world/zones-data.js`, `world/variants.js`, `fx/zoneMarks.js`, `ZONES` in config, the bots.js zone layer, the hud.js zone parts, and the `zones` lightmaps.

**Files:**
- Create: `Game/Zones.cs`, `World/ZonesData.cs`, `Fx/ZoneMarks.cs`
- Modify: `Match.cs` (mode), `BotBrain.cs` (zone layer), `UI/Hud.cs`, `UI/Menus/Mode.cs`, `LayoutData.load(id, "zones")`, `SmokeTest.cs` and `GameMain.cs` (`-smoketest-mode zones|boss` → `GameMain.startMode`, default `"turf"`)

- [ ] **Step 1: Port** the rules, the data, the marks, the bot layer and the HUD.
- [ ] **Step 2: Verify.** For each of the 5 zone stages, run `-Mode zones -Map <id> -Tag t16-<id>`: PASS. Extend the zones-mode summary with zone ownership and points. The zone lightmap hash matches.
- [ ] **Step 3: Phone check, log and commit.**

### Task 17: Boss mode (optional — ask the user before starting)

**Source:** `src/boss/*` (10 files, ~280 KB), `ui/hud-boss.js`, `ui/boss-art.js`, `audio/bossAudio.js`, `docs/BOSS.md`. Respect `noBoss` maps and `bossFallbackMap`.

**Bakes:** the boss model geometry (`bossModelGeo.js`, `bossModel.js`) via Node, or via the browser if it uses canvas. Boss sounds and art go through the Task 11 and Task 12 pages.

- [ ] **Step 1: Ask the user** whether to port Boss mode now, later, or never. Stop here if not now.
- [ ] **Step 2: Port** (files under `Assets/_Game/Scripts/Boss/`, one per JS file). Verify with `-Mode boss -Tag t17`: PASS. Phone check. Update the README (M7). Log and commit.

### Task 18: Character material polish (deferred M2 work)

**Source:** `game/character-mats.js` (109 KB) and the face shader parts of `character-face.js`. Currently missing in `Shaders/Kid/Kid.shader`: cloth graphics and patterns, the lid and mouth vertex rig (blinks are drawn on the eyeball instead), the parallax iris, gummy hair, the tank gauge.

**Decision for the vertex rigs** (A4: Unity gives only skinned positions):
1. Evaluate the JS vertex-shader lid and mouth deformation in Node at key parameter values.
2. Bake them as **blend shapes** on the face mesh.
3. Drive the blend weights from the same parameters at runtime.
Everything that is per-pixel ports directly to HLSL.

- [ ] **Step 1: Port** the cloth graphics, iris, hair and tank to the HLSL shaders, then bake the lid and mouth blend shapes (extend `Tools/bake/character.mjs`).
- [ ] **Step 2: Compare against the web.** `KidCapture.Lineup` against the web character: serve `reference/inkwave/tools/character-lab.html` through `browser.mjs` and screenshot the same styles. Log both.
- [ ] **Step 3: Smoke and phone check** (kid count × shader cost on the low tier). Log and commit.

### Task 19: Mobile polish and release (M8)

**Files:**
- Modify:
  - `Editor/ProjectSetup.cs`: version `1.0.0`, bundleVersionCode `1`, targetSdk 36, minSdk 26, ARM64 only, no INTERNET permission. Check `Library/Bee/Android/.../AndroidManifest.xml` after a build.
  - `Editor/GameBuilds.cs`: add `BuildAndroidRelease` (AAB, non-development, `-keystore` path and alias read from environment variables `CS_KEYSTORE`, `CS_KEYSTORE_PASS`, `CS_KEY_ALIAS`, `CS_KEY_PASS`; never hard-coded).
  - `.gitignore`: add `*.keystore`, `*.jks`.
- Create: `Core/DeviceProfile.cs`. It auto-picks the quality tier on first launch from `SystemInfo` (RAM, GPU tier) and sets `Application.targetFrameRate = 60`. The user can override it in settings.
- Icon and splash: ask the user for Color Splash icon art, or offer to bake one from the game's own art. Never use INKWAVE branding.

- [ ] **Step 1: Profile on the device.** Over USB with the Unity Profiler, play one match per stage on the low tier, and fix the top 3 costs. Record before and after fps.
- [ ] **Step 2: Keystore.** Ask the user to create the release keystore themselves (`keytool` from the Unity-bundled JDK) and keep the passwords. Then build the AAB with `BuildAndroidRelease`.
- [ ] **Step 3: Verify.**
  - the AAB builds;
  - the merged manifest has no INTERNET permission;
  - a release APK (or `bundletool` install, if available) plays a full match in airplane mode on the phone.
- [ ] **Step 4: Update the README** (M8, how to build a release). Log, commit, and tell the user the build is a release candidate.

---

## Part C — The verification log (`docs/PORT_LOG.md`)

Claude will verify each task from this file, so every claim must point at evidence on disk.

```markdown
# Port log — Color Splash

## Task N: <title> — <YYYY-MM-DD>
- Files: <created / modified paths>
- JS sources ported: <src/... paths> (fully | partially: <what is missing and why>)
- Compile: `Tools/cc.ps1` → exit 0
- Builds: Windows Succeeded (log line) | Android Succeeded (APK path, size MB)
- Smoke: <tag> → PASS (Builds/Smoke/<tag>/summary.txt), fps avg X (baseline Y)
- Captures: <paths> (+ the web reference paths)
- Phone: <user's verdict, verbatim> | not required for this task
- Known gaps / deviations from the JS: <list, or "none">
- Commit: <hash> | not committed (user declined)
```

When a step fails and you change approach, write down why in "Known gaps / deviations". Never mark a task done with a FAIL, a TIMEOUT or a skipped step. Report it in the log instead and stop for the user.
