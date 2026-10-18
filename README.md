# How to Steve

Play as Steve inside How to Fish.

## Overview

### What is How to Steve?

How to Steve is a mod that runs real Minecraft alongside How to Fish. A local Minecraft server owns the blocks, items, crafting and survival rules; a BepInEx plugin draws that world inside How to Fish, so you can mine, build, craft and fight with Minecraft rules on a How to Fish island. A bridge between the two logs in as Steve and keeps him where your How to Fish player is, and the island itself is mirrored into Minecraft as invisible blocks so both games agree on where the ground is.

Everything runs on your own PC. The server and bridge only listen on `127.0.0.1`, and the plugin switches itself off unless you're in an offline world, as host, with nobody else connected, so it never touches anyone else's game.

How to Steve is not produced, approved or endorsed by Dazed Games, Mojang Studios or Microsoft. It ships no game code and no Minecraft assets: textures and sounds load at runtime from your own Minecraft install.

### Key Features

- **Real Minecraft**: Blocks come from a real Paper server through Mineflayer, rendered with the textures and models from your own client.
- **Building**: Press B for Minecraft mode, then break blocks with survival timing and place them on Minecraft blocks or straight onto the island. Everything you build is solid.
- **Survival and Crafting**: The Minecraft hotbar, hearts, food and XP, plus the inventory with the 2×2 grid, crafting tables, furnaces and chests.
- **Combat**: Hit How to Fish creatures with Minecraft weapons, with Java Edition damage and attack cooldown, through the game's own hit system.
- **Terrain Sync**: The island is voxelized into invisible barrier blocks, so Minecraft's physics and placement match what's on screen.
- **One-Click Launcher**: Checks everything, installs the mod, starts the server and bridge, launches How to Fish and shuts it all down cleanly when you're done.

## Getting Started

### Prerequisites

Ensure you have the following installed on your system. You can verify each one with the provided commands in a terminal.

1. **Java** (21+) runs the local Minecraft server. Check if Java is installed by running:

    ```bash
    java -version
    ```

2. **Minecraft: Java Edition** provides the textures, models and sounds. Start it once from the Minecraft Launcher, then check that a version folder exists:

    ```bash
    dir "%APPDATA%\.minecraft\versions"
    ```

3. **How to Fish** is needed on Steam (Windows).

### Installation

1. Download `HowToSteve-<version>-win-x64.zip` from the [Releases](../../releases) page and extract it anywhere, for example `Documents\HowToSteve`.

2. Run `HowToSteveLauncher.exe`.

