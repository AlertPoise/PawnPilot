param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [ValidateSet('Install','Remove')][string]$Action = 'Install',
    [switch]$Apply,
    [switch]$M2AProbe
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$resolvedGame = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $resolvedGame 'Probably Stolen.exe'))) {
    throw 'GameRoot must contain Probably Stolen.exe'
}
if (Get-Process -Name 'Probably Stolen' -ErrorAction SilentlyContinue) {
    throw 'Exit the game normally before installing or removing the bridge.'
}
$modsRoot = [IO.Path]::GetFullPath((Join-Path $resolvedGame 'Mods'))
$target = [IO.Path]::GetFullPath((Join-Path $modsRoot 'pawnpilot_m0'))
if ([IO.Path]::GetDirectoryName($target) -ne $modsRoot -or [IO.Path]::GetFileName($target) -ne 'pawnpilot_m0') {
    throw 'Unexpected installation target'
}
foreach ($path in @($resolvedGame, $modsRoot, $target)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Reparse point installation paths are not supported'
    }
}
$files = @('PawnPilot.Probe.dll', 'manifest.xml', 'session.json')
$receipt = Join-Path $projectRoot $(if ($M2AProbe) { 'artifacts/m2a/install-receipt.json' } else { 'artifacts/m1-install-receipt.json' })
if ($Action -eq 'Install') {
    if (Test-Path -LiteralPath $target) { throw 'Target already exists; inspect and remove only this experiment before reinstalling.' }
    $package = Join-Path $projectRoot $(if ($M2AProbe) { 'artifacts/m2a/package/pawnpilot_m0' } else { 'artifacts/package/pawnpilot_m0' })
    $sources = @((Join-Path $package $files[0]), (Join-Path $package $files[1]), (Join-Path $projectRoot 'artifacts/session.json'))
    foreach ($source in $sources) { if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source: $source" } }
    Write-Output "Install exactly DLL, manifest and local session config into $target"
    if (-not $Apply) { Write-Output 'Dry run only. Obtain approval for game-directory writes before -Apply.'; return }
    New-Item -ItemType Directory -Path $target | Out-Null
    for ($i = 0; $i -lt $files.Count; $i++) { Copy-Item -LiteralPath $sources[$i] -Destination (Join-Path $target $files[$i]) }
    $hashes = @{}
    foreach ($name in $files) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $target $name) -Algorithm SHA256).Hash }
    @{target=$target; hashes=$hashes} | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
} else {
    if (-not (Test-Path -LiteralPath $target)) { Write-Output 'Mod already absent.'; return }
    if (-not (Test-Path -LiteralPath $receipt)) { throw 'Missing installation receipt; inspect manually.' }
    $record = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($record.target -ne $target) { throw 'Receipt target mismatch' }
    $items = @(Get-ChildItem -LiteralPath $target -Force)
    if ($items.Count -ne 3) { throw 'Unexpected mod directory contents; refusing removal.' }
    foreach ($item in $items) {
        if ($item.PSIsContainer -or $item.Name -notin $files -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected mod file' }
        if ((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ne $record.hashes.($item.Name)) { throw 'Installed file changed; refusing removal.' }
    }
    Write-Output "Remove only the three receipt-matched files and empty directory $target"
    if (-not $Apply) { Write-Output 'Dry run only. Disable this Mod in the game before approved cleanup.'; return }
    foreach ($name in $files) { Remove-Item -LiteralPath (Join-Path $target $name) }
    Remove-Item -LiteralPath $target
}
