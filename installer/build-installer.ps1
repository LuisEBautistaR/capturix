# Builds artifacts\installer\CapturixSetup-<version>.exe:
#   1. publishes Capturix.exe and Capturix.Editor.exe self-contained for win-x64 (no .NET needed on the target PC)
#   2. compiles installer\Capturix.iss with Inno Setup 6
# Usage: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Capturix $version"

$publish = Join-Path $root 'artifacts\publish\Capturix'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($project in 'src\Capturix.Capture\Capturix.Capture.csproj', 'src\Capturix.Editor\Capturix.Editor.csproj') {
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló: $project" }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'No se encontró Inno Setup 6 (winget install JRSoftware.InnoSetup)' }

& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'Capturix.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC falló' }

$setup = Join-Path $root "artifacts\installer\CapturixSetup-$version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  CapturixSetup-$version.exe" | Set-Content "$setup.sha256" -Encoding ascii
Write-Host ("Instalador: {0} ({1:N1} MB)`nSHA-256: {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $hash)
