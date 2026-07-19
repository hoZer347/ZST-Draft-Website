# Watchdog: keeps the local DraftLeague.Web dev server running.
#
# Runs as a logon scheduled task (see register-watchdog.ps1). Every few seconds
# it relaunches the server if it isn't up. During a rebuild the server must be
# stopped to unlock the exe, so the watchdog honours a pause flag: while
# `.server-paused` exists next to this script it stands down and relaunches
# nothing. The rebuild flow creates that file before killing the server and
# deletes it after relaunching.

$ErrorActionPreference = 'Continue'
$root  = $PSScriptRoot
$exe   = Join-Path $root 'bin\Debug\net9.0\DraftLeague.Web.exe'
$pause = Join-Path $root '.server-paused'
$logf  = Join-Path $root 'keep-server-up.log'

function Log($m) { "$(Get-Date -Format 'HH:mm:ss') $m" | Out-File -FilePath $logf -Append -Encoding utf8 }

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS        = 'http://localhost:5211'

Log "watchdog start; root=$root exe-exists=$(Test-Path $exe)"

while ($true) {
    try {
        if (-not (Test-Path $pause)) {
            $running = Get-Process -Name 'DraftLeague.Web' -ErrorAction SilentlyContinue
            if (-not $running -and (Test-Path $exe)) {
                Start-Process -FilePath $exe -WorkingDirectory $root -WindowStyle Hidden
                Log "relaunched server"
            }
        }
    } catch {
        Log "ERR: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 5
}
