# SteveFish plugin (BepInEx 5)

C# class library that BepInEx loads into How to Fish's Mono runtime. Generated with `dotnet new bepinex5plugin`
(BepInEx.Templates 2.0.0-be.4) and adapted so the game's own assemblies are referenced from the install, never copied.

## One-time setup

1. Install BepInEx 5.4.x **x64** into the folder that contains `How to Fish.exe`
   (`C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish\`). Tested with 5.4.23.5.
2. Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` (git-ignored) and fix the paths if your
   Steam library is elsewhere. `HowToFishManaged` points at `How to Fish_Data\Managed`, `BepInExDir` at the BepInEx folder.
3. .NET SDK 8 or newer (only used to compile against `netstandard2.1`).

## Build and install

```powershell
dotnet build -c Release                 # -> bin\Release\netstandard2.1\SteveFish.Plugin.dll
dotnet build -c Release -p:Deploy=true  # also copies it to BepInEx\plugins\SteveFish\
```

Only `SteveFish.Plugin.dll` is deployed. Unity, Newtonsoft.Json 13, FishNet and `ClientWebSocket` come from the game
itself (`Private=false` references), so nothing else is bundled.

Config is created on first launch at `BepInEx\config\dev.riley.stevefish.cfg` (`BridgeUrl`, `PingIntervalSeconds`,
`MinecraftModeKey`). Logs go to `BepInEx\LogOutput.log`, tagged `:SteveFish]`.

## Layout

| file | role |
|---|---|
| `src/Plugin.cs` | `[BepInPlugin("dev.riley.stevefish", "SteveFish", "0.1.0")]` entry point; `Awake()` runs once at startup |
| `src/Config.cs` | BepInEx config bindings |
| `src/Bridge/BridgeClient.cs` | WebSocket client: connect, backoff 1/2/4/8/10 s, send loop, receive loop, heartbeat (3 missed pongs = reconnect) |
| `src/Bridge/Envelope.cs` | protocol v0 envelope and hello/hello_ack/ping/pong/error DTOs |
| `src/Bridge/MainThread.cs` | `MonoBehaviour` that runs posted actions in `Update()`; all game-state reads go through it |
| `src/Safety/SoloGuard.cs` | the three solo-only checks (game-map §7); Phase 2 only logs them |
| `src/Patches/` | Harmony patches, empty until Phase 3 |

C# / Unity notes for a web dev: a `MonoBehaviour` is a component attached to a GameObject; Unity calls its `Update()`
once per frame on the main thread, like a `requestAnimationFrame` callback. Unity objects aren't thread-safe, so the
socket threads only enqueue closures and `MainThread.Update()` runs them, which is the same idea as posting to an event loop.
