using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using HowToSteve.Bridge;
using UnityEngine;

namespace HowToSteve.Minecraft
{
    /// <summary>Plain arrays for one section mesh, built off the main thread.</summary>
    internal sealed class MeshData
    {
        public (int x, int y, int z) Section;
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector2> Uvs = new List<Vector2>();
        public readonly List<int> Opaque = new List<int>();
        public readonly List<int> Cutout = new List<int>();
        // Collision-only mesh: solid blocks only (flowers, grass, torches and fluids stay walk-through).
        public readonly List<Vector3> CollisionVertices = new List<Vector3>();
        public readonly List<int> CollisionTriangles = new List<int>();
        public bool IsEmpty => Vertices.Count == 0;
    }

    /// <summary>
    /// Owns the client-side copy of the Minecraft blocks near Steve and turns sections into meshes.
    /// Everything here runs on one dedicated thread, so the store needs no locks. Finished meshes are
    /// handed to <see cref="MeshReady"/> (called on the worker thread; the receiver posts to the main thread).
    /// </summary>
    internal sealed class MeshWorker : IDisposable
    {
        private sealed class Section
        {
            public ushort[] Indices;   // 4096 palette indices, (ly*16+lz)*16+lx
            public int[] Palette;      // palette index → model id
        }

        private readonly BlockModels _models;
        private readonly BlockAtlas _atlas;
        private readonly ManualLogSource _log;
        private readonly bool _flipWinding;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        private readonly Dictionary<(int, int, int), Section> _sections = new Dictionary<(int, int, int), Section>();
        private readonly HashSet<(int, int, int)> _dirty = new HashSet<(int, int, int)>();
        private readonly Dictionary<string, int> _stateIds = new Dictionary<string, int>();
        private readonly List<BakedModel> _modelById = new List<BakedModel>();

        /// <summary>(section, mesh) — mesh is null when the section should be removed.</summary>
        public event Action<(int x, int y, int z), MeshData> MeshReady;

        public int SectionCount { get; private set; }

        public MeshWorker(BlockModels models, BlockAtlas atlas, bool flipWinding, ManualLogSource log)
        {
            _models = models;
            _atlas = atlas;
            _flipWinding = flipWinding;
            _log = log;
            StateId("minecraft:air"); // id 0 is always air
            _thread = new Thread(Run) { IsBackground = true, Name = "HowToSteve.MeshWorker" };
            _thread.Start();
        }

        // ---------- inputs (any thread) ----------

        public void Post(BlocksNearPayload msg)
        {
            if (msg == null || _queue.IsAddingCompleted) return;
            _queue.Add(() => Apply(msg));
        }

        public void Clear()
        {
            if (_queue.IsAddingCompleted) return;
            _queue.Add(() =>
            {
                foreach (var key in _sections.Keys) MeshReady?.Invoke(key, null);
                _sections.Clear();
                _dirty.Clear();
                SectionCount = 0;
            });
        }

        public void Dispose() => _queue.CompleteAdding();

        // ---------- worker thread ----------

        private void Run()
        {
            try
            {
                foreach (var job in _queue.GetConsumingEnumerable())
                {
                    RunSafely(job);
                    // Drain whatever else is queued before meshing, so a burst of sections meshes once.
                    while (_queue.TryTake(out var more)) RunSafely(more);
                    FlushDirty();
                }
            }
            catch (Exception e)
            {
                _log.LogError($"mesh worker stopped: {e}");
            }
        }

        private void RunSafely(Action a)
        {
            try { a(); }
            catch (Exception e) { _log.LogWarning($"mesh worker job failed: {e.Message}"); }
        }

