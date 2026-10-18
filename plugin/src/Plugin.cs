using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SteveFish.Bridge;
using SteveFish.Minecraft;
using SteveFish.Patches;
using SteveFish.Safety;
using UnityEngine;

namespace SteveFish
{
    // BepInEx finds this class by its [BepInPlugin] attribute and calls Awake() once at game start.
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private PluginConfig _config;
        private BridgeClient _bridge;
        private string _unityVersion;
        private McTransform _transform;
        private PlayerStateSender _sender;
        private JarAssets _jar;
        private BlockAtlas _atlas;
        private MeshWorker _worker;
        private WorldRenderer _renderer;
        private Harmony _harmony;
        private McClientState _mcState;
        private BuildController _build;
        private HudOverlay _hud;
        private TerrainVoxelizer _terrain;
        private SteveViewModel _viewModel;
        private InventoryScreen _screen;
        private McSounds _sounds;
        private volatile bool _bridgeHasWindow;
        private Vector3 _baseOrigin;
        private string _islandScene;
        private float _islandSince;
        private string _terrainSentFor;

        // Written from the socket thread, read on the main thread.
        private volatile bool _mcSpawned;
        private volatile bool _bridgeHasPlayerState;
        private volatile bool _bridgeHasBlocks;
        private volatile bool _bridgeHasBuilding;
        private volatile bool _bridgeHasTerrain;

        // Main thread only.
        private bool _guardOk;
        private string _lastGuard;
        private float _nextGuardCheck;
        private float _nextStatus;
        private float _diagAt = -1f;
        private bool _diagDone;

        private void Awake()
        {
            Logger = base.Logger;
            _unityVersion = Application.unityVersion; // Unity API: read here, on the main thread
            Logger.LogInfo($"loaded, unity={_unityVersion} plugin={MyPluginInfo.PLUGIN_VERSION}");

            _config = new PluginConfig(Config);
            _transform = _config.BuildTransform();
            _baseOrigin = _transform.McOrigin;
            MainThread.Create(Logger);

            // Minecraft assets come from the player's own client jar, at runtime only.
            _jar = JarAssets.Open(_config.ClientJar.Value, _config.PreferredVersion.Value, Logger);
            _atlas = new BlockAtlas(_jar, Logger);
            _atlas.CreateTexture();
            var models = new BlockModels(_jar, Logger);
            _worker = new MeshWorker(models, _atlas, _config.FlipWinding.Value, Logger);
            _renderer = WorldRenderer.Create(_transform, _atlas, _config.RenderLayer.Value, Logger);
            _worker.MeshReady += (key, mesh) => _renderer.Enqueue(key, mesh);
            _mcState = new McClientState();
            _build = new BuildController(_renderer, _mcState, _config.MinecraftModeKey.Value, (type, payload) => _bridge.Send(type, payload), Logger);
            if (_config.ShowHud.Value) _hud = HudOverlay.Create(_jar, _mcState, _renderer, _config.HudScale.Value);
            _screen = new InventoryScreen(_mcState, _config.InventoryKey.Value, (type, payload) => _bridge.Send(type, payload), Logger);
            if (_hud != null)
            {
                _hud.AttachScreen(_screen);
                _build.LoadTexture = _hud.TextureAt;
                _viewModel = new SteveViewModel(_jar, _mcState, _hud.IconFor, _renderer.MakeViewModelMaterial);
                _build.Swung += _viewModel.Swing;
            }
            if (_config.Sounds.Value && _jar != null)
            {
                _sounds = new McSounds(_jar.JarPath, Logger);
                _sounds.Preload();
                _build.Sounds = _sounds;
            }
            _terrain = new TerrainVoxelizer(_renderer, _config.ShellDepth.Value, (type, payload) => _bridge.Send(type, payload), Logger);

            _sender = new PlayerStateSender(_transform, 20f, () => _guardOk && _bridge.IsConnected && _bridgeHasPlayerState,
                ps => _bridge.Send(MessageTypes.PlayerState, ps));

            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            PlayerTickPatch.OnLocalTick = _sender.Tick;
            PlayerTickPatch.TryInstall(_harmony, Logger);

            _bridge = new BridgeClient(_config.BridgeUrl.Value, _config.PingIntervalSeconds.Value, BuildHello, Logger);
            _bridge.PongReceived += rtt => MainThread.Post(() => OnPong(rtt));
            _bridge.MessageReceived += OnMessageAnyThread;
            _bridge.Disconnected += OnBridgeDisconnected;
            _bridge.Start();

            MainThread.PerFrame += PerFrame;
        }

        private HelloPayload BuildHello() => new HelloPayload
        {
            PluginVersion = MyPluginInfo.PLUGIN_VERSION,
            UnityVersion = _unityVersion,
            Transform = new TransformDto
            {
                McOrigin = new double[] { _transform.McOrigin.x, _transform.McOrigin.y, _transform.McOrigin.z },
                Scale = _transform.Scale,
                FlipZ = _transform.FlipZ,
            },
            Features = new List<string> { Features.PlayerState, Features.BlocksNear, Features.Dig, Features.Place, Features.Inventory, Features.HudState, "terrain_upload", "window" },
        };

