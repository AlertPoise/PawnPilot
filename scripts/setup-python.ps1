param([string]$Python = 'python', [string]$Uv = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:UV_CACHE_DIR = Join-Path $projectRoot '.uv-cache'
$env:UV_PYTHON_DOWNLOADS = 'never'
if (-not $Uv) {
    $localUv = Join-Path $projectRoot '.tools/uv/bin/uv.exe'
    if (Test-Path -LiteralPath $localUv) { $Uv = $localUv }
    elseif (Get-Command uv -ErrorAction SilentlyContinue) { $Uv = (Get-Command uv).Source }
    else { throw 'Install uv 0.13.6 first, or provide -Uv path. No global install is performed by this script.' }
}
& $Uv sync --frozen --no-config --python $Python
if ($LASTEXITCODE -ne 0) { throw 'Locked Python environment setup failed' }
& (Join-Path $projectRoot '.venv/Scripts/python.exe') -c "import importlib.metadata as m; print('MCP SDK', m.version('mcp'))"