        private void Apply(BlocksNearPayload msg)
        {
            switch (msg.Kind)
            {
                case "section":
                {
                    var key = Key(msg.Section);
                    if (msg.Data == null || msg.Palette == null)
                    {
                        if (_sections.Remove(key)) MarkAround(key);
                        MeshReady?.Invoke(key, null);
                        break;
                    }
                    var raw = Convert.FromBase64String(msg.Data);
                    if (raw.Length != 4096 * 2) throw new FormatException($"section data is {raw.Length} bytes");
                    var s = new Section { Indices = new ushort[4096], Palette = new int[msg.Palette.Length] };
                    Buffer.BlockCopy(raw, 0, s.Indices, 0, raw.Length); // little-endian on x86/x64
                    for (var i = 0; i < msg.Palette.Length; i++) s.Palette[i] = StateId(msg.Palette[i]);
                    _sections[key] = s;
                    MarkAround(key);
                    break;
                }
                case "update":
                {
                    if (msg.Pos == null || msg.Pos.Length != 3) break;
                    int x = msg.Pos[0], y = msg.Pos[1], z = msg.Pos[2];
                    var key = (FloorDiv(x), FloorDiv(y), FloorDiv(z));
                    if (!_sections.TryGetValue(key, out var s))
                    {
                        // An all-air section we never stored: create it so the new block shows up.
                        s = new Section { Indices = new ushort[4096], Palette = new[] { 0 } };
                        _sections[key] = s;
                    }
                    var id = StateId(msg.State);
                    var idx = Array.IndexOf(s.Palette, id);
                    if (idx < 0)
                    {
                        idx = s.Palette.Length;
                        Array.Resize(ref s.Palette, idx + 1);
                        s.Palette[idx] = id;
                    }
                    int lx = Mod(x), ly = Mod(y), lz = Mod(z);
                    s.Indices[(ly * 16 + lz) * 16 + lx] = (ushort)idx;
                    _dirty.Add(key);
                    if (lx == 0) _dirty.Add((key.Item1 - 1, key.Item2, key.Item3));
                    if (lx == 15) _dirty.Add((key.Item1 + 1, key.Item2, key.Item3));
                    if (ly == 0) _dirty.Add((key.Item1, key.Item2 - 1, key.Item3));
                    if (ly == 15) _dirty.Add((key.Item1, key.Item2 + 1, key.Item3));
                    if (lz == 0) _dirty.Add((key.Item1, key.Item2, key.Item3 - 1));
                    if (lz == 15) _dirty.Add((key.Item1, key.Item2, key.Item3 + 1));
                    break;
                }
                case "unload":
                {
                    var key = Key(msg.Section);
                    if (_sections.Remove(key)) MarkAround(key);
                    MeshReady?.Invoke(key, null);
                    break;
                }
            }
            SectionCount = _sections.Count;
        }

        private void MarkAround((int, int, int) k)
        {
            _dirty.Add(k);
            _dirty.Add((k.Item1 - 1, k.Item2, k.Item3)); _dirty.Add((k.Item1 + 1, k.Item2, k.Item3));
            _dirty.Add((k.Item1, k.Item2 - 1, k.Item3)); _dirty.Add((k.Item1, k.Item2 + 1, k.Item3));
            _dirty.Add((k.Item1, k.Item2, k.Item3 - 1)); _dirty.Add((k.Item1, k.Item2, k.Item3 + 1));
        }

        private void FlushDirty()
        {
            if (_dirty.Count == 0) return;
            var keys = new List<(int, int, int)>(_dirty);
            _dirty.Clear();
            foreach (var key in keys)
            {
                if (!_sections.ContainsKey(key)) continue; // neighbour we don't have
                var mesh = BuildMesh(key);
                MeshReady?.Invoke(key, mesh.IsEmpty ? null : mesh);
            }
        }

        private int StateId(string state)
        {
            state = state ?? "minecraft:air";
            if (_stateIds.TryGetValue(state, out var id)) return id;
            id = _modelById.Count;
            _stateIds[state] = id;
            _modelById.Add(_models.Get(state));
            return id;
        }

