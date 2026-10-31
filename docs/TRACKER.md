# Color Splash — port tracker

This file is the single place that says **where the port stands**: what is finished end to end, what is in
progress, and what remains. Read it first in a new session.

- **The plan** (task-by-task instructions, Tasks 0–19): `docs/superpowers/plans/2026-10-31-color-splash-port.md`
- **The evidence log** (per task: files, commands, captures, smoke results, commits): `docs/PORT_LOG.md`
- **The project overview + milestone table:** `README.md`

Last updated: 2026-10-31, after Task 7 part 2c (commit `c8e6e96`).

---

## 1. Project in one paragraph

Color Splash is a Unity 6000.5.9f1 (URP 17.5) C# port of INKWAVE (MIT, © Jayden Davis, three.js), built as an
offline Android game. The package is `com.zoiko.colorsplash`. The reference source is a read-only clone in
`reference/inkwave` (git-ignored). Gameplay runs in "three space": everything sits under a world root mirrored on Z,
and the JS numbers and camelCase names are kept for traceability. Static procedural content is **baked from the
original JS** instead of being translated:
- under Node: `Tools/bake/*.mjs` (kid, weapons, special props, texlib slots);
- in headless Chrome over CDP: `Tools/bake/browser.mjs` + `Tools/bake/pages/*` (texlib, prop colliders, the stage
  export).

Runtime behaviour and shaders are ported by hand.

## 2. How to resume (commands)

All paths are relative to `D:\color_splash\Inkwave_anroid`. Run git only inside this folder: it is its own repo
(origin `https://github.com/KAMANIshashikanth/Inkwave_anroid.git`, branch `main`), nested in the parent repo
`D:\color_splash`.

| What | Command | Pass condition |
|---|---|---|
| Compile check (works with the editor open) | `powershell -ExecutionPolicy Bypass -File Tools/cc.ps1` | last line `exit 0` |
| Windows build (editor closed) | `& "C:\Program Files\Unity\Hub\Editor\6000.5.9f1\Editor\Unity.exe" -batchmode -quit -projectPath D:\color_splash\Inkwave_anroid -executeMethod Inkwave.EditorTools.GameBuilds.BuildWindowsDevelopment -logFile Builds\build_win.log` | log has `StandaloneWindows64 build Succeeded` |
| Android build | the same with `BuildAndroidDevelopment` and `-logFile Builds\build_android.log` | `Android build Succeeded`. The APK is `Builds/Android/ColorSplash.apk`, about 103 MB on disk; Unity's printed size includes symbols. |
| Smoke (Windows) | `powershell -ExecutionPolicy Bypass -File Tools/smoke.ps1 -Tag <tag> [-Weapon id] [-Subs id,…] [-Special id] [-Time dusk] [-Roster random]` | `<tag> exit 0 PASS`; `Builds/Smoke/<tag>/summary.txt` + `player.log` show `fps: avg …  errors 0` |
| Stage capture (editor batch, no `-nographics`) | `Unity.exe -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.StageCapture.Tidewater -captureOut Builds/Captures/<dir> [-captureTime dusk] [-captureTexlib off] [-captureDressing off]` | PNGs in `<dir>` |
| Web reference shot | `node Tools/bake/browser.mjs --angle d3d11 --url "/reference/inkwave/index.html?map=tidewater&time=day&autostart=300&quality=high" --shot <png> --wait 3 --eval "<async js: wait for __inkwave.match.state==='playing', pose __inkwave.rig.cinematic(p,p,l,l,1e6), await 2.5 s, __inkwave.R.render(), return canvas.toDataURL()>"` | a plain CDP screenshot of the game is blank, so read the canvas |
| Stage export (bake) | `node Tools/bake/browser.mjs stage "stage=<id>&time=day\|dusk&quality=low" --angle d3d11 --timeout 560` | `exported … objects …` → `Assets/_Game/Resources/Stages/<id>/` |
| Texlib slot table | `node --import ./Tools/bake/register.mjs Tools/bake/texlib-slots.mjs` | `texlib_slots.json: 50 slots` |
| Phone autopilot | install with `adb -s FMA6GMKN7DEMKB5H install -r Builds/Android/ColorSplash.apk`, then `am start -n com.zoiko.colorsplash/com.unity3d.player.UnityPlayerGameActivity -e unity "'-smoketest -smoketest-seconds 90 -smoketest-timescale 2 [-smoketest-time dusk\|-smoketest-special id]'"`, poll `/storage/emulated/0/Android/data/com.zoiko.colorsplash/files/summary.txt` (`Tools/phone-smoke.sh` does this per special) | `PASS`, fps avg; min fps is polluted by the smoke screenshots |

