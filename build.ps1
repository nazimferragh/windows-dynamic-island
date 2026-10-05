<#
.SYNOPSIS
  Publishes Dynamic Island and packages it into dist\DynamicIsland-Setup-<version>.exe.
.DESCRIPTION
  Stages (for code signing in CI, the app exe is signed between them):
    Publish    build the self-contained app into publish\
    Installer  package publish\ into the installer
    All        both (default)
.EXAMPLE
  .\build.ps1
  .\build.ps1 -Version 0.3.0 -Stage Publish
#>
param(
    [string]$Version,
    [ValidateSet('All', 'Publish', 'Installer')][string]$Stage = 'All'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DynamicIsland\DynamicIsland.csproj'
$publish = Join-Path $root 'publish'

if (-not $Version) {
    $Version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText
}
Write-Host "Dynamic Island $Version ($Stage)" -ForegroundColor Cyan

if ($Stage -in 'All', 'Publish') {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    # Self-contained single exe: users don't need to install .NET. Not compressed inside: a
    # compressed bundle unpacks every assembly into private RAM at start, an uncompressed one is
    # mapped straight from the file (shared, pageable). The installer compresses it anyway, so the
    # download stays the same size. ReadyToRun: precompiled, so less JIT work (CPU) at start.
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:DebugType=none `
        -p:Version=$Version `
        -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

if ($Stage -in 'All', 'Installer') {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source }
    if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

    & $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\DynamicIsland.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    Write-Host "Done: dist\DynamicIsland-Setup-$Version.exe" -ForegroundColor Green
}
