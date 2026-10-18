# SteveFish bridge protocol

**Version:** 0 (draft; session subset implemented in Phase 2)
**Parties:** the BepInEx plugin inside How to Fish ("plugin") and the Node/TypeScript Mineflayer service ("bridge").
**Status:** `hello`, `hello_ack`, `ping`, `pong`, `error`, `mc_status`, `player_state`, `blocks_near`, `blocks_request`, `dig`/`dig_result`, `place`/`place_result`, `inventory`, `select_slot` and `hud_state` are implemented (`bridge/src/protocol.ts`, `plugin/src/Bridge/Envelope.cs`); everything else is design only. Any field marked *(TBD: Ux)* depends on open unknown Ux in `game-map.md` §9.

## 1. Transport

- WebSocket, text frames, one JSON object per frame, UTF-8.
- The **bridge is the server**, bound to `127.0.0.1` only (never `0.0.0.0`). Default port `25599`, overridable by `STEVEFISH_PORT` on the bridge and `BridgeUrl` in the plugin's BepInEx config.
- One plugin connection at a time. A second connection is rejected with `error{code:"busy"}` and closed.
- The plugin reconnects with backoff (1s, 2s, 4s, capped at 10s). The game must keep running normally while disconnected: no exceptions, no hitches, Minecraft overlay simply hidden.
- Why loopback only: the project rule is solo/local-only. Nothing on the wire should be reachable from another machine, and nothing here touches How to Fish's own networking.

## 2. Envelope

Every message:

```json
{ "v": 0, "type": "player_state", "seq": 1842, "ts": 1760770000123, "re": null, "payload": { } }
```

| field | type | meaning |
|---|---|---|
| `v` | int | protocol major version. A mismatch after `hello` closes the socket with `error{code:"version"}`. |
| `type` | string | message type, snake_case, from the tables below. |
| `seq` | int | per-sender monotonically increasing counter. |
| `ts` | int | sender wall clock, Unix ms. Diagnostic only; never used for ordering. |
| `re` | int \| null | the `seq` of the message this answers (requests that expect a reply, e.g. `dig`, `place`, `ping`). |
| `payload` | object | type-specific body. |

Unknown `type` values are logged and ignored, never fatal. Unknown payload fields are ignored. This is what lets v0.x add fields without a major bump.

## 3. Coordinates and units

- **The wire speaks Minecraft coordinates only.** Block positions are integer `[x, y, z]`; entity positions are double `[x, y, z]` in blocks; +Y is up.
- Angles are degrees, Minecraft convention: `yaw` 0 = facing +Z (south), 90 = facing −X (west); `pitch` −90 = straight up, +90 = straight down.
- **The plugin owns the Unity ↔ Minecraft transform** (origin offset, scale, axis flip). The bridge never sees Unity coordinates. The transform is sent in `hello` (and again in `transform_changed`) so the bridge can log/debug it, and lives in the plugin config:
  - `u` is the position **in the current island's local space** (`Island.CurIsland.transform.parent.InverseTransformPoint(world)`), because How to Fish moves island roots at runtime (game-map §3).
  - `mc = mcOrigin + scale * (u.x, u.y, flipZ ? -u.z : u.z)` with defaults `mcOrigin = [0, 64, 0]`, `scale = 1.0`, `flipZ = true`. Unity is left-handed and Minecraft right-handed, so one horizontal axis has to flip. Scale is *(TBD: U2, player capsule height)*.
- Block states use Minecraft's namespaced string form, e.g. `minecraft:oak_stairs[facing=north,half=bottom]`. The plugin maps these to meshes/textures loaded from the player's own Minecraft install at runtime.

## 4. Message catalogue

Direction: **P→B** plugin to bridge, **B→P** bridge to plugin.

### 4.1 Session

**`hello`** P→B, first message after connect.
```json
{ "plugin": "stevefish", "pluginVersion": "0.1.0", "game": "How to Fish", "unityVersion": "6000.4.4f1",
  "transform": { "mcOrigin": [0,64,0], "scale": 1.0, "flipZ": true },
  "features": ["player_state", "blocks_near", "dig", "place", "inventory", "hud_state", "terrain_upload"] }
```

**`hello_ack`** B→P.
```json
{ "bridgeVersion": "0.1.0", "mc": { "version": "1.21.1", "username": "Steve", "spawned": true },
  "features": ["..."], "rates": { "player_state_hz": 20, "hud_state_hz": 10 } }
```
`features` in the ack is the intersection both sides support. `mc.spawned=false` means the bot is still connecting; a later `mc_status` tells the plugin when it is in.

**`transform_changed`** P→B, when the island changes or moves (`Island.SetMiniPos`, `OnActiveIslandChange`): `{ "island": 3, "transform": { ... same shape as hello ... } }`. The bridge treats any terrain it wrote for the old island as stale.

**`mc_status`** B→P, whenever the Minecraft login state changes.
```json
{ "state": "connecting" | "spawned" | "kicked" | "disconnected", "reason": "string|null" }
```

**`ping`** either way, `{ "nonce": "abc" }` → **`pong`** `{ "nonce": "abc" }` with `re` set. Used for the Phase 2 smoke test and as a 5s heartbeat; three missed pongs closes the socket.

**`error`** either way.
```json
{ "code": "version" | "busy" | "bad_message" | "not_spawned" | "rejected" | "internal", "message": "human text", "detail": {} }
```

### 4.2 `player_state` P→B, ~20 Hz (only when changed)

Steve follows the How to Fish player. The bridge applies it to the Mineflayer bot.
```json
{ "pos": [12.5, 70.0, -3.25], "yaw": 135.0, "pitch": 10.0,
  "vel": [0.0, -0.08, 0.0], "onGround": true,
  "camera": { "pos": [12.5, 71.62, -3.25], "yaw": 135.0, "pitch": 10.0, "fov": 70.0 },
  "flags": { "sprinting": false, "sneaking": false, "swimming": false, "onBoat": false } }
```
- `pos` is the player root (`Player.Transform`); `camera` is `Player.CamObject` (first-person, at `pos + up·CamHeight` plus head-bob). Sent from a Postfix on `Player.TickUpdate` (game-map §2), downsampled to `player_state_hz`.
- `flags.onBoat`: when true, How to Fish moves the player with the boat; positions are still sent in island space.
- The bridge runs the bot with Mineflayer physics **off** and writes the pose directly; the How to Fish controller is the authority for movement. Server side, the local server needs movement checks relaxed (see `decisions.md`).

### 4.3 `blocks_near` B→P

The bridge streams the Minecraft world around Steve so the plugin can render it.

Snapshot of one 16×16×16 chunk section:
```json
{ "kind": "section", "section": [0, 4, -1],
  "palette": ["minecraft:air", "minecraft:stone", "minecraft:oak_log[axis=y]"],
  "data": "<base64 of 4096 little-endian uint16 palette indices>",
  "light": null }
```
- `section` is `[chunkX, sectionY, chunkZ]`; block `(lx, ly, lz)` inside it is index `(ly*16 + lz)*16 + lx`, Minecraft's own ordering.
- An all-air section is sent as `"palette": ["minecraft:air"], "data": null`.
- `light` is reserved (null in v0).

Single-block change:
```json
{ "kind": "update", "pos": [12, 69, -4], "state": "minecraft:air" }
```

Unload (Steve walked away):
```json
{ "kind": "unload", "section": [0, 4, -1] }
```

The bridge sends sections within `viewRadius` (default 4 chunks horizontally, ±3 sections vertically) sorted nearest first, then `update` deltas from Mineflayer's `blockUpdate` events. The plugin can ask for a resend with **`blocks_request`** P→B `{ "sections": [[0,4,-1]] }`.

Blocks the bridge itself placed for `terrain_upload` (barrier blocks, §4.7) are **never** sent in `blocks_near`; they are invisible by design.

### 4.4 `dig` P→B

