# Builds the How to Steve release files: HowToSteve-<version>-win-x64.zip and its .sha256
#
#   powershell -ExecutionPolicy Bypass -File tools\release\build-release.ps1 -Downloads   # into your Downloads folder
#   powershell -ExecutionPolicy Bypass -File tools\release\build-release.ps1             # into dist\
#   powershell -ExecutionPolicy Bypass -File tools\release\build-release.ps1 -OutDir D:\somewhere
#
# Zip layout (extract anywhere, run HowToSteveLauncher.exe):
#   HowToSteveLauncher.exe          self-contained .NET launcher (no runtime install needed)
#   plugin\HowToSteve.Plugin.dll    the BepInEx plugin (the launcher installs it into the game)
#   bridge\node.exe                Node.js runtime (MIT, see bridge\NODE-LICENSE)
#   bridge\app\                    compiled bridge + production node_modules
#   README.txt
#
# Not in the zip, downloaded by the launcher on first run with pinned SHA-256: BepInEx, Paper.
# Never in the zip: game files, Minecraft assets, worlds, secrets.
#
# Needs: .NET SDK 8+, Node 20+, and plugin\Directory.Build.props.user pointing at a How to Fish
# install (the plugin compiles against the game's own DLLs, which is why releases are built locally).
param(
    [switch]$Downloads,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$version = ([xml](Get-Content "$root\launcher\HowToSteve.Launcher.csproj")).Project.PropertyGroup.Version | Select-Object -First 1
$stage = Join-Path $root "dist\HowToSteve"
if ($Downloads) {
    # The real Downloads folder (it can be redirected, e.g. to OneDrive), from the shell's known-folder setting.
    $known = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' -ErrorAction SilentlyContinue).'{374DE290-123F-4565-9164-39C4925E467B}'
    $OutDir = if ($known) { [Environment]::ExpandEnvironmentVariables($known) } else { Join-Path $env:USERPROFILE 'Downloads' }
}
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$zip = Join-Path $OutDir "HowToSteve-$version-win-x64.zip"

$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node -and (Test-Path 'C:\Program Files\nodejs-lts\node.exe')) { $node = 'C:\Program Files\nodejs-lts\node.exe' }
if (-not $node) { throw 'node.exe not found (needed to bundle the bridge runtime)' }
$nodeDir = Split-Path $node
$npm = Join-Path $nodeDir 'npm.cmd'
$env:Path = "$nodeDir;$env:Path"

Write-Host "How to Steve $version -> $zip"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force "$stage\plugin", "$stage\bridge\app" | Out-Null

Write-Host '== plugin'
dotnet build "$root\plugin\HowToSteve.Plugin.csproj" -c Release --nologo -v q
if ($LASTEXITCODE) { throw 'plugin build failed' }
Copy-Item "$root\plugin\bin\Release\netstandard2.1\HowToSteve.Plugin.dll" "$stage\plugin\"

Write-Host '== bridge'
Push-Location "$root\bridge"
try {
    & $npm ci --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'npm ci failed' }
    & $npm test
    if ($LASTEXITCODE) { throw 'bridge tests failed' }
    & $npm run build
    if ($LASTEXITCODE) { throw 'bridge build failed' }
} finally { Pop-Location }
Copy-Item "$root\bridge\package.json", "$root\bridge\package-lock.json" "$stage\bridge\app\"
Copy-Item -Recurse "$root\bridge\dist" "$stage\bridge\app\dist"
Push-Location "$stage\bridge\app"
try {
    & $npm ci --omit=dev --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'npm ci (production) failed' }
} finally { Pop-Location }
Copy-Item $node "$stage\bridge\node.exe"
$license = Join-Path $nodeDir 'LICENSE'
if (Test-Path $license) { Copy-Item $license "$stage\bridge\NODE-LICENSE" }

Write-Host '== launcher'
dotnet publish "$root\launcher\HowToSteve.Launcher.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$root\launcher\bin\publish" --nologo -v q
if ($LASTEXITCODE) { throw 'launcher publish failed' }
Copy-Item "$root\launcher\bin\publish\HowToSteveLauncher.exe" "$stage\"

@"
How to Steve $version — play as Steve inside How to Fish

You need: Java 21+, Minecraft Java Edition (run it once), How to Fish (Steam).

1. Extract this folder anywhere (e.g. Documents\HowToSteve).
2. Run HowToSteveLauncher.exe.
3. Tick "I accept the Minecraft EULA" (https://aka.ms/MinecraftEULA) and press Play.
   First run downloads BepInEx and the Paper server (both verified by SHA-256) and installs
   the How to Steve plugin into How to Fish.
4. In How to Fish start an OFFLINE world. B = Minecraft mode, E = inventory.

Everything runs on this PC only (127.0.0.1). Your Minecraft world lives in
%LOCALAPPDATA%\HowToSteve\server.
To go back to plain How to Fish: set enabled = false in doorstop_config.ini in the game folder.
"@ | Set-Content "$stage\README.txt" -Encoding utf8

Write-Host '== zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Set-Content "$zip.sha256" "$sha  $(Split-Path $zip -Leaf)" -Encoding ascii
Write-Host "done: $zip ($mb MB)  sha256 $sha"
