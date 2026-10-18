namespace SteveFish.Launcher;

/// <summary>Everything the launcher downloads, pinned by version and SHA-256. Bump these deliberately.</summary>
internal static class Pins
{
    public const string HowToFishAppId = "4001890";
    public const string GameExe = "How to Fish.exe";

    public const string BepInExVersion = "5.4.23.5";
    public const string BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    public const string BepInExSha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4";

    public const string PaperVersion = "1.21.11";
    public const int PaperBuild = 132;
    public const string PaperSha256 = "5ffef465eeeb5f2a3c23a24419d97c51afd7dbb4923ff42df9a3f58bba1ccfba";
    public static readonly string PaperUrl = $"https://fill-data.papermc.io/v1/objects/{PaperSha256}/paper-{PaperVersion}-{PaperBuild}.jar";

    public const int JavaMajor = 21;
    public const string EulaUrl = "https://aka.ms/MinecraftEULA";

    public const int BridgePort = 25599;
    public const int ServerPort = 25565;
    public const int RconPort = 25575;
}
