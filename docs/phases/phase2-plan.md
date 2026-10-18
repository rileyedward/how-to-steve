# Phase 2 plan: injected DLL ⇄ bridge ping

Goal: prove that a BepInEx 5 plugin loads into How to Fish (Unity 6000.4.4f1, Mono) and holds a versioned WebSocket conversation with the Node bridge. No Minecraft, no Harmony patches yet.

## Repository layout

```
stevefish/
├─ docs/
│  ├─ game-map.md          Phase 1 map (hook points, unknowns)
│  ├─ protocol.md          wire protocol, v0
│  ├─ decisions.md         decision log
│  └─ phase2-plan.md       this file
├─ plugin/                 from the HowToFish BepInEx template (fallback: `dotnet new bepinex5plugin`)
│  ├─ SteveFish.Plugin.csproj      netstandard2.1; references resolved from $(HowToFishManaged), never copied into the repo
│  ├─ Directory.Build.props.user.example   sets HowToFishManaged = …\How to Fish_Data\Managed and BepInExDir
│  ├─ src/
│  │  ├─ Plugin.cs                 [BepInPlugin("dev.riley.stevefish", "SteveFish", "0.1.0")]; reads config, starts BridgeClient, creates the dispatcher
│  │  ├─ Config.cs                 BridgeUrl (ws://127.0.0.1:25599), PingIntervalSeconds, MinecraftModeKey
│  │  ├─ Bridge/BridgeClient.cs    ClientWebSocket (from the game's System.dll); connect, reconnect with backoff, send queue, receive loop
│  │  ├─ Bridge/Envelope.cs        { v, type, seq, ts, re, payload } + DTOs for hello / hello_ack / ping / pong / error (Newtonsoft.Json 13 from the game)
│  │  ├─ Bridge/MainThread.cs      MonoBehaviour that drains a ConcurrentQueue<Action> in Update(); Unity APIs only run here
│  │  ├─ Safety/SoloGuard.cs       evaluates the three solo-only conditions (game-map §7); Phase 2 only logs them
│  │  └─ Patches/                  empty in Phase 2 (first patch, Player.TickUpdate postfix, lands in Phase 3)
│  └─ README.md                    build + install (copy DLL to BepInEx/plugins/SteveFish/)
├─ bridge/
│  ├─ package.json                 typescript, ws, tsx, vitest (mineflayer is added in Phase 3)
│  ├─ tsconfig.json
│  ├─ src/
│  │  ├─ index.ts                  entry: reads STEVEFISH_PORT, starts server
│  │  ├─ server.ts                 WebSocket server bound to 127.0.0.1 only; one client; hello/ping/error handling
│  │  ├─ protocol.ts               envelope + message types mirroring protocol.md, runtime validation
│  │  └─ log.ts
│  └─ test/
│     ├─ protocol.test.ts          envelope validation, unknown-type tolerance, version mismatch
│     └─ pingpong.test.ts          fake plugin client: hello → hello_ack → ping → pong
├─ tools/rea/                      the dnfile/dncil analysis script; its output dir is git-ignored
└─ .gitignore                      *.dll, Managed/, BepInEx/, tools/rea/out/, *.decompiled.*, Minecraft assets
```

Notes for a web dev: a **BepInEx plugin** is a C# class library (`.dll`) that BepInEx loads into the game's Mono runtime at startup; the class with `[BepInPlugin]` is the entry point, and its `Awake()` runs once like a constructor. **Harmony** (HarmonyX in BepInEx 5) rewrites a game method at runtime so your code runs before (Prefix) or after (Postfix) it, which works like middleware around a function you don't own. Phase 2 doesn't need any patches.

## Smallest ping-pong test

**Automated, bridge only** (`npm test` in `bridge/`): `pingpong.test.ts` starts the server on a random port, connects a `ws` client that plays the plugin, and asserts `hello` → `hello_ack` (with `mc.spawned=false`, `features` intersection) → `ping{nonce}` → `pong{nonce}` with `re` = the ping's `seq`. Also asserts that a `v:1` hello is rejected with `error{code:"version"}` and that a second client gets `error{code:"busy"}`.

**Manual, in-game** (the actual Phase 2 proof):

1. `cd bridge && npm run dev`, which prints `listening on 127.0.0.1:25599`.
2. Launch How to Fish with BepInEx and the plugin, and start an **offline** game.
3. Plugin `Awake()` logs `[SteveFish] loaded, unity=6000.4.4f1` (answers unknown U1).
4. Plugin connects and sends `hello` with `unityVersion`; the bridge prints it.
5. Every 5 s the plugin sends `ping`, the bridge answers `pong`, and the plugin logs `[SteveFish] pong rtt=Nms` **from the main-thread dispatcher**, together with `LocalPlayer=<present|null>` and the three SoloGuard values. That proves the plugin can read game state on the main thread when bridge messages arrive.
6. Stop the bridge: the game keeps running and the plugin logs reconnect attempts. Start it again: the plugin reconnects and the pings resume.

Pass = steps 3–6 visible in `BepInEx/LogOutput.log` and the bridge console. That covers injection, transport, envelope/versioning, threading and resilience, which is everything Phase 3 builds on.

## Prerequisites

- BepInEx 5.4.x (x64, Mono) unpacked into the How to Fish folder. U1 is whether it loads on Unity 6.4.
- .NET SDK 8+ (only to *build* the plugin against netstandard2.1)
- Node 20+
- Optional now, needed by Phase 3: UnityExplorer (BepInEx 5 Mono build) to work through game-map §9.
