# Boot the whole local stack by hand (the same thing the boot task and logon .vbs do).
#
# Nothing here starts the server directly: the watchdog (keep-server-up.ps1) owns
# that, plus cloudflared and the two Node servers, and it relaunches anything that
# dies within ~20s. So this script only has to (1) clear a stale pause flag, (2)
# make sure a watchdog is running, and (3) wait for :5211 to answer.
#
# Safe to run twice. A live watchdog is left alone unless its stamp in .live no
# longer matches its code, and never gets a second one beside it, because two would
# race to relaunch the same processes. The tunnel restarts only on deploy-live.ps1's
# .tunnel-restart-requested flag, which that script drops after its battle check.

$ErrorActionPreference = 'Continue'
$root  = $PSScriptRoot
$pause = Join-Path $root '.server-paused'
$pidf  = Join-Path $root '.watchdog.pid'
$alive = Join-Path $root '.watchdog.alive'
$watch = Join-Path $root 'keep-server-up.ps1'
. (Join-Path $root 'port-check.ps1')
. (Join-Path $root 'live-code.ps1')

Write-Host "Draft League: booting the local stack" -ForegroundColor Cyan
Write-Host "  root: $root"

# A leftover .server-paused (from a rebuild that died mid-flight) silently keeps
# the server down forever, the watchdog stands down while it exists.
if (Test-Path $pause) {
    Remove-Item $pause -Force
    Write-Host "  cleared a stale .server-paused flag" -ForegroundColor Yellow
}

# restart-node.ps1 hands its restart here the same way.
$nodeRestart = Join-Path $root '.node-restart-requested'
if (Test-Path $nodeRestart) {
    try {
        & (Join-Path $root 'restart-node.ps1') -Port ([int[]]((Get-Content $nodeRestart -Raw).Trim() -split ','))
        Remove-Item $nodeRestart -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "  requested Node restart failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Is a watchdog already up? Identify it by its own PID file, never by string
# matching command lines (this script's name would match itself).
$running = $false
if (Test-Path $pidf) {
    $wpid = (Get-Content $pidf -Raw).Trim()
    $proc = Get-Process -Id $wpid -ErrorAction SilentlyContinue
    # The PID is reused after a reboot, so also require a recent heartbeat.
    if ($proc -and (Test-Path $alive) -and ((Get-Date) - (Get-Item $alive).LastWriteTime).TotalMinutes -lt 2) {
        $running = -not (Test-StampReused 'watchdog')
        if ($running -and (Get-StaleReason 'watchdog')) {
            try {
                Stop-Process -Id $wpid -Force -ErrorAction Stop
                $running = $false
                Write-Host "  stopped a stale watchdog (pid $wpid)" -ForegroundColor Yellow
            } catch {
                Write-Host "  stale watchdog (pid $wpid) is out of reach; Start-ScheduledTask DraftLeagueBoot replaces it" -ForegroundColor Yellow
            }
        }
        if ($running) { Write-Host "  watchdog already running (pid $wpid)" -ForegroundColor Green }
    }
}

if (-not $running) {
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList `
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$watch`""
    Write-Host "  started the watchdog" -ForegroundColor Green
}

$tunnelRestart = Join-Path $root '.tunnel-restart-requested'
if (Test-Path $tunnelRestart) {
    try {
        Get-Process -Name 'cloudflared' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction Stop
        Write-Host "  stopped the tunnel for the watchdog to relaunch" -ForegroundColor Yellow
    } catch {
        Write-Host "  requested tunnel restart failed: $($_.Exception.Message)" -ForegroundColor Red
    } finally {
        Remove-Item $tunnelRestart -Force -ErrorAction SilentlyContinue
    }
}

# The watchdog launches on a 5s loop, so give it a couple of cycles plus the
# server's own startup (EF model build, first request JIT).
Write-Host -NoNewline "  waiting for http://localhost:5211 "
$code = $null
foreach ($i in 1..40) {
    try {
        $code = (Invoke-WebRequest 'http://localhost:5211/' -UseBasicParsing -TimeoutSec 3).StatusCode
    } catch {
        $code = $null
    }
    if ($code -eq 200) { break }
    Write-Host -NoNewline '.'
    Start-Sleep -Seconds 2
}
Write-Host ''

function Port($port, $label) {
    if (Test-Listening $port) { Write-Host "  [up]   $label (:$port)" -ForegroundColor Green }
    else     { Write-Host "  [down] $label (:$port)" -ForegroundColor Red }
}

if ($code -eq 200) {
    Write-Host "Server is up." -ForegroundColor Green
} else {
    Write-Host "Server did NOT come up. Check server\keep-server-up.log and server\server.err.log." -ForegroundColor Red
    Write-Host "If run\DraftLeagueLive.exe is missing, promote a build: powershell -File server\deploy-run.ps1" -ForegroundColor Red
}

Port 5211 'web + API'
Port 8787 'Showdown battle server'
Port 8791 'Showdown client / Teambuilder'
if (Get-Process -Name 'cloudflared' -ErrorAction SilentlyContinue) {
    Write-Host "  [up]   cloudflare tunnel (dev/zst/play.loomhozer.ca)" -ForegroundColor Green
} else {
    Write-Host "  [down] cloudflare tunnel" -ForegroundColor Red
}

Write-Host ''
Write-Host "Local:  http://localhost:5211"
Write-Host "Public: https://dev.loomhozer.ca"
