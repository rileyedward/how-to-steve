using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace SteveFish.Minecraft
{
    /// <summary>
    /// Draws the Minecraft blocks near Steve inside How to Fish. Hierarchy:
    /// <c>SteveFish.World</c> (DontDestroyOnLoad, copies the island anchor's pose every frame)
    /// └ <c>MC</c> (applies McTransform, so children use raw Minecraft coordinates)
    ///   └ one GameObject per 16³ section with MeshFilter + MeshRenderer.
    /// These are plain local GameObjects: nothing is networked (game-map §5).
    /// </summary>
    internal sealed class WorldRenderer : MonoBehaviour
    {
        private const int MaxMeshUploadsPerFrame = 24;

        private ManualLogSource _log;
        private McTransform _transform;
        private BlockAtlas _atlas;
        private int _layer;
        private Transform _mcRoot;
        private readonly Dictionary<(int, int, int), GameObject> _sections = new Dictionary<(int, int, int), GameObject>();
        private readonly ConcurrentQueue<KeyValuePair<(int, int, int), MeshData>> _incoming =
            new ConcurrentQueue<KeyValuePair<(int, int, int), MeshData>>();
        private Material _opaque;
        private Material _cutout;
        private bool _materialsTried;

        /// <summary>Set by the plugin: true only when SoloGuard is OK, the bridge is up and Steve is spawned.</summary>
        public bool Active { get; set; }
        public int LiveSections => _sections.Count;
        public string ShaderName { get; private set; } = "(none yet)";

        public static WorldRenderer Create(McTransform transform, BlockAtlas atlas, int layer, ManualLogSource log)
        {
            var go = new GameObject("SteveFish.World");
            DontDestroyOnLoad(go);
            var r = go.AddComponent<WorldRenderer>();
            r._log = log;
            r._transform = transform;
            r._atlas = atlas;
            r._layer = layer;
            var mc = new GameObject("MC");
            mc.transform.SetParent(go.transform, false);
            transform.ApplyToRoot(mc.transform);
            r._mcRoot = mc.transform;
            go.SetActive(true);
            return r;
        }

        /// <summary>Any thread.</summary>
        public void Enqueue((int, int, int) key, MeshData mesh) =>
            _incoming.Enqueue(new KeyValuePair<(int, int, int), MeshData>(key, mesh));

        private void LateUpdate()
        {
            _atlas.UploadPending();

            for (var i = 0; i < MaxMeshUploadsPerFrame && _incoming.TryDequeue(out var job); i++)
                ApplyMesh(job.Key, job.Value);

            var anchor = Active ? McTransform.CurrentAnchor() : null;
            var show = anchor != null;
            if (_mcRoot.gameObject.activeSelf != show) _mcRoot.gameObject.SetActive(show);
            if (!show) return;

            transform.SetPositionAndRotation(anchor.position, anchor.rotation);
            transform.localScale = anchor.lossyScale;
        }

        private void ApplyMesh((int x, int y, int z) key, MeshData data)
        {
            if (data == null)
            {
                if (_sections.TryGetValue(key, out var old))
                {
                    DestroySection(old);
                    _sections.Remove(key);
                }
                return;
            }

            EnsureMaterials();
            if (!_sections.TryGetValue(key, out var go))
            {
                go = new GameObject($"section {key.x},{key.y},{key.z}") { layer = _layer };
                go.transform.SetParent(_mcRoot, false);
                go.transform.localPosition = new Vector3(key.x * 16, key.y * 16, key.z * 16);
                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = new Mesh { name = go.name };
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { _opaque, _cutout };
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                // Collision on layer 8 (Level), like the island's own MeshColliders (game-map U3/U5), so the
                // game's ground check and movement treat Minecraft blocks as ground (decisions D8).
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = null;
                go.AddComponent<SectionMarker>().Key = key;
                _sections[key] = go;
            }

            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            mesh.Clear();
            mesh.indexFormat = data.Vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.Uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(data.Opaque, 0, false);
            mesh.SetTriangles(data.Cutout, 1, false);
            mesh.RecalculateBounds();

            var col = go.GetComponent<MeshCollider>();
            var oldCollision = col.sharedMesh;
            col.sharedMesh = null;
            if (oldCollision != null) Destroy(oldCollision);
            if (data.CollisionTriangles.Count > 0)
            {
                var cm = new Mesh { name = go.name + " collision" };
                cm.indexFormat = data.CollisionVertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                cm.SetVertices(data.CollisionVertices);
                cm.SetTriangles(data.CollisionTriangles, 0, true);
                col.sharedMesh = cm;
            }
        }

        private static void DestroySection(GameObject go)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            var mc = go.GetComponent<MeshCollider>();
            if (mc != null && mc.sharedMesh != null) Destroy(mc.sharedMesh);
            Destroy(go);
        }

        public int SectionLayer => _layer;

        /// <summary>Re-applies the Minecraft transform (the island changed and with it the per-island origin).</summary>
        public void ReapplyTransform() => _transform.ApplyToRoot(_mcRoot);

        /// <summary>The Minecraft-space root (children use raw Minecraft coordinates).</summary>
        public Transform McRoot => _mcRoot;

        /// <summary>True when the overlay is drawn this frame.</summary>
        public bool Visible => _mcRoot != null && _mcRoot.gameObject.activeInHierarchy;

        /// <summary>Unlit, double-sided, alpha-clipped: for the first-person arm and held item.</summary>
        public Material MakeViewModelMaterial(Color color, Texture2D tex)
        {
            var m = MakeUnlitMaterial(color);
            if (tex != null)
            {
                m.mainTexture = tex;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            }
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.AlphaTest;
            return m;
        }

        public Material MakeUnlitMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            var m = new Material(shader) { name = "SteveFish.Unlit" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            m.color = color;
            return m;
        }

        private void EnsureMaterials()
        {
            if (_materialsTried) return;
            _materialsTried = true;
            var shader = FindShader(out var source);
            ShaderName = shader != null ? $"{shader.name} ({source})" : "none found";
            _log.LogInfo($"block material shader: {ShaderName}");
            _opaque = MakeMaterial(shader, false);
            _cutout = MakeMaterial(shader, true);
        }

        /// <summary>U8: URP Lit may be stripped from the build, so fall back to whatever URP shader the islands use.</summary>
        private static Shader FindShader(out string source)
        {
            foreach (var name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Unlit" })
            {
                var s = Shader.Find(name);
                if (s != null) { source = "Shader.Find"; return s; }
            }
            foreach (var r in FindObjectsByType<MeshRenderer>())
            {
                var m = r.sharedMaterial;
                if (m != null && m.shader != null && m.shader.name.StartsWith("Universal Render Pipeline/"))
                {
                    source = $"borrowed from {r.name}";
                    return m.shader;
                }
            }
            source = "fallback";
            return Shader.Find("Sprites/Default");
        }

        private Material MakeMaterial(Shader shader, bool cutout)
        {
            var m = new Material(shader) { name = cutout ? "SteveFish.Blocks.Cutout" : "SteveFish.Blocks.Opaque" };
            m.mainTexture = _atlas.Texture;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", _atlas.Texture);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
            if (m.HasProperty("_EnvironmentReflections")) m.SetFloat("_EnvironmentReflections", 0f);
            if (cutout)
            {
                if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            return m;
        }

        public void ClearAll()
        {
            while (_incoming.TryDequeue(out _)) { }
            foreach (var go in _sections.Values) DestroySection(go);
            _sections.Clear();
        }
    }

    /// <summary>Tags our section GameObjects so raycasts can tell Minecraft blocks from island ground.</summary>
    internal sealed class SectionMarker : MonoBehaviour
    {
        public (int x, int y, int z) Key;
    }
}