param([string]$Configuration = 'Release', [switch]$M2AProbe)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
dotnet build src/PawnPilot.Probe/PawnPilot.Probe.csproj -c $Configuration --nologo "-p:M2AReadOnlyProbe=$($M2AProbe.IsPresent.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
# 仅打包到项目内；不执行游戏目录安装。
$package = Join-Path $projectRoot $(if ($M2AProbe) { 'artifacts/m2a/package/pawnpilot_m0' } else { 'artifacts/package/pawnpilot_m0' })
New-Item -ItemType Directory -Path $package -Force | Out-Null
Copy-Item -LiteralPath "src/PawnPilot.Probe/bin/$Configuration/netstandard2.1/PawnPilot.Probe.dll" -Destination $package
Copy-Item -LiteralPath 'src/PawnPilot.Probe/manifest.xml' -Destination $package
Write-Output "Package prepared: $package (DLL + manifest; no auto-install)"