        private BakedModel ModelAt(int wx, int wy, int wz)
        {
            if (!_sections.TryGetValue((FloorDiv(wx), FloorDiv(wy), FloorDiv(wz)), out var s)) return BlockModels.Air;
            var idx = s.Indices[(Mod(wy) * 16 + Mod(wz)) * 16 + Mod(wx)];
            return idx < s.Palette.Length ? _modelById[s.Palette[idx]] : BlockModels.Air;
        }

        private MeshData BuildMesh((int x, int y, int z) key)
        {
            var mesh = new MeshData { Section = key };
            var s = _sections[key];
            int bx = key.x * 16, by = key.y * 16, bz = key.z * 16;

            for (var i = 0; i < 4096; i++)
            {
                var pi = s.Indices[i];
                if (pi >= s.Palette.Length) continue;
                var model = _modelById[s.Palette[pi]];
                if (model.IsAir) continue;
                int lx = i & 15, lz = (i >> 4) & 15, ly = i >> 8;

                foreach (var q in model.Quads)
                {
                    if (q.CullDir >= 0)
                    {
                        var d = Dirs.Vectors[q.CullDir];
                        var n = ModelAt(bx + lx + d.x, by + ly + d.y, bz + lz + d.z);
                        if (n.FullOpaque) continue;
                        if (model.CullSame && n.BlockName == model.BlockName) continue;
                        if (model.Fluid != null && n.Fluid == model.Fluid) continue;
                    }
                    AddQuad(mesh, q, new Vector3(lx, ly, lz), model.FullOpaque ? mesh.Opaque : mesh.Cutout);
                    if (model.Solid) AddCollisionQuad(mesh, q, new Vector3(lx, ly, lz));
                }
            }
            return mesh;
        }

        private void AddQuad(MeshData m, BakedQuad q, Vector3 offset, List<int> tris)
        {
            var slot = _atlas.SlotOf(q.Texture);
            var start = m.Vertices.Count;
            var normal = Vector3.Cross(q.Pos[1] - q.Pos[0], q.Pos[3] - q.Pos[0]).normalized;
            for (var k = 0; k < 4; k++)
            {
                m.Vertices.Add(q.Pos[k] + offset);
                m.Normals.Add(normal);
                m.Uvs.Add(BlockAtlas.AtlasUv(slot, q.Uv[k]));
            }
            // Corners are TL, BL, BR, TR. This order is clockwise in Unity's (left-handed) reading of the raw
            // Minecraft coordinates, which is what Unity treats as front-facing (docs/phases/phase3-plan.md).
            if (_flipWinding)
            {
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 1);
                tris.Add(start); tris.Add(start + 3); tris.Add(start + 2);
            }
            else
            {
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }
        }

        private void AddCollisionQuad(MeshData m, BakedQuad q, Vector3 offset)
        {
            var start = m.CollisionVertices.Count;
            for (var k = 0; k < 4; k++) m.CollisionVertices.Add(q.Pos[k] + offset);
            if (_flipWinding)
            {
                m.CollisionTriangles.Add(start); m.CollisionTriangles.Add(start + 2); m.CollisionTriangles.Add(start + 1);
                m.CollisionTriangles.Add(start); m.CollisionTriangles.Add(start + 3); m.CollisionTriangles.Add(start + 2);
            }
            else
            {
                m.CollisionTriangles.Add(start); m.CollisionTriangles.Add(start + 1); m.CollisionTriangles.Add(start + 2);
                m.CollisionTriangles.Add(start); m.CollisionTriangles.Add(start + 2); m.CollisionTriangles.Add(start + 3);
            }
        }

        private static (int, int, int) Key(int[] s) => s == null || s.Length != 3 ? (0, 0, 0) : (s[0], s[1], s[2]);
        private static int FloorDiv(int v) => v >> 4;      // arithmetic shift = floor division by 16
        private static int Mod(int v) => v & 15;
    }
}
