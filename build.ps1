<#
.SYNOPSIS
  Publishes Dynamic Island and packages it into dist\DynamicIsland-Setup-<version>.exe.
.EXAMPLE
  .\build.ps1
  .\build.ps1 -Version 0.2.0
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DynamicIsland\DynamicIsland.csproj'

if (-not $Version) {
    $Version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText
}
Write-Host "Building Dynamic Island $Version" -ForegroundColor Cyan

$publish = Join-Path $root 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Self-contained single exe: users don't need to install .NET.
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

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