After every build, run `git checkout -- Assets/_Game/Scenes/Main.unity`: every build regenerates that scene, and
it must not be committed.

## 3. Milestones

| # | Scope | Status |
|---|---|---|
| M0 | Setup, config, event bus, input + touch, three-space bridge, grey box | **done** |
| M1 | Playable core on Tidewater: level, physics, player, camera, ink, squid, bots, Turf War, HUD-lite | **done**; phone passed ("no lag") |
| M2 | Procedural squidkid (baked meshes, full character.js animation) | **done**; phone passed ("too good"). Material polish deferred → Task 18 |
| M3 | All 12 mains, 15 subs, 19 specials | **done**; phone gate 19/19 specials PASS |
| M4 | All 7 stages, props, environment, textures | **in progress**: Tidewater done except the items in §5. Other six stages = Task 8 |
| M5 | FX + procedural audio | not started (Tasks 10–11) |
| M6 | UI: fonts/icons, full HUD, minimap, menus, showcase | not started (Tasks 12–15) |
| M7 | Zone Control (+ optional Boss) | not started (Tasks 16–17) |
| M8 | Mobile polish, release | not started (Task 19) + the optimisation pass (§7) |

## 4. Completed tasks (end to end)

Each one is verified: compile check, build, smoke PASS, a log record and a commit. The evidence for each is in
`docs/PORT_LOG.md` under the same heading.

| Task | What it delivered | Commit(s) | Verified by |
|---|---|---|---|
| 0 | Baseline, `Tools/cc.ps1`, `Tools/smoke.ps1`, `docs/PORT_LOG.md` | `b855257` | other model; reviewed |
| 1 | Boomerang crash fix (non-readable CharacterGeo mesh) | `7140039` | other model; accepted after a rerun |
| 2 | Torpedo kit sub (all 15 subs done) | `8f3f45e` | other model; accepted. Its "Task 4" was fake and was reverted (`af5b03d`); see the log's review |
| 3 | Specials framework + barrage ×5, bubbler, sonar; `SpecialSystem.resolve` falls back to Slam | `0d58516` | Claude, 10/10 smoke |
| 4 | Strike, zooka, wail, blower; `Tools/bake/specials.mjs` → `Resources/SpecialProps` | `1c45286` | Claude, 5/5 smoke |
| 5 | Kraken, ink jet, mega stamp, cheer orb, zipline, crab rig — all 19 specials | `2273b8a` | M3 regression 17/17; phone 19/19 (`4ea8830`) |
| 6 | Browser baker; texlib (46 layers → Texture2DArrays, `World/Texlib.cs`, slot table); baked AO with exact layout parity (hash `8b3b5d82`); prop colliders; texlib sampling in `Level.shader` (anti-tiling, stairs, overlays, modelled detail, gel, relief, metalness) | `d7255a1`, `6067ee8` | smoke t6 / t6b 59.8 fps; web compare at the paving pose |
| 7.1 | Stage exporter `Tools/bake/pages/stage.js` (IWS1 meshes, materials, textures, lights, sky + IBL cubes) → runtime `StageLoader` / `StageMaterials` / `StageEnvironment`, `Inkwave/Prop`, `Inkwave/Env/Sky`; sun /π, SH ambient from hemisphere + IBL cube, reflections, fog; one shadow cascade; culling chunks; day ↔ dusk rotation per new match (TEMPORARY) | `0ef3c44`, `8263dbe` | smoke t7d 55.0 / dusk 56.9 |
| 7.2a | Murals in `Level.shader`; `StageAnimator` (boats, buoys, gulls + wing flap, ferris wheel, lighthouse); `StageGrade` (renderer.js grade + Khronos neutral tone map, render-graph pass) | `7ca1a9b` | smoke t7f 55.4 / 58.3; paving 144/106/102 vs web 141/100/87 |
| 7.2b | Sea: `Inkwave/Env/Sea` (open-sea SEA_VERT/FRAG), env uniforms as globals, deck distance field bake | `c77d5d6` | smoke t7g |
| 7.2c | `Inkwave/Env/Scenery` (patchScenery HZ_*), lighthouse beam, cloth (instanced, team colours), spawn pads + barriers (`StageDecor`); **post fix**: URP post off (UberPost double-gamma) | `fc62a3d` | smoke t7j 53.4 / 55.1; phone t7j day 45.0 / dusk 43.2 fps, 0 errors (`c8e6e96`) |

## 5. In progress — Task 7 (Tidewater fully dressed): what is left

