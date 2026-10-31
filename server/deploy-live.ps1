# Takes the watchdog, the tunnel config and the Node servers live, and proves it.
# Restarts only what is stale, through DraftLeagueBoot since the stack runs in
# session 0, then checks every stamp against the code on disk and holds sockets
# through the tunnel. Exits nonzero unless all of that passes. The .NET server has
# its own deploy, deploy-run.ps1.
#
#   powershell -ExecutionPolicy Bypass -File server\deploy-live.ps1
#
# Refuses to restart the tunnel or :8787 during a live battle unless -Force.
param([switch]$Force, [int]$HoldSeconds = 300, [int]$TimeoutSec = 120)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'port-check.ps1')
. (Join-Path $root 'live-code.ps1')
$probe = Join-Path $battleDir 'scripts\live-probe.js'
$logf  = Join-Path $root 'keep-server-up.log'
$alive = Join-Path $root '.watchdog.alive'
$tunnelRequest = Join-Path $root '.tunnel-restart-requested'
$names = 'watchdog', 'tunnel', '8787', '8791'
$livePorts = 5211, 8787, 8791
$heartbeatLimitSec = 30

function Get-RelaunchCount {
    @(Select-String -Path $logf -Pattern 'relaunched' -ErrorAction SilentlyContinue).Count
}

function Get-LiveBattles {
    if (-not (Test-Listening 8787)) { return 0 }
    $battles = & node $probe battles
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the live battle count.' }
    return [int]$battles
}

function Assert-Current([string[]]$wanted) {
    $left = @($wanted | Where-Object { Get-StaleReason $_ })
    if ($left.Count -gt 0) { throw (($left | ForEach-Object { "$_ is still stale: $(Get-StaleReason $_)" }) -join '; ') }
}

function Wait-Current([string[]]$wanted) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline -and @($wanted | Where-Object { Get-StaleReason $_ }).Count -gt 0) { Start-Sleep -Seconds 2 }
    Assert-Current $wanted
}

try {
    $stale = @($names | Where-Object { Get-StaleReason $_ })
    foreach ($name in $stale) { Write-Host "stale: $name, $(Get-StaleReason $name)" }

    if (-not $Force -and ($stale -contains 'tunnel' -or $stale -contains '8787')) {
        $battles = Get-LiveBattles
        if ($battles -gt 0) { throw "$battles battle(s) are live, and restarting drops them. Nothing was restarted." }
    }

    if ($stale -contains 'watchdog' -or $stale -contains 'tunnel') {
        $busyUntil = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-ScheduledTask -TaskName 'DraftLeagueBoot').State -eq 'Running' -and (Get-Date) -lt $busyUntil) { Start-Sleep -Seconds 1 }
        if ($stale -contains 'tunnel') { Set-Content $tunnelRequest '' }
        try {
            Start-ScheduledTask -TaskName 'DraftLeagueBoot'
            Wait-Current 'watchdog', 'tunnel'
        } finally {
            Remove-Item $tunnelRequest -Force -ErrorAction SilentlyContinue
        }
    }

    $nodePorts = @($stale | Where-Object { $_ -in '8787', '8791' } | ForEach-Object { [int]$_ })
    if ($nodePorts.Count -gt 0) { & (Join-Path $root 'restart-node.ps1') -Port $nodePorts }

    Wait-Current $names
    foreach ($port in $livePorts) {
        if (-not (Test-Listening $port)) { throw ":$port is not answering." }
    }
    if (-not (Test-Path $alive) -or ((Get-Date) - (Get-Item $alive).LastWriteTime).TotalSeconds -gt $heartbeatLimitSec) {
        throw 'The watchdog has stopped its heartbeat.'
    }
    $null = Get-LiveBattles

    $relaunches = Get-RelaunchCount
    & node $probe hold $HoldSeconds
    if ($LASTEXITCODE -ne 0) { throw 'A socket through the tunnel closed during the hold.' }
    if ((Get-RelaunchCount) -ne $relaunches) { throw 'The watchdog relaunched something during the hold.' }
    Assert-Current $names

    foreach ($name in $names) {
        $stamp = Get-LiveStamp $name
        Write-Host "live: $name pid $($stamp.processId) since $($stamp.started) code $($stamp.hash.Substring(0, 12))"
    }
    exit 0
} catch {
    Write-Host "Deploy failed: $_" -ForegroundColor Red
    exit 1
}
