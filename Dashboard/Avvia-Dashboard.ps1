$dashboardRuntimeRoot = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies"
$dashboardNodeFolder = Join-Path $dashboardRuntimeRoot "node\bin"
$dashboardPnpm = Join-Path $dashboardRuntimeRoot "bin\fallback\pnpm.cmd"

if (-not (Test-Path -LiteralPath $dashboardPnpm)) {
    Write-Error "Runtime locale non trovato. Apri il progetto con Codex e riprova."
    exit 1
}

$env:Path = "$dashboardNodeFolder;$env:Path"
Set-Location -LiteralPath $PSScriptRoot
& $dashboardPnpm run dev
