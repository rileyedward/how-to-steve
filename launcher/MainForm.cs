using System.Diagnostics;

namespace SteveFish.Launcher;

/// <summary>
/// The launcher window: a checklist of prerequisites, the EULA box, Play / Stop, and live logs for the
/// server and bridge. Play does everything in order: set-up steps that are missing → Minecraft server →
/// bridge (Steve logs in) → How to Fish through Steam. Closing the game (optional) or the launcher stops
/// the server and bridge cleanly.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private readonly ManagedProcess _server = new("Minecraft server");
    private readonly ManagedProcess _bridge = new("bridge");
    private CancellationTokenSource _cts = new();

    private readonly TableLayoutPanel _checks = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
    private readonly CheckBox _eula = new() { Text = "I have read and accept the Minecraft EULA", AutoSize = true };
    private readonly LinkLabel _eulaLink = new() { Text = Pins.EulaUrl, AutoSize = true };
    private readonly CheckBox _stopWithGame = new() { Text = "Stop the server and bridge when How to Fish closes", AutoSize = true };
    private readonly Button _play = new() { Text = "▶  Play", Width = 160, Height = 44, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12, FontStyle.Bold) };
    private readonly Button _stop = new() { Text = "■  Stop all", Width = 120, Height = 44, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready.", Padding = new Padding(8, 14, 0, 0) };
    private readonly TextBox _launcherLog = LogBox();
    private readonly TextBox _serverLog = LogBox();
    private readonly TextBox _bridgeLog = LogBox();
    private readonly TextBox _serverCmd = new() { Dock = DockStyle.Fill, PlaceholderText = "Server command, e.g.  give Steve minecraft:diamond 5   (Enter to send)" };
    private readonly System.Windows.Forms.Timer _gameWatch = new() { Interval = 2000 };

    private string? _gameDir;
    private bool _busy, _gameSeen, _closing, _serverReady, _bridgeReady;
    private TaskCompletionSource<bool>? _serverDone, _bridgeSpawned;

    private readonly bool _launchGame;
    private readonly StreamWriter? _fileLog;

    public MainForm(bool autoPlay = false, bool launchGame = true)
    {
        _launchGame = launchGame;
        try
        {
            Directory.CreateDirectory(Detect.DataDir);
            _fileLog = new StreamWriter(Path.Combine(Detect.DataDir, "launcher.log"), append: false) { AutoFlush = true };
        }
        catch (Exception) { _fileLog = null; }
        Text = "SteveFish Launcher";
        Width = 900;
        Height = 760;
        MinimumSize = new Size(760, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont!;

        var header = new Label
        {
            Text = "SteveFish — play as Steve in How to Fish",
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
            AutoSize = true,
            Padding = new Padding(8, 10, 8, 0),
            Dock = DockStyle.Top,
        };
        var sub = new Label
        {
            Text = "Everything runs on this PC only. Start an OFFLINE world in How to Fish; press B in game for Minecraft mode, E for the inventory.",
            AutoSize = true,
            Padding = new Padding(10, 2, 8, 6),
            Dock = DockStyle.Top,
        };

        _checks.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        _checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _checks.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var eulaRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        eulaRow.Controls.Add(_eula);
        eulaRow.Controls.Add(_eulaLink);
        _eulaLink.LinkClicked += (_, _) => Open(Pins.EulaUrl);
        _eula.CheckedChanged += (_, _) => { if (_eula.Checked && !Setup.EulaAccepted()) { Setup.AcceptEula(); Log("Minecraft EULA accepted."); } RefreshChecks(); };

        var optsRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        _stopWithGame.Checked = _settings.StopWithGame;
        _stopWithGame.CheckedChanged += (_, _) => { _settings.StopWithGame = _stopWithGame.Checked; _settings.Save(); };
        optsRow.Controls.Add(_stopWithGame);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 6) };
        buttons.Controls.Add(_play);
        buttons.Controls.Add(_stop);
        buttons.Controls.Add(_status);
        _play.Click += async (_, _) => await PlayAsync();
        _stop.Click += async (_, _) => await StopAllAsync("stopped from the launcher");

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Launcher", _launcherLog));
        var serverPage = Page("Minecraft server", _serverLog);
        var cmdPanel = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        cmdPanel.Controls.Add(_serverCmd);
        serverPage.Controls.Add(cmdPanel);
        _serverCmd.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || string.IsNullOrWhiteSpace(_serverCmd.Text)) return;
            e.SuppressKeyPress = true;
            if (_server.Running) { _server.Send(_serverCmd.Text.TrimStart('/')); AppendTo(_serverLog, "> " + _serverCmd.Text); }
            _serverCmd.Clear();
        };
        tabs.TabPages.Add(serverPage);
        tabs.TabPages.Add(Page("Bridge", _bridgeLog));

        Controls.Add(tabs);
        Controls.Add(buttons);
        Controls.Add(optsRow);
        Controls.Add(eulaRow);
        Controls.Add(_checks);
        Controls.Add(sub);
        Controls.Add(header);

        _server.Line += l => OnServerLine(l);
        _bridge.Line += l => OnBridgeLine(l);
        _server.Exited += code => BeginInvoke(() => { Log($"Minecraft server exited ({code})."); _serverReady = false; _serverDone?.TrySetResult(false); UpdateButtons(); });
        _bridge.Exited += code => BeginInvoke(() => { Log($"Bridge exited ({code})."); _bridgeReady = false; _bridgeSpawned?.TrySetResult(false); UpdateButtons(); });
        _gameWatch.Tick += async (_, _) => await WatchGame();
        _gameWatch.Start();

        Shown += async (_, _) =>
        {
            RefreshChecks();
            Log($"SteveFish launcher {Application.ProductVersion} — data folder: {Detect.DataDir}");
            if (autoPlay) await PlayAsync();
        };
        FormClosing += OnFormClosing;
    }

    // ---------- checklist ----------

    private void RefreshChecks()
    {
        _checks.SuspendLayout();
        _checks.Controls.Clear();
        _checks.RowStyles.Clear();

        var java = Detect.FindJava();
        Row("Java 21+", java != null ? $"✔ Java {java.Version}  ({java.Path})" : "✖ Not found — install Java 21 or newer",
            java == null ? ("Get Java", () => Open("https://adoptium.net/temurin/releases/?version=21")) : null, java != null);

        var jar = Detect.FindMinecraftJar();
        Row("Minecraft (Java Edition)", jar != null ? $"✔ textures from {Path.GetFileName(Path.GetDirectoryName(jar))}" : "✖ No client found in %APPDATA%\\.minecraft — run Minecraft once",
            null, jar != null);

        _gameDir = Detect.FindGameDir(_settings.GameDir);
        Row("How to Fish", _gameDir != null ? $"✔ {_gameDir}" : "✖ Not found through Steam",
            ("Browse…", BrowseGame), _gameDir != null);

        if (_gameDir != null)
        {
            var bep = Detect.IsBepInExInstalled(_gameDir);
            Row("BepInEx (mod loader)", bep ? $"✔ installed" : $"• will be installed on Play (BepInEx {Pins.BepInExVersion})", null, bep);
            var plugin = Detect.IsPluginCurrent(_gameDir);
            var pluginMissing = !File.Exists(Detect.BundledPlugin);
            Row("SteveFish plugin", pluginMissing ? "✖ plugin\\SteveFish.Plugin.dll is missing next to the launcher" : plugin ? "✔ up to date" : "• will be installed/updated on Play",
                null, plugin);
        }

        var serverReady = Setup.ServerReady();
        Row("Minecraft server", serverReady ? $"✔ Paper {Pins.PaperVersion} ready in {Detect.ServerDir}" : $"• Paper {Pins.PaperVersion} will be downloaded on Play (~55 MB)",
            ("Open folder", () => { Directory.CreateDirectory(Detect.ServerDir); Open(Detect.ServerDir); }), serverReady);

        var bridgeOk = File.Exists(Detect.BundledNode) && File.Exists(Detect.BundledBridgeEntry);
        Row("Bridge", bridgeOk ? "✔ bundled" : "✖ bridge\\ folder is missing next to the launcher", null, bridgeOk);

        _eula.Checked = Setup.EulaAccepted();
        _eula.Enabled = !_eula.Checked;
        _checks.ResumeLayout();
        UpdateButtons();
    }

    private void Row(string what, string state, (string text, Action act)? action, bool ok)
    {
        _checks.Controls.Add(new Label { Text = what, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 0, 0) });
        _checks.Controls.Add(new Label { Text = state, AutoSize = true, ForeColor = ok ? Color.DarkGreen : Color.DarkGoldenrod, Padding = new Padding(0, 6, 0, 0), MaximumSize = new Size(560, 0) });
        if (action is { } a)
        {
            var b = new Button { Text = a.text, AutoSize = true };
            b.Click += (_, _) => { a.act(); RefreshChecks(); };
            _checks.Controls.Add(b);
        }
        else _checks.Controls.Add(new Label());
    }

    private void BrowseGame()
    {
        using var dlg = new FolderBrowserDialog { Description = "Pick the folder that contains \"How to Fish.exe\"" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!File.Exists(Path.Combine(dlg.SelectedPath, Pins.GameExe)))
        {
            MessageBox.Show(this, "That folder doesn't contain \"How to Fish.exe\".", "SteveFish", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.GameDir = dlg.SelectedPath;
        _settings.Save();
    }

    private void UpdateButtons()
    {
        var anyRunning = _server.Running || _bridge.Running;
        _play.Enabled = !_busy && !(_serverReady && _bridgeReady);
        _stop.Enabled = !_busy && anyRunning;
    }

    // ---------- play / stop ----------

    private async Task PlayAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            RefreshChecks();
            var java = Detect.FindJava() ?? throw new UserError("Java 21 or newer is needed for the Minecraft server. Click \"Get Java\".");
            if (Detect.FindMinecraftJar() == null) throw new UserError("Minecraft Java Edition wasn't found. Start it once from the Minecraft Launcher so its files are on this PC.");
            var gameDir = _gameDir ?? throw new UserError("How to Fish wasn't found. Use \"Browse…\" to pick its folder.");
            if (!Setup.EulaAccepted()) throw new UserError("Please read and accept the Minecraft EULA (tick the box) — the server can't start without it.");
            if (!File.Exists(Detect.BundledNode) || !File.Exists(Detect.BundledBridgeEntry)) throw new UserError("The bridge folder is missing next to the launcher. Re-extract the SteveFish zip.");

            Status("Setting up…");
            if (!Detect.IsBepInExInstalled(gameDir))
            {
                if (Detect.IsGameRunning()) throw new UserError("Close How to Fish first so BepInEx can be installed.");
                await Setup.InstallBepInEx(gameDir, Log, ct);
            }
            if (!Detect.IsPluginCurrent(gameDir))
            {
                if (Detect.IsGameRunning()) throw new UserError("Close How to Fish first so the SteveFish plugin can be updated.");
                Setup.InstallPlugin(gameDir, Log);
            }
            await Setup.PrepareServer(Log, ct);
            RefreshChecks();

            // Minecraft server
            if (!_server.Running)
            {
                if (Detect.IsPortInUse(Pins.ServerPort))
                    Log($"Port {Pins.ServerPort} is already in use — assuming a SteveFish server is already running and using it.");
                else
                {
                    Status("Starting the Minecraft server…");
                    _serverDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var mem = Math.Clamp(_settings.ServerMemoryMb, 1024, 16384);
                    _server.Start(java.Path, $"-Xms1G -Xmx{mem}M -XX:+UseG1GC -jar paper.jar --nogui", Detect.ServerDir);
                    if (!await WaitFor(_serverDone.Task, TimeSpan.FromMinutes(4), ct)) throw new UserError("The Minecraft server didn't start. See the \"Minecraft server\" tab.");
                }
            }

            // Bridge (Steve logs in)
            if (!_bridge.Running)
            {
                if (Detect.IsPortInUse(Pins.BridgePort))
                    Log($"Port {Pins.BridgePort} is already in use — another bridge is running; using it.");
                else
                {
                    Status("Starting the bridge…");
                    _bridgeSpawned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var env = new Dictionary<string, string>
                    {
                        ["STEVEFISH_RCON_PASSWORD"] = File.ReadAllText(Setup.RconSecretFile).Trim(),
                        ["NODE_ENV"] = "production",
                    };
                    var bridgeDir = Path.GetDirectoryName(Detect.BundledNode)!;
                    _bridge.Start(Detect.BundledNode, $"\"{Detect.BundledBridgeEntry}\"", Path.Combine(bridgeDir, "app"), env);
                    if (!await WaitFor(_bridgeSpawned.Task, TimeSpan.FromSeconds(90), ct)) throw new UserError("Steve didn't log in. See the \"Bridge\" tab.");
                }
            }

            // How to Fish
            if (Detect.IsGameRunning()) Status("All set — How to Fish is already running. Load an offline world.");
            else if (_settings.LaunchGame && _launchGame)
            {
                Status("Launching How to Fish…");
                Open($"steam://rungameid/{Pins.HowToFishAppId}");
                Status("All set — start an OFFLINE world in How to Fish.");
            }
            Log("Ready: server, bridge and Steve are up.");
        }
        catch (UserError e)
        {
            Status(e.Message);
            Log("! " + e.Message);
            MessageBox.Show(this, e.Message, "SteveFish", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            Status("Cancelled.");
        }
        catch (Exception e)
        {
            Status("Something went wrong — see the Launcher tab.");
            Log("! " + e);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private async Task StopAllAsync(string why)
    {
        if (!_server.Running && !_bridge.Running) return;
        _busy = true;
        UpdateButtons();
        _cts.Cancel();
        Status("Stopping…");
        Log($"Stopping ({why}).");
        await _bridge.StopAsync("stop", TimeSpan.FromSeconds(8));
        await _server.StopAsync("stop", TimeSpan.FromSeconds(60)); // "stop" saves the world first
        _serverReady = _bridgeReady = false;
        _busy = false;
        Status("Stopped.");
        UpdateButtons();
    }

    private async Task WatchGame()
    {
        var running = Detect.IsGameRunning();
        if (running) _gameSeen = true;
        else if (_gameSeen)
        {
            _gameSeen = false;
            Log("How to Fish closed.");
            if (_settings.StopWithGame && !_busy && (_server.Running || _bridge.Running)) await StopAllAsync("How to Fish closed");
        }
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing || (!_server.Running && !_bridge.Running)) return;
        e.Cancel = true;
        _closing = true;
        await StopAllAsync("launcher closing");
        Close();
    }

    // ---------- output ----------

    private void OnServerLine(string line)
    {
        _fileLog?.WriteLine("[server] " + line);
        BeginInvoke(() => AppendTo(_serverLog, line));
        if (line.Contains("Done (") && line.Contains("For help"))
        {
            _serverReady = true;
            _serverDone?.TrySetResult(true);
            BeginInvoke(() => { Log("Minecraft server is up."); UpdateButtons(); });
        }
    }

    private void OnBridgeLine(string line)
    {
        _fileLog?.WriteLine("[bridge] " + line);
        BeginInvoke(() => AppendTo(_bridgeLog, line));
        if (line.Contains("minecraft: spawned"))
        {
            _bridgeReady = true;
            _bridgeSpawned?.TrySetResult(true);
            BeginInvoke(() => { Log("Steve is in the Minecraft world."); UpdateButtons(); });
        }
        else if (line.Contains("plugin connected")) BeginInvoke(() => Status("How to Fish is connected — have fun!"));
    }

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _fileLog?.WriteLine(line);
        AppendTo(_launcherLog, line);
    }

    private void Status(string text)
    {
        _status.Text = text;
        _fileLog?.WriteLine("[status] " + text);
    }

    private static void AppendTo(TextBox box, string line)
    {
        if (box.TextLength > 400_000) box.Text = box.Text[^200_000..];
        box.AppendText(line + Environment.NewLine);
    }

    private static TextBox LogBox() => new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9),
        BackColor = Color.FromArgb(24, 24, 24),
        ForeColor = Color.Gainsboro,
    };

    private static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title);
        p.Controls.Add(content);
        return p;
    }

    private static async Task<bool> WaitFor(Task<bool> task, TimeSpan timeout, CancellationToken ct)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout, ct));
        return done == task && task.Result;
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { /* nothing registered */ }
    }

    private sealed class UserError(string message) : Exception(message);
}
