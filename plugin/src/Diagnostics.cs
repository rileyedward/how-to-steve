using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FishNet;
using HarmonyLib;
using UnityEngine;

namespace HowToSteve
{
    /// <summary>
    /// One-shot runtime answers to the game-map §9 unknowns, logged as "[diag] Ux ...". Main thread only.
    /// Each probe is isolated so one failure can't hide the rest.
    /// </summary>
    internal static class Diagnostics
    {
        public static void Run(ManualLogSource log)
        {
            Probe(log, "U2", () =>
            {
                var mv = Player.LocalPlayer?.Movement;
                var col = mv == null ? null : AccessTools.Field(typeof(PlayerMovement), "_col")?.GetValue(mv) as CapsuleCollider;
                var scale = Player.LocalPlayer?.Transform?.lossyScale;
                return col == null ? "no capsule" : $"player capsule height={col.height:F3} radius={col.radius:F3} center={col.center} lossyScale={scale}";
            });
            Probe(log, "U3", () =>
            {
                var sb = new StringBuilder();
                foreach (var p in typeof(GameInfo).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(LayerMask)))
                {
                    var m = (LayerMask)p.GetValue(null);
                    sb.Append($"{p.Name}=0x{m.value:X}[{LayerNames(m.value)}] ");
                }
                return sb.Length == 0 ? "no LayerMask properties" : sb.ToString();
            });
            Probe(log, "U4", () =>
            {
                var pairs = new[] { (3, 8), (24, 8), (24, 11), (24, 12), (24, 13), (3, 11), (24, 16) };
                return string.Join(" ", pairs.Select(p => $"{LayerMask.LayerToName(p.Item1)}x{LayerMask.LayerToName(p.Item2)}={(Physics.GetIgnoreLayerCollision(p.Item1, p.Item2) ? "ignored" : "collide")}"));
            });
            Probe(log, "U5", () =>
            {
                var island = Island.CurIsland;
                if (island == null) return "no current island";
                var scene = island.gameObject.scene;
                var counts = new Dictionary<string, int>();
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                    {
                        var k = $"{c.GetType().Name}@{LayerMask.LayerToName(c.gameObject.layer)}{(c.isTrigger ? "(trigger)" : "")}";
                        counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
                    }
                var anchor = McTransform.CurrentAnchor();
                return $"scene={scene.name} island={island.name} anchor={anchor?.name} anchorPos={anchor?.position} IslandSize={Island.IslandSize} colliders: " +
                       string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"));
            });
            Probe(log, "U6", () => $"FishNet TickRate={InstanceFinder.TimeManager?.TickRate}");
            Probe(log, "U8", () => string.Join(" ", new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Unlit" }
                .Select(n => $"'{n}'={(Shader.Find(n) != null ? "present" : "missing")}")));
            Probe(log, "U10", () =>
            {
                var sb = new StringBuilder();
                foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>())
                {
                    var t = mb.GetType();
                    if (!t.Name.Contains("Transport")) continue;
                    sb.Append($"{t.FullName}: ");
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (f.Name.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append($"{f.Name}={f.GetValue(mb)} ");
                        else if (f.Name.IndexOf("ConnectionData", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var cd = f.GetValue(mb);
                            if (cd == null) continue;
                            foreach (var cf in cd.GetType().GetFields())
                                if (cf.Name.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0 || cf.Name.IndexOf("address", StringComparison.OrdinalIgnoreCase) >= 0)
                                    sb.Append($"{f.Name}.{cf.Name}={cf.GetValue(cd)} ");
                        }
                    }
                    sb.Append("| ");
                }
                return sb.Length == 0 ? "no transport components" : sb.ToString();
            });
            Probe(log, "U12", () =>
            {
                var cam = GameInfo.CurCamera;
                if (cam == null) return "no CurCamera";
                return $"camera={cam.name} cullingMask=0x{cam.cullingMask:X} includes Level(8)={(cam.cullingMask & (1 << 8)) != 0} near={cam.nearClipPlane} far={cam.farClipPlane}";
            });
        }

        private static void Probe(ManualLogSource log, string id, Func<string> probe)
        {
            try { log.LogInfo($"[diag] {id} {probe()}"); }
            catch (Exception e) { log.LogInfo($"[diag] {id} failed: {e.GetType().Name}: {e.Message}"); }
        }

        private static string LayerNames(int mask)
        {
            var names = new List<string>();
            for (var i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) names.Add($"{i}:{LayerMask.LayerToName(i)}");
            return string.Join(",", names);
        }
    }
}
