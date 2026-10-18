using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteveFish.Launcher;

/// <summary>Finds what's already on this PC. Nothing here changes anything.</summary>
internal static class Detect
{
    /// <summary>Launcher data: server world, RCON secret, settings. %LOCALAPPDATA%\SteveFish.</summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteveFish");
    public static string ServerDir => Path.Combine(DataDir, "server");

    /// <summary>Folder the launcher exe lives in (release layout: plugin\, bridge\ next to it).</summary>
    public static string AppDir => AppContext.BaseDirectory;

    public static string BundledPlugin => Path.Combine(AppDir, "plugin", "SteveFish.Plugin.dll");
    public static string BundledNode => Path.Combine(AppDir, "bridge", "node.exe");
    public static string BundledBridgeEntry => Path.Combine(AppDir, "bridge", "app", "dist", "src", "index.js");

    // ---------- Java ----------

    public sealed record JavaInfo(string Path, int Major, string Version);

    /// <summary>Java 21+ from JAVA_HOME, common install folders, or PATH; newest wins.</summary>
    public static JavaInfo? FindJava()
    {
        var candidates = new List<string>();
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home)) candidates.Add(Path.Combine(home, "bin", "java.exe"));
        foreach (var root in new[]
                 {
                     @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Java", @"C:\Program Files\Microsoft",
                     @"C:\Program Files\Zulu", @"C:\Program Files\Amazon Corretto", @"C:\Program Files\BellSoft",
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root)) candidates.Add(Path.Combine(d, "bin", "java.exe"));
        }
        // The Minecraft launcher ships its own Java runtimes; they work for the server too.
        var mcRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
        if (Directory.Exists(mcRuntime))
            candidates.AddRange(Directory.GetFiles(mcRuntime, "java.exe", SearchOption.AllDirectories));
        var pf86Runtime = @"C:\Program Files (x86)\Minecraft Launcher\runtime";
        if (Directory.Exists(pf86Runtime))
            candidates.AddRange(Directory.GetFiles(pf86Runtime, "java.exe", SearchOption.AllDirectories));
        candidates.Add("java.exe"); // PATH

        JavaInfo? best = null;
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (c != "java.exe" && !File.Exists(c)) continue;
            var info = Probe(c);
            if (info != null && info.Major >= Pins.JavaMajor && (best == null || info.Major > best.Major)) best = info;
        }
        return best;
    }

    private static JavaInfo? Probe(string java)
    {
        try
        {
            var psi = new ProcessStartInfo(java, "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = Regex.Match(text, "version \"(\\d+)(?:\\.(\\d+))?[^\"]*\"");
            if (!m.Success) return null;
            var major = int.Parse(m.Groups[1].Value);
            if (major == 1 && m.Groups[2].Success) major = int.Parse(m.Groups[2].Value); // "1.8.0" style
            var path = java;
            if (java == "java.exe")
            {
                using var where = Process.Start(new ProcessStartInfo("where", "java") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                path = where?.StandardOutput.ReadLine()?.Trim() ?? java;
            }
            return new JavaInfo(path, major, m.Value.Replace("version ", "").Trim('"'));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- Minecraft ----------

    /// <summary>A Minecraft Java client jar in %APPDATA%\.minecraft\versions (the plugin reads textures from it).</summary>
    public static string? FindMinecraftJar()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .Select(d => Path.Combine(d, Path.GetFileName(d) + ".jar"))
            .Where(File.Exists)
            .OrderBy(j => j.Contains('-') ? 1 : 0)
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // ---------- How to Fish (Steam) ----------

    /// <summary>The folder that contains "How to Fish.exe", found through Steam's library list.</summary>
    public static string? FindGameDir(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(Path.Combine(overridePath, Pins.GameExe))) return overridePath;
        foreach (var lib in SteamLibraries())
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{Pins.HowToFishAppId}.acf");
            if (!File.Exists(manifest)) continue;
            var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
            if (!m.Success) continue;
            var install = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
            if (File.Exists(Path.Combine(install, Pins.GameExe))) return install;
            // How to Fish nests its files one level down ("How to Fish\How to Fish\").
            foreach (var sub in Directory.Exists(install) ? Directory.GetDirectories(install) : Array.Empty<string>())
                if (File.Exists(Path.Combine(sub, Pins.GameExe))) return sub;
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? @"C:\Program Files (x86)\Steam";
        steam = steam.Replace('/', '\\');
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsBepInExInstalled(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "winhttp.dll")) && File.Exists(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll"));

    public static string PluginPath(string gameDir) => Path.Combine(gameDir, "BepInEx", "plugins", "SteveFish", "SteveFish.Plugin.dll");

    /// <summary>True when the installed plugin is byte-identical to the one bundled with this launcher.</summary>
    public static bool IsPluginCurrent(string gameDir)
    {
        var installed = PluginPath(gameDir);
        if (!File.Exists(installed) || !File.Exists(BundledPlugin)) return false;
        return Sha256(installed) == Sha256(BundledPlugin);
    }

    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
    }

    public static bool IsGameRunning() => Process.GetProcessesByName("How to Fish").Length > 0;

    public static bool IsPortInUse(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
}
