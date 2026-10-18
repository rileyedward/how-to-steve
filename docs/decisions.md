# How to Steve decisions

Newest last. Each entry: decision, why, status.

**D1. Bridge is the WebSocket server on 127.0.0.1:25599; the plugin is the client.** The game can restart without restarting the bridge, and binding to loopback keeps everything local-only. *Accepted (draft, 2026-10-18).*

**D2. The wire speaks Minecraft coordinates only; the plugin owns the transform, anchored to the current island's root.** How to Fish moves island roots at runtime (`Island.SetMiniPos`), so world space is not stable. *Accepted (draft).*

**D3. How to Fish's controller is the authority for Steve's movement.** Mineflayer physics is off and the bot's pose is written from `player_state`. The local Minecraft server needs relaxed movement checks (`allow-flight=true`, and a large `max-tick-time` / movement-speed tolerance, or a Paper config that disables "moved too quickly"). *Accepted (draft); exact server settings chosen in Phase 3.*

**D4. Terrain sync writes `minecraft:barrier` blocks via RCON `/fill` and `/setblock` on the local server.** Barriers are invisible and solid, and RCON avoids giving the bot creative powers. Terrain sync never overwrites non-air, non-barrier blocks. *Accepted (draft).*

**D5. Damage into How to Fish goes through the game's own hit path (`Creature.LocalHit`, `Server.HitPlayer`), not a custom patch.** Effects, boss immunity and server state stay consistent. *Accepted (draft).*

**D6. Solo-only guard:** the plugin activates only in an offline lobby (`ConnectionManager.IsUsingSteam == false`), as host, with exactly one player, and it tears down if that changes. *Accepted (draft).*

**D7. The plugin uses the game's own `ClientWebSocket` (System.dll) and `Newtonsoft.Json` 13.** No bundled networking or JSON libraries. *Accepted (draft).*

**D8. Minecraft blocks rendered in How to Fish get `MeshCollider`s on layer 8 (Level)** so the HTF player can stand on them; the voxelizer excludes them. *Proposed; confirm in Phase 4.*

**Open (Phase 6): who owns Steve's health and hunger?** How to Fish has `PlayerVitals` health and fullness, Minecraft has health and food. Options offered to the maintainer (2026-10-18): A, Minecraft authoritative (HTF damage → `/damage Steve`; Minecraft death → HTF death via `Server.HitPlayer`; recommended); B, separate; C, How to Fish authoritative (Minecraft hearts mirror HTF). *Undecided; until the maintainer picks, the behaviour is B (separate).*

**D9. Local-only git repository.** During development the repo lived only on the dev PC, with no remote and nothing pushed. *Superseded 2026-10-18: cleaned up for publishing on GitHub by the maintainer; tooling never pushes.* `.gitignore` excludes all DLLs, `Managed/`, `BepInEx/`, decompiled output and Minecraft assets; game paths come from the git-ignored `plugin/Directory.Build.props.user`. *Accepted (2026-10-18).*

**D10. Plugin scaffolded from the official BepInEx template** (`dotnet new bepinex5plugin`, BepInEx.Templates 2.0.0-be.4), not a community template; the template's `UnityEngine.Modules` NuGet reference was replaced by `Private=false` references to the game's own `Managed/` DLLs (UnityEngine, Newtonsoft.Json, Assembly-CSharp, FishNet.Runtime). *Accepted (2026-10-18).*

