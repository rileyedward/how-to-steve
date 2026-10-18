using System;
using System.Collections.Generic;
using UnityEngine;

namespace HowToSteve.Minecraft
{
    /// <summary>
    /// Minecraft-mode first-person view model: Steve's right arm (default skin from the client jar's
    /// entity/player/wide/steve.png; nothing is fetched online) holding the selected hotbar item.
    /// It's parented to the active camera each frame, so it follows death/fish cameras too.
    /// Main thread only.
    /// </summary>
    internal sealed class SteveViewModel
    {
        private readonly JarAssets _jar;
        private readonly McClientState _state;
        private readonly Func<string, Texture2D> _icon;
        private readonly Func<Color, Texture2D, Material> _material;

        private GameObject _root, _arm, _held;
        private MeshRenderer _heldRenderer;
        private string _heldItem;
        private float _swingStart = -10f;
        private const float SwingTime = 0.3f;
        private readonly Dictionary<string, Material> _iconMaterials = new Dictionary<string, Material>();
        private Mesh _cube, _quad;

        public SteveViewModel(JarAssets jar, McClientState state, Func<string, Texture2D> icon, Func<Color, Texture2D, Material> material)
        {
            _jar = jar;
            _state = state;
            _icon = icon;
            _material = material;
        }

        public void Swing()
        {
            if (Time.unscaledTime - _swingStart > SwingTime * 0.8f) _swingStart = Time.unscaledTime;
        }

        public void Tick(bool visible)
        {
            var cam = GameInfo.CurCamera != null ? GameInfo.CurCamera : Camera.main;
            if (!visible || cam == null)
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                return;
            }
            if (_root == null) Build();
            if (_root.transform.parent != cam.transform) _root.transform.SetParent(cam.transform, false);
            if (!_root.activeSelf) _root.SetActive(true);
            _root.layer = cam.gameObject.layer;

            // Keep swinging while a dig is in progress.
            if (_state.DigPos != null) Swing();
            var t = Mathf.Clamp01((Time.unscaledTime - _swingStart) / SwingTime);
            var swing = Mathf.Sin(t * Mathf.PI);
            _root.transform.localPosition = new Vector3(0.30f, -0.30f - 0.05f * swing, 0.42f);
            _root.transform.localRotation = Quaternion.Euler(-10f - 45f * swing, -12f + 10f * swing, 0f);

            UpdateHeld();
        }

        private void Build()
        {
            _root = new GameObject("HowToSteve.ViewModel");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _cube = UnitCube();
            _quad = UnitQuad();

            // Arm box 4×4×12 skin pixels, long axis forward (+Z).
            _arm = new GameObject("arm");
            _arm.transform.SetParent(_root.transform, false);
            _arm.transform.localScale = new Vector3(0.10f, 0.10f, 0.30f);
            _arm.AddComponent<MeshFilter>().sharedMesh = ArmMesh();
            var skin = LoadSkin();
            var mr = _arm.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _material(Color.white, skin);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _held = new GameObject("held");
            _held.transform.SetParent(_root.transform, false);
            _held.AddComponent<MeshFilter>();
            _heldRenderer = _held.AddComponent<MeshRenderer>();
            _heldRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void UpdateHeld()
        {
            var slot = _state.HotbarSlot(_state.HeldSlot);
            var item = slot?.Item;
            if (item == _heldItem) return;
            _heldItem = item;
            var tex = item != null ? _icon(item) : null;
            if (tex == null) { _held.SetActive(false); return; }
            _held.SetActive(true);
            if (!_iconMaterials.TryGetValue(item, out var mat))
            {
                mat = _material(Color.white, tex);
                _iconMaterials[item] = mat;
            }
            _heldRenderer.sharedMaterial = mat;
            var isItem = _jar.ReadBytes($"assets/minecraft/textures/item/{(item.StartsWith("minecraft:") ? item.Substring(10) : item)}.png") != null;
            var mf = _held.GetComponent<MeshFilter>();
            if (isItem)
            {
                mf.sharedMesh = _quad;
                _held.transform.localPosition = new Vector3(-0.02f, 0.10f, 0.18f);
                _held.transform.localRotation = Quaternion.Euler(0, -70, 20);
                _held.transform.localScale = Vector3.one * 0.22f;
            }
            else
            {
                mf.sharedMesh = _cube;
                _held.transform.localPosition = new Vector3(-0.02f, 0.08f, 0.17f);
                _held.transform.localRotation = Quaternion.Euler(10, 40, 0);
                _held.transform.localScale = Vector3.one * 0.14f;
            }
        }

        private Texture2D LoadSkin()
        {
            var bytes = _jar?.ReadBytes("assets/minecraft/textures/entity/player/wide/steve.png");
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            if (bytes == null || !ImageConversion.LoadImage(tex, bytes, false))
            {
                tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, new Color(0.78f, 0.58f, 0.45f));
                tex.Apply();
            }
            tex.filterMode = FilterMode.Point;
            return tex;
        }

        /// <summary>Unit box (-0.5..0.5) with Steve's right-arm UVs from a 64×64 skin.</summary>
        private static Mesh ArmMesh()
        {
            // Skin regions (x, y, w, h) in pixels, top-left origin.
            Rect Px(float x, float y, float w, float h) => new Rect(x / 64f, 1f - (y + h) / 64f, w / 64f, h / 64f);
            var shoulder = Px(44, 16, 4, 4);  // "top" of the arm
            var hand = Px(48, 16, 4, 4);      // "bottom"
            var outer = Px(40, 20, 4, 12);
            var front = Px(44, 20, 4, 12);
            var inner = Px(48, 20, 4, 12);
            var back = Px(52, 20, 4, 12);

            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            // Each face: 4 corners (a..d) with UVs where the 12px length runs from shoulder (z=-0.5) to hand (z=+0.5).
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Rect r, bool lengthAlongV)
            {
                var s = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                if (lengthAlongV)
                {
                    uv.Add(new Vector2(r.xMin, r.yMax)); uv.Add(new Vector2(r.xMax, r.yMax));
                    uv.Add(new Vector2(r.xMax, r.yMin)); uv.Add(new Vector2(r.xMin, r.yMin));
                }
                else
                {
                    uv.Add(new Vector2(r.xMin, r.yMin)); uv.Add(new Vector2(r.xMax, r.yMin));
                    uv.Add(new Vector2(r.xMax, r.yMax)); uv.Add(new Vector2(r.xMin, r.yMax));
                }
                tri.Add(s); tri.Add(s + 2); tri.Add(s + 1);
                tri.Add(s); tri.Add(s + 3); tri.Add(s + 2);
            }
            const float h = 0.5f;
            // +Y (seen by the camera): front of the arm; -X (inner side, also visible): inner.
            Face(new Vector3(-h, h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), new Vector3(-h, h, h), front, true);
            Face(new Vector3(h, -h, -h), new Vector3(-h, -h, -h), new Vector3(-h, -h, h), new Vector3(h, -h, h), back, true);
            Face(new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(-h, h, h), new Vector3(-h, -h, h), inner, true);
            Face(new Vector3(h, h, -h), new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(h, h, h), outer, true);
            Face(new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(h, h, h), new Vector3(h, -h, h), hand, false);
            Face(new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h), new Vector3(-h, -h, -h), shoulder, false);

            var m = new Mesh { name = "steve arm" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        private static Mesh UnitCube()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            return mesh;
        }

        private static Mesh UnitQuad()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            return mesh;
        }
    }
}
