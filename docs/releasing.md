# Releasing How to Steve

Releases are built locally, because the plugin compiles against How to Fish's own DLLs, which can't be put in a repo or a CI runner.

1. Bump `<Version>` in `launcher/HowToSteve.Launcher.csproj` (and the plugin's `<Version>` / bridge `package.json` if they changed).
2. `powershell -ExecutionPolicy Bypass -File tools\release\build-release.ps1 -Downloads`
   - builds the plugin, runs the bridge tests, compiles the bridge, installs its production `node_modules`, bundles `node.exe`, and publishes the launcher as a self-contained single exe;
   - writes `HowToSteve-<version>-win-x64.zip` and a `.sha256` into your Downloads folder (without `-Downloads` they go to `dist\`; `-OutDir <folder>` picks any folder).
3. Smoke test: extract the zip into an empty folder and run `HowToSteveLauncher.exe --play --no-game`; the Launcher tab should end with "Ready: server, bridge and Steve are up". Close the launcher and check that the server log ends with "Saving worlds".
4. Publish the release on GitHub:
   `gh release create v<version> "$env:USERPROFILE\Downloads\HowToSteve-<version>-win-x64.zip" "$env:USERPROFILE\Downloads\HowToSteve-<version>-win-x64.zip.sha256" --title "How to Steve <version>" --notes "..."`

## What's in the zip, and what isn't

| in the zip | not in the zip (downloaded or found at runtime) |
|---|---|
| launcher exe, plugin DLL, compiled bridge + `node_modules`, `node.exe` (MIT license included) | BepInEx (pinned GitHub release, SHA-256 checked), Paper (pinned build, SHA-256 checked), Java (user's install), Minecraft textures and sounds (user's `.minecraft`), How to Fish (Steam) |

Never in the zip or the repo: game DLLs, decompiled code, Minecraft assets, worlds, RCON secrets.
