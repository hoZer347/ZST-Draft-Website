# Runs every test suite in the repo and prints one summary.
#
#   .\tools\run-tests.ps1                 all suites
#   .\tools\run-tests.ps1 -Only server    .NET integration tests
#   .\tools\run-tests.ps1 -Only web       front-end (node --test)
#   .\tools\run-tests.ps1 -Only battle    battle-server (node --test)
#
# Exits non-zero if anything failed, so it can gate a commit.

param(
    [ValidateSet('all', 'server', 'web', 'battle')]
    [string]$Only = 'all'
)

$repo       = Split-Path $PSScriptRoot -Parent
$serverTest = Join-Path $repo 'server.Tests'
$webDir     = Join-Path $repo 'web'
$battleDir  = Join-Path $repo 'battle-server'

$results = @()

function Show($name, $ok, $detail) {
    $script:results += [pscustomobject]@{ Suite = $name; Passed = $ok; Detail = $detail }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    $mark   = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0,-8} {1,-10} {2}" -f $mark, $name, $detail) -ForegroundColor $colour
}

# ── server (.NET xUnit) ────────────────────────────────────────────────────
if ($Only -in @('all', 'server')) {
    Write-Host "`nServer (xUnit integration)" -ForegroundColor Cyan

    # A running server holds DraftLeague.Web.exe open and the build fails with a
    # file lock rather than anything meaningful. Stop it; it's a dev instance.
    $running = Get-Process DraftLeague.Web -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host '  stopping running dev server (holds the build output open)' -ForegroundColor DarkYellow
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }

    Push-Location $serverTest
    $out = & dotnet test --nologo -v q 2>&1 | Out-String
    $code = $LASTEXITCODE
    Pop-Location

    $line = ($out -split "`n" | Where-Object { $_ -match 'Passed!|Failed!|error' } | Select-Object -First 1)
    if ($line) { $detail = $line.Trim() } else { $detail = "exit $code" }
    Show 'server' ($code -eq 0) $detail
    if ($code -ne 0) { Write-Host $out -ForegroundColor DarkGray }
}

# ── web (node --test) ───────────────────────────────────────────────────────
if ($Only -in @('all', 'web')) {
    Write-Host "`nWeb (node --test)" -ForegroundColor Cyan

    Push-Location $webDir
    $out = & npm test 2>&1 | Out-String
    $code = $LASTEXITCODE
    Pop-Location

    $line = ($out -split "`n" | Where-Object { $_ -match '# pass|# fail|pass \d|fail \d' } | Select-Object -First 1)
    if ($line) { $detail = $line.Trim() } else { $detail = "exit $code" }
    Show 'web' ($code -eq 0) $detail
    if ($code -ne 0) { Write-Host $out -ForegroundColor DarkGray }
}

# ── battle-server (node --test) ─────────────────────────────────────────────
if ($Only -in @('all', 'battle')) {
    Write-Host "`nBattle server (node --test)" -ForegroundColor Cyan

    Push-Location $battleDir
    $out = & npm test 2>&1 | Out-String
    $code = $LASTEXITCODE
    Pop-Location

    $line = ($out -split "`n" | Where-Object { $_ -match '# pass|# fail|pass \d|fail \d' } | Select-Object -First 1)
    if ($line) { $detail = $line.Trim() } else { $detail = "exit $code" }
    Show 'battle' ($code -eq 0) $detail
    if ($code -ne 0) { Write-Host $out -ForegroundColor DarkGray }
}

# ── summary ──────────────────────────────────────────────────────────────
$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "All suites passed ($($results.Count))" -ForegroundColor Green
    exit 0
}
Write-Host "$($failed.Count) of $($results.Count) suite(s) failed: $($failed.Suite -join ', ')" -ForegroundColor Red
exit 1
