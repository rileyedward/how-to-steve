using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace SteveFish.Minecraft
{
    /// <summary>
    /// Minecraft block sounds from the player's own install: the launcher keeps sound files as hashed objects
    /// under .minecraft\assets\objects, listed in assets\indexes\&lt;id&gt;.json (id from the version json).
    /// Files are read in place and decoded by Unity (UnityWebRequest + OGG); nothing is copied.
    /// Main thread only.
    /// </summary>
    internal sealed class McSounds
    {
        private readonly ManualLogSource _log;
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>();   // "dig/stone1" -> path
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private readonly List<KeyValuePair<string, UnityWebRequest>> _loading = new List<KeyValuePair<string, UnityWebRequest>>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly System.Random _rng = new System.Random();
        public float Volume = 0.7f;

        public McSounds(string jarPath, ManualLogSource log)
        {
            _log = log;
            try { LoadIndex(jarPath); }
            catch (Exception e) { _log.LogWarning($"sounds: index not loaded ({e.Message}); playing silently"); }
        }

        private void LoadIndex(string jarPath)
        {
            if (string.IsNullOrEmpty(jarPath)) return;
            var versionDir = Path.GetDirectoryName(jarPath);
            var mcDir = Path.GetDirectoryName(Path.GetDirectoryName(versionDir));
            var versionJson = Path.Combine(versionDir, Path.GetFileName(versionDir) + ".json");
            if (!File.Exists(versionJson)) return;
            var indexId = (string)JObject.Parse(File.ReadAllText(versionJson))["assetIndex"]?["id"];
            var indexPath = Path.Combine(mcDir, "assets", "indexes", indexId + ".json");
            if (indexId == null || !File.Exists(indexPath)) return;
            var objects = (JObject)JObject.Parse(File.ReadAllText(indexPath))["objects"];
            foreach (var kv in objects)
            {
                if (!kv.Key.StartsWith("minecraft/sounds/") || !kv.Key.EndsWith(".ogg")) continue;
                var hash = (string)kv.Value["hash"];
                var file = Path.Combine(mcDir, "assets", "objects", hash.Substring(0, 2), hash);
                _files[kv.Key.Substring("minecraft/sounds/".Length, kv.Key.Length - "minecraft/sounds/".Length - 4)] = file;
            }
            _log.LogInfo($"sounds: {_files.Count} Minecraft sounds indexed (asset index {indexId})");
        }

        /// <summary>Vanilla-ish sound group for a block state, from its name.</summary>
        public static string GroupOf(string state)
        {
            var n = state ?? "";
            var br = n.IndexOf('['); if (br >= 0) n = n.Substring(0, br);
            if (n.StartsWith("minecraft:")) n = n.Substring(10);
            if (n.Contains("glass") || n == "ice" || n.Contains("lantern") || n == "beacon") return "glass";
            if (n.Contains("wool") || n.Contains("carpet")) return "cloth";
            if (n.Contains("sand")) return "sand";
            if (n == "gravel" || n == "dirt" || n == "coarse_dirt" || n == "farmland" || n == "clay") return "gravel";
            if (n == "grass_block" || n.Contains("leaves") || n.Contains("grass") || n.Contains("fern") || n == "mycelium" || n.Contains("sapling") || n.Contains("flower") ||
                n == "poppy" || n == "dandelion" || n.Contains("orchid") || n == "hay_block" || n.Contains("moss")) return "grass";
            if (n.Contains("snow")) return "snow";
            if (n.Contains("log") || n.Contains("planks") || n.Contains("wood") || n.Contains("crafting_table") || n.Contains("bookshelf") ||
                n.Contains("fence") || n.Contains("door") || n.Contains("chest") || n.Contains("barrel") || n.Contains("oak") || n.Contains("spruce") ||
                n.Contains("birch") || n.Contains("jungle") || n.Contains("acacia") || n.Contains("cherry") || n.Contains("mangrove") || n.Contains("bamboo") ||
                n == "torch" || n == "ladder" || n.Contains("sign")) return "wood";
            return "stone";
        }

        /// <summary>Plays sounds/{category}/{group}N.ogg at a world position (category: dig, step).</summary>
        public void Play(string category, string group, Vector3 at, float volume = 1f, float pitch = 1f)
        {
            var key = PickVariant($"{category}/{group}");
            if (key == null) return;
            if (_clips.TryGetValue(key, out var clip))
            {
                if (clip != null) PlayAt(clip, at, Volume * volume, pitch);
                return;
            }
            if (_failed.Contains(key) || _loading.Exists(l => l.Key == key)) return;
            var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(_files[key]).AbsoluteUri, AudioType.OGGVORBIS);
            req.SendWebRequest();
            _loading.Add(new KeyValuePair<string, UnityWebRequest>(key, req));
        }

        private string PickVariant(string prefix)
        {
            var found = new List<string>();
            for (var i = 1; i <= 6; i++) if (_files.ContainsKey(prefix + i)) found.Add(prefix + i);
            if (found.Count == 0 && _files.ContainsKey(prefix)) found.Add(prefix);
            return found.Count == 0 ? null : found[_rng.Next(found.Count)];
        }

        /// <summary>Starts loading every block group's dig/step variants so the first use isn't silent.</summary>
        public void Preload()
        {
            foreach (var cat in new[] { "dig", "step" })
                foreach (var g in new[] { "stone", "wood", "grass", "gravel", "sand", "glass", "cloth", "snow" })
                    for (var i = 1; i <= 6; i++)
                    {
                        var key = $"{cat}/{g}{i}";
                        if (!_files.ContainsKey(key) || _clips.ContainsKey(key) || _loading.Exists(l => l.Key == key)) continue;
                        var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(_files[key]).AbsoluteUri, AudioType.OGGVORBIS);
                        req.SendWebRequest();
                        _loading.Add(new KeyValuePair<string, UnityWebRequest>(key, req));
                    }
        }

        /// <summary>Finishes pending loads. Call once per frame.</summary>
        public void Tick()
        {
            for (var i = _loading.Count - 1; i >= 0; i--)
            {
                var (key, req) = (_loading[i].Key, _loading[i].Value);
                if (!req.isDone) continue;
                _loading.RemoveAt(i);
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(req);
                    clip.name = "mc:" + key;
                    _clips[key] = clip;
                }
                else
                {
                    _failed.Add(key);
                    _log.LogWarning($"sounds: {key} failed: {req.error}");
                }
                req.Dispose();
            }
        }

        private static void PlayAt(AudioClip clip, Vector3 at, float volume, float pitch)
        {
            var go = new GameObject("SteveFish.Sound") { transform = { position = at } };
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.volume = volume;
            src.pitch = pitch;
            src.spatialBlend = 1f;
            src.minDistance = 2f;
            src.maxDistance = 24f;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length / Mathf.Max(0.1f, pitch) + 0.1f);
        }
    }
}
