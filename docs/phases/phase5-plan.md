# Phase 5 plan: terrain sync + Steve visuals

## Terrain sync (protocol §4.8, decisions D4)

Goal: Minecraft knows where the How to Fish island is, so placing, digging and Steve's server-side position agree with what's on screen.

- **What gets voxelized:** the non-trigger colliders on layer 8 (Level) in the current island's scene, with no Rigidbody or a kinematic one. On Island1 that's the 13 MeshColliders (game-map U5). Our own block colliders move to layer 11 (InvisibleColliders), which the player still collides with and stands on (U3 `CanJumpOnLayers`, U4), so the scan never sees them (D21).
- **How:** one downward `Physics.RaycastAll` per block column (cell centre) over the union of those colliders' bounds, transformed into Minecraft space. A column is solid from its highest hit down to `ShellDepth` blocks below it (default 4). That's enough for Minecraft physics and placement, and keeps the barrier count low.
- **Wire:** `terrain_upload{region:"island:<scene>", revision, origin, size, encoding:"bitset-rle", data, final}`, chunked at 256 KiB of base64. Sent 2 s after an island becomes current, and again when the scene changes.
- **Per-island Minecraft areas:** `IslandN` scenes get `mcOrigin.x += (N-1) * 4096`, so each island has its own patch of the Minecraft world and builds don't bleed between islands. Island1 keeps its origin, so Phase 3/4 builds stay put.
- **Bridge:** decodes the bitset, diffs it against the previous revision of the same region, `forceload`s the region's chunks, and writes vertical column runs with `fill … minecraft:barrier keep` (only replaces air). Removed cells get `fill … air replace minecraft:barrier`. Replies `terrain_ack{region, revision, applied, skipped}`. A different region for the same island area replaces the old one.

## Steve visuals

- **First-person arm and held item** (Minecraft mode only): a camera-attached view model with Steve's right arm, textured from the jar's `entity/player/wide/steve.png` (the default skin; nothing fetched online). The held hotbar item sits in the hand: a small textured cube for blocks, a flat sprite for items. Swing animation on dig and place.
- The How to Fish body and camera are untouched.

## Test

1. Load the island: the bridge logs `terrain_upload … applied=N`, and `npm run mc -- "execute if block <x> <y> <z> minecraft:barrier"` succeeds on the island surface.
2. Place a block on the island ground: no on-demand anchor needed (log shows a normal place).
3. In Minecraft mode, Steve's arm and the held item show at the bottom right and swing when you dig or place.
