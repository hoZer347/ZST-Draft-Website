# Watchdog: keeps the local DraftLeague.Web dev server running.
#
# Runs as a logon scheduled task / Startup launcher (see the .vbs in the user
# Startup folder). Every few seconds it relaunches the server if it isn't up,
# unless the pause flag exists. During a rebuild the server must be stopped to
# unlock the exe, so the rebuild flow creates `.server-paused` before killing
# the server and deletes it after — while that file exists the watchdog stands
# down and relaunches nothing.
#
# Identity: on start it writes its own PID to `.watchdog.pid`, and every loop it
# overwrites `.watchdog.alive` with a timestamp. Manage the watchdog through
# those files — NEVER by string-matching command lines, because a management
# command that mentions this script's name matches (and would kill) itself.

$ErrorActionPreference = 'Continue'
$root  = $PSScriptRoot
# Run a RENAMED COPY (DraftLeagueLive.exe) from run/, NOT the build output. The
# VS Code C# Dev Kit hard-kills the running server so it can rebuild — and it
# targets it by PROCESS NAME ("DraftLeague.Web"), so a same-named copy in run/
# still got killed. DraftLeagueLive.exe is the same apphost renamed (it still
# loads DraftLeague.Web.dll by the DLL's embedded name), so its process name is
# "DraftLeagueLive" and the Dev Kit no longer matches it. Confirmed the deaths
# were TerminateProcess (crash logs end mid-request, no shutdown message).
# Promote a new build to live with deploy-run.ps1 (which re-creates the rename).
$exe   = Join-Path $root 'run\DraftLeagueLive.exe'
$pause = Join-Path $root '.server-paused'
$pidf  = Join-Path $root '.watchdog.pid'
$alive = Join-Path $root '.watchdog.alive'
$logf  = Join-Path $root 'keep-server-up.log'
# Server stdout/stderr, captured so we can see WHY it exits. Rotated to .prev on
# each relaunch, so right after a death the .prev files hold the dead run's final
# output: a graceful "Application is shutting down" (something asked it to stop),
# an exception (it crashed), or an abrupt cutoff mid-log (it was hard-killed).
$outlog  = Join-Path $root 'server.out.log'
$errlog  = Join-Path $root 'server.err.log'
$outprev = Join-Path $root 'server.out.prev.log'
$errprev = Join-Path $root 'server.err.prev.log'
# Public tunnel: a named Cloudflare tunnel exposes the local :5211 server (which
# serves both the web app and the API) at https://dev.loomhozer.ca, and the
# Showdown battle server (:8787) at https://showdown.loomhozer.ca. Config lives
# in %USERPROFILE%\.cloudflared\config.yml.
$cloudflared = 'C:\Program Files (x86)\cloudflared\cloudflared.exe'
# Self-hosted Showdown server (battle-server), for the teambuilder's custom format.
$node        = 'C:\Program Files\nodejs\node.exe'
$showdownDir = 'C:\Users\3hoze\Desktop\Pokemon Draft League\battle-server'

function Log($m) { "$(Get-Date -Format 'HH:mm:ss') $m" | Out-File -FilePath $logf -Append -Encoding utf8 }

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS        = 'http://localhost:5211'

"$PID" | Out-File -FilePath $pidf -Encoding ascii -NoNewline
Log "watchdog start (pid $PID); root=$root exe-exists=$(Test-Path $exe)"

# Enforce an always-awake power profile (AC / plugged-in) so the server keeps
# serving when we walk away. The system never sleeps or hibernates and disks
# stay spun up; only the monitor is allowed to power off (where most of the
# visible savings are anyway, and it doesn't touch the server). Done once at
# startup — the settings persist, so no need to reassert each loop.
try {
    & powercfg /change standby-timeout-ac 0    # never sleep
    & powercfg /change hibernate-timeout-ac 0  # never hibernate
    & powercfg /change disk-timeout-ac 0       # keep disk alive for DB/IO
    & powercfg /change monitor-timeout-ac 10   # screen off after 10 min
    Log "applied always-awake power profile (AC)"
} catch {
    Log "WARN: powercfg failed: $($_.Exception.Message)"
}

while ($true) {
    try {
        "$(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $alive -Encoding ascii -NoNewline
        if (-not (Test-Path $pause)) {
            $running = Get-Process -Name 'DraftLeagueLive' -ErrorAction SilentlyContinue
            if (-not $running -and (Test-Path $exe)) {
                # Preserve the previous (just-died) run's output before overwriting.
                if (Test-Path $outlog) { Move-Item -Force $outlog $outprev -ErrorAction SilentlyContinue }
                if (Test-Path $errlog) { Move-Item -Force $errlog $errprev -ErrorAction SilentlyContinue }
                Start-Process -FilePath $exe -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $outlog -RedirectStandardError $errlog
                Log "relaunched server"
            }
        }
        # Keep the Cloudflare tunnel up too — independent of the rebuild pause,
        # since the tunnel doesn't lock the build output.
        $cf = Get-Process -Name 'cloudflared' -ErrorAction SilentlyContinue
        if (-not $cf -and (Test-Path $cloudflared)) {
            Start-Process -FilePath $cloudflared -ArgumentList 'tunnel', 'run', 'loom-tunnel' -WindowStyle Hidden
            Log "relaunched cloudflared tunnel"
        }

        # Keep the Showdown battle server up (port 8787), checked by listener.
        $sd = Get-NetTCPConnection -LocalPort 8787 -State Listen -ErrorAction SilentlyContinue
        if (-not $sd -and (Test-Path $node)) {
            Start-Process -FilePath $node -ArgumentList 'scripts\showdown.js' -WorkingDirectory $showdownDir -WindowStyle Hidden
            Log "relaunched showdown server"
        }

        # Keep the self-hosted Showdown client static server up (port 8791),
        # served publicly at play.loomhozer.ca.
        $cl = Get-NetTCPConnection -LocalPort 8791 -State Listen -ErrorAction SilentlyContinue
        if (-not $cl -and (Test-Path $node)) {
            Start-Process -FilePath $node -ArgumentList 'scripts\serve-client.js' -WorkingDirectory $showdownDir -WindowStyle Hidden
            Log "relaunched client server"
        }
    } catch {
        Log "ERR: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 5
}