3. Read and accept the [Minecraft EULA](https://aka.ms/MinecraftEULA) by ticking the box in the launcher.

4. Press **Play**. On the first run the launcher installs [BepInEx](https://github.com/BepInEx/BepInEx) 5 and the How to Steve plugin into How to Fish and downloads the [Paper](https://papermc.io) server. Both downloads are checked against pinned SHA-256 hashes, and no game files are changed.

5. When How to Fish opens, start an **offline** world.

Your Minecraft world, settings and `launcher.log` are kept in `%LOCALAPPDATA%\HowToSteve`. To play plain How to Fish again, set `enabled = false` in `doorstop_config.ini` in the game folder.

### Controls

| Key | Action |
|---|---|
| `B` | Toggle Minecraft mode (crosshair, HUD, Steve's arm). How to Fish's clicks, scroll and number keys pause while it's on |
| Left mouse (hold) | Break the targeted block, or hit a How to Fish creature with the held item |
| Right mouse | Place the held block, or open a crafting table, furnace or chest (hold crouch to place against them instead) |
| Mouse wheel, `1`–`9` | Select a hotbar slot |
| `E` | Open the inventory with the 2×2 crafting grid. Shift-click moves stacks; clicking outside drops the held stack |

The launcher's **Minecraft server** tab takes server commands, for example `give Steve minecraft:diamond_pickaxe`.

## Development

### Prerequisites

1. **.NET SDK** (8+) builds the plugin and the launcher. Check it by running:

    ```bash
    dotnet --version
    ```

2. **Node** (20+) and **NPM** run and build the bridge. Check them by running:

    ```bash
    node --version
    npm --version
    ```

3. **How to Fish** must be installed: the plugin compiles against the game's own assemblies, which are never copied into the repository.

### Installation

1. Point the plugin build at your How to Fish install. Copy the example file and fix the paths if your Steam library is elsewhere:

    ```bash
    copy plugin\Directory.Build.props.user.example plugin\Directory.Build.props.user
    ```

2. Install the bridge dependencies:

    ```bash
    cd bridge
    npm install
    ```

3. Build the plugin and copy it into the game's `BepInEx\plugins\HowToSteve` folder (close How to Fish first; BepInEx must already be installed, for example by running the launcher once):

    ```bash
    dotnet build plugin -c Release -p:Deploy=true
    ```

4. Start the Minecraft server. The first run stops at the EULA check: put `eula=true` in `server\run\eula.txt` once you've accepted it, then run it again:

    ```bash
    powershell -ExecutionPolicy Bypass -File server\start.ps1
    ```

5. Start the bridge, which logs in as Steve and listens for the plugin on `127.0.0.1:25599`:

    ```bash
    cd bridge
    npm run dev
    ```

6. Launch How to Fish and start an offline world. The plugin and bridge reconnect on their own, in any order.

Useful bridge commands:

| Command | What it does |
|---|---|
| `npm run mc -- "<command>"` | Run a server command over RCON, e.g. `npm run mc -- "time set day"` |
| `npm run demo` | Build a sample row of blocks in front of Steve (`npm run demo -- clear` removes it) |
| `npm run clear-barriers -- x1 y1 z1 x2 y2 z2` | Remove terrain-sync barriers in a box; other blocks are never touched |

### Project Structure

| Folder | What it is |
|---|---|
| `launcher/` | The one-click launcher (C#, WinForms, published as a self-contained single exe) |
| `plugin/` | The BepInEx 5 plugin: rendering, input, HUD, inventory screens, terrain voxelizer ([plugin/README.md](plugin/README.md)) |
| `bridge/` | Node and TypeScript: the Mineflayer bot, block streaming, building, windows, terrain writer |
| `server/` | Paper config templates (embedded in the launcher) and `start.ps1` for running the server by hand |
| `tools/release/` | The release build script |
| `docs/` | [Protocol](docs/protocol.md), [game map](docs/game-map.md) (game internals with evidence), [decisions](docs/decisions.md), [releasing](docs/releasing.md) and the per-phase plans in `docs/phases/` |

## How It Works

The plugin and the bridge talk over a local WebSocket with a versioned JSON protocol ([docs/protocol.md](docs/protocol.md)):

- **Plugin → bridge:** the player's position and look (streamed through a Harmony postfix on the game's player tick), dig, place, hotbar and window clicks, and the voxelized island.
- **Bridge → plugin:** Minecraft blocks near Steve in 16³ sections, block updates, inventory, hearts and food, container windows, and dig progress.

The wire speaks Minecraft coordinates only. The plugin owns the transform between the How to Fish island and the Minecraft world, so each island gets its own area of the world. The Minecraft server is the authority for every block and item; the plugin never decides what happens, it only shows what the server says.

## Releasing

Releases are built locally, because the plugin compiles against the game's assemblies:

```bash
powershell -ExecutionPolicy Bypass -File tools\release\build-release.ps1 -Downloads
```

This runs the bridge tests and writes `HowToSteve-<version>-win-x64.zip` and its `.sha256` file into your Downloads folder (leave out `-Downloads` to write them to `dist\` instead). See [docs/releasing.md](docs/releasing.md) for the smoke test and the publish command.

## Testing

Run the bridge test suite, which covers the protocol, block streaming, building, windows and terrain:

```bash
cd bridge
npm test
```

Type-check the bridge, and build the plugin and launcher:

```bash
npm run typecheck
dotnet build plugin -c Release
dotnet build launcher -c Release
```

## Data and Licensing

How to Steve contains no How to Fish code or assets and no Minecraft assets. Block models, textures, GUI sprites, the default Steve skin and sounds are read at runtime from the player's own Minecraft installation and are never copied or redistributed. Game internals in [docs/game-map.md](docs/game-map.md) were mapped from type and method metadata; no decompiled code is included.

The release zip bundles [Node.js](https://nodejs.org) (MIT, license included as `bridge\NODE-LICENSE`) and the bridge's npm dependencies under their own licenses. BepInEx (LGPL-2.1) and Paper (GPL-3.0) are downloaded from their official sources on first run. Running the Minecraft server requires accepting the [Minecraft EULA](https://aka.ms/MinecraftEULA).