What is done: every scene object of the web stage is drawn, lit, animated and team-coloured, with the web game's grade
and tone mapping, for day and dusk. What remains:

1. **Bloom** (renderer.js `UnrealBloomPass`, `BLOOM = [0.28, 0.45, 2.4]` or the theme's `grade.bloom`; medium/high
   tiers only, so the phone on low does not need it). Add it to `StageGrade` as extra render-graph passes before the
   grade blit. Do **not** use URP's Bloom: URP post must stay off (see pitfalls).
2. **Dusk lamp pools** in `Level.shader` (levelMaterial.js `LAMP_LIGHTS` + `setLevelLamps`: up to 12 bulbs from
   `layout.decor.lamps`, mirrored, warm `#ffc48a` × 16 × night). The layout's decor lamps must reach C#.
3. **Ink coat look** — levelMaterial/inkShading `INK_LIGHTS` / `INK_LIGHT_MAPS` / `INK_SHADE` (tamed grazing Fresnel,
   hue-neutral ambient, rich shade fill). URP's clear coat has a fixed F0 and F90, so grazing ink reads paler than on
   the web.
4. **See-through window** (levelMaterial `uSeeOn` …: geometry in front of the player dissolves). It could also go
   with the HUD in Task 13.
5. Not needed on Tidewater but needed by other stages (Task 8): spinner / blink instanced props (`props:spin:*`,
   `props:blink`, which StageLoader currently merges as static), palm frond sway (`decor.js` frondMaterial, `sway`
   attribute), grate blocks (`Level.buildGeometry` filter, GRATE material), marina water (Halyard).
6. **Plan step 3:** 3 poses × day/dusk side by side against the web (only the paving pose is logged so far).
7. **Phone gate (plan step 5):** autopilot passed (45.0 / 43.2 fps). **Waiting for the user's own verdict** after
   playing day and dusk by touch.

## 6. Remaining tasks (8 → 19), in order

The full instructions are in the plan; this is the condensed to-do list.

- **Task 8 — The other six stages**, in this order: kelpline, saltpan, lockgate, terraces, crossmarket, halyard.
  - Bake the layouts with Node (`Tools/bake/layouts.mjs` → `Resources/Stages/<id>/layout.json`) and load them with
    `World/LayoutData.cs`. Do both the turf and zones variants.
  - Oracle: the layout hash must equal `assets/lightmaps/<id>.json`; check Tidewater through the JSON path first.
  - Per stage: run the stage export for day + dusk (it reuses Task 7's exporter as-is; `Stages/<id>/colliders.<mode>.json`
    comes from `pages/colliders.js`), port any stage-specific shader, smoke `-Map <id>`, capture against the web,
    commit.
  - `GameMain` currently hard-codes `Tidewater.build()`. Add `startMap` and `-smoketest-map`.
  - Halyard needs marina water (GLSL_SEA_MARINA, planar reflection) and props-marina-*.
- **Task 9 — M4 close:** rotate maps × times each match (temporary until the menus), APK size and texture memory
  budget (APK under 150 MB), phone gate with at least 3 stages played by the user.
- **Task 10 — FX (M5):**
  - port fx.js (pooled particles via instanced draws, not ParticleSystem), fxHooks, screenfx (uGUI overlay) and
    swimWake (the ink wake in `Level.shader`: `uWake*` / `uSwimH/F`);
  - `FxStubs.cs` is the contract;
  - smoke with roller / charger / booyah; keep the fps drop under 10 %.
- **Task 11 — Audio (M5 close):**
  - bake audio.js / music.js through OfflineAudioContext in Chrome → WAV + `audio.json`;
  - port `AudioSystem` (48 voices, 24 loops), `MusicPlayer` and the mixer;
  - smoke must log 0 `missing clip`;
  - the user listens on the phone.
- **Task 12 — UI foundation:** **ask the user for the TitanOne + Rubik TTF files** (woff2 cannot be converted
  here). Bake the icons, art and map thumbnails in the browser; add `UiIcons`, `UiTheme` and `UiUtil`.
- **Task 13 — Full HUD:** hud.js (replaces `HudLite`), minimap, diorama, Super Jump UI; test 16:9 and 20:9 layouts;
  touch check.
- **Task 14 — Menus and flow:**
  - title → setup → loadout → match → results, plus settings persistence (`PlayerPrefs` `colorsplash.settings`),
    credits (MIT + "Original game: INKWAVE by Jayden Davis") and pause on app backgrounding;
  - remove the day/dusk rotation;
  - add `Dev/MenuWalk.cs`.
