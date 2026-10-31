# Watchdog: keeps the local DraftLeague.Web dev server running.
#
# Launched at boot by the DraftLeagueBoot task (install-boot-task.ps1) and at
# sign-in by the Startup-folder .vbs, both via start-server.ps1. Every few
# seconds it relaunches the server if it isn't up, unless the pause flag exists. During a rebuild the server must be stopped to
# unlock the exe, so the rebuild flow creates `.server-paused` before killing
# the server and deletes it after, while that file exists the watchdog stands
# down and relaunches nothing.
#
# Identity: on start it writes its own PID to `.watchdog.pid`, and every loop it
# overwrites `.watchdog.alive` with a timestamp. Manage the watchdog through
# those files, NEVER by string-matching command lines, because a management
# command that mentions this script's name matches (and would kill) itself.

$ErrorActionPreference = 'Continue'
$root  = $PSScriptRoot
# Run a RENAMED COPY (DraftLeagueLive.exe) from run/, NOT the build output. The
# VS Code C# Dev Kit hard-kills the running server so it can rebuild, and it
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
# One pair per launch: showdown.js's child inherits stdio, so an orphan could lock a fixed name and block the relaunch.
$showdownLogs    = Join-Path $root 'showdown-logs'
$showdownLogKeep = 40
# Unrelated project, same box and same tunnel: the payments page on :8080,
# public at justfuckingpayme.com. Kept alive here so there is one watchdog.
$payDir      = 'C:\Users\3hoze\pay-page'
$payEnv      = Join-Path $payDir '.env'
# Unrelated project, same box and same tunnel: the personal site on :8082,
# public at loomhozer.ca / www.loomhozer.ca. Serves its prebuilt dist/, so a
# content change needs `npm run build` in that repo (no restart required).
$siteDir     = 'C:\Users\3hoze\Desktop\Website'
$siteScript  = Join-Path $siteDir 'serve-site.cjs'

function Log($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" | Out-File -FilePath $logf -Append -Encoding utf8 }

$missLimit = 3
$launches = @{}
$misses = @{}
. (Join-Path $root 'port-check.ps1')
. (Join-Path $root 'live-code.ps1')

function Start-Stamped([string]$name, [scriptblock]$launch) {
    $hash = ''
    try { $hash = Get-CodeHash $name } catch { Log "ERR: could not hash ${name}: $($_.Exception.Message)" }
    $process = & $launch
    try { Write-LiveStamp $name $process.Id $hash } catch { Log "ERR: could not stamp ${name}: $($_.Exception.Message)" }
    $process
}

function Keep-Listening([int]$port, [string]$name, [scriptblock]$launch) {
    $last = $launches[$port]
    if (Test-Listening $port) {
        $misses[$port] = 0
        return $true
    }
    $misses[$port] = [int]$misses[$port] + 1
    if ($misses[$port] -lt $missLimit) { return $false }
    if ($last -and -not $last.HasExited) {
        if ($misses[$port] -eq $missLimit) { Log "$name (pid $($last.Id)) is alive but not answering on :$port, left running" }
        return $false
    }
    $misses[$port] = 0
    $launches[$port] = & $launch
    Log "relaunched $name"
    return $false
}

function Start-Showdown {
    $null = New-Item -ItemType Directory -Force -Path $showdownLogs
    $launchedAt = Get-Date -Format 'yyyyMMdd-HHmmss'
    $process = Start-Process -FilePath $node -ArgumentList 'scripts\showdown.js' -WorkingDirectory $showdownDir -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $showdownLogs "$launchedAt.out.log") `
        -RedirectStandardError  (Join-Path $showdownLogs "$launchedAt.err.log")
    Get-ChildItem -Path $showdownLogs -Filter '*.log' | Sort-Object Name -Descending |
        Select-Object -Skip $showdownLogKeep | Remove-Item -Force -ErrorAction SilentlyContinue
    $process
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS        = 'http://localhost:5211'

"$PID" | Out-File -FilePath $pidf -Encoding ascii -NoNewline
Write-LiveStamp 'watchdog' $PID (Get-CodeHash 'watchdog')
Log "watchdog start (pid $PID); root=$root exe-exists=$(Test-Path $exe)"

# Enforce an always-awake power profile (AC / plugged-in) so the server keeps
# serving when we walk away. The system never sleeps or hibernates and disks
# stay spun up; only the monitor is allowed to power off (where most of the
# visible savings are anyway, and it doesn't touch the server). Done once at
# startup, the settings persist, so no need to reassert each loop.
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
        # Keep the Cloudflare tunnel up too, independent of the rebuild pause,
        # since the tunnel doesn't lock the build output.
        $cf = Get-Process -Name 'cloudflared' -ErrorAction SilentlyContinue
        if (-not $cf -and (Test-Path $cloudflared)) {
            $null = Start-Stamped 'tunnel' {
                Start-Process -FilePath $cloudflared -ArgumentList 'tunnel', 'run', 'loom-tunnel' -WindowStyle Hidden -PassThru
            }
            Log "relaunched cloudflared tunnel"
        }

        # Keep the Showdown battle server up (port 8787), checked by listener.
        if (Test-Path $node) {
            $null = Keep-Listening 8787 'showdown server' { Start-Stamped '8787' { Start-Showdown } }
        }

        # Keep the self-hosted Showdown client static server up (port 8791),
        # served publicly at play.loomhozer.ca.
        if (Test-Path $node) {
            $null = Keep-Listening 8791 'client server' {
                Start-Stamped '8791' {
                    Start-Process -FilePath $node -ArgumentList 'scripts\serve-client.js' -WorkingDirectory $showdownDir -WindowStyle Hidden -PassThru
                }
            }
        }

        # Keep the pay-page server up (port 8080), a separate project served
        # publicly through the same tunnel at justfuckingpayme.com. It reads
        # .env once at start, so a config change needs the process killed.
        # Its subscription scheduler is why this matters: if the process dies
        # overnight, recurring charges silently stop firing and nobody gets an
        # error saying so.
        if ((Test-Path $node) -and (Test-Path $payEnv)) {
            $null = Keep-Listening 8080 'pay-page server' {
                Start-Process -FilePath $node -ArgumentList "--env-file=$payEnv", 'server/index.js' `
                    -WorkingDirectory $payDir -WindowStyle Hidden -PassThru `
                    -RedirectStandardOutput (Join-Path $payDir 'server.out.log') `
                    -RedirectStandardError  (Join-Path $payDir 'server.err.log')
            }
        }
        # Keep the personal-site static server up (port 8082), a separate
        # project served through the same tunnel at loomhozer.ca.
        if ((Test-Path $node) -and (Test-Path $siteScript)) {
            $null = Keep-Listening 8082 'site server' {
                Start-Process -FilePath $node -ArgumentList 'serve-site.cjs' `
                    -WorkingDirectory $siteDir -WindowStyle Hidden -PassThru `
                    -RedirectStandardOutput (Join-Path $siteDir 'server.out.log') `
                    -RedirectStandardError  (Join-Path $siteDir 'server.err.log')
            }
        }
    } catch {
        Log "ERR: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 5
}
