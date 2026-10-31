# Color Splash (Unity project `Inkwave_anroid`)

An offline Android version of **INKWAVE**, the 4v4 turf-war ink shooter by Jayden Davis (MIT licence). It is a C# / Unity 6 (URP) translation of the open-source three.js game, built for phones. The app is called **Color Splash**, with package id `com.zoiko.colorsplash`.

- Original game: INKWAVE by Jayden Davis, MIT licence. See [LICENSE-INKWAVE.txt](LICENSE-INKWAVE.txt). INKWAVE is an independent project and is not affiliated with Nintendo.
- Translation source: `reference/inkwave/`, a git clone of `github.com/zgjzgj1985/inkwave`. It is git-ignored and read-only; clone it again to restore it.
- Not ported: online play (`src/net`, `server/`), Electron and the dev labs. The game is bots-only and fully offline.

## Layout
```
Assets/_Game/Scripts/      one C# file per JS module; each header names its source (src/…)
  Config.cs                src/config.js
  Core/                    src/core   (Ctx = ctx.js, InputState = input.js, TouchControls = touch.js)
  Core/Three/              three.js stand-ins: ThreeSpace (coordinate bridge), Geo (BufferGeometry + generators)
  Game/ World/ Fx/ UI/ Audio/   src/game, src/world, …
  Dev/                     grey box + test helpers
  Editor/                  project setup, scene builders, captures, builds
Assets/_Game/Shaders/      URP HLSL ports of the GLSL / onBeforeCompile shaders
Assets/_Game/Resources/Kid, KidWeapons/   baked squidkid + weapon meshes (IWK1 files + json), see Tools/bake
Assets/Settings/           URP assets (mobile + PC), copied from PaintBattle (the PC renderer's SSAO is off: AO is baked)
Tools/bake/                Node bakers that run INKWAVE's own JS generators (character-geo, character-weapons)
```

## Port conventions
- **Names follow the JS.** Fields and methods keep their camelCase JS names (`actor.addTurf()`, `PLAYER.runSpeed`), so any C# line traces back to its source.
- **Three space.** Gameplay uses INKWAVE's numbers unchanged: metres, Y up, the kid faces +Z at yaw 0, right-handed.
  - Every world object is parented under `ThreeSpace.root`, which is mirrored on Z (scale 1,1,-1). Unity then draws the right-handed scene exactly as three.js does, including three's counter-clockwise front faces. The M0 spike verified this: `Builds/Captures/greybox.png`, face colours by axis.
  - Positions and quaternions are copied as they are.
  - Convert a three Euler with `ThreeSpace.eulerXYZ` (three's order is XYZ; Unity's `Quaternion.Euler` is ZXY).
  - Place the camera with `ThreeSpace.placeCamera` / `cameraLookAt`.
- **Own physics.** `physics.js` (oriented boxes) is ported. PhysX is not used, so movement feels the same as the original.
- **Bake, don't generate on the phone.** Procedural content that never changes is baked by editor tools into `Assets/_Game/Baked`.
  - The squidkid and weapon meshes are baked by running INKWAVE's own generators under Node (the three.js import map is resolved by `Tools/bake/hooks.mjs`), so the geometry is exactly the web game's. The C# side only loads them (`CharacterGeo`, `WeaponModel`).
  - The phone set is the `low` quality, `game` + `far` tiers. Hair is baked per (style, hat); the brow strokes are split off and joined at load.
- **Events.** `Ctx.on/emit` with a single `Ev` payload class carry the events listed in `reference/inkwave/docs/EVENTS.md`.

## Commands
Use the Unity 6000.5.9f1 editor at `C:\Program Files\Unity\Hub\Editor\6000.5.9f1\Editor\Unity.exe`. Batch mode requires the project to be closed in the editor.
```
Unity -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.ProjectSetup.Apply
Unity -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.GreyBoxTools.BuildAndCapture -captureOut out.png
Unity -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.KidCapture.Lineup -captureOut DIR
Unity -batchmode -quit -projectPath . -executeMethod Inkwave.EditorTools.KitCapture.Kits -captureOut DIR
```
Re-bake the squidkid and weapons (Node 20+, needs `reference/inkwave`):
```
node --import ./Tools/bake/register.mjs Tools/bake/character.mjs [--quality low] [--tiers game,far]
node --import ./Tools/bake/register.mjs Tools/bake/weapons.mjs
```
Captures render only with a GPU: do not pass `-nographics`.

## Smoke test
```
Builds/Windows/ColorSplash.exe -smoketest -smoketest-out DIR -smoketest-seconds 60 -smoketest-timescale 2
```
A bot plays the local player through a whole match. The run writes screenshots and `summary.txt` (splats, coverage, per-player stats, fps, errors). It exits with code 0 on PASS: no errors, and both teams inked turf.

