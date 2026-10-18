namespace SteveFish.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One launcher at a time: a second copy would fight over the server and bridge ports.
        using var single = new Mutex(true, @"Local\SteveFishLauncher", out var first);
        if (!first)
        {
            MessageBox.Show("The SteveFish launcher is already running.", "SteveFish", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        // --play: press Play on start (for a desktop shortcut). --no-game: don't launch How to Fish.
        var args = Environment.GetCommandLineArgs();
        Application.Run(new MainForm(autoPlay: args.Contains("--play"), launchGame: !args.Contains("--no-game")));
    }
}
