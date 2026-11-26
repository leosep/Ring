<#
.SYNOPSIS
    Publica RingControl para Windows y genera el instalador y el paquete portable.

.DESCRIPTION
    Produce en artifacts/:
      - RingControlSetup-<versión>.exe  (instalador: GUI + CLI/NVR)
      - RingControl-<versión>-win-x64.zip (versión portable)
      - SHA256SUMS.txt
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publishRoot = Join-Path $artifacts "publish\win-x64"
$guiDir = Join-Path $publishRoot "gui"
$cliDir = Join-Path $publishRoot "cli"
$setupDir = Join-Path $publishRoot "setup"
$staging = Join-Path $artifacts "staging\RingControl"
$payload = Join-Path $root "src\RingControl.Setup\payload.zip"
$portableZip = Join-Path $artifacts "RingControl-$Version-win-x64.zip"
$setupExe = Join-Path $artifacts "RingControlSetup-$Version.exe"

function Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') falló con código $LASTEXITCODE"
    }
}

if (-not (Test-Path (Join-Path $root "README.md"))) {
    throw "Falta README.md en la raíz del repositorio."
}

Step "Limpiando artefactos"
if (Test-Path $artifacts) {
    Remove-Item $artifacts -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Step "Publicando la aplicación de Windows (win-x64, self-contained)"
Invoke-Dotnet @(
    "publish", (Join-Path $root "src\RingControl.App\RingControl.App.csproj"),
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:Version=$Version",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $guiDir
)

Step "Publicando la CLI y el NVR (win-x64, single-file)"
Invoke-Dotnet @(
    "publish", (Join-Path $root "src\RingControl.Cli\RingControl.Cli.csproj"),
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:Version=$Version",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $cliDir
)

Step "Preparando el paquete"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item (Join-Path $guiDir "*") $staging -Recurse -Force

$stagingCli = Join-Path $staging "cli"
New-Item -ItemType Directory -Force -Path $stagingCli | Out-Null
Copy-Item (Join-Path $cliDir "*") $stagingCli -Recurse -Force

Copy-Item (Join-Path $root "README.md") $staging -Force
Copy-Item (Join-Path $root "assets\ringcontrol.ico") (Join-Path $staging "ringcontrol.ico") -Force
"RingControl $Version (win-x64)" | Set-Content (Join-Path $staging "VERSION.txt") -Encoding UTF8

Step "Creando el ZIP portable"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging,
    $portableZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

if (-not $SkipInstaller) {
    Step "Generando el instalador"
    Copy-Item $portableZip $payload -Force

    Invoke-Dotnet @(
        "publish", (Join-Path $root "src\RingControl.Setup\RingControl.Setup.csproj"),
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:Version=$Version",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $setupDir
    )

    $builtSetup = Join-Path $setupDir "RingControlSetup.exe"
    if (-not (Test-Path $builtSetup)) {
        throw "No se generó RingControlSetup.exe"
    }

    Copy-Item $builtSetup $setupExe -Force
    Remove-Item $payload -Force -ErrorAction SilentlyContinue
}

Step "Calculando sumas SHA-256"
$lines = Get-ChildItem $artifacts -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
$lines | Set-Content (Join-Path $artifacts "SHA256SUMS.txt") -Encoding ASCII

Step "Listo"
Write-Host ""
Get-ChildItem $artifacts -File | Select-Object Name, @{ Name = "MB"; Expression = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host "Instalador: $setupExe" -ForegroundColor Green
Write-Host "Portable:   $portableZip" -ForegroundColor Green
