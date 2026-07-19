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
$exe   = Join-Path $root 'bin\Debug\net9.0\DraftLeague.Web.exe'
$pause = Join-Path $root '.server-paused'
$pidf  = Join-Path $root '.watchdog.pid'
$alive = Join-Path $root '.watchdog.alive'
$logf  = Join-Path $root 'keep-server-up.log'
# Public tunnel: ngrok exposes the local :5211 server on a fixed reserved domain
# (endpoint defined in %LOCALAPPDATA%\ngrok\ngrok.yml) so the Netlify site can
# reach this machine's API over the internet.
$ngrok = 'C:\Users\3hoze\ngrok\ngrok.exe'

function Log($m) { "$(Get-Date -Format 'HH:mm:ss') $m" | Out-File -FilePath $logf -Append -Encoding utf8 }

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS        = 'http://localhost:5211'

"$PID" | Out-File -FilePath $pidf -Encoding ascii -NoNewline
Log "watchdog start (pid $PID); root=$root exe-exists=$(Test-Path $exe)"

while ($true) {
    try {
        "$(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $alive -Encoding ascii -NoNewline
        if (-not (Test-Path $pause)) {
            $running = Get-Process -Name 'DraftLeague.Web' -ErrorAction SilentlyContinue
            if (-not $running -and (Test-Path $exe)) {
                Start-Process -FilePath $exe -WorkingDirectory $root -WindowStyle Hidden
                Log "relaunched server"
            }
        }
        # Keep the ngrok tunnel up too — independent of the rebuild pause, since
        # the tunnel doesn't lock the build output.
        $ng = Get-Process -Name 'ngrok' -ErrorAction SilentlyContinue
        if (-not $ng -and (Test-Path $ngrok)) {
            Start-Process -FilePath $ngrok -ArgumentList 'start', '--all' -WindowStyle Hidden
            Log "relaunched ngrok"
        }
    } catch {
        Log "ERR: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 5
}