## Milestones
| # | Scope | Status |
|---|---|---|
| M0 | Setup, config, event bus, input + touch, three-space bridge, grey box | done |
| M1 | Playable core on Tidewater Plaza: level, physics, player, camera, ink, squid, Spritzer + bomb, bots, Turf War, HUD | done; phone test passed ("no lag") |
| M2 | Procedural squidkid (rig + animation) | done for now: model, rig, full animation, weapon models; phone test passed. Faithful material port (cloth graphics, lid/mouth rig, parallax iris) deferred to polish |
| M3 | All 12 weapons, 15 subs, 19 specials | done: all 12 mains, 15 subs and 19 specials (smoke PASS each); phone gate passed (19/19 specials on the device) |
| M4 | All 7 stages, props, environment, textures | in progress: browser baker, texture library sampled by the level shader, baked AO with exact layout parity, prop colliders; Tidewater dressing exported from the web build (props, decor, scenery, sky + clouds, sea stand-in) with its sun / sky-fill / image light / fog, day and dusk. Next: sea + sky + scenery shaders, animation, murals, the grade pass (Task 7 part 2) |
| M5 | FX + procedural audio | — |
| M6 | Menus, HUD, map, locker | — |
| M7 | Zone Control, Boss mode | — |
| M8 | Mobile polish, release build | — |

What M1 contains:
- **Ported as in the JS:**
  - `mapkit` and the Tidewater layout
  - `level` (blocks, faces, bevelled geometry)
  - `physics`
  - `paint` (GPU atlas splat shader and CPU turf grid; floods excluded)
  - the ink layer of `inkShading`
  - `actor`, `player`, `cameraRig` (including the map diorama)
  - `nav`
  - `match` (Turf War)
- **Partial:** `bots` covers the Turf War brain. The Zone, Boss and threat layers come later.
- **Stand-ins:**
  - `HudLite` replaces `hud.js`.
  - Specials play as Tidal Slam.
  - Only the Spritzer and Popper Blaster are in the bot roster.
  - FX and audio are no-op stubs.

What M2 contains so far:
- **Baked from the JS generators:** every kid mesh (skin, cloth, eyes, 8 hair styles × 4 hats + 4 brow kinds, tank, squid) at the `game` and `far` tiers, and all 15 built-in weapon models + 10 sub props.
- **Ported as in the JS:** `character.js` complete (`Character*.cs`: root tracking, stepping, every pose layer and one-shot, dances, face/blinks/gaze, IK, hair springs, squid form, LOD tiers with dither cross-fade), `character-style.js`, `animateWeapon`.
- **First pass, to be ported in full:** the materials (`Shaders/Kid/Kid.shader`). The vertex contracts are honoured (colour sources, mask, eyes, team ink, AO); not yet ported: cloth graphics/patterns, the lid and mouth vertex rig (blinks draw on the eyeball instead), parallax iris, gummy hair, tank gauge.
- **Not yet:** kit weapon models (brolly, bow, blade, mitts: M3), menu camera attention (M6). `CapsuleCharacter` (the M1 stand-in) is no longer used.

What M3 contains so far:
- **Ported as in the JS:** `weapons.js` complete for the 8 built-in mains: Spritzer, Swell Roller, Glint Charger, Popper Blaster, Bilge Bucket, Squall Spinner, Twinfire Pistols (dodge roll + planted turret), Swish Brush. This includes the charger beams and laser sights, the ink teardrop blobs with satellite droplets (`Shaders/Fx/InkBlob`, `Shaders/Fx/Ribbon`), the Ink Tempest storm cloud, and the bots' trigger logic for each kind.
- **Kit weapons** (`Game/Kits/`, one file per kit module + its model; registry = `Kits.cs`, port of `kits/registry.js`). Bots and the roster pick up every kit as it registers:
  - **Canopy Brolly** (`Brolly.cs`, `BrollyModel.cs`): pellet blasts scored as one hit per victim, the held canopy shield (blocks shots, beams, blasts; tears; breaks; regrows), the launched sliding wall, bot shield / launch logic. The 8 gores are baked in their own frame so a plain Transform poses them.
  - **Tideline Bow** (`Bow.cs`, `BowModel.cs`): three-arrow volleys (flat fan on the ground, upright in the air), lodged arrows that tick and burst, the draw animation (string, limbs, nocked arrows).
  - **Brine Cutlass** (`Blade.cs`, `BladeModel.cs`, `BladeFX.cs`): quick cuts with a crescent of drops + the blade hitbox, the charged overhead cut with lunge and ink wave, the swordsman bot tactics, slash arcs, the charge-lit edge and aura (`Shaders/Fx/BladeFx`, `BladeAura`, Kid kind 14).
  - **Sponge Mitts** (`Mitts.cs`, `MittsModel.cs`): alternating ink fists, the charged leap (trajectory preview, landing telegraph, splash, armour), wall cling, sponge guard, bot pounce / wall tactics; sponge pores (Kid kind 15).
  - Shared: `Kits.blockShot` / `blockRay` complement `subs.js` (canopies catch shots and beams); the bot brain's shield layer (flank a held canopy, steer round or break a launched one).
  - `KitCapture.Kits` (editor) renders each kit in its key states.