**D11. BepInEx 5.4.23.5 (win x64)** installed into the game folder: adds only `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and `BepInEx/`; no game file is modified. Uninstall = delete those. *Accepted (2026-10-18); confirmed loading on Unity 6000.4.4f1 in the Phase 2 in-game test (U1 resolved).*

**D12. Bridge lifetime is tied to the process, not the plugin component.** The plugin stops the WebSocket client on `Application.quitting`, not `OnDestroy`, because some Unity games destroy BepInEx's manager GameObject on scene load; the dispatcher lives on its own `DontDestroyOnLoad` object. *Accepted (2026-10-18).*

**D13. Minecraft server version is Paper 1.21.11 on Java 21, not the newest 26.x.** Checked 2026-10-18: Mineflayer 4.39's tested versions end at `26.1`, Paper ships 26.1.1/26.1.2 (not 26.1) and every 26.x build needs Java 25. 1.21.11 is the newest version both support exactly. Block models and textures still come from the player's installed client jar (26.3 during development) (format unchanged except 26.x texture objects `{sprite, force_translucent}`, which the plugin handles). Revisit when Mineflayer lists 26.x releases Paper builds. *Accepted (2026-10-18).*

**D14. The local server runs `online-mode=false`, bound to 127.0.0.1.** The bot logs in as "Steve" without a Microsoft account. That is only acceptable because neither the game port (25565) nor RCON (25575) is reachable from another machine. The RCON password is generated per machine into the git-ignored `server/run/rcon.secret`. *Accepted (2026-10-18).*

**D15. Phase 3 world is a void flat world.** Everything rendered in How to Fish is something placed on purpose (demo builds now, Steve's building in Phase 4), so Minecraft terrain never covers the islands. *Accepted (2026-10-18); a "real terrain" option can come later.*

**D16. Mineflayer pins vec3 0.1.10** (the version Mineflayer bundles), so bridge code and Mineflayer share one Vec3 type. *Accepted (2026-10-18).*

**D17. Unknowns U2-U12 are answered by a one-shot `[diag]` block the plugin logs**, not only by hand in UnityExplorer, so the evidence is in `LogOutput.log`. UnityExplorer 4.13.6 (yukieiji fork, BepInEx 5 Mono) was tried and **removed**: on Unity 6000.4 it throws `MissingMethodException: Scene.get_handle()` every frame. *Accepted (2026-10-18).*

**D18. Placing on How to Fish ground anchors an on-demand barrier.** Until terrain sync (Phase 5) voxelizes islands, Minecraft has nothing under the island surface to place against. When the plugin's raycast hits island ground, it sends `place` with `against` = the air block just under the hit point; the bridge runs `setblock … minecraft:barrier keep` over RCON, then places normally. Same rules as D4: invisible, never streamed, never overwrites a block. Phase 5's voxelizer will treat these as part of the terrain. *Accepted (2026-10-18).*

**D19. Minecraft mode disables the game's own mouse/scroll/number-key InputActions instead of patching click handlers.** Actions in non-menu maps whose bindings start with `<Mouse>/leftButton`, `rightButton`, `scroll` or `<Keyboard>/1..9` are disabled while the mode is on (re-disabled each frame) and restored after. This sidesteps U7 (which components bind clicks) and works without Harmony. The disabled list is logged as `[diag] U7`. *Accepted (2026-10-18).*

**D20. One-time starter kit for Steve** (stone tools, cobblestone, dirt, planks, logs, crafting table, torches, glass), given over RCON on first spawn and tracked with the scoreboard tag `howtosteve_kit` (`stevefish_kit` from before the rename also counts). Survival rules stay on. *Accepted (2026-10-18).*

**D21. Minecraft block colliders live on layer 11 (InvisibleColliders), not 8 (supersedes D8's layer choice).** The player collides with 11 and can stand on it (U3 `CanJumpOnLayers` includes 11, U4 LocalPlayer×InvisibleColliders collides), and the terrain voxelizer only scans layer 8, so it can never mistake our own blocks for island ground. Cost: How to Fish's `ProjectileHitLayer` doesn't include 11, so the game's own projectiles and fishing lines pass through Minecraft blocks. The build raycast ignores the island's own layer-11 invisible walls. Config key `[Render] SectionLayer`. *Accepted (2026-10-18).*

**D22. Terrain is voxelized as a surface shell.** One downward raycast per column; a cell is solid if its centre is under the surface, down to `ShellDepth` (4) blocks. The same "cell centre under the surface" rounding is used when placing on island ground, so the voxelizer and BuildController always agree on which block is "the ground". *Accepted (2026-10-18).*

**D23. Each `IslandN` scene gets its own Minecraft area** (`McOriginX + (N-1)*4096`), so builds on one island don't appear on another. Island1 keeps the original origin. *Accepted (2026-10-18).*

**D24. Minecraft weapons hit How to Fish creatures through `Creature.LocalHit` with Java Edition damage** (fist 1, stone sword 5, stone axe 9, …) times vanilla's cooldown multiplier, scaled by How to Fish's own punch damage (read from `PlayerPunching` at runtime) so "fist = punch". Reach 3.5 blocks, `GameInfo.ProjectileHitLayer` (U3). Only runs while SoloGuard is OK, like everything else. *Accepted (2026-10-18).*

**D25. Block sounds are loaded from `.minecraft/assets/objects` via the version's asset index**, decoded by Unity at runtime, never copied. The sound group (stone, wood, grass, gravel, sand, glass, cloth, snow) is picked from the block name. *Accepted (2026-10-18).*

**D26. Terrain sync skips sea-floor columns and force-loads per chunk.** The first in-game sync (Island1) scanned a 601×600 box because the island's level colliders include the sea floor. Columns whose ground is more than 2 m under `WaterManager.WaterHeight` are now skipped. The bridge also merges neighbouring columns into box fills, force-loads only the chunks it writes (vanilla caps `forceload` at 256 chunks per command, so the first version's single big `forceload` failed), retries `not loaded`, and counts `applied` from the server's replies instead of assuming success. `npm run clear-barriers` removed the 372k stray barriers from that first sync. *Accepted (2026-10-18).*
**D27. One-click launcher** (`launcher/`, C# WinForms, .NET 10, self-contained single-file exe so nothing needs installing). It runs the Paper server and the bundled bridge (`node.exe` + compiled JS) as child processes in a kill-on-close job object, stops them gracefully (`stop` on stdin; the server saves its worlds), and launches How to Fish via `steam://rungameid/4001890`. It asks for the Minecraft EULA itself and never accepts it silently. BepInEx and Paper are downloaded on first run, pinned by SHA-256; nothing third-party is redistributed except Node.js (MIT). *Accepted (2026-10-18).*

**D28. Launcher data lives in `%LOCALAPPDATA%\HowToSteve`** (`server\` with the world and RCON secret, `launcher.json`, `launcher.log`). The development world from `server/run/` was copied there on 2026-10-18; `server/run/` is now only for running the server by hand. *Accepted (2026-10-18).*

**D29. Renamed SteveFish → How to Steve (2026-10-18).** Display name "How to Steve"; code identifiers `HowToSteve` (namespaces, `HowToSteve.Plugin.dll`, `HowToSteveLauncher.exe`, `HowToSteve-<version>-win-x64.zip`); ids `howtosteve` (BepInEx GUID `dev.riley.howtosteve`, protocol `hello.plugin`), npm package `how-to-steve-bridge`, env vars `HOWTOSTEVE_*`. Existing installs carry over: the launcher moves `%LOCALAPPDATA%\SteveFish` to `%LOCALAPPDATA%\HowToSteve` once, removes the old `BepInEx\plugins\SteveFish` plugin so two copies never load, and renames the old config to the new GUID. The old starter-kit tag `stevefish_kit` still counts as "kit given". *Accepted (2026-10-18).*
