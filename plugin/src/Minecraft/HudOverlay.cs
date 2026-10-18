using System.Collections.Generic;
using UnityEngine;

namespace SteveFish.Minecraft
{
    /// <summary>
    /// The Minecraft HUD (hotbar, hearts, food, XP, crosshair) drawn with IMGUI. IMGUI is Unity's
    /// immediate-mode GUI: OnGUI() runs every frame and draws directly, so there's no Canvas to manage.
    /// Sprites come from the client jar's textures/gui/sprites/hud at runtime. Layout follows vanilla's
    /// (in GUI pixels, times the GUI scale).
    /// </summary>
    internal sealed class HudOverlay : MonoBehaviour
    {
        private JarAssets _jar;
        private McClientState _state;
        private WorldRenderer _renderer;
        private int _scaleSetting;
        private readonly Dictionary<string, Texture2D> _sprites = new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, Texture2D> _icons = new Dictionary<string, Texture2D>();
        private GUIStyle _count, _label, _level, _plain;
        private InventoryScreen _screen;
        private Texture2D _dim, _highlight;

        public void AttachScreen(InventoryScreen screen) => _screen = screen;

        public static HudOverlay Create(JarAssets jar, McClientState state, WorldRenderer renderer, int scale)
        {
            var go = new GameObject("SteveFish.Hud");
            DontDestroyOnLoad(go);
            var h = go.AddComponent<HudOverlay>();
            h._jar = jar;
            h._state = state;
            h._renderer = renderer;
            h._scaleSetting = scale;
            return h;
        }

        private void OnGUI()
        {
            if (_jar == null || _renderer == null || !_renderer.Visible || Event.current.type != EventType.Repaint) return;
            EnsureStyles();

            // Vanilla auto GUI scale: largest s with width/s >= 320 and height/s >= 240.
            var s = _scaleSetting > 0 ? _scaleSetting : Mathf.Max(1, Mathf.Min(Screen.width / 320, Screen.height / 240));
            float W = Screen.width, H = Screen.height, cx = W / 2f;
            Rect R(float x, float y, float w, float h) => new Rect(x, y, w * s, h * s);

            // Hotbar
            var hotbarX = cx - 91 * s;
            var hotbarY = H - 22 * s;
            Draw("hud/hotbar", R(hotbarX, hotbarY, 182, 22));
            for (var i = 0; i < 9; i++)
            {
                var slot = _state.HotbarSlot(i);
                if (slot == null) continue;
                var r = R(hotbarX + (3 + i * 20) * s, hotbarY + 3 * s, 16, 16);
                var icon = Icon(slot.Item);
                if (icon != null) GUI.DrawTexture(r, icon);
                if (slot.Count > 1) Shadowed(new Rect(r.x, r.y + 7 * s, 17 * s, 10 * s), slot.Count.ToString(), _count);
            }
            Draw("hud/hotbar_selection", R(hotbarX + (-1 + _state.HeldSlot * 20) * s, hotbarY - 1 * s, 24, 23));

            var hud = _state.Hud;
            if (hud != null && hud.GameMode != "creative" && hud.GameMode != "spectator")
            {
                // XP bar + level
                var xpY = H - 29 * s;
                Draw("hud/experience_bar_background", R(hotbarX, xpY, 182, 5));
                var p = Mathf.Clamp01(hud.XpProgress);
                var prog = Sprite("hud/experience_bar_progress");
                if (prog != null && p > 0) GUI.DrawTextureWithTexCoords(R(hotbarX, xpY, 182 * p, 5), prog, new Rect(0, 0, p, 1));
                if (hud.XpLevel > 0) Shadowed(new Rect(cx - 30 * s, xpY - 9 * s, 60 * s, 10 * s), hud.XpLevel.ToString(), _level, null, new Color(0.5f, 1f, 0.13f));

                // Hearts (left) and food (right)
                var rowY = H - 39 * s;
                var health = Mathf.CeilToInt(hud.Health);
                for (var i = 0; i < 10; i++)
                {
                    var r = R(hotbarX + i * 8 * s, rowY, 9, 9);
                    Draw("hud/heart/container", r);
                    if (health >= i * 2 + 2) Draw("hud/heart/full", r);
                    else if (health == i * 2 + 1) Draw("hud/heart/half", r);
                }
                for (var i = 0; i < 10; i++)
                {
                    var r = R(cx + 91 * s - (i * 8 + 9) * s, rowY, 9, 9);
                    Draw("hud/food_empty", r);
                    if (hud.Food >= i * 2 + 2) Draw("hud/food_full", r);
                    else if (hud.Food == i * 2 + 1) Draw("hud/food_half", r);
                }
            }

            if (_state.ScreenOpen && _screen != null)
            {
                _screen.Draw(this, s);
                return;
            }

            if (_state.MinecraftMode)
            {
                Draw("hud/crosshair", new Rect(cx - 7.5f * s, H / 2f - 7.5f * s, 15 * s, 15 * s));
                if (_state.DigPos != null && _state.DigProgress > 0)
                {
                    var bw = 32 * s; var by = H / 2f + 10 * s;
                    GUI.DrawTexture(new Rect(cx - bw / 2, by, bw, 2 * s), Texture2D.blackTexture);
                    GUI.DrawTexture(new Rect(cx - bw / 2, by, bw * Mathf.Clamp01(_state.DigProgress), 2 * s), Texture2D.whiteTexture);
                }
                Shadowed(new Rect(8, 8, 400, 30), "Minecraft mode  [B]", _label);
            }

            if (_state.Toast != null && Time.unscaledTime < _state.ToastUntil)
                Shadowed(new Rect(cx - 200, H - 60 * s, 400, 20 * s / 2f + 10), _state.Toast, _label, TextAnchor.MiddleCenter);
        }