        // Socket thread. Block data goes straight to the mesh worker; everything else to the main thread.
        private void OnMessageAnyThread(Envelope env)
        {
            if (env.Type == MessageTypes.BlocksNear)
            {
                if (_guardOk) _worker.Post(env.PayloadAs<BlocksNearPayload>());
                return;
            }
            if (env.Type == MessageTypes.HelloAck)
            {
                var ack = env.PayloadAs<HelloAckPayload>();
                var features = ack?.Features ?? new List<string>();
                _bridgeHasPlayerState = features.Contains(Features.PlayerState);
                _bridgeHasBlocks = features.Contains(Features.BlocksNear);
                _bridgeHasBuilding = features.Contains(Features.Dig) && features.Contains(Features.Place);
                _bridgeHasTerrain = features.Contains("terrain_upload");
                _bridgeHasWindow = features.Contains("window");
                MainThread.Post(() => _terrainSentFor = null); // fresh bridge session: send terrain again
                _mcSpawned = ack?.Mc?.Spawned ?? false;
            }
            else if (env.Type == MessageTypes.McStatus)
            {
                var st = env.PayloadAs<McStatusPayload>();
                _mcSpawned = st?.State == "spawned";
                if (!_mcSpawned) _worker.Clear();
            }
            MainThread.Post(() => OnMessage(env));
        }

        private void OnBridgeDisconnected()
        {
            _mcSpawned = false;
            _bridgeHasPlayerState = false;
            _bridgeHasBlocks = false;
            _bridgeHasBuilding = false;
            _bridgeHasTerrain = false;
            _bridgeHasWindow = false;
            _worker.Clear();
            MainThread.Post(() => _mcState.Clear());
        }

        private void PerFrame()
        {
            var now = Time.unscaledTime;
            if (now >= _nextGuardCheck)
            {
                _nextGuardCheck = now + 1f;
                UpdateGuard();
            }

            // Fallback when the Harmony patch couldn't be installed (game update): poll at frame rate.
            if (!PlayerTickPatch.Installed && Player.LocalPlayer != null) _sender.Tick(Player.LocalPlayer);

            _renderer.Active = _guardOk && _config.RenderBlocks.Value && _bridge.IsConnected && _mcSpawned && _bridgeHasBlocks;
            _screen.Tick(_renderer.Visible && _mcState.MinecraftMode && _bridgeHasWindow);
            _build.Tick(_renderer.Active && _bridgeHasBuilding);
            TrackIsland(now);
            _terrain.Tick();
            _sounds?.Tick();
            _viewModel?.Tick(_renderer.Visible && _mcState.MinecraftMode);

            if (_diagAt > 0 && now >= _diagAt && !_diagDone)
            {
                _diagDone = true;
                if (_config.RunDiagnostics.Value) Diagnostics.Run(Logger);
            }

            if (now >= _nextStatus && _bridge.IsConnected && _guardOk)
            {
                _nextStatus = now + 30f;
                var p = _sender.LastMcPos;
                Logger.LogInfo($"status: steve={(p.HasValue ? $"{p.Value.x:F1},{p.Value.y:F1},{p.Value.z:F1}" : "?")} sent={_sender.Sent} " +
                               $"mc.spawned={_mcSpawned} sections={_worker.SectionCount} meshes={_renderer.LiveSections} atlasTiles={_atlas.SlotCount} shader={_renderer.ShaderName}");
            }
        }

        /// <summary>Per-island Minecraft areas (IslandN → McOriginX + (N-1)*4096) and terrain sync triggers.</summary>
        private void TrackIsland(float now)
        {
            var island = Island.CurIsland;
            var scene = island != null ? island.gameObject.scene.name : null;
            if (scene != _islandScene)
            {
                _islandScene = scene;
                _islandSince = now;
                _terrain.Cancel();
                if (scene != null && _config.PerIslandOrigin.Value)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(scene, @"^Island(\d+)$");
                    var n = m.Success ? int.Parse(m.Groups[1].Value) : 1;
                    var origin = _baseOrigin + new Vector3((n - 1) * 4096, 0, 0);
                    if (origin != _transform.McOrigin)
                    {
                        _transform.McOrigin = origin;
                        _renderer.ReapplyTransform();
                        _worker.Clear();
                        _needResync = true;
                    }
                    Logger.LogInfo($"island {scene}: Minecraft origin {_transform.McOrigin}");
                }
            }

            if (scene == null || !_config.TerrainSync.Value || _terrain.Busy || _terrainSentFor == scene) return;
            if (!_guardOk || !_bridge.IsConnected || !_bridgeHasTerrain || !_renderer.Visible || now - _islandSince < 2f) return;
            _terrainSentFor = scene;
            _terrain.Begin(island);
        }

        private void UpdateGuard()
        {
            var snap = SoloGuard.Evaluate();
            var ok = snap.AllowsActivation && Player.LocalPlayer != null;
            var text = snap.ToString();
            if (text != _lastGuard)
            {
                Logger.LogInfo($"guard: {text}");
                _lastGuard = text;
            }
            if (_guardOk && !ok)
            {
                Logger.LogWarning("solo guard no longer satisfied: hiding Minecraft overlay and pausing player_state");
                _worker.Clear();
            }
            if (!_guardOk && ok)
            {
                // Block messages were dropped while the guard was off: ask for the sections around Steve again.
                _needResync = true;
                if (!_diagDone) _diagAt = Time.unscaledTime + 3f;
            }
            _guardOk = ok;

            if (_needResync && _guardOk && _bridge.IsConnected && _bridgeHasBlocks && _sender.LastMcPos.HasValue)
            {
                _needResync = false;
                var p = _sender.LastMcPos.Value;
                int cx = Mathf.FloorToInt(p.x / 16f), sy = Mathf.FloorToInt(p.y / 16f), cz = Mathf.FloorToInt(p.z / 16f);
                var sections = new List<int[]>();
                for (var dx = -4; dx <= 4; dx++)
                    for (var dz = -4; dz <= 4; dz++)
                        for (var dy = -3; dy <= 3; dy++)
                            sections.Add(new[] { cx + dx, sy + dy, cz + dz });
                _bridge.Send(MessageTypes.BlocksRequest, new BlocksRequestPayload { Sections = sections });
            }
        }

        private bool _needResync;

        // Runs on the main thread via MainThread, so reading game statics is safe here.
        private void OnPong(double rttMs)
        {
            var localPlayer = Player.LocalPlayer != null ? "present" : "null";
            Logger.LogDebug($"pong rtt={rttMs:F1}ms LocalPlayer={localPlayer} {SoloGuard.Evaluate()}");
        }

        private void OnMessage(Envelope env)
        {
            switch (env.Type)
            {
                case MessageTypes.HelloAck:
                    var ack = env.PayloadAs<HelloAckPayload>();
                    Logger.LogInfo($"hello_ack bridge={ack?.BridgeVersion} mc={ack?.Mc?.Version} {ack?.Mc?.Username} spawned={ack?.Mc?.Spawned} features=[{string.Join(",", ack?.Features ?? new List<string>())}]");
                    break;
                case MessageTypes.McStatus:
                    var st = env.PayloadAs<McStatusPayload>();
                    Logger.LogInfo($"mc_status {st?.State}{(st?.Reason != null ? $" ({st.Reason})" : "")}");
                    break;
                case BuildTypes.Inventory:
                    _mcState.Inventory = env.PayloadAs<InventoryPayload>();
                    if (_mcState.Inventory != null) _mcState.HeldSlot = _mcState.Inventory.HeldSlot;
                    break;
                case BuildTypes.HudState:
                    _mcState.Hud = env.PayloadAs<HudStatePayload>();
                    break;
                case BuildTypes.DigResult:
                    _build.OnDigResult(env.PayloadAs<DigResultPayload>());
                    break;
                case "window_open":
                    _mcState.Window = env.PayloadAs<WindowPayload>();
                    break;
                case "window_update":
                    var wu = env.PayloadAs<WindowPayload>();
                    if (_mcState.Window != null && wu != null && wu.WindowId == _mcState.Window.WindowId) _mcState.Window = wu;
                    break;
                case "window_close":
                    var wc = env.PayloadAs<WindowIdPayload>();
                    if (_mcState.Window != null && wc != null && wc.WindowId == _mcState.Window.WindowId) _screen.Close(false);
                    break;
                case "terrain_ack":
                    var ta = env.PayloadAs<TerrainAckPayload>();
                    Logger.LogInfo($"terrain_ack {ta?.Region} rev {ta?.Revision}: applied={ta?.Applied} skipped={ta?.Skipped}");
                    break;
                case BuildTypes.PlaceResult:
                    _build.OnPlaceResult(env.PayloadAs<PlaceResultPayload>());
                    break;
                case MessageTypes.Error:
                    var err = env.PayloadAs<ErrorPayload>();
                    Logger.LogWarning($"bridge error {err?.Code}: {err?.Message}");
                    break;
                default:
                    Logger.LogDebug($"ignoring unknown message type '{env.Type}'");
                    break;
            }
        }

        // Some games destroy BepInEx's manager object on scene load, so the bridge is tied to the
        // process lifetime instead of this component (the MainThread object is DontDestroyOnLoad).
        private void OnEnable() => Application.quitting += OnQuit;

        private void OnDestroy() => Logger.LogDebug("plugin component destroyed; bridge keeps running until quit");

        private void OnQuit()
        {
            _build?.Shutdown();
            _bridge?.Stop();
            _worker?.Dispose();
            _jar?.Dispose();
        }
    }
}
