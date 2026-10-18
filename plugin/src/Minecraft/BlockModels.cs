using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HowToSteve.Minecraft
{
    /// <summary>Faces in Minecraft terms. Index order matches <see cref="Dirs.Vectors"/>.</summary>
    internal static class Dirs
    {
        public const int Down = 0, Up = 1, North = 2, South = 3, West = 4, East = 5;
        public static readonly string[] Names = { "down", "up", "north", "south", "west", "east" };
        public static readonly Vector3Int[] Vectors =
        {
            new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, 0, -1),
            new Vector3Int(0, 0, 1), new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0),
        };

        public static int Parse(string s)
        {
            if (s == "bottom") return Down;
            if (s == "top") return Up;
            return Array.IndexOf(Names, s);
        }

        public static int FromVector(Vector3 v)
        {
            var best = 0; var bestDot = float.MinValue;
            for (var i = 0; i < 6; i++)
            {
                var d = Vector3.Dot(v, Vectors[i]);
                if (d > bestDot) { bestDot = d; best = i; }
            }
            return best;
        }
    }

    /// <summary>A texture as the atlas needs it: base image, optional tint, optional tinted overlay.</summary>
    internal readonly struct TextureSpec : IEquatable<TextureSpec>
    {
        public const int NoTint = -1;
        public readonly string Base;      // e.g. "block/stone"; "missingno" for a checkerboard
        public readonly int BaseTint;     // 0xRRGGBB or NoTint
        public readonly string Overlay;   // e.g. "block/grass_block_side_overlay" or null
        public readonly int OverlayTint;

        public TextureSpec(string @base, int baseTint = NoTint, string overlay = null, int overlayTint = NoTint)
        {
            Base = @base; BaseTint = baseTint; Overlay = overlay; OverlayTint = overlayTint;
        }

        public TextureSpec WithOverlay(string overlay, int tint) => new TextureSpec(Base, BaseTint, overlay, tint);

        public bool Equals(TextureSpec o) => Base == o.Base && BaseTint == o.BaseTint && Overlay == o.Overlay && OverlayTint == o.OverlayTint;
        public override bool Equals(object obj) => obj is TextureSpec o && Equals(o);
        public override int GetHashCode() => ((Base?.GetHashCode() ?? 0) * 397) ^ BaseTint ^ ((Overlay?.GetHashCode() ?? 0) * 31) ^ (OverlayTint << 3);
        public override string ToString() => Overlay == null ? $"{Base}#{BaseTint:X}" : $"{Base}#{BaseTint:X}+{Overlay}#{OverlayTint:X}";
    }

    /// <summary>One textured quad in block-local Minecraft space (0..1). Corners are TL, BL, BR, TR seen from outside.</summary>
    internal sealed class BakedQuad
    {
        public Vector3[] Pos = new Vector3[4];
        public Vector2[] Uv = new Vector2[4];   // 0..1 within the texture, v pointing down (Minecraft convention)
        public TextureSpec Texture;
        public int CullDir = -1;                // cull when the neighbour on this side hides it
        public int FaceDir;                     // facing after rotation (for flat shading)
    }

    internal sealed class BakedModel
    {
        public readonly List<BakedQuad> Quads = new List<BakedQuad>();
        public string BlockName;
        public bool FullOpaque;   // hides the neighbour faces touching it
        public bool CullSame;     // glass-like: faces against the same block are hidden
        public bool Cutout;       // needs the alpha-cutout material
        public string Fluid;      // "water" / "lava" for fluid blocks
        public bool Solid;        // gets collision (Phase 4 heuristic: not a fluid, has at least one culled face)
        public bool IsAir => Quads.Count == 0 && Fluid == null;
    }

    /// <summary>
    /// Resolves block state strings ("minecraft:oak_stairs[facing=north,...]") into baked quads, following
    /// vanilla's blockstate (variants / multipart) → model (parent chain, texture refs, elements) rules.
    /// Simplifications for Phase 3: weighted variants use the first entry, uvlock is ignored,
    /// fluids are drawn as plain 14/16-high cubes, biome tints are fixed plains colours.
    /// Thread-safe; all caches are concurrent.
    /// </summary>
    internal sealed class BlockModels
    {
        private readonly JarAssets _jar;
        private readonly ManualLogSource _log;
        private readonly ConcurrentDictionary<string, BakedModel> _baked = new ConcurrentDictionary<string, BakedModel>();
        private readonly ConcurrentDictionary<string, UnbakedModel> _models = new ConcurrentDictionary<string, UnbakedModel>();
        private readonly ConcurrentDictionary<string, JObject> _blockstates = new ConcurrentDictionary<string, JObject>();
        private readonly ConcurrentDictionary<string, bool> _warned = new ConcurrentDictionary<string, bool>();

        public static readonly BakedModel Air = new BakedModel { BlockName = "minecraft:air" };

        public BlockModels(JarAssets jar, ManualLogSource log)
        {
            _jar = jar;
            _log = log;
        }

        public BakedModel Get(string state)
        {
            if (string.IsNullOrEmpty(state)) return Air;
            return _baked.GetOrAdd(state, s =>
            {
                try { return Bake(s); }
                catch (Exception e)
                {
                    Warn(s, $"model bake failed for {s}: {e.Message}");
                    return MissingModel(s);
                }
            });
        }

        // ---------- block states ----------

        private BakedModel Bake(string state)
        {
            ParseState(state, out var name, out var props);
            var shortName = name.StartsWith("minecraft:") ? name.Substring(10) : name;
            if (shortName == "air" || shortName == "cave_air" || shortName == "void_air" || shortName == "barrier" ||
                shortName == "light" || shortName == "structure_void")
                return Air;

            if (shortName == "water" || shortName == "bubble_column") return FluidModel(name, "water");
            if (shortName == "lava") return FluidModel(name, "lava");

            var bs = _jar == null ? null : _blockstates.GetOrAdd(name, n =>
            {
                var text = _jar.ReadText(JarAssets.ResourcePath(n, "blockstates", ".json"));
                return text == null ? null : JObject.Parse(text);
            });
            if (bs == null)
            {
                Warn(name, $"no blockstate for {name}");
                return MissingModel(name);
            }

            var applies = new List<JObject>();
            if (bs["variants"] is JObject variants)
            {
                foreach (var kv in variants)
                {
                    if (!VariantMatches(kv.Key, props)) continue;
                    applies.Add(FirstApply(kv.Value));
                    break;
                }
            }
            else if (bs["multipart"] is JArray parts)
            {
                foreach (var part in parts)
                {
                    if (part["when"] is JObject when && !WhenMatches(when, props)) continue;
                    applies.Add(FirstApply(part["apply"]));
                }
            }

            var model = new BakedModel { BlockName = name };
            foreach (var apply in applies)
            {
                if (apply == null) continue;
                var um = GetModel((string)apply["model"]);
                if (um == null) continue;
                var rx = (int?)apply["x"] ?? 0;
                var ry = (int?)apply["y"] ?? 0;
                BakeElements(model, um, rx, ry, shortName);
            }
            if (applies.Count == 0) Warn(name, $"no variant matched {state}");

            model.CullSame = shortName.Contains("glass") || shortName == "ice";
            model.FullOpaque = IsFullCube(model) && !IsTransparentName(shortName);
            model.Cutout = !model.FullOpaque;
            model.Solid = model.Quads.Exists(q => q.CullDir >= 0);
            return model;
        }

        public static void ParseState(string state, out string name, out Dictionary<string, string> props)
        {
            props = new Dictionary<string, string>();
            var br = state.IndexOf('[');
            name = br < 0 ? state : state.Substring(0, br);
            if (!name.Contains(":")) name = "minecraft:" + name;
            if (br < 0) return;
            var inner = state.Substring(br + 1).TrimEnd(']');
            foreach (var pair in inner.Split(','))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) props[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            }
        }

        private static bool VariantMatches(string key, Dictionary<string, string> props)
        {
            if (string.IsNullOrEmpty(key) || key == "normal") return true;
            foreach (var cond in key.Split(','))
            {
                var eq = cond.IndexOf('=');
                if (eq < 0) continue;
                if (!props.TryGetValue(cond.Substring(0, eq), out var v) || v != cond.Substring(eq + 1)) return false;
            }
            return true;
        }

        private static bool WhenMatches(JObject when, Dictionary<string, string> props)
        {
            if (when["OR"] is JArray or)
            {
                foreach (var c in or) if (c is JObject o && WhenMatches(o, props)) return true;
                return false;
            }
            if (when["AND"] is JArray and)
            {
                foreach (var c in and) if (c is JObject o && !WhenMatches(o, props)) return false;
                return true;
            }
            foreach (var kv in when)
            {
                props.TryGetValue(kv.Key, out var actual);
                var allowed = ((string)kv.Value ?? "").Split('|');
                var negate = false;
                if (allowed.Length == 1 && allowed[0].StartsWith("!")) { negate = true; allowed[0] = allowed[0].Substring(1); }
                var hit = Array.IndexOf(allowed, actual ?? "") >= 0;
                if (hit == negate) return false;
            }
            return true;
        }

        private static JObject FirstApply(JToken t) => t is JArray a ? a.Count > 0 ? a[0] as JObject : null : t as JObject;

        // ---------- models ----------

        private sealed class UnbakedModel
        {
            public Dictionary<string, string> Textures = new Dictionary<string, string>();
            public JArray Elements;
        }

        private UnbakedModel GetModel(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _models.GetOrAdd(Normalize(id), LoadModel);
        }

        private UnbakedModel LoadModel(string id)
        {
            // Walk the parent chain child → root; nearest elements win, child textures override parents.
            var chain = new List<JObject>();
            var cur = id;
            for (var depth = 0; cur != null && depth < 24; depth++)
            {
                if (cur.StartsWith("builtin/")) break;
                var text = _jar.ReadText(JarAssets.ResourcePath(cur, "models", ".json"));
                if (text == null) { Warn(cur, $"missing model {cur}"); break; }
                var json = JObject.Parse(text);
                chain.Add(json);
                var parent = (string)json["parent"];
                cur = parent == null ? null : Normalize(parent);
            }
            if (chain.Count == 0) return null;

            var model = new UnbakedModel();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i]["textures"] is JObject tex)
                    foreach (var kv in tex)
                    {
                        // 26.x also allows { "sprite": "minecraft:block/glass", "force_translucent": true }.
                        model.Textures[kv.Key] = kv.Value is JObject o ? (string)o["sprite"] : (string)kv.Value;
                    }
            }
            foreach (var m in chain)
            {
                if (m["elements"] is JArray els) { model.Elements = els; break; }
            }
            return model;
        }

        private static string Normalize(string id)
        {
            var s = id.StartsWith("minecraft:") ? id.Substring(10) : id;
            return s;
        }

        private static string ResolveTexture(UnbakedModel m, string reference)
        {
            var r = reference;
            for (var i = 0; i < 16 && r != null && r.StartsWith("#"); i++)
                m.Textures.TryGetValue(r.Substring(1), out r);
            return r == null || r.StartsWith("#") ? null : Normalize(r);
        }

        // ---------- baking ----------

        private static readonly int[] FaceOrder = { Dirs.Down, Dirs.Up, Dirs.North, Dirs.South, Dirs.West, Dirs.East };

        private void BakeElements(BakedModel into, UnbakedModel um, int rotX, int rotY, string shortName)
        {
            if (um.Elements == null) return;
            var firstQuad = into.Quads.Count;
            foreach (var elTok in um.Elements)
            {
                if (!(elTok is JObject el)) continue;
                var from = Vec((JArray)el["from"]);
                var to = Vec((JArray)el["to"]);
                var faces = el["faces"] as JObject;
                if (faces == null) continue;
                var rot = el["rotation"] as JObject;

                foreach (var dir in FaceOrder)
                {
                    if (!(faces[Dirs.Names[dir]] is JObject face)) continue;
                    var texPath = ResolveTexture(um, (string)face["texture"]);
                    var tint = face["tintindex"] != null ? TintFor(shortName) : TextureSpec.NoTint;

                    var corners = Corners(dir, from, to);
                    var uv = face["uv"] is JArray uvArr ? Vec4(uvArr) : DefaultUv(dir, from, to);
                    var quad = new BakedQuad();
                    var faceRot = ((int?)face["rotation"] ?? 0) / 90 & 3;
                    var uvc = new[]
                    {
                        new Vector2(uv.x, uv.y), new Vector2(uv.x, uv.w), new Vector2(uv.z, uv.w), new Vector2(uv.z, uv.y),
                    };
                    for (var i = 0; i < 4; i++)
                    {
                        var p = corners[i];
                        if (rot != null) p = RotateElement(p, rot);
                        p = RotateVariant(p, rotX, rotY);
                        quad.Pos[i] = p / 16f;
                        var t = uvc[(i + faceRot) & 3];
                        quad.Uv[i] = t / 16f;
                    }
                    quad.FaceDir = Dirs.FromVector(RotateVariantDir(Dirs.Vectors[dir], rotX, rotY));
                    var cull = face["cullface"] != null ? Dirs.Parse((string)face["cullface"]) : -1;
                    quad.CullDir = cull < 0 ? -1 : Dirs.FromVector(RotateVariantDir(Dirs.Vectors[cull], rotX, rotY));

                    var spec = new TextureSpec(texPath ?? "missingno", tint);
                    // An element face that exactly covers an earlier face (grass_block's side overlay) is
                    // composited into the earlier texture instead of z-fighting with it.
                    var under = FindCoplanar(into.Quads, firstQuad, quad);
                    if (under != null) { under.Texture = under.Texture.WithOverlay(spec.Base, spec.BaseTint); continue; }
                    quad.Texture = spec;
                    into.Quads.Add(quad);
                }
            }
        }

        private static BakedQuad FindCoplanar(List<BakedQuad> quads, int start, BakedQuad q)
        {
            for (var i = start; i < quads.Count; i++)
            {
                var o = quads[i];
                if (o.FaceDir != q.FaceDir || o.Texture.Overlay != null) continue;
                var same = true;
                for (var k = 0; k < 4 && same; k++) same = (o.Pos[k] - q.Pos[k]).sqrMagnitude < 1e-8f;
                if (same) return o;
            }
            return null;
        }

        /// <summary>TL, BL, BR, TR as seen from outside the face, in Minecraft pixel space.</summary>
        private static Vector3[] Corners(int dir, Vector3 f, Vector3 t)
        {
            switch (dir)
            {
                case Dirs.North: return new[] { V(t.x, t.y, f.z), V(t.x, f.y, f.z), V(f.x, f.y, f.z), V(f.x, t.y, f.z) };
                case Dirs.South: return new[] { V(f.x, t.y, t.z), V(f.x, f.y, t.z), V(t.x, f.y, t.z), V(t.x, t.y, t.z) };
                case Dirs.West: return new[] { V(f.x, t.y, f.z), V(f.x, f.y, f.z), V(f.x, f.y, t.z), V(f.x, t.y, t.z) };
                case Dirs.East: return new[] { V(t.x, t.y, t.z), V(t.x, f.y, t.z), V(t.x, f.y, f.z), V(t.x, t.y, f.z) };
                case Dirs.Up: return new[] { V(f.x, t.y, f.z), V(f.x, t.y, t.z), V(t.x, t.y, t.z), V(t.x, t.y, f.z) };
                default: return new[] { V(f.x, f.y, t.z), V(f.x, f.y, f.z), V(t.x, f.y, f.z), V(t.x, f.y, t.z) };
            }
        }

        /// <summary>Vanilla's default UVs when a face has no "uv": [u1, v1, u2, v2] in pixels.</summary>
        private static Vector4 DefaultUv(int dir, Vector3 f, Vector3 t)
        {
            switch (dir)
            {
                case Dirs.Down: return new Vector4(f.x, 16 - t.z, t.x, 16 - f.z);
                case Dirs.Up: return new Vector4(f.x, f.z, t.x, t.z);
                case Dirs.North: return new Vector4(16 - t.x, 16 - t.y, 16 - f.x, 16 - f.y);
                case Dirs.South: return new Vector4(f.x, 16 - t.y, t.x, 16 - f.y);
                case Dirs.West: return new Vector4(f.z, 16 - t.y, t.z, 16 - f.y);
                default: return new Vector4(16 - t.z, 16 - t.y, 16 - f.z, 16 - f.y);
            }
        }

        private static Vector3 RotateElement(Vector3 p, JObject rot)
        {
            var origin = Vec((JArray)rot["origin"]);
            var axis = (string)rot["axis"];
            var angle = ((float?)rot["angle"] ?? 0f) * Mathf.Deg2Rad;
            var rescale = (bool?)rot["rescale"] ?? false;
            var d = p - origin;
            var scale = rescale ? 1f / Mathf.Cos(angle) : 1f;
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            switch (axis)
            {
                case "x": d = new Vector3(d.x, (d.y * c - d.z * s) * scale, (d.y * s + d.z * c) * scale); break;
                case "y": d = new Vector3((d.x * c + d.z * s) * scale, d.y, (-d.x * s + d.z * c) * scale); break;
                default: d = new Vector3((d.x * c - d.y * s) * scale, (d.x * s + d.y * c) * scale, d.z); break;
            }
            return origin + d;
        }

        /// <summary>Blockstate "x" then "y" rotation around the block centre (x:270 turns north to up; y:90 turns north to east).</summary>
        private static Vector3 RotateVariant(Vector3 p, int rotX, int rotY) =>
            RotateVariantDir(p - new Vector3(8, 8, 8), rotX, rotY) + new Vector3(8, 8, 8);

        private static Vector3 RotateVariantDir(Vector3 d, int rotX, int rotY)
        {
            if (rotX != 0)
            {
                float a = rotX * Mathf.Deg2Rad, c = Mathf.Round(Mathf.Cos(a)), s = Mathf.Round(Mathf.Sin(a));
                d = new Vector3(d.x, d.y * c + d.z * s, -d.y * s + d.z * c);
            }
            if (rotY != 0)
            {
                float a = rotY * Mathf.Deg2Rad, c = Mathf.Round(Mathf.Cos(a)), s = Mathf.Round(Mathf.Sin(a));
                d = new Vector3(d.x * c - d.z * s, d.y, d.x * s + d.z * c);
            }
            return d;
        }

        private static bool IsFullCube(BakedModel m)
        {
            // Six axis-aligned faces covering the whole block, each culled against its own side.
            var seen = 0;
            foreach (var q in m.Quads)
            {
                if (q.CullDir < 0 || q.CullDir != q.FaceDir) continue;
                Vector3 min = q.Pos[0], max = q.Pos[0];
                for (var i = 1; i < 4; i++) { min = Vector3.Min(min, q.Pos[i]); max = Vector3.Max(max, q.Pos[i]); }
                var size = max - min;
                var area = (size.x < 1e-4f ? 1 : size.x) * (size.y < 1e-4f ? 1 : size.y) * (size.z < 1e-4f ? 1 : size.z);
                if (area > 0.999f) seen |= 1 << q.FaceDir;
            }
            return seen == 0x3F;
        }

        private static bool IsTransparentName(string n) =>
            n.Contains("glass") || n.Contains("leaves") || n == "ice" || n == "frosted_ice" || n == "slime_block" ||
            n == "honey_block" || n == "spawner" || n == "trial_spawner" || n == "mangrove_roots" || n == "beacon" ||
            n == "vault" || n == "copper_grate" || n.EndsWith("_grate");

        private static int TintFor(string n)
        {
            if (n == "birch_leaves") return 0x80A755;
            if (n == "spruce_leaves") return 0x619961;
            if (n.EndsWith("_leaves") || n == "vine") return 0x77AB2F;
            if (n == "lily_pad") return 0x208030;
            if (n == "redstone_wire") return 0xC80000;
            if (n.Contains("stem")) return 0xE0C71C;
            if (n == "water" || n.EndsWith("water_cauldron")) return 0x3F76E4;
            return 0x91BD59; // grass colour (plains) for grass_block, short_grass, fern, sugar_cane, ...
        }

        private static BakedModel FluidModel(string name, string fluid)
        {
            var m = new BakedModel { BlockName = name, Fluid = fluid, Cutout = true };
            var tex = fluid == "water"
                ? new TextureSpec("block/water_still", 0x3F76E4)
                : new TextureSpec("block/lava_still");
            AddCube(m, new Vector3(0, 0, 0), new Vector3(16, 14, 16), tex);
            return m;
        }

        private BakedModel MissingModel(string name)
        {
            var m = new BakedModel { BlockName = name, FullOpaque = false, Cutout = true };
            AddCube(m, Vector3.zero, new Vector3(16, 16, 16), new TextureSpec("missingno"));
            return m;
        }

        private static void AddCube(BakedModel m, Vector3 from, Vector3 to, TextureSpec tex)
        {
            foreach (var dir in FaceOrder)
            {
                var c = Corners(dir, from, to);
                var uv = DefaultUv(dir, from, to);
                var uvc = new[] { new Vector2(uv.x, uv.y), new Vector2(uv.x, uv.w), new Vector2(uv.z, uv.w), new Vector2(uv.z, uv.y) };
                var q = new BakedQuad { Texture = tex, CullDir = dir, FaceDir = dir };
                for (var i = 0; i < 4; i++) { q.Pos[i] = c[i] / 16f; q.Uv[i] = uvc[i] / 16f; }
                m.Quads.Add(q);
            }
        }

        private void Warn(string key, string message)
        {
            if (_warned.TryAdd(key, true)) _log.LogWarning(message);
        }

        private static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        private static Vector3 Vec(JArray a) =>
            new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());

        private static Vector4 Vec4(JArray a) =>
            new Vector4(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(), a[3].Value<float>());
    }
}
