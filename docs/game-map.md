# How to Fish game map (Phase 1)

How to Fish internals, mapped for SteveFish. Every claim cites its evidence:

- **[IL]**: static analysis of `Assembly-CSharp.dll` (type, field, method, and which methods each one calls / reads / writes). This is the "REA" pass.
- **[data]**: read straight from a game data file.
- **[inferred]**: a reasonable reading of names and call patterns, not proven.
- **[unknown]**: not answered yet. Each one has a UnityExplorer check listed in §9.

Snapshot: build installed on the dev PC, 2026-10-18, `Assembly-CSharp.dll` 910,848 bytes, build-guid `1c18b87aff73483482a395ec956de5c7`. Re-run the pass after any game update (§10).

Tooling note: the cloud sandbox couldn't download .NET, so `ilspycmd` wasn't available. The pass used `dnfile` + `dncil` (Python): every type, field and method signature, plus every call, field access and string literal inside each method body. That's enough to place hooks precisely. It does **not** give readable C# bodies, so branch logic (the `if`s inside a method) is marked [inferred] where it matters. Running ILSpy / dnSpy on your PC fills those gaps. The decompiled output stays local and is never committed.

---

## 1. Engine and runtime

| fact | value | evidence |
|---|---|---|
| Install path | `C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish\` (note the nested `How to Fish` folder) | [data] directory listing |
| Scripting backend | **Mono**. `MonoBleedingEdge/` and `How to Fish_Data/Managed/*.dll` are present, `GameAssembly.dll` is not (no IL2CPP) | [data] |
| Unity version | **6000.4.4f1** (Unity 6.4) | [data] `globalgamemanagers` header |
| Render pipeline | URP (`Unity.RenderPipelines.Universal.Runtime.dll`, `GameInfo._urpAsset : UniversalRenderPipelineAsset`) | [data] + [IL] |
| Graphics | D3D12 (`D3D12/` folder, `dstorage.dll`) | [data] |
| Developer | Dazed Games (`app.info`) | [data] |
| Game assemblies | `Assembly-CSharp.dll` (427 types), `Assembly-CSharp-firstpass.dll` (LeanTween + FishySteamworks transport) | [IL] |
| JSON in-process | `Newtonsoft.Json.dll` 13.0 ships with the game. The plugin can use it without bundling anything | [IL] |
| WebSocket in-process | `System.dll` keeps `System.Net.WebSockets.ClientWebSocket` with `ConnectAsync/SendAsync/ReceiveAsync` intact (not stripped) | [IL] |

**BepInEx:** BepInEx 5 targets Mono games, so it applies. Whether the current BepInEx 5.4.x Doorstop loads cleanly on Unity 6000.4 is **[unknown]**. That's the first thing the Phase 2 smoke test proves.

---

## 2. Player controller and camera

**Root:** `Player : FishNet.Object.NetworkBehaviour`, one per connected player.

| what | where | evidence |
|---|---|---|
| Local player handle | `static Player Player.LocalPlayer`, set in `Player.InitializePlayer()` when `Owner.IsLocalClient` | [IL] |
| Component hub | getters on `Player`: `Movement`, `Camera`, `Inventory`, `Holding`, `Vitals`, `Punching`, `Body`, `Skin`, `Dying`, `Rigidbody`, `Transform`, `CamObject`, `CurCam` | [IL] |
| Lifecycle | `OnStartClient()` → `PlayerManager.AddPlayer` → coroutine `InitializeWhenIslandReady` subscribes `TimeManager.OnTick` → `Player.TickUpdate()` | [IL] |
| Network send of own pose | `Player.TickUpdate()` → `SendPosRot()` → `Server.UpdatePlayerPosRot(player, pos, Vector2 rot, onBoat, teleport, channel)`. Position is made **boat-local** with `InverseTransformPoint` when `Movement.OnBoat`; rotation is `(cam.localEulerAngles.x, .y)` | [IL] |
| Teleport | `Player.LocalTeleport(Vector3 pos, float rot, bool instant)` → `PlayerMovement.Teleport(pos, instant)` + `PlayerCamera.SetRot`. Server-driven version: `Server.TeleportPlayer(player, pos, rot)` → `Player.RPCTeleport` | [IL] |
| Input gate | `Player.get_BlockInputs` is true when dead, paused, typing in chat, "thinking", or self-driving boat | [IL] |
| Body / physics | `PlayerMovement : NetworkBehaviour`. `Rigidbody _rig`, `CapsuleCollider _col`, `_footCol`. Physics in `FixedUpdate()` → `GroundCheck()` / `Move()` / `UnderWaterCheck()` | [IL] |
| Ground check | `GroundCheck()` does a `Physics.SphereCast` from the capsule bottom with mask `GameInfo.CanJumpOnLayers`, sets `Grounded`, `CurGroundTransform`, `CurFeetWorldPos` | [IL] |
| Useful state | `PlayerMovement.Grounded`, `Sprinting`, `Velocity`, `OnBoat`, `CurFeetWorldPos`, `_isSwimming`, `_isCrouching`, `_isHeadUnderwater` | [IL] |
| Camera | `PlayerCamera : NetworkBehaviour` (first-person). `Camera _cam`, `CamTransform`, `CamHeight`, `CamPosNoEffects`, `_curFov`. `Update()` → `MouseMovement` → `HeadBobbing` → `SetCamPosRot()` → `SetFov` | [IL] |
| Camera placement | `SetCamPosRot()` places the camera at `Player.Transform.position + up * CamHeight` + head-bob + screen shake | [IL] |
| Active camera | `GameInfo.CurCamera` (set via `Player.SetCurCam` → `GameInfo.SetCam`). Switches for death cam / fish cam | [IL] |
| Third-person | `ThirdPersonCamera` is a separate dev/debug orbit camera, not the normal view | [IL] + [inferred] |

### Hook (a): stream player position/camera out

**Recommended:** Harmony **Postfix on `Player.TickUpdate()`**, guarded by `__instance == Player.LocalPlayer`.

- It runs once per network tick on the same cadence the game already uses to send its own pose, so it's cheap and consistent. [IL]
- Read in the postfix: `__instance.Transform.position` (feet/root), `__instance.Movement.Velocity`, `.Grounded`, `.Sprinting`, `.OnBoat`, `__instance.CamObject` (camera transform: position plus `eulerAngles`), `GameInfo.CurCamera.fieldOfView`.
- Send **world** position, not the boat-local one the game sends.
- The tick rate is a FishNet `TimeManager` setting. **[unknown]** value (likely 30–60 Hz). The plugin downsamples to `player_state_hz`.

Alternative with no patch: a plugin `MonoBehaviour.LateUpdate()` that reads `Player.LocalPlayer`. Simpler, but it runs at frame rate. Keep it as the fallback if the patch breaks after an update.

---

## 3. Terrain, island colliders and physics layers

### Layers ([data], parsed from `TagManager` in `globalgamemanagers`)

| # | name | # | name |
|---|---|---|---|
| 0 | Default | 15 | OutlinedObject |
| 1 | TransparentFX | 16 | DecorationBlock |
| 2 | Ignore Raycast | 17 | ItemPartNoPlayerCol |
| 3 | Player | 18 | OutlinedObjectNoPlayerCol |
| 4 | Water | 19 | InventoryItem |
| 5 | UI | 20 | SellTrigger |
| 6 | Item | 21 | Interactable |
| 7 | ItemInHand | 22 | Gambling |
| **8** | **Level** | 23 | Boat |
| 9 | ItemPart | 24 | LocalPlayer |
| 10 | BoatItemCollider | 25 | Lava |
| 11 | InvisibleColliders | 26 | Skybox |
| 12 | InvisibleGroundCollider | 27 | BoatDynamic |
| 13 | InvisiblePlayerCollider | 28 | NPC |
| 14 | NoCollision | 29–31 | (unused) |

The game reads layers through `LayerMask` fields serialized on `GameInfo` (the main "game constants" singleton, `GameInfo._instance`): `LevelLayer`, `CanJumpOnLayers`, `ItemLayer`, `PlayerLayer(s)`, `BoatLayer`, `NpcLayer`, `ProjectileHitLayer`, `LegStepOnLayers`, `AffectedByExplosionLayer`, … [IL]. The mask *values* (which layers each contains) are scene data. **[unknown]**: read them at runtime (§9). The layer-collision matrix is also **[unknown]** (`Physics.GetIgnoreLayerCollision`).

### Islands

| what | where | evidence |
|---|---|---|
| Island = additive scene | `IslandManager.LoadIsland(byte)` → `QueueRequest` → coroutine `ProcessRequest` → `UnloadAllIslandsRoutine` then `SceneManager.LoadSceneAsync`. Scene names in `IslandManager._scenes`, metadata in `IslandManager._islandInfos : IslandInfo[]` | [IL] |
| Load-complete signal | `IslandManager.OnFirstIslandLoaded` (Action) fires; `IslandManager.IsInitialized`, `IsLoading` | [IL] |
| Island root | `Island : MonoBehaviour` in each island scene. `static Island CurIsland`, `static Island MiniIsland`, `static Vector3 IslandPos`, `static float IslandSize`. `Island.Start()` sets `CurIsland` | [IL] |
| Islands move | Mini islands are repositioned at runtime: `Island.SetMiniPos(Vector2 xz)` sets the island's **parent transform** position. `IslandManager.GetCurIslandMovedDiff()` = `IslandPosition − ArchivedIslandPosition`; `IslandManager.IslandBuildOffset` | [IL] |
| Which island is active (networked) | `OnlineIslandManager` SyncVars `_curIsland`, `_activeIsland`, `_maxIslandUnlocked`, `_miniPos`; `OnActiveIslandChange`, `OnMiniPosChange` | [IL] |
| Water | Not a collider. `WaterManager` is analytic Gerstner waves: `GetWaterHeight(Vector3)`, `IsUnderWater(Vector3)`, `WaterHeight` | [IL] |
| Lava | `MainLava` trigger (`OnTriggerEnter/Stay` → `TouchLava`), layer 25 | [IL] + [data] |
| Decorations (grass, rocks) | `DecorationManager` → GPU-instanced batches via `InstanceManager`, ground-snapped with raycasts. Render-only, **no colliders** | [IL] + [inferred] |
| Level geometry layer | Code assigns `"Level"` by name in `Item.UpdateLayer()`, and `"Level"/"Default"/"Interactable"/"LocalPlayer"/"OutlinedObject"` appear as `NameToLayer` strings | [IL] |
| Collider types on islands | MeshCollider vs TerrainCollider vs primitives: **[unknown]**. `UnityEngine.TerrainModule` ships, but no game code references `Terrain` | [IL] (absence) |

### Implications for SteveFish

- **Anchor the Minecraft origin to the island, not to world (0,0,0).** Islands get re-parented and moved, so `mcOrigin` should be defined relative to `Island.CurIsland.transform.parent`. Re-voxelize (`terrain_upload` new `revision`) when `OnlineIslandManager.OnActiveIslandChange` or `OnMiniPosChange` fires.
- **Voxelizer mask:** start with `Level(8) | InvisibleGroundCollider(12) | InvisibleColliders(11)`, then confirm against `GameInfo.CanJumpOnLayers` at runtime. Use `Physics.CheckBox(cellCenter, 0.5·scale, identity, mask)` per cell inside the island bounds (`Island.IslandSize`).
- **Minecraft blocks should collide in How to Fish too.** If rendered block chunks get `MeshCollider`s on layer **8 Level**, the HTF player's `GroundCheck` sphere-cast lands on them and Steve can stand on what he builds. The voxelizer must then **exclude** our own block colliders (tag them, or temporarily disable them while scanning).

### Hook (b): render external block meshes

Rendering doesn't need to patch a render method. URP draws any `MeshRenderer` in the scene. What needs hooks is *lifecycle* and *anchoring*:

| hook | type | why |
|---|---|---|
| `IslandManager.OnFirstIslandLoaded` (event), plus Postfix `Island.Start()` | subscribe / Postfix | an island is now `Island.CurIsland`: (re)build the block root and request `blocks_near` |
| Prefix `IslandManager.UnloadIslands()` | Prefix | islands are about to unload: hide/clear block meshes so nothing floats in the void during the load |
| Postfix `Island.SetMiniPos(Vector2)` and `OnlineIslandManager.OnActiveIslandChange` | Postfix | the island moved or changed: re-anchor the block root and bump the terrain revision |

Layout: one `SteveFishWorld` root marked `DontDestroyOnLoad`, so island scene unloads don't destroy it. It copies the pose of `Island.CurIsland.transform.parent` whenever the hooks above fire. Under it, one GameObject per 16³ section with `MeshFilter` + `MeshRenderer` (+ `MeshCollider` on layer 8). Meshes are built off the main thread as arrays and uploaded on the main thread. Block textures come from a runtime-built atlas loaded from your own Minecraft `.jar` (never shipped). Material: **[unknown] U8**. Also **[unknown] U12**: whether `GameInfo.CurCamera.cullingMask` includes layer 8 (it almost certainly does, since islands are on it).

---

## 4. Damage and health

### Model

| what | where | evidence |
|---|---|---|
| Damageable base | `Item : NetworkBehaviour` (everything physical: fish, crabs, birds, bottles, dead players) → `Creature : Item` (has HP) → `Fish`, `Crab`, `Bird` → `AttackingFish` (`Piranha`, `Manta`, `BowheadWhale`), `Spidercrab`, `Albatross` | [IL] |
| Creature HP | `SyncVar<int> Creature._hp` (server-authoritative), `_maxHp`, `_bossHpMultiplier`, `IsDead`, client-predicted `_localHp` / `_localIsDead` | [IL] |
| Client hit entry | `Creature.LocalHit(Transform hitTransform, Vector3 point, Vector3 dir, Player player, int damage, bool rangedHit, Vector3 force, bool fromNpc, bool withArrow, bool whileInspecting)` (virtual on `Item`). Applies headshot multiplier (`GameInfo.HeadShotDamageMulti` vs `Creature.HeadPos`), plays effects, hitmarker, kill score, then calls `Server.HitCreature(...)` | [IL] |
| Server apply | `Server.HitCreature(Creature, Player, int damage, Vector3 hitPoint, Vector3 dir)` [ServerRpc] → `Creature.ServerChangeHp(int damage)` (checks `IsServerInitialized`, `IsDead`, `BossManager.IsImmortal`, writes `_hp`) | [IL] |
| Who calls `LocalHit` | `Melee.HitTarget`, `PlayerPunching.HitTarget`, `Weapon.Shoot`, `ProjectileManager.Hit`, `Disc.OnCollisionEnter`, `ItemExtraRigidbody.OnCollisionEnter`, `DazedCommands.UseKillBossCommand` | [IL] |
| Hit target lookup | `Melee.HitTarget` resolves a collider to an item with `ItemManager.Get(Collider)` and players with `PlayerManager.GetPlayerFromBodyPart` | [IL] |
| Explosions | `ExplosionManager.ServerExplode(Item, ExplosionInfo)` → `ServerChangeHp` directly | [IL] |
| Death / health events | `Creature.OnHealthChange(prev, next, asServer)`, `Creature.OnDeath()`, `Action OnCreatureDeath`, boss events `OnBossTakeDamage/OnBossDeath` | [IL] |
| Player HP | `PlayerVitals : NetworkBehaviour`. `SyncVar` `_syncedHealth`, `_syncedFullness` (hunger!), `_syncedPoison`, `_syncedFire`; `Health`, `HealthPercent` | [IL] |
| Player damage (server) | `PlayerVitals.TakeDamage(int amount, Vector3 pos, Vector3 force, bool ignoreInvulnerability)`: checks god mode, AFK, invulnerability window, `ServerSettings.DamageMultiplier`, then `PlayerDying.ServerDie` at 0 | [IL] |
| Player damage callers | `Server.HitPlayer(Player, int, Vector3 force, Vector3 pos, byte damageType, Player playerWhoHit)` [ServerRpc], `CrabArms.OnAttackFinished`, hunger/poison/fire ticks | [IL] |
| Enemy attacks | `AttackingFish.DamageOnCollision(Player, Vector3)` (`_onHitDamage`, `_timeBetweenDamage`), `CrabArms` | [IL] |
| `DamageType` enum | `NoEffect, Generic, Penetration, Bite` (sent as `byte` in `HitPlayer`) | [IL] |

### Hook (c): apply damage from an outside source

**Recommended: don't patch. Call the game's own hit path.** When a Minecraft attack lands on an HTF creature:

1. Raycast from the camera (`GameInfo.CurCamera`) with `GameInfo.ProjectileHitLayer`, or take the creature the bridge names.
2. `var item = ItemManager.Get(hit.collider)`; `var creature = item?.Creature`.
3. `creature.LocalHit(hit.transform, hit.point, dir, Player.LocalPlayer, damage, rangedHit:false, force, fromNpc:false, withArrow:false, whileInspecting:false)`.

This goes through the same code a melee hit uses, so effects, hitmarkers, kill score, boss immunity and the server SyncVar update all just work. It reaches the server via the `Server.HitCreature` ServerRpc, and in an offline lobby the server is this same process (§7).

For Minecraft → **player** damage (e.g. a Minecraft zombie hitting Steve-in-HTF), call `Server.Instance.HitPlayer(Player.LocalPlayer, dmg, force, pos, (byte)DamageType.Generic, null)`. The Phase 6 open question is which side owns Steve's health; see `decisions.md`.

To **observe** HTF damage (for the Minecraft HUD): Postfix `PlayerVitals.OnHealthChange` and `OnFullnessChange` (the SyncVar callbacks).

---

## 5. Object spawning

| what | where | evidence |
|---|---|---|
| Generic item spawn (server) | `ItemManager.SpawnNewItem(Item prefab, Vector3 pos, Quaternion rot)` → `Object.Instantiate<Item>` under `Server.DynamicObjectsHolder` → `NetworkBehaviour.Spawn(gameObject)` | [IL] |
| Prefab registry | `GameInfo.GetSpawnable(string name)` / `GetSpawnable(byte id)` / `IDToItem(byte)`; `GameInfo._nameToSpawnable`, `_allCreatures`; `Item._id : byte` | [IL] |
| Fish on a hook | `CreatureManager.HookItem(Item prefab, Bait bait)` (rolls shiny/"drip", attaches to bait, hands physics simulation to the rod owner) | [IL] |
| Other spawners | `AlbatrossSpawner.TickUpdate`, `Barrel.TrySpawnItem`, `BossManager.SpawnBossTrophy`, `BoatManager.TrySpawnBoat`, `ExplosionManager.ServerExplode`, `ItemSpawner.SpawnItem` (coroutine), `PlayerInventory.SpawnStartingItems` | [IL] |
| Dev spawn command | `DazedCommands.UseSpawnCommand(string, bool asDead)`; host-only (string `"Only host is allowed to type commands"`) | [IL] |
| Item registry at runtime | `ItemManager.Items : Dictionary<Transform, Item>`, `Add/Remove/Get(Collider)` | [IL] |
| Player spawn points | `SpawnManager` (`MainSpawnPos`, `MiniSpawnPos`, `GetPlayerSpawnPos`) | [IL] |

SteveFish relevance: the Minecraft blocks we render are **not** FishNet objects. They are local-only GameObjects we create ourselves, so nothing about them is networked. Dropping a Minecraft item *into* How to Fish as a physical object (Phase 6) would go through `ItemManager.SpawnNewItem` with an existing prefab.

---

## 6. Input handling

| what | where | evidence |
|---|---|---|
| System | **Unity Input System** (new), single `PlayerInput` at `GameInfo.Input` (`GameInfo._playerInput`) | [IL] |
| Binding style | Each component's `BindInputs()` / `UnbindInputs()` looks up actions by name on `PlayerInput.actions[...]` and adds `performed`/`canceled` handlers | [IL] |
| Action names seen | `PlayerMove`, `PlayerLook`, `PlayerLeftClick`, `PlayerRightClick`, `PlayerJump`, `PlayerSprint`, `PlayerCrouch`, `PlayerPickUp`, `PlayerDrop`, `PlayerTab`, `PlayerChangeSkin`, `Inventory1`, `ChangeBait`, `Pause`, `Skip`, `PushToTalk`; maps `Gameplay`, `Common/Menu` | [IL] string literals |
| Legacy input | A few `UnityEngine.Input.GetKeyDown` calls remain (e.g. `PlayerCamera.Update` cheat key `'t'` toggles timescale when `ClientSettings.CheatsEnabled`) | [IL] |
| Movement input | `PlayerMovement.MoveInput/SprintInput/JumpInput/CrouchInput` (+ `…Canceled`) | [IL] |
| Look input | `PlayerCamera.MouseInput`, `MouseClick`, `ControllerRotation` | [IL] |

**Conflict to design around:** `PlayerLeftClick` / `PlayerRightClick` already mean punch/use/fish. Proposed: a **Minecraft mode** toggle key (default `B`, read by the plugin via `Keyboard.current`). While it's on, the plugin turns mouse buttons into `dig`/`place` and suppresses the HTF actions with a Prefix returning `false` on `PlayerPunching`'s / `PlayerHolding`'s click handlers. Exact handler names come from the bodies of `PlayerCamera.BindInputs` (it binds `PlayerLeftClick` → `MouseClick`). **[unknown]**: which other components bind `PlayerLeftClick` (7 lookups seen). Hotbar: mouse wheel and `1`–`9` → `select_slot` while in Minecraft mode.

---

## 7. Networking

| what | where | evidence |
|---|---|---|
| Library | **FishNet** (`FishNet.Runtime.dll`; `NetworkBehaviour`, `SyncVar<T>`, `TimeManager`, generated RPC stubs `RpcWriter___…/RpcLogic___…`) | [IL] + [data] |
| Transports | FishNet `Multipass` choosing between **FishySteamworks** (Steam relay, online) and **UnityTransport** (`FishyUnityTransport.dll`, offline) | [IL] `ConnectionManager.SetTransport(bool toSteam)` |
| Offline / solo | `ConnectionManager.CreateOfflineLobby()` → `SetTransport(false)` → client address `"localhost"` → `StartConnection`. Solo play is a **local FishNet host**: server and client in one process | [IL] |
| Online | `ConnectionManager.CreateOnlineLobby(maxPlayers)` / `JoinOnlineLobby(CSteamID)`; `SteamManager` handles lobbies; `SteamLobbyAuthenticator` rejects non-lobby members | [IL] |
| Central RPC hub | `Server : NetworkBehaviour` (`Server.Instance`) holds most ServerRpcs: `UpdatePlayerPosRot`, `HitCreature`, `HitPlayer`, `TeleportPlayer`, `SpawnPlayer`, `BuyItem`, … | [IL] |
| Flag | `ConnectionManager.IsUsingSteam` (bool) | [IL] |
| Voice | MetaVoiceChat over FishNet (`FishNetNetProvider`) | [IL] |
| UnityTransport port | **[unknown]** (FishNet's UTP default is 7770). SteveFish's bridge port 25599 should not collide | |

### Solo-only guard (project rule)

The plugin enables **nothing** unless all of these hold, checked every time a session starts:

1. `ConnectionManager.IsUsingSteam == false` (offline lobby, UnityTransport to localhost), and
2. the local player is the server (`InstanceFinder.IsServerStarted && InstanceFinder.IsClientStarted`), and
3. `PlayerManager.Players.Count == 1`.

If any of them goes false mid-session (someone joins, or a Steam lobby starts), the plugin tears down the Minecraft overlay and stops calling game damage/spawn APIs. Since everything SteveFish adds is local GameObjects, and the only game APIs it calls run on a host you own, nothing reaches another player.

---

## 8. Other useful types (for Phase 4–6)

- **HUD:** `PlayerUI` with `VitalsUI` (HTF health/hunger), `ItemUI`, `WeaponUI`, `FishingUI`, `BossUI`, `OnHitUI`; `CanvasManager`. Draw the Minecraft HUD on our own Canvas; optionally hide `VitalsUI`.
- **Inventory:** `PlayerInventory : NetworkBehaviour`, `Server.SelectInvSlot`, `PutItemInInventory`.
- **Hunger already exists:** `PlayerVitals._syncedFullness`, `LowerFullnessTick`, `DamageFromFullness`. Decide whether Minecraft hunger or HTF fullness wins (decisions.md).
- **Save system:** `SaveManager`, `AutoSaver`, `SaveSystem`, `SavedPlayer/SavedItem/SavedWorldItem`. SteveFish must not write into these.
- **Settings / god mode:** `ServerSettings.DamageMultiplier`, `UseFriendlyFire`; `PlayerManager.InGodMode`; `ClientSettings.CheatsEnabled`.

---

## 9. Open unknowns, with the UnityExplorer check for each

UnityExplorer 4.13.6 (BepInEx 5 Mono) does **not** work on Unity 6000.4: it throws `MissingMethodException: Scene.get_handle()` every frame. Runtime answers come from the plugin's `[diag]` block instead (decisions D17).

| # | unknown | how to resolve (UnityExplorer, in an offline lobby) |
|---|---|---|
| ~~U1~~ | **Resolved (Phase 2, 2026-10-18): yes.** BepInEx 5.4.23.5 x64 loads on Unity 6000.4.4f1 Mono (`Running under Unity v6000.4.4.3542935`, `Supports SRE: True`), the Chainloader starts, and the plugin's `Awake` logs `loaded, unity=6000.4.4f1`. The game's `ClientWebSocket` and Newtonsoft.Json work in-process (pong RTT ≈1 ms). Side observation: in the main menu SoloGuard reads `IsUsingSteam=true`, host=no, players=0; it flips to offline/host/1 player once the offline world loads | `BepInEx/LogOutput.log` |
| ~~U2~~ | **Resolved:** player capsule height 1.700, radius 0.375, player lossyScale 1. Minecraft's player is 1.8 tall, so **1 Unity unit = 1 block** (scale 1.0) is right | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| ~~U3~~ | **Resolved:** `LevelLayer`=0x100 [8 Level]; `CanJumpOnLayers`=0x1800AB08 [3 Player, 8 Level, 9 ItemPart, 11 InvisibleColliders, 13 InvisiblePlayerCollider, 15 OutlinedObject, 27 BoatDynamic, 28 NPC] (note: **not** 12 InvisibleGroundCollider); `ProjectileHitLayer`=0x18068398 [3,4,7,8,9,15,17,18,27,28]; `LegStepOnLayers`=0x18068308; `PlayerLayers`=0x1000008 [3,24] | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| ~~U4~~ | **Resolved (key pairs):** Player×Level, LocalPlayer×Level, LocalPlayer×InvisibleColliders/InvisibleGroundCollider/InvisiblePlayerCollider all collide; LocalPlayer×DecorationBlock ignored | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| ~~U5~~ | **Resolved (Island1):** level geometry is **13 MeshColliders on layer 8 Level** (no TerrainCollider); plus 2 non-trigger BoxColliders on InvisibleColliders and assorted triggers. Island root is `IslandManager`, its parent (our anchor) `IslandHolder` at (-200, 0, 600). `Island.IslandSize`=9 | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| ~~U6~~ | **Resolved:** FishNet `TickRate`=50 Hz, so the `Player.TickUpdate` postfix runs at 50 Hz and is downsampled to 20 Hz | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| U7 | Which components bind `PlayerLeftClick` / `PlayerRightClick` | Hooks tab, or ILSpy on `PlayerHolding`/`PlayerPunching.BindInputs` |
| ~~U8~~ | **Resolved:** `Universal Render Pipeline/Lit` and `/Unlit` are present, `/Simple Lit` is stripped. Block materials use URP/Lit (opaque + alpha-clip) | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| ~~U9~~ | **Resolved (Phase 2):** `ConnectionManager.IsUsingSteam` is a `public static bool` field; `ConnectionManager.Instance` is a public static field. Also confirmed: `PlayerManager.Players` is `public static List<Player>`, `Player.LocalPlayer` is a `public static Player` field, FishNet `InstanceFinder.IsServerStarted/IsClientStarted` exist | [IL] metadata reflection on the installed `Assembly-CSharp.dll` / `FishNet.Runtime.dll`, 2026-10-18; the plugin compiles against them directly in `SoloGuard.cs` |
| ~~U10~~ | **Resolved:** `FishNet.Transporting.UTP.UnityTransport` `ConnectionData.Address=localhost`, `Port=7777`, behind a `Multipass` transport. No clash with 25565/25575/25599 | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| ~~U12~~ | **Resolved:** `GameInfo.CurCamera` (`Camera`) cullingMask=0xEE7FFFF includes layer 8; near 0.01, far 500 | [runtime] SteveFish `[diag]` in `BepInEx/LogOutput.log`, offline world Island1, 2026-10-18 |
| U11 | Whether How to Fish's anti-cheat/validation on the server rejects big pose jumps (matters only if we ever drive HTF from Minecraft) | Test teleport via `Player.LocalTeleport` |

---

## 10. Re-running the pass

The analysis script (`dnfile` + `dncil`) lives outside the repo and its output is never committed. After a game update: re-copy `Assembly-CSharp.dll`, re-run, and diff the type/method index against the previous run to see whether any hook target in §2/§4/§6 moved. Harmony patches in the plugin should target by name and log loudly (not crash) when a target is missing.