- **Task 15 — Showcase:** the loadout pedestal and the results podium (showcase.js); M6 phone gate.
- **Task 16 — Zone Control (M7):** zones.js, zones-data, zone marks, the bots' zone layer, the HUD; smoke on the 5
  zone stages.
- **Task 17 — Boss mode:** optional. **Ask the user first.**
- **Task 18 — Character material polish:** cloth graphics, parallax iris, gummy hair, tank gauge; the lid and mouth
  rigs baked as blend shapes.
- **Task 19 — Release (M8):** device profile, release AAB (keystore from env vars; the user creates it), no INTERNET
  permission, icon/splash (ask the user, no INKWAVE branding), a profiling pass on the device.

## 7. Deferred optimisation pass (user decision 2026-10-31: features first, then one pass "smooth as slime")

Phone (OPPO CPH2371, low tier) averages: M3 52–59 fps → Task 7.1 40.8 / 38.2 → Task 7.2 45.0 / 43.2 (day / dusk).
Known costs and hitch sources, in rough order:
- the stage dressing is about 677k triangles on the low bake: lower the prop LOD for the low tier, cull more
  aggressively, give far scenery a cheaper shader;
- the per-pixel cost of the sea shader (four wave taps, sky gradient, haze) and of the texlib sampling (hex
  anti-tiling = 9 taps);
- the 512 texlib set and all stages' `.zones` lightmaps ship in the Android build: strip them;
- first-use shader compiles (add a warm-up), per-frame allocations in specials (`G.actors.ToArray` …) and the
  `StageDecor` cloth draws (a new MaterialPropertyBlock per batch);
- the smoke's own screenshots freeze the phone about 0.5 s, so ignore min fps in phone summaries.

## 8. Decisions in force (do not re-open)

- Offline only (no Online mode). Rebrand "Color Splash", `com.zoiko.colorsplash`. Keep the MIT credit.
- Squid form IS ported (the "no squid" rule is only for the sibling PaintBattle project, which is never touched).
- Port `physics.js` instead of using PhysX. Keep JS names. Bake static procedural content; port runtime code and
  shaders.
- The stage dressing loads at runtime from `Resources/Stages/<id>/`; no editor prefab importer.
- `StageEnvironment` replaces the plan's name `Environment`, which would clash with `System.Environment`.
- The phone uses the `low` tier; the texlib is 256 on touch and 512 on desktop high.
- Commit after each task. **Push only when the user asks.** No sub-agents. Never kill the user's Unity editor.

## 9. Pitfalls (paid for already)

- **URP post must stay off** (`renderPostProcessing = false`). URP's UberPost converts linear → sRGB when it believes
  it is the last pass, so the grade pass after it encodes twice: players only, crushed and over-saturated. Whether
  UberPost is stripped varies by build (`player.log`: "UberPost is not supported").
- **PowerShell 5.1** `Get-Content -Raw` reads UTF-8 as ANSI and mangles `—`/`→`. Edit UTF-8 docs with Edit, Bash or
  Node, never with a PowerShell read-modify-write.
- **Unity colour API:** `Material.SetColor` and `Light.color` treat values as sRGB. Write three's linear colours with
  `SetVector`, and give lights `linear.gamma`.
- **GLSL → HLSL:**
  - `mat2(a,b,c,d)` is column-major: write a `float2x2` by rows so that `M*v == mul(M,v)`;
  - `mod` ≠ `fmod` for negative values;
  - world-keyed patterns read the three-space position (z negated); cross products flip under the mirror.
- **Cube maps** baked in three space are rearranged for Unity's mirrored world on load (+Z ↔ −Z, s/t flips).
- **JsonUtility** cannot read nested arrays or dictionaries: the exporter flattens them. Missing arrays come back
  empty, never null.
- **Merged meshes** spanning the whole stage defeat culling: StageLoader chunks them (cells ≥ 20 m, ≤ 8×8).
- **Batch captures:** never use `-nographics`; the first render of a session misses lighting (do a warm-up capture).
  Cloth is drawn from `beginCameraRendering` with `[ExecuteAlways]` so that captures see it.
- **Headless Chrome:** use `--angle d3d11`; the web game reaches `playing` after about 45 s. A CDP screenshot of the
  game is blank: render, then read the canvas.
- More in `docs/PORT_LOG.md` and plan §A4: layout-hash parity, partial-class init order, kit registration, skinned
  rest positions, and so on.

## 10. Waiting on the user

- The phone verdict on Task 7 (does Tidewater with the full dressing feel smooth enough by touch, day and dusk?).
- Later: font files (Task 12), Boss mode yes/no (Task 17), release keystore + icon art (Task 19).
