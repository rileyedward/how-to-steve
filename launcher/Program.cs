namespace HowToSteve.Launcher;

internal static class Program
{
    /// <summary>What the one-time data-folder migration did, shown in the launcher log.</summary>
    public static string? MigrationNote;

    [STAThread]
    private static void Main()
    {
        // One launcher at a time: a second copy would fight over the server and bridge ports.
        using var single = new Mutex(true, @"Local\HowToSteveLauncher", out var first);
        if (!first)
        {
            MessageBox.Show("The How to Steve launcher is already running.", "How to Steve", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Before anything touches the data folder: carry over a pre-rename (SteveFish) install.
        MigrationNote = Detect.MigrateLegacyData();
        ApplicationConfiguration.Initialize();
        // --play: press Play on start (for a desktop shortcut). --no-game: don't launch How to Fish.
        var args = Environment.GetCommandLineArgs();
        Application.Run(new MainForm(autoPlay: args.Contains("--play"), launchGame: !args.Contains("--no-game")));
    }
}
