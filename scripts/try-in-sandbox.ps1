<#
.SYNOPSIS
  Opens a disposable Windows Sandbox to try Dynamic Island exactly like a brand-new user would.
.DESCRIPTION
  The sandbox is a clean, throwaway copy of Windows: nothing done inside it touches this PC, and it
  is wiped when closed. It opens the GitHub Releases page so you download and install the real
  published installer. The local dist\ folder (your latest build) is also on the sandbox desktop,
  read-only, as "Local build".

  Requires Windows 10/11 Pro or Enterprise with the Windows Sandbox feature turned on.
.EXAMPLE
  .\scripts\try-in-sandbox.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandbox)) {
    throw "Windows Sandbox isn't available. Turn on 'Windows Sandbox' in 'Turn Windows features on or off' and restart."
}

$releases = 'https://github.com/nazimferragh/windows-dynamic-island/releases/latest'
$config = @"
<Configuration>
  <MemoryInMB>4096</MemoryInMB>
  <vGPU>Enable</vGPU>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$dist</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\Local build</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>cmd.exe /c start "" "$releases"</Command>
  </LogonCommand>
</Configuration>
"@

$wsb = Join-Path $env:TEMP 'DynamicIsland-Sandbox.wsb'
Set-Content -Path $wsb -Value $config -Encoding UTF8
Start-Process $wsb
Write-Host "Sandbox starting. Close its window to throw everything away." -ForegroundColor Green
