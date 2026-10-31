# Restart the Node servers (Showdown battle server :8787, client static server
# :8791) so edited battle-server scripts load; the watchdog relaunches them.
#
#   .\restart-node.ps1 8791          # or 8787, or 8791,8787
#
# When the boot task started the stack it runs in session 0, out of a normal
# shell's reach, so the restart is handed to that task (start-server.ps1 runs this
# script there when .node-restart-requested exists, and deletes it on success).
param([Parameter(Mandatory)][ValidateSet(8787, 8791)][int[]]$Port)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'port-check.ps1')
$request = Join-Path $PSScriptRoot '.node-restart-requested'
$handoffTimeoutSec = 90
$serverScripts = @{ 8787 = 'scripts\showdown.js'; 8791 = 'scripts\serve-client.js' }

function Get-ServerProcessId([int]$p)
{
    $serverScript = $serverScripts[$p]
    if (-not $serverScript) { throw "No server script is known for port $p." }
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
        Where-Object { $_.CommandLine -like "*$serverScript*" } |
        ForEach-Object { $_.ProcessId }
}

$down = @($Port | Where-Object { -not (Test-Listening $_) -and @(Get-ServerProcessId $_).Count -eq 0 })
if ($down.Count -gt 0 -and (Get-Process -Id $PID).SessionId -eq 0) { throw "Port(s) $($down -join ', ') are not running, so there is nothing to restart." }

$stopped = @()
$handoff = $false
foreach ($p in $Port) {
    $ids = @(Get-ServerProcessId $p)
    if ($ids.Count -eq 0) { $handoff = $true; continue }
    foreach ($id in $ids) {
        & taskkill /T /F /PID $id | Out-Null
        if ($LASTEXITCODE -eq 0) { $stopped += $id } else { $handoff = $true }
    }
}

$timeoutSec = $handoffTimeoutSec
if ($handoff) {
    if ((Get-Process -Id $PID).SessionId -eq 0) { throw "Can't find or stop the Node servers on port(s) $($Port -join ', ')." }
    Set-Content -Path $request -Value ($Port -join ',') -Encoding ascii
    # The task ignores a start while a run (say, a deploy's) is still going.
    $busyUntil = (Get-Date).AddSeconds($handoffTimeoutSec)
    while ((Get-ScheduledTask -TaskName 'DraftLeagueBoot').State -eq 'Running' -and (Get-Date) -lt $busyUntil) { Start-Sleep -Seconds 1 }
    try {
        Start-ScheduledTask -TaskName 'DraftLeagueBoot'
    } catch {
        Remove-Item $request -Force -ErrorAction SilentlyContinue
        throw "Can't stop the Node servers and can't start the DraftLeagueBoot task: $($_.Exception.Message)"
    }
    # The task's own wait has to end first.
    $timeoutSec = $handoffTimeoutSec * 2
}

$deadline = (Get-Date).AddSeconds($timeoutSec)
do {
    Start-Sleep -Seconds 1
    $waiting = ($handoff -and (Test-Path $request)) -or
        @($stopped | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -gt 0 -or
        @($Port | Where-Object { -not (Test-Listening $_) }).Count -gt 0
} while ($waiting -and (Get-Date) -lt $deadline)
if ($waiting) {
    if ($handoff) { Remove-Item $request -Force -ErrorAction SilentlyContinue }
    throw "Port(s) $($Port -join ', ') did not come back on a new process within ${timeoutSec}s."
}
Write-Host "Restarted port(s) $($Port -join ', ')."
