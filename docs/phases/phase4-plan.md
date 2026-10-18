# Phase 4 plan: two-way building + HUD

Goal: in How to Fish, press **B** for Minecraft mode, then break and place real Minecraft blocks with the mouse, pick hotbar slots, stand on what you built, and see Minecraft's hotbar, hearts, food and XP. Minecraft (the Paper server) stays the authority for every block and item.

## Plugin

- **Colliders (D8).** Each section gets a second, collision-only mesh in a `MeshCollider` on layer 8 (Level), the same layer and collider type as the island ground (game-map U5), so the game's own `GroundCheck`/`CanJumpOnLayers` (U3) treat it as ground. It holds the render quads of every *solid* block. "Solid" is a heuristic: not a fluid, and the model has at least one face with `cullface`. That covers cubes, slabs, stairs and fences; flowers, grass and torches stay walk-through.
- **Minecraft mode** (config `MinecraftModeKey`, default `B`, read through the new Input System's `Keyboard.current`). While on:
  - every game `InputAction` bound to the left or right mouse button, the scroll wheel or keys `1`–`9` is disabled, and re-disabled each frame in case the game re-enables its maps. This avoids patching the game's click handlers (U7 stays open but isn't needed). The actions are restored when Minecraft mode turns off.
  - Left button held → `dig start`, release → `dig cancel`. Right click → `place`. Wheel and `1`–`9` → `select_slot`.
- **Targeting.** `Physics.Raycast` from `GameInfo.CurCamera` (max 5 blocks) against layer 8. If the hit collider is one of our section colliders, the target is that Minecraft block and face. If it's island ground, the target is the air block under the hit point in Minecraft coordinates, so you can place on the island. The bridge turns that air block into an on-demand invisible barrier first (see below). A black wireframe box outlines the targeted block.
- **HUD** (IMGUI, `OnGUI`), drawn with sprites from the client jar (`textures/gui/sprites/hud/*`): hotbar with item icons (item texture, else block texture), selection, hearts, food, XP bar and level, a crosshair in Minecraft mode, a small "MINECRAFT MODE" tag and dig progress. Shown only while the overlay is active. HTF's own `VitalsUI` is untouched (Phase 6 decides who owns health).

## Bridge

- `dig` → `bot.dig(block, 'ignore', face)` with `bot.targetDigFace` set from the face, since Steve's look comes from How to Fish (D3). Progress `dig_result{ok:null, progress}` at 5 Hz from `bot.digTime`, then `ok:true|false`. `cancel` → `bot.stopDigging()`.
- `place` → `bot._placeBlockWithOptions(ref, face, {forceLook:'ignore', delta: cursor, swingArm:'right'})`. If `against` is air in Minecraft, the bridge first runs `setblock x y z minecraft:barrier keep` over RCON. This is terrain sync in miniature (D4, D18): the barrier is invisible, never streamed, and never overwrites anything.
- `inventory` (46 slots, Minecraft window numbering) on spawn and on every `updateSlot`/`heldItemChanged`; `hud_state` on `health`, `breath`, `experience`, game mode changes (≤10 Hz).
- `select_slot` → `bot.setQuickBarSlot`.
- **Starter kit** on the first spawn of the world (tracked with the scoreboard tag `stevefish_kit` on Steve): a stone pickaxe, axe and shovel, 64 cobblestone, 64 dirt, 32 oak planks, 16 oak logs, a crafting table, 16 torches and 16 glass. Survival stays survival.

## Test

1. B toggles Minecraft mode (HUD tag + crosshair); HTF punching and fishing don't fire while it's on.
2. Stand on the demo blocks.
3. Break a demo block: it takes survival time, disappears, and the drop lands in the hotbar.
4. Place cobblestone on a demo block and on bare island ground; build a small tower and climb it.
5. Scroll / 1–9 changes the hotbar selection; hearts, food and XP show.
6. Toggle off: HTF controls behave normally again.
