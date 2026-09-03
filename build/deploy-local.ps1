# Build G-Loom and copy the resulting .gha into the local Rhino 8 Grasshopper Libraries folder.
# Restart Rhino after running for the new build to load.
#
# Usage:  powershell -ExecutionPolicy Bypass -File build\deploy-local.ps1
#   or:   pwsh build\deploy-local.ps1
# Override defaults via env vars:  $env:DOTNET=...  $env:CONFIG=Debug

$ErrorActionPreference = 'Stop'

$Dotnet = if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }
$Config = if ($env:CONFIG) { $env:CONFIG } else { 'Release' }

$Root      = Resolve-Path (Join-Path $PSScriptRoot '..')
$Project   = Join-Path $Root 'src\GLoom.Plugin\GLoom.Plugin.csproj'
$BuildDir  = Join-Path $Root "src\GLoom.Plugin\bin\$Config\net7.0-windows"
$Output    = Join-Path $BuildDir 'GLoom.gha'

# Windows Rhino 8 stores third-party Grasshopper libraries under %APPDATA%\Grasshopper\Libraries
# (different layout from macOS, where they live inside the Rhino plug-in folder).
$LibrariesDir = Join-Path $env:APPDATA 'Grasshopper\Libraries'
$TargetDir    = Join-Path $LibrariesDir 'G-Loom'

if (-not (Get-Command $Dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "[deploy-local] '$Dotnet' not found on PATH. Install the .NET 7+ SDK or set `$env:DOTNET."
}

if (-not (Test-Path $LibrariesDir)) {
    Write-Error @"
[deploy-local] Grasshopper Libraries folder not found:
  $LibrariesDir
[deploy-local] Open Rhino 8 + Grasshopper at least once first.
"@
}

Write-Host "[deploy-local] Building $Project ($Config)..."
& $Dotnet build $Project -c $Config --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "[deploy-local] dotnet build failed (exit $LASTEXITCODE)." }

if (-not (Test-Path $Output)) {
    Write-Error "[deploy-local] Build did not produce $Output"
}

# Wipe and copy. We shell out to the system `git` CLI (no LibGit2Sharp), so the
# .gha is the only thing that needs to ship.
if (Test-Path $TargetDir) { Remove-Item -Recurse -Force $TargetDir }
New-Item -ItemType Directory -Path $TargetDir | Out-Null
Copy-Item -Path $Output -Destination $TargetDir

# Remove any stray GLoom.gha elsewhere in the Grasshopper tree (e.g. a flat
# Libraries\GLoom.gha left by an older deploy layout). Two same-identity
# assemblies load in conflict and the host keeps whichever loads first, so a
# stale one will shadow the new build. Keep only the one we just deployed.
$Canonical = Join-Path $TargetDir 'GLoom.gha'
Get-ChildItem -Path (Join-Path $env:APPDATA 'Grasshopper') -Recurse -Filter 'GLoom.gha' -File -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -ne $Canonical } |
  ForEach-Object {
    try {
      Remove-Item -LiteralPath $_.FullName -Force
      Write-Host "[deploy-local] Removed stray assembly: $($_.FullName)"
    } catch {
      Write-Host "[deploy-local] WARNING: stray not removed (locked by Rhino?): $($_.FullName)"
    }
  }

Write-Host "[deploy-local] Deployed to: $TargetDir"
Get-ChildItem $TargetDir | ForEach-Object { Write-Host "[deploy-local]   $($_.Name)" }
Write-Host '[deploy-local] Restart Rhino to load the new build.'
