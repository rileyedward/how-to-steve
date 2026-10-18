using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using HowToSteve.Bridge;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HowToSteve.Minecraft
{
    /// <summary>
    /// Turns the current island's level colliders into a block bitset for terrain_upload (protocol §4.8),
    /// so Minecraft gets invisible barriers where How to Fish has ground. Main thread; the scan is spread
    /// over frames (a few thousand raycasts each) so it never hitches.
    /// </summary>
    internal sealed class TerrainVoxelizer
    {
        private const int RaysPerFrame = 3000;
        private const int ChunkBytes = 192 * 1024; // binary bytes per message (≈256 KiB base64)
        private const int MaxCells = 32 * 1024 * 1024;
        private const float SeaFloorMargin = 2f; // metres below the water line still counted (shallows, beaches)

        private readonly WorldRenderer _renderer;
        private readonly ManualLogSource _log;
        private readonly int _shellDepth;
        private readonly Action<string, object> _send;

        private Scan _scan;
        private int _revision;
        public string LastRegion { get; private set; }

        private sealed class Scan
        {
            public string Region;
            public Vector3Int Origin, Size;
            public HashSet<Collider> Allowed;
            public byte[] Bits;
            public int Column;
            public int Solid;
            public int Underwater;
            public float StartedAt;
        }

        public TerrainVoxelizer(WorldRenderer renderer, int shellDepth, Action<string, object> send, ManualLogSource log)
        {
            _renderer = renderer;
            _shellDepth = Mathf.Clamp(shellDepth, 1, 64);
            _send = send;
            _log = log;
        }

        public bool Busy => _scan != null;

        public void Cancel() => _scan = null;

        /// <summary>Starts scanning the current island. Returns false if there's nothing to scan.</summary>
        public bool Begin(Island island)
        {
            if (island == null) return false;
            var scene = island.gameObject.scene;
            var allowed = CollectColliders(scene);
            if (allowed.Count == 0) { _log.LogWarning($"terrain: no level colliders in {scene.name}"); return false; }

            // Union of the colliders' world bounds, as a Minecraft-space box.
            var root = _renderer.McRoot;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var c in allowed)
            {
                var b = c.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    var mc = root.InverseTransformPoint(corner);
                    min = Vector3.Min(min, mc);
                    max = Vector3.Max(max, mc);
                }
            }
            var origin = new Vector3Int(Mathf.FloorToInt(min.x), Mathf.FloorToInt(min.y) - _shellDepth, Mathf.FloorToInt(min.z));
            var size = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(max.x) - origin.x, 1, 1024),
                Mathf.Clamp(Mathf.CeilToInt(max.y) - origin.y + 1, 1, 1024),
                Mathf.Clamp(Mathf.CeilToInt(max.z) - origin.z, 1, 1024));
            if ((long)size.x * size.y * size.z > MaxCells) { _log.LogWarning($"terrain: {scene.name} is too big ({size})"); return false; }

            _scan = new Scan
            {
                Region = "island:" + scene.name,
                Origin = origin,
                Size = size,
                Allowed = allowed,
                Bits = new byte[size.x * size.y * size.z],
                StartedAt = Time.realtimeSinceStartup,
            };
            _log.LogInfo($"terrain: scanning {scene.name}: {allowed.Count} colliders, box origin={origin} size={size}");
            return true;
        }

        /// <summary>Static, solid level colliders in the island scene (no triggers, no moving rigidbodies).</summary>
        private HashSet<Collider> CollectColliders(Scene scene)
        {
            var set = new HashSet<Collider>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var c in root.GetComponentsInChildren<Collider>(false))
                {
                    if (c.isTrigger || !c.enabled || c.gameObject.layer != 8) continue;
                    if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                    if (c.GetComponent<SectionMarker>() != null) continue;
                    set.Add(c);
                }
            return set;
        }

        /// <summary>Call once per frame. Sends terrain_upload when the scan finishes.</summary>
        public void Tick()
        {
            var s = _scan;
            if (s == null) return;
            var root = _renderer.McRoot;
            var down = root.TransformVector(Vector3.down);
            var unit = down.magnitude;
            down /= unit;
            var columns = s.Size.x * s.Size.z;
            var rayLength = (s.Size.y + 2) * unit;
            // Sea floor isn't somewhere Steve builds: skip columns whose ground is well under the water line.
            var waterY = float.NegativeInfinity;
            try { waterY = WaterManager.WaterHeight - SeaFloorMargin; } catch (Exception) { /* no water manager */ }

            for (var n = 0; n < RaysPerFrame && s.Column < columns; n++, s.Column++)
            {
                int x = s.Column % s.Size.x, z = s.Column / s.Size.x;
                var top = root.TransformPoint(new Vector3(s.Origin.x + x + 0.5f, s.Origin.y + s.Size.y + 1, s.Origin.z + z + 0.5f));
                var hits = Physics.RaycastAll(top, down, rayLength, 1 << 8, QueryTriggerInteraction.Ignore);
                var bestDist = float.MaxValue;
                var bestY = float.NaN;
                var bestWorldY = 0f;
                foreach (var h in hits)
                {
                    if (h.distance >= bestDist || !s.Allowed.Contains(h.collider)) continue;
                    bestDist = h.distance;
                    bestY = root.InverseTransformPoint(h.point).y;
                    bestWorldY = h.point.y;
                }
                if (float.IsNaN(bestY)) continue;
                if (bestWorldY < waterY) { s.Underwater++; continue; }

                // Solid if the cell's centre is under the surface (same rounding as BuildController).
                var topCell = Mathf.FloorToInt(bestY - 0.5f) - s.Origin.y;
                for (var y = Mathf.Min(topCell, s.Size.y - 1); y >= 0 && y > topCell - _shellDepth; y--)
                {
                    s.Bits[(y * s.Size.z + z) * s.Size.x + x] = 1;
                    s.Solid++;
                }
            }
            if (s.Column < columns) return;

            _scan = null;
            Upload(s);
        }

        private void Upload(Scan s)
        {
            var rle = EncodeRle(s.Bits);
            _revision++;
            var chunks = (rle.Length + ChunkBytes - 1) / ChunkBytes;
            for (var i = 0; i < chunks; i++)
            {
                var len = Math.Min(ChunkBytes, rle.Length - i * ChunkBytes);
                _send("terrain_upload", new TerrainUploadPayload
                {
                    Region = s.Region,
                    Revision = _revision,
                    Origin = new[] { s.Origin.x, s.Origin.y, s.Origin.z },
                    Size = new[] { s.Size.x, s.Size.y, s.Size.z },
                    Encoding = "bitset-rle",
                    Data = Convert.ToBase64String(rle, i * ChunkBytes, len),
                    Final = i == chunks - 1,
                });
            }
            LastRegion = s.Region;
            _log.LogInfo($"terrain: sent {s.Region} rev {_revision}: {s.Solid} solid cells ({s.Underwater} sea-floor columns skipped), {rle.Length} bytes RLE in {chunks} message(s), " +
                         $"scan took {Time.realtimeSinceStartup - s.StartedAt:F1}s");
        }

        /// <summary>Alternating uint16 LE run lengths, starting with empty; long runs split by zero-length runs.</summary>
        public static byte[] EncodeRle(byte[] bits)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                byte current = 0;
                var run = 0;
                foreach (var b0 in bits)
                {
                    var b = b0 != 0 ? (byte)1 : (byte)0;
                    if (b == current)
                    {
                        if (run == 0xFFFF) { w.Write((ushort)run); w.Write((ushort)0); run = 0; }
                        run++;
                    }
                    else
                    {
                        w.Write((ushort)run);
                        current = b;
                        run = 1;
                    }
                }
                w.Write((ushort)run);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