```json
{ "action": "start" | "cancel" | "finish", "pos": [12, 69, -4], "face": "up" }
```
`face` is one of `down, up, north, south, west, east`. The bridge calls Mineflayer's `bot.dig` on `start` (Mineflayer times the break itself), aborts on `cancel`. `finish` is optional and only used if the plugin wants instant-break in creative.

Reply **`dig_result`** B→P with `re`:
```json
{ "pos": [12, 69, -4], "ok": true, "reason": null, "progress": 1.0 }
```
While digging, the bridge may send `dig_result` with `ok:null, progress:0..1` at ~5 Hz for the crack overlay.

### 4.5 `place` P→B

```json
{ "against": [12, 68, -4], "face": "up", "cursor": [0.5, 1.0, 0.5], "hand": "main" }
```
Same semantics as a Minecraft right-click on a block face: the bridge calls `bot.placeBlock` with the held item. Reply **`place_result`** `{ "pos": [12,69,-4], "ok": true, "state": "minecraft:cobblestone", "reason": null }`.

The plugin never decides what block results; the server does, and the plugin learns it from `blocks_near{kind:"update"}`.

### 4.6 `inventory`

**`inventory`** B→P, full snapshot on spawn and on any change:
```json
{ "heldSlot": 0,
  "slots": [ { "slot": 36, "item": "minecraft:cobblestone", "count": 32, "nbt": null }, null, ... ],
  "cursor": null }
```
`slots` uses the Minecraft player-window numbering (0 craft output, 1–4 craft grid, 5–8 armour, 9–35 main, 36–44 hotbar, 45 offhand), length 46, `null` for empty.

**`select_slot`** P→B `{ "hotbar": 0..8 }`: mouse wheel / number keys.

**`window_click`** P→B, reserved for Phase 4 (crafting UI): `{ "slot": 12, "button": 0, "mode": 0 }` mirroring Minecraft's click packet.

### 4.7 `hud_state` B→P, on change, capped ~10 Hz

```json
{ "health": 20.0, "food": 20, "saturation": 5.0, "xpLevel": 0, "xpProgress": 0.0,
  "air": 300, "armor": 0, "gameMode": "survival", "dead": false,
  "effects": [ { "id": "minecraft:speed", "amplifier": 0, "ticks": 200 } ] }
```
The plugin draws the Minecraft HUD from this plus `inventory.heldSlot`. Minecraft owns these numbers. How to Fish has its own health and hunger (`PlayerVitals` health/fullness, game-map §4/§8); which side wins is a Phase 6 decision in `decisions.md`. Until then the Minecraft HUD shows Minecraft's numbers and How to Fish's `VitalsUI` stays as is.

### 4.8 `terrain_upload` P→B

The plugin voxelizes How to Fish island colliders into blocks so Minecraft's physics and placement agree with what is on screen. The bridge writes them as `minecraft:barrier` (invisible, solid).

```json
{ "region": "island_main", "revision": 3, "origin": [-64, 40, -64], "size": [128, 48, 128],
  "encoding": "bitset-rle",
  "data": "<base64>",
  "final": true }
```
- `data`: the region as a bitset (1 = solid) in `(y*sizeZ + z)*sizeX + x` order, run-length encoded as alternating uint16 run lengths starting with empty, then base64. Large regions may be split across several messages with the same `region`/`revision`; `final:true` marks the last one.
- The bridge diffs against the previous revision of that region and only touches changed cells.
- Bridge reply **`terrain_ack`** `{ "region": "island_main", "revision": 3, "applied": 18234, "skipped": 0 }`.
- Writing barriers needs operator commands, which Mineflayer does not do as a normal player action. v0 plan: the bridge sends `/fill` and `/setblock` through RCON to the local server (Steve is op on that server only). Fallback if RCON is unavailable: the bot issues the commands via `bot.chat`.
- The bridge never overwrites a non-air, non-barrier block, so terrain sync can't destroy something the player built.

## 5. Rates and back-pressure

