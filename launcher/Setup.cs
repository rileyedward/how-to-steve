using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace SteveFish.Launcher;

/// <summary>First-run setup. Every download is pinned by SHA-256 (see <see cref="Pins"/>).</summary>
internal static class Setup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static Setup()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SteveFishLauncher/1.0 (local)");
    }

    /// <summary>Downloads to a temp file, checks the hash, then moves into place.</summary>
    public static async Task DownloadPinned(string url, string sha256, string dest, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part";
        log($"Downloading {Path.GetFileName(new Uri(url).LocalPath)}…");
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct);
        }
        var hash = Detect.Sha256(tmp);
        if (!string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"Download hash mismatch for {url} (got {hash}). Nothing was installed.");
        }
        File.Move(tmp, dest, overwrite: true);
        log($"  ok ({new FileInfo(dest).Length / 1024} KB, SHA-256 verified)");
    }

    // ---------- game side ----------

    /// <summary>Extracts BepInEx 5 into the game folder. Only adds files; never overwrites a game file.</summary>
    public static async Task InstallBepInEx(string gameDir, Action<string> log, CancellationToken ct)
    {
        var zip = Path.Combine(Detect.DataDir, "downloads", $"BepInEx_win_x64_{Pins.BepInExVersion}.zip");
        if (!File.Exists(zip) || Detect.Sha256(zip) != Pins.BepInExSha256)
            await DownloadPinned(Pins.BepInExUrl, Pins.BepInExSha256, zip, log, ct);
        using var archive = ZipFile.OpenRead(zip);
        var added = 0;
        foreach (var e in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(gameDir, e.FullName));
            if (!target.StartsWith(Path.GetFullPath(gameDir), StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
            if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(target); continue; }
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target);
            added++;
        }
        log($"BepInEx {Pins.BepInExVersion} installed into the game folder ({added} files added, no game files changed).");
    }

    public static void InstallPlugin(string gameDir, Action<string> log)
    {
        if (!File.Exists(Detect.BundledPlugin)) throw new FileNotFoundException("The plugin isn't next to the launcher (plugin\\SteveFish.Plugin.dll).");
        var dest = Detect.PluginPath(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(Detect.BundledPlugin, dest, overwrite: true);
        log($"SteveFish plugin installed: {dest}");
    }

    // ---------- server side ----------

    public static string EulaFile => Path.Combine(Detect.ServerDir, "eula.txt");
    public static string RconSecretFile => Path.Combine(Detect.ServerDir, "rcon.secret");
    public static string PaperJar => Path.Combine(Detect.ServerDir, "paper.jar");

    public static bool EulaAccepted() =>
        File.Exists(EulaFile) && File.ReadAllLines(EulaFile).Any(l => l.Trim().Equals("eula=true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Only called after the user ticks "I accept the Minecraft EULA" in the launcher.</summary>
    public static void AcceptEula()
    {
        Directory.CreateDirectory(Detect.ServerDir);
        File.WriteAllText(EulaFile, $"# Accepted in the SteveFish launcher on {DateTime.Now:yyyy-MM-dd HH:mm} ({Pins.EulaUrl})\neula=true\n");
    }

    public static bool ServerReady() => File.Exists(PaperJar) && Detect.Sha256(PaperJar) == Pins.PaperSha256 && File.Exists(RconSecretFile);

    /// <summary>Paper jar, config (loopback-only, void world), RCON secret. Existing files are kept.</summary>
    public static async Task PrepareServer(Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Detect.ServerDir);
        if (!File.Exists(PaperJar) || Detect.Sha256(PaperJar) != Pins.PaperSha256)
            await DownloadPinned(Pins.PaperUrl, Pins.PaperSha256, PaperJar, log, ct);

        if (!File.Exists(RconSecretFile))
        {
            var bytes = RandomNumberGenerator.GetBytes(24);
            File.WriteAllText(RconSecretFile, Convert.ToBase64String(bytes).Replace('+', 'A').Replace('/', 'B').TrimEnd('='));
        }
        var props = Path.Combine(Detect.ServerDir, "server.properties");
        if (!File.Exists(props))
        {
            var text = Resource("server.properties").Replace("__GENERATED__", File.ReadAllText(RconSecretFile).Trim());
            File.WriteAllText(props, text);
            log("Server config written (127.0.0.1 only, void world).");
        }
        var spigot = Path.Combine(Detect.ServerDir, "spigot.yml");
        if (!File.Exists(spigot)) File.WriteAllText(spigot, Resource("spigot.yml"));
    }

    private static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"missing embedded resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Copies an existing server folder (e.g. the dev one in the repo) into the launcher's data folder.</summary>
    public static void ImportServer(string from, Action<string> log)
    {
        if (Directory.Exists(Path.Combine(Detect.ServerDir, "world"))) throw new InvalidOperationException("The launcher already has a world; not overwriting it.");
        CopyDir(from, Detect.ServerDir, skip: new[] { "logs" });
        log($"Imported server folder from {from}");
    }

    private static void CopyDir(string from, string to, string[] skip)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(d);
            if (skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            CopyDir(d, Path.Combine(to, name), Array.Empty<string>());
        }
    }
}
