# Releasing SteveFish

Releases are built locally, because the plugin compiles against How to Fish's own DLLs, which can't be put in a repo or a CI runner.

1. Bump `<Version>` in `launcher/SteveFish.Launcher.csproj` (and the plugin's `<Version>` / bridge `package.json` if they changed).
2. `powershell -ExecutionPolicy Bypass -File tools\release\build-release.ps1`
   - builds the plugin, runs the bridge tests, compiles the bridge, installs its production `node_modules`, bundles `node.exe`, and publishes the launcher as a self-contained single exe;
   - writes `dist\SteveFish-<version>-win-x64.zip` and a `.sha256` next to it.
3. Smoke test: extract the zip into an empty folder and run `SteveFishLauncher.exe --play --no-game`; the Launcher tab should end with "Ready: server, bridge and Steve are up". Close the launcher and check that the server log ends with "Saving worlds".
4. Publish the release on GitHub:
   `gh release create v<version> dist\SteveFish-<version>-win-x64.zip dist\SteveFish-<version>-win-x64.zip.sha256 --title "SteveFish <version>" --notes "..."`

## What's in the zip, and what isn't

| in the zip | not in the zip (downloaded or found at runtime) |
|---|---|
| launcher exe, plugin DLL, compiled bridge + `node_modules`, `node.exe` (MIT license included) | BepInEx (pinned GitHub release, SHA-256 checked), Paper (pinned build, SHA-256 checked), Java (user's install), Minecraft textures and sounds (user's `.minecraft`), How to Fish (Steam) |

Never in the zip or the repo: game DLLs, decompiled code, Minecraft assets, worlds, RCON secrets.
