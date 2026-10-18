using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx.Logging;

namespace HowToSteve.Minecraft
{
    /// <summary>
    /// Reads block models and textures straight out of the player's own Minecraft client jar at runtime.
    /// Nothing from the jar is copied to disk or into the repo. Thread-safe (one lock around the archive).
    /// </summary>
    internal sealed class JarAssets : IDisposable
    {
        private readonly ZipArchive _zip;
        private readonly object _lock = new object();
        public string JarPath { get; }

        private JarAssets(string jarPath)
        {
            JarPath = jarPath;
            _zip = ZipFile.OpenRead(jarPath);
        }

        /// <summary>
        /// Opens <paramref name="configured"/> if set; otherwise prefers the exact server version under
        /// %APPDATA%\.minecraft\versions, then the newest release-looking version that has a jar.
        /// </summary>
        public static JarAssets Open(string configured, string preferredVersion, ManualLogSource log)
        {
            var path = string.IsNullOrWhiteSpace(configured) ? FindJar(preferredVersion) : configured.Trim();
            if (path == null || !File.Exists(path))
            {
                log.LogWarning($"Minecraft client jar not found ({path ?? "no versions folder"}); set [Minecraft] ClientJar in the config");
                return null;
            }
            try
            {
                var assets = new JarAssets(path);
                log.LogInfo($"Minecraft assets: {path}");
                return assets;
            }
            catch (Exception e)
            {
                log.LogError($"can't open {path}: {e.Message}");
                return null;
            }
        }

        private static string FindJar(string preferredVersion)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions");
            if (!Directory.Exists(root)) return null;
            string JarOf(string v) => Path.Combine(root, v, v + ".jar");

            if (!string.IsNullOrEmpty(preferredVersion) && File.Exists(JarOf(preferredVersion))) return JarOf(preferredVersion);

            var candidates = Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(v => File.Exists(JarOf(v)))
                .ToList();
            // Releases before snapshots/pre-releases, then highest version first.
            var best = candidates
                .OrderBy(v => v.Contains("-") || v.Contains("w") ? 1 : 0)
                .ThenByDescending(v => VersionKey(v))
                .FirstOrDefault();
            return best == null ? null : JarOf(best);
        }

        private static string VersionKey(string v) =>
            string.Join(".", v.Split('.', '-').Select(p => int.TryParse(p, out var n) ? n.ToString("D5") : p));

        public byte[] ReadBytes(string entry)
        {
            lock (_lock)
            {
                var e = _zip.GetEntry(entry);
                if (e == null) return null;
                using (var s = e.Open())
                using (var ms = new MemoryStream((int)Math.Max(0, e.Length)))
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        public string ReadText(string entry)
        {
            var b = ReadBytes(entry);
            return b == null ? null : System.Text.Encoding.UTF8.GetString(b);
        }

        /// <summary>"minecraft:block/stone" or "block/stone" → "assets/minecraft/{kind}/block/stone{ext}".</summary>
        public static string ResourcePath(string id, string kind, string ext)
        {
            var ns = "minecraft";
            var path = id;
            var colon = id.IndexOf(':');
            if (colon >= 0) { ns = id.Substring(0, colon); path = id.Substring(colon + 1); }
            return $"assets/{ns}/{kind}/{path}{ext}";
        }

        public void Dispose()
        {
            lock (_lock) _zip.Dispose();
        }
    }
}
