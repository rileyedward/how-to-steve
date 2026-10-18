using BepInEx.Configuration;
using UnityEngine;

namespace SteveFish
{
    /// <summary>Bound to BepInEx\config\dev.riley.stevefish.cfg (created on first launch; new keys are added automatically).</summary>
    internal sealed class PluginConfig
    {
        public ConfigEntry<string> BridgeUrl { get; }
        public ConfigEntry<float> PingIntervalSeconds { get; }
        public ConfigEntry<string> MinecraftModeKey { get; }
        public ConfigEntry<string> InventoryKey { get; }

        public ConfigEntry<string> ClientJar { get; }
        public ConfigEntry<string> PreferredVersion { get; }

        public ConfigEntry<float> OriginX { get; }
        public ConfigEntry<float> OriginY { get; }
        public ConfigEntry<float> OriginZ { get; }
        public ConfigEntry<float> Scale { get; }
        public ConfigEntry<bool> FlipZ { get; }

        public ConfigEntry<bool> RenderBlocks { get; }
        public ConfigEntry<int> RenderLayer { get; }
        public ConfigEntry<bool> FlipWinding { get; }
        public ConfigEntry<bool> RunDiagnostics { get; }
        public ConfigEntry<bool> ShowHud { get; }
        public ConfigEntry<bool> Sounds { get; }
        public ConfigEntry<int> HudScale { get; }
        public ConfigEntry<bool> TerrainSync { get; }
        public ConfigEntry<int> ShellDepth { get; }
        public ConfigEntry<bool> PerIslandOrigin { get; }

        public PluginConfig(ConfigFile file)
        {
            BridgeUrl = file.Bind("Bridge", "BridgeUrl", "ws://127.0.0.1:25599",
                "WebSocket URL of the SteveFish bridge. Must be a loopback address (local-only).");
            PingIntervalSeconds = file.Bind("Bridge", "PingIntervalSeconds", 5f,
                "Seconds between heartbeat pings. Three missed pongs drop the connection.");
            MinecraftModeKey = file.Bind("Input", "MinecraftModeKey", "B",
                "Input System key name that toggles Minecraft mode (used from Phase 4).");

            InventoryKey = file.Bind("Input", "InventoryKey", "E", "Opens the Minecraft inventory (with the 2x2 crafting grid) while in Minecraft mode.");
            ClientJar = file.Bind("Minecraft", "ClientJar", "",
                "Full path to a Minecraft client .jar to read block models/textures from. Empty = auto-detect in %APPDATA%\\.minecraft\\versions.");
            PreferredVersion = file.Bind("Minecraft", "PreferredVersion", "1.21.11",
                "Version folder to prefer when auto-detecting the client jar (matches the local server).");

            OriginX = file.Bind("Transform", "McOriginX", 0f, "Minecraft X of the island anchor's origin.");
            OriginY = file.Bind("Transform", "McOriginY", 64f, "Minecraft Y of the island anchor's origin.");
            OriginZ = file.Bind("Transform", "McOriginZ", 0f, "Minecraft Z of the island anchor's origin.");
            Scale = file.Bind("Transform", "Scale", 1f, "Minecraft blocks per Unity unit (U2).");
            FlipZ = file.Bind("Transform", "FlipZ", true, "Flip Z between Unity (left-handed) and Minecraft (right-handed).");

            RenderBlocks = file.Bind("Render", "RenderBlocks", true, "Draw Minecraft blocks in How to Fish.");
            RenderLayer = file.Bind("Render", "SectionLayer", 11,
                "Unity layer for Minecraft block meshes + colliders. 11 = InvisibleColliders: the player collides with it and can stand on it (game-map U3/U4), and the terrain voxelizer (layer 8 only) never sees our own blocks (decisions D21).");
            FlipWinding = file.Bind("Render", "FlipWinding", false, "Set to true if blocks render inside-out (you see their back faces).");
            ShowHud = file.Bind("Render", "ShowHud", true, "Draw the Minecraft hotbar, hearts, food and XP while the overlay is active.");
            Sounds = file.Bind("Render", "Sounds", true, "Play Minecraft block sounds (from your own .minecraft assets).");
            HudScale = file.Bind("Render", "HudScale", 0, "Minecraft GUI scale for the HUD (0 = automatic, like vanilla).");
            TerrainSync = file.Bind("Terrain", "TerrainSync", true, "Voxelize the island into invisible Minecraft barriers so placing and physics agree (protocol 4.8).");
            ShellDepth = file.Bind("Terrain", "ShellDepth", 4, "Blocks of barrier under the island surface per column.");
            PerIslandOrigin = file.Bind("Terrain", "PerIslandOrigin", true, "Give each IslandN scene its own Minecraft area (McOriginX + (N-1)*4096).");
            RunDiagnostics = file.Bind("Debug", "RunDiagnostics", true, "Log the [diag] block once per session.");
        }

        public McTransform BuildTransform() => new McTransform
        {
            McOrigin = new Vector3(OriginX.Value, OriginY.Value, OriginZ.Value),
            Scale = Scale.Value <= 0 ? 1f : Scale.Value,
            FlipZ = FlipZ.Value,
        };
    }
}