        private void EnsureStyles()
        {
            if (_count != null) return;
            _count = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Bold, fontSize = 18 };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            _level = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
            _plain = new GUIStyle(GUI.skin.label) { fontSize = 18 };
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, TextAnchor? anchor = null, Color? color = null)
        {
            var oldAnchor = style.alignment;
            var oldColor = style.normal.textColor;
            if (anchor.HasValue) style.alignment = anchor.Value;
            style.normal.textColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
            style.normal.textColor = color ?? Color.white;
            GUI.Label(r, text, style);
            style.normal.textColor = oldColor;
            style.alignment = oldAnchor;
        }

        // ---------- helpers for InventoryScreen ----------

        public Texture2D Dim => _dim ?? (_dim = Solid(new Color(0, 0, 0, 0.55f)));
        public Texture2D Highlight => _highlight ?? (_highlight = Solid(new Color(1, 1, 1, 0.45f)));

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        /// <summary>Any jar texture by entry path, cached (e.g. assets/minecraft/textures/block/destroy_stage_3.png).</summary>
        public Texture2D TextureAt(string entry)
        {
            if (_sprites.TryGetValue(entry, out var t)) return t;
            t = LoadPng(entry);
            _sprites[entry] = t;
            return t;
        }

        /// <summary>assets/minecraft/textures/gui/{rel}.png, cached.</summary>
        public Texture2D GuiTexture(string rel)
        {
            var key = "gui:" + rel;
            if (_sprites.TryGetValue(key, out var t)) return t;
            t = LoadPng($"assets/minecraft/textures/gui/{rel}.png");
            _sprites[key] = t;
            return t;
        }

        public void Text(Rect r, string text, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            var c = _plain.normal.textColor;
            _plain.normal.textColor = color;
            GUI.Label(r, text, _plain);
            _plain.normal.textColor = c;
        }

        public void ItemStack(Rect r, SteveFish.Bridge.InventorySlotDto stack, float s)
        {
            var icon = Icon(stack.Item);
            if (icon != null) GUI.DrawTexture(r, icon);
            if (stack.Count > 1) Shadowed(new Rect(r.x, r.y + 7 * s, 17 * s, 10 * s), stack.Count.ToString(), _count);
        }

        private void Draw(string sprite, Rect r)
        {
            var t = Sprite(sprite);
            if (t != null) GUI.DrawTexture(r, t);
        }

        private Texture2D Sprite(string name)
        {
            if (_sprites.TryGetValue(name, out var t)) return t;
            t = LoadPng($"assets/minecraft/textures/gui/sprites/{name}.png");
            _sprites[name] = t;
            return t;
        }

        public Texture2D IconFor(string item) => Icon(item);

        /// <summary>Item icon: the item texture, else a block texture (front/side/top), else none.</summary>
        private Texture2D Icon(string item)
        {
            if (string.IsNullOrEmpty(item)) return null;
            if (_icons.TryGetValue(item, out var t)) return t;
            var n = item.StartsWith("minecraft:") ? item.Substring(10) : item;
            foreach (var path in new[] { $"item/{n}", $"block/{n}", $"block/{n}_front", $"block/{n}_side", $"block/{n}_top" })
            {
                t = LoadPng($"assets/minecraft/textures/{path}.png", firstFrameOnly: true);
                if (t != null) break;
            }
            _icons[item] = t;
            return t;
        }

        private Texture2D LoadPng(string entry, bool firstFrameOnly = false)
        {
            var bytes = _jar.ReadBytes(entry);
            if (bytes == null) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(tex, bytes, false)) { Destroy(tex); return null; }
            if (firstFrameOnly && tex.height > tex.width)
            {
                // Animated strip: keep the top square (last rows in Unity's bottom-up order).
                var w = tex.width;
                var px = tex.GetPixels(0, tex.height - w, w, w);
                var sq = new Texture2D(w, w, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                sq.SetPixels(px);
                sq.Apply(false);
                Destroy(tex);
                tex = sq;
            }
            tex.filterMode = FilterMode.Point;
            return tex;
        }
    }
}