- **Subs:** `subs.js` is ported complete (`Game/Subs.cs`, 10 built-in subs: Splat Bomb, Cling Charge, Pop Pellet, Skitter Bomb, Echo Orb, Drip Curtain, Twirl Sprinkler, Lurk Mine, Hop Beacon, Murk Bomb), with throw arcs, fuses, painting, damage, per-sub limits, bot throwing and threat warnings. The 5 kit subs live in `Game/Kits/`:
  - **Shaker Bomb** (`Shaker.cs`): shake-to-charge, up to three hopping blasts, ink trail.
  - **Waddle Bomb** (`Waddle.cs`): waddles after sensed foes, sense ring, shoot-down.
  - **Whirl Boomerang** (`Boomerang.cs`): flat out-flight, hover shred, orbit return, burst; readable mesh for its blur.
  - **Tracer Bolt** (`Tracer.cs`): ricocheting bolt, ink puddles on bounces, marking trail.
  - **Tide Torpedo** (`Torpedo.cs`): bomb arc, mid-air lock with a dashed team ring, fin unfold and homing swim; locked burst sprays droplets, unlocked burst is a plain bomb; one out at a time. World parts baked into `Resources/KidWeapons/sub_torpedo`, ring shader `Shaders/Fx/LockRing`.
- **Specials:** the `specials.js` framework (`Game/Specials/SpecialSystem.cs`: `G.specials`, the state flags, start / tick / body / move / weapon / end per kind, force fields, the shotHit / rayHit / areaHit hooks wired into every weapon, sub and kit). Tidal Slam and Ink Tempest stay in `Actor.cs` / `Weapons.cs`. Ported kinds, one file each: **Bomb Barrage** ×5 (`Barrage.cs`: free throws of its bomb at its throw gap), **Bubble Guard** (`Bubbler.cs`: shove-instead-of-damage field, shared on touch, `Shaders/Fx/Bubble`), **Deep Sonar** (`Sonar.cs`: reveal + track every foe; slowed, burns ink), **Vortex Strike** (`Strike.cs`: bot targeting, missile, swirling vortex `Shaders/Fx/Swirl`; the player's map cursor is a ground ring slid by the move stick until the full map arrives), **Twister Zooka** (`Zooka.cs`: piercing twisters), **Howl Box** (`Wail.cs`: shoulder speaker + guide, placed speaker, sound beam `Shaders/Fx/Beam`), **Bubble Blower** (`Blower.cs`: blown bubbles that wall off enemies, soak enemy fire and blow up on team fire), **Kraken** (`Kraken.cs`: ride the kraken prop, hops, leaping slams, shove-only hits), **Ink Jet** (`Jetpack.cs`: the pack replaces the tank, hover / boost / blast shots, Return marker + super jump home), **Mega Stamp** (`Stamp.cs`: slow-turning heavy swings with a front guard, mid-air flip, the long-range throw), **Cheer Orb** (`Booyah.cs`: charge overhead, teammates cheer it up, thrown orb swells and bursts; `SpecialSystem.cheer`), **Zipline** (`Zipcaster.cs`: dark shroud `Shaders/Fx/Aura`, tether zips, wall cling, impact blasts, Return marker), **Crab Rig** (`Crab.cs`: walker with gatling + cannon shells, armoured rolling ball, its own hp; hits from behind / above reach the rider). World props (missile, speaker; kraken, jetpack, crab for later) are baked from `special-props.js` by `Tools/bake/specials.mjs` into `Resources/SpecialProps` (loader `Game/SpecialProps.cs`). A special not ported yet plays as Tidal Slam (`SpecialSystem.resolve`, one warning per id).
- **Not ported:** upstream's legacy dualies, slosher and splatling. `WEAPON_SUCCESSOR` maps them to twins, bucket and spinner, and no menu offers them.
- **Smoke test:** `-smoketest-weapon ID` picks the local player's main weapon; `-smoketest-special ID` its special (screenshots `special_Na/b_<id>.png` 0.3 s / 1.5 s after each start; `specials:` counts in `summary.txt`). Each run also saves `fire_N.png` frames of your own shots in flight.
