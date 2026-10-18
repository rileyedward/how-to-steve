# Phase 3 plan: real Minecraft online, blocks rendered in How to Fish

Goal: a real local Minecraft server runs, the bridge logs in as **Steve** with Mineflayer, Steve follows the How to Fish player, and the Minecraft blocks around Steve are drawn inside How to Fish with textures from the player's own Minecraft install. Still read-only from the game's side (no digging or placing, which is Phase 4).

## Versions (checked 2026-10-18)

| piece | version | why |
|---|---|---|
| Minecraft server | **Paper 1.21.11** (build 132, STABLE) | Newest version that Mineflayer 4.39 lists as tested *and* Paper builds exactly. Mineflayer's newest is `26.1`, but Paper only ships 26.1.1/26.1.2 and those need Java 25. See D13. |
| Java | Temurin **21.0.12** | Paper 1.21.11 needs Java 21+ |
| Mineflayer | 4.39.0 (minecraft-data 3.117, has `pc/1.21.11`) | |
| Client assets | The player's `%APPDATA%\.minecraft\versions\26.3\26.3.jar` | Only used for block models and textures. Read at runtime, never copied into the repo. Block model JSON format is unchanged (checked `cube_all`, `grass_block`, `oak_stairs`, `oak_fence` multipart). |

A 26.3 client can't join a 1.21.11 server. Joining isn't required: the bot is the only player. To look at the world with a real client, add a 1.21.11 installation in the launcher (that also gives an exactly matching jar the plugin will prefer).

## Server (`server/`)

- `server/start.ps1` checks that Java 21 and `run/paper.jar` exist (download pinned by SHA-256), copies `server/config/*` into `server/run/` on first start, generates an RCON password into `run/rcon.secret`, and starts Paper. Everything in `server/run/` (jar, world, logs, secrets) is git-ignored.
- `server.properties`: `server-ip=127.0.0.1` (loopback only; RCON binds there too), `online-mode=false` (offline login for the bot, safe only because nothing is reachable from outside the PC; D14), `level-type=flat` with a **void** preset (one air layer), `allow-flight=true`, `spawn-protection=0`, `difficulty=peaceful`, `view-distance=6`, RCON on 127.0.0.1:25575.
- `spigot.yml`: `moved-too-quickly-multiplier: 1000`, `moved-wrongly-threshold: 1000` so the server accepts Steve being moved by How to Fish (D3).
- The Mojang EULA must be accepted by the player (`run/eula.txt`), never by tooling.

## Bridge

- `src/minecraft.ts`: `MinecraftLink` creates the bot (`auth:'offline'`, `username:'Steve'`, `version:'1.21.11'`, `physicsEnabled:false`), reconnects with backoff, and emits `mc_status`. `hello_ack.mc` reports the real state.
- `player_state` (P→B) → `bot.entity.position/yaw/pitch`. Mineflayer still sends the pose every tick with physics off, but only while the chunk under Steve is loaded, so jumps over 32 blocks also send an RCON `tp Steve …`.
- `src/blocks.ts`: `BlockStreamer` keeps the set of sections within `viewRadius` (4 chunks, ±3 sections) of Steve, sends them nearest first as `blocks_near{kind:"section"}`, sends `update` for `blockUpdate` events and `unload` when out of range. State ids become `minecraft:name[props]` strings through prismarine-block, cached. `minecraft:barrier` is always sent as air (protocol §4.3).
- `src/rcon.ts`: a small Source RCON client. `npm run mc -- "<command>"` runs one command; `npm run demo` builds a sample of blocks in front of Steve (stone, planks, log, glass, leaves, grass, stairs, slab, fence, flowers, torch, water).
- Features advertised: `player_state`, `blocks_near`.

## Plugin

All of this is enabled only while SoloGuard says OK (offline, host, 1 player); it is torn down when that changes (D6).

- **Harmony** (first patch): Postfix on `Player.TickUpdate()` (private, so it's targeted by name; game-map §2) for `Player.LocalPlayer`. It sends `player_state` at ≤20 Hz, only when changed. Positions go through `McTransform` (island-local, `mcOrigin`, `scale`, `flipZ`; protocol §3).
- **Assets**: `JarAssets` opens the Minecraft client jar (config `MinecraftJar`, else auto-picks `versions/1.21.11`, else the newest version folder with a jar). `BlockModels` resolves blockstates (variants and multipart), model parents, texture references, element rotation and variant x/y rotation into baked quads. `BlockAtlas` packs textures on demand into one 2048² point-filtered atlas: first animation frame only, biome tint baked in (plains colours), overlays such as the grass side composited.
- **World**: one worker thread owns the section store and builds meshes (face culling via `cullface`, fluids as simple cubes). The main thread only uploads meshes and atlas tiles. `How to SteveWorld` is a `DontDestroyOnLoad` root that copies `Island.CurIsland`'s parent pose every frame. Its child applies the MC→Unity transform (`scale (1/s, 1/s, -1/s)`), so meshes are built in raw Minecraft coordinates. Two materials: opaque and alpha-cutout. Layer 8 (Level). No colliders yet (Phase 4, D8).
- **Diagnostics** (once, when the local player first appears): logs U2 capsule height, U3 layer masks, U4 key layer collisions, U5 island collider types, U6 FishNet tick rate, U8 shader availability, U10 transport port, U12 camera culling mask. This answers most §9 unknowns from the log, with or without UnityExplorer.
- **UnityExplorer** (BepInEx 5 Mono build) installed for interactive inspection. If it fails on Unity 6.4 it is removed, since the diagnostics above cover the same ground.

## Test

1. `server/start.ps1` → `Done` in the server console. `npm run dev` → `mc_status spawned`.
2. Launch How to Fish, offline world. Log: hello_ack `mc.spawned=True`, `features=[player_state,blocks_near]`, diagnostics block.
3. Walk around: the bridge logs Steve's position; `npm run mc -- "data get entity Steve Pos"` agrees.
4. `npm run demo`: the sample blocks appear in How to Fish a few metres in front of the player, textured, the right way round.
5. `npm run mc -- "setblock ~ ~ ~ minecraft:gold_block"`-style changes show up live (`update`).
6. Bridge or server restart: the overlay hides, then comes back.

Automated: bridge unit tests for section encoding, state strings, barrier filtering, yaw/pitch conversion and player_state validation.

## Result (2026-10-18)

Passed in an offline world: Steve follows the How to Fish player on the Paper server, `npm run demo` blocks appear in How to Fish, textured, the right way round and sitting on the ground (play-tested: the blocks were visible). No collision yet, as planned (Phase 4, D8). Still to check in the next session: live `update` messages (step 5) and a server restart (step 6). UnityExplorer removed (D17).