| stream | default rate | behaviour if the socket is slow |
|---|---|---|
| `player_state` | 20 Hz | drop stale ones, send only latest |
| `hud_state` | ≤10 Hz | coalesce |
| `blocks_near` sections | as fast as sent | queue, nearest first |
| `blocks_near` updates | per event | never dropped |
| `terrain_upload` | on demand | chunked, ack before next region |

The plugin uses `System.Net.WebSockets.ClientWebSocket` and `Newtonsoft.Json` 13, both already shipped in the game's `Managed/` folder (game-map §1), so it bundles no networking or JSON libraries. The client runs off Unity's main thread; incoming messages go into a thread-safe queue drained once per frame (Unity APIs are main-thread only).

## 6. Versioning rules

- Adding a message type or optional field: no version change, mention in the changelog below.
- Renaming/removing a field or changing its meaning: bump `v`.
- The bridge is the reference: its TypeScript types (`bridge/src/protocol.ts`) are generated from or checked against this document, and the plugin's C# DTOs mirror them.

## 7. Changelog

- **v0.5 (2026-10-18, Phase 6, no version bump):** feature `window`. B→P `window_open`/`window_update` `{windowId, type, title, slots:[{slot,item,count}|null], cursor}` (`type` is the vanilla menu id, e.g. `minecraft:crafting`, `minecraft:generic_9x3`, `minecraft:furnace`), `window_close {windowId}` either way. P→B `window_click {windowId, slot, button, mode}` (vanilla click semantics, `slot:-999` drops the cursor stack; windowId 0 is the player inventory with its 2×2 grid). `inventory.cursor` now carries the cursor stack. `place` against an interactable block (crafting table, furnace, chest, …) opens it unless `player_state.flags.sneaking`; the reply is `place_result{ok:false, reason:"opened"}`.
- **v0.4 (2026-10-18, Phase 5, no version bump):** `terrain_upload`/`terrain_ack` implemented. `region` is `island:<scene name>`; chunks split the RLE byte stream on even byte boundaries and are concatenated after base64-decoding; the ack's `re` is the final chunk's `seq`. The bridge `forceload`s the region and writes column runs with `fill … barrier keep`; cells dropped in a later revision are cleared with `fill … air replace minecraft:barrier`.
- **v0.3 (2026-10-18, Phase 4, no version bump):** `dig`/`dig_result` (progress `ok:null` at ~5 Hz from the bridge's dig timer; `cancel` gets no reply), `place`/`place_result` (`against` may be an air block over How to Fish ground: the bridge anchors a barrier first, D18; failure reasons include `empty_hand`, `not_spawned`, `unloaded`), `inventory` (sent after `hello_ack` and on change, debounced 50 ms), `hud_state` (after `hello_ack` and on change, ≤10 Hz; `armor` and `effects` are 0/[] for now), `select_slot`.
- **v0.2 (2026-10-18, Phase 3, no version bump):** `mc_status` is also sent right after `hello_ack`. `blocks_near` sections are streamed after the first `player_state`; the view volume follows Steve with 2 blocks of hysteresis across section borders. `blocks_request` re-sends sections in loaded columns.
- **v0.1 (2026-10-18, Phase 2 implementation notes, no version bump):**
  - `hello_ack.mc.version` may be `null` while the bridge has no Minecraft session configured (Phase 2 always sends `{version:null, username:"Steve", spawned:false}`).
  - Messages other than `hello` before `hello` get `error{code:"bad_message"}` with `re` set; the socket stays open.
  - Invalid JSON, binary frames and malformed envelopes get `error{code:"bad_message"}`; the socket stays open.
  - Close codes: `4001` busy, `4002` version (sent right after the matching `error`).
  - In Phase 2 both sides advertise `features: []`, so `hello_ack.features` is empty.
  - Missing `re` is read as `null`, missing `payload` as `{}`.
- **v0 (draft, 2026-10-18):** initial message set: hello/hello_ack, transform_changed, mc_status, ping/pong, error, player_state, blocks_near (+blocks_request), dig/dig_result, place/place_result, inventory/select_slot/window_click, hud_state, terrain_upload/terrain_ack.
