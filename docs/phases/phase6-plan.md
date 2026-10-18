# Phase 6 plan: crossover combat + polish

## Done (2026-10-18)

- **Inventory and crafting** (feature `window`, protocol v0.5): **E** in Minecraft mode opens the player inventory with the 2×2 crafting grid; right-clicking a crafting table, furnace, chest, barrel and so on opens it (sneak to place against it instead). Screens use the jar's `gui/container` textures and vanilla slot layouts. Every click is a real `window_click` the server resolves. Verified live: 2×2 grid, 1 oak log → 4 planks.
- **Combat, Minecraft → How to Fish** (D5, D24): left click on an HTF creature in Minecraft mode calls `Creature.LocalHit` with Java Edition weapon damage × the attack-cooldown multiplier `0.2 + 0.8·charge²`, scaled so a Minecraft fist equals HTF's own punch (read at runtime from `PlayerPunching`, logged as `[diag] combat`). Goes through the game's own hit path, so effects, hitmarkers, kills and boss rules apply.
- **Polish:** crack overlay (`block/destroy_stage_0..9`) while breaking, Minecraft dig/place/hit sounds from the player's own `.minecraft/assets` (D25).

## Verified in game (2026-10-18)

Sounds indexed (4961), the inventory and crafting-table screens opened, and terrain sync ran (it led to the D26 fixes). Play-tested: everything worked.

## Open

- **Health ownership** (decisions "Open (Phase 6)"): waiting on the maintainer's choice (A: Minecraft authoritative, B: separate, C: How to Fish authoritative).
- Tool durability and hunger effects from HTF combat aren't mirrored to Minecraft.
- Minecraft mobs never reach How to Fish (the server is peaceful, void world).
