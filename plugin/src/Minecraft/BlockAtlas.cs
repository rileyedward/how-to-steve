using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SteveFish.Minecraft
{
    /// <summary>
    /// One big point-filtered texture holding every block texture we've needed so far, 16×16 per tile.
    /// Slots are handed out from any thread (<see cref="SlotOf"/>); the pixels are decoded and written on
    /// the main thread (<see cref="UploadPending"/>), because Texture2D is a Unity object.
    /// </summary>
    internal sealed class BlockAtlas
    {
        public const int Tile = 16;
        public const int Size = 2048;
        public const int TilesPerRow = Size / Tile;
        private const float Inset = 1f / 512f; // fraction of a tile, keeps point sampling off the neighbour's pixels

        private readonly JarAssets _jar;
        private readonly ManualLogSource _log;
        private readonly ConcurrentDictionary<TextureSpec, int> _slots = new ConcurrentDictionary<TextureSpec, int>();
        private readonly ConcurrentQueue<KeyValuePair<TextureSpec, int>> _pending = new ConcurrentQueue<KeyValuePair<TextureSpec, int>>();
        private readonly Dictionary<string, Color32[]> _decoded = new Dictionary<string, Color32[]>();
        private int _next;

        public Texture2D Texture { get; private set; }
        public int SlotCount => _next;

        public BlockAtlas(JarAssets jar, ManualLogSource log)
        {
            _jar = jar;
            _log = log;
        }

        /// <summary>Main thread.</summary>
        public void CreateTexture()
        {
            Texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "SteveFish.BlockAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };
            var clear = new Color32[Size * Size];
            Texture.SetPixels32(clear);
            Texture.Apply(false);
        }

        /// <summary>Any thread. Returns the tile index for this texture, scheduling its upload if new.</summary>
        public int SlotOf(TextureSpec spec) =>
            _slots.GetOrAdd(spec, s =>
            {
                var slot = System.Threading.Interlocked.Increment(ref _next) - 1;
                if (slot >= TilesPerRow * TilesPerRow) slot = 0; // atlas full: reuse tile 0 rather than crash
                _pending.Enqueue(new KeyValuePair<TextureSpec, int>(s, slot));
                return slot;
            });

        /// <summary>Atlas UV for a texture-local coordinate (u right, v down, both 0..1) in a tile.</summary>
        public static Vector2 AtlasUv(int slot, Vector2 local)
        {
            var tx = slot % TilesPerRow;
            var ty = slot / TilesPerRow;
            var u = Inset + Mathf.Clamp01(local.x) * (1f - 2f * Inset);
            var v = Inset + (1f - Mathf.Clamp01(local.y)) * (1f - 2f * Inset);
            return new Vector2((tx + u) * Tile / Size, (ty + v) * Tile / Size);
        }

        /// <summary>Main thread. Decodes and writes up to <paramref name="max"/> pending tiles; returns how many.</summary>
        public int UploadPending(int max = 64)
        {
            if (Texture == null) return 0;
            var n = 0;
            while (n < max && _pending.TryDequeue(out var job))
            {
                var pixels = Compose(job.Key);
                var tx = job.Value % TilesPerRow;
                var ty = job.Value / TilesPerRow;
                Texture.SetPixels32(tx * Tile, ty * Tile, Tile, Tile, pixels);
                n++;
            }
            if (n > 0) Texture.Apply(false);
            return n;
        }

        private Color32[] Compose(TextureSpec spec)
        {
            var basePx = Tinted(Load(spec.Base), spec.BaseTint);
            if (spec.Overlay == null) return basePx;
            var over = Tinted(Load(spec.Overlay), spec.OverlayTint);
            var outPx = new Color32[basePx.Length];
            for (var i = 0; i < outPx.Length; i++)
            {
                var a = over[i].a / 255f;
                outPx[i] = new Color32(
                    (byte)(basePx[i].r + (over[i].r - basePx[i].r) * a),
                    (byte)(basePx[i].g + (over[i].g - basePx[i].g) * a),
                    (byte)(basePx[i].b + (over[i].b - basePx[i].b) * a),
                    (byte)Mathf.Max(basePx[i].a, over[i].a));
            }
            return outPx;
        }

        private static Color32[] Tinted(Color32[] src, int tint)
        {
            if (tint == TextureSpec.NoTint) return src;
            byte r = (byte)(tint >> 16), g = (byte)(tint >> 8), b = (byte)tint;
            var dst = new Color32[src.Length];
            for (var i = 0; i < src.Length; i++)
                dst[i] = new Color32((byte)(src[i].r * r / 255), (byte)(src[i].g * g / 255), (byte)(src[i].b * b / 255), src[i].a);
            return dst;
        }

        /// <summary>16×16 pixels (Unity row order: bottom row first) of the first animation frame.</summary>
        private Color32[] Load(string path)
        {
            if (_decoded.TryGetValue(path, out var cached)) return cached;
            Color32[] result = null;
            var bytes = path == "missingno" ? null : _jar?.ReadBytes(JarAssets.ResourcePath(path, "textures", ".png"));
            if (bytes != null)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (ImageConversion.LoadImage(tex, bytes, false))
                {
                    var w = tex.width;
                    var frame = Mathf.Min(w, tex.height);
                    var src = tex.GetPixels32();
                    // Animated textures are vertical strips; the first frame is the top square, which is the
                    // last rows in Unity's bottom-up order.
                    var frameTop = tex.height - frame;
                    result = new Color32[Tile * Tile];
                    for (var y = 0; y < Tile; y++)
                        for (var x = 0; x < Tile; x++)
                        {
                            var sx = x * frame / Tile;
                            var sy = frameTop + y * frame / Tile;
                            result[y * Tile + x] = src[sy * w + sx];
                        }
                }
                Object.Destroy(tex);
            }
            if (result == null)
            {
                if (path != "missingno") _log.LogWarning($"missing texture {path}");
                result = Checker();
            }
            _decoded[path] = result;
            return result;
        }

        private static Color32[] Checker()
        {
            var px = new Color32[Tile * Tile];
            for (var y = 0; y < Tile; y++)
                for (var x = 0; x < Tile; x++)
                    px[y * Tile + x] = ((x / 8) ^ (y / 8)) == 0 ? new Color32(248, 0, 248, 255) : new Color32(0, 0, 0, 255);
            return px;
        }
    }
}
