# Starts the local HowToSteve Paper server (127.0.0.1 only).
# Usage:  powershell -ExecutionPolicy Bypass -File server\start.ps1
$ErrorActionPreference = 'Stop'

$PaperVersion = '1.21.11'
$PaperBuild   = 132
$PaperSha256  = '5ffef465eeeb5f2a3c23a24419d97c51afd7dbb4923ff42df9a3f58bba1ccfba'
$PaperUrl     = "https://fill-data.papermc.io/v1/objects/$PaperSha256/paper-$PaperVersion-$PaperBuild.jar"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$run  = Join-Path $here 'run'
New-Item -ItemType Directory -Force $run | Out-Null

# Java 21
$java = $env:HOWTOSTEVE_JAVA
if (-not $java) {
    $jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -like 'jdk-21*' | Sort-Object Name -Descending | Select-Object -First 1
    if ($jdk) { $java = Join-Path $jdk.FullName 'bin\java.exe' } else { $java = 'java' }
}

# Paper jar, pinned by hash
$jar = Join-Path $run 'paper.jar'
if (-not (Test-Path $jar)) {
    Write-Host "Downloading Paper $PaperVersion build $PaperBuild"
    Invoke-WebRequest $PaperUrl -OutFile $jar -UseBasicParsing
}
$hash = (Get-FileHash $jar -Algorithm SHA256).Hash.ToLower()
if ($hash -ne $PaperSha256) { throw "paper.jar hash mismatch ($hash); delete it and retry" }

# First start: copy our config, generate the RCON password
$secret = Join-Path $run 'rcon.secret'
if (-not (Test-Path $secret)) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [IO.File]::WriteAllText($secret, [Convert]::ToBase64String($bytes).Replace('+','A').Replace('/','B').TrimEnd('='))
}
$props = Join-Path $run 'server.properties'
if (-not (Test-Path $props)) {
    $pw = [IO.File]::ReadAllText($secret).Trim()
    $text = [IO.File]::ReadAllText((Join-Path $here 'config\server.properties')).Replace('__GENERATED__', $pw)
    [IO.File]::WriteAllText($props, $text)
}
$spigot = Join-Path $run 'spigot.yml'
if (-not (Test-Path $spigot)) { Copy-Item (Join-Path $here 'config\spigot.yml') $spigot }

# The EULA is the player's to accept; never written by this script.
$eula = Join-Path $run 'eula.txt'
if (-not (Test-Path $eula) -or -not (Select-String -Path $eula -Pattern '^eula=true' -Quiet)) {
    Write-Warning "Minecraft EULA not accepted. Read https://aka.ms/MinecraftEULA, then put 'eula=true' in $eula"
    exit 1
}

Push-Location $run
try {
    & $java -Xms1G -Xmx2G -XX:+UseG1GC -jar paper.jar --nogui
} finally {
    Pop-Location
}
