# Promote the latest build (bin\Debug\net9.0) to the live copy (run\) the
# watchdog runs. Use this after changing server code + building - the watchdog
# runs run\ (not bin\Debug) so the VS Code C# Dev Kit's rebuilds can't kill the
# live server, but that also means code changes don't go live until you copy them
# here.
#
#   1. build the server (dotnet build, or let the IDE build)
#   2. .\deploy-run.ps1
#
# Uses the pause flag so the watchdog stands down while run\ is unlocked + copied.
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$src   = Join-Path $root 'bin\Debug\net9.0'
$dst   = Join-Path $root 'run'
$pause = Join-Path $root '.server-paused'

if (-not (Test-Path (Join-Path $src 'DraftLeague.Web.dll'))) {
    Write-Error "No build at $src - run 'dotnet build' first."; exit 1
}

New-Item -ItemType File -Path $pause -Force | Out-Null   # watchdog stands down
try {
    # The live server runs as DraftLeagueLive.exe (renamed so the Dev Kit doesn't
    # kill it by name — see keep-server-up.ps1). Stop it to unlock run\.
    Get-Process DraftLeagueLive -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2                                 # let run\ file locks release
    robocopy $src $dst /MIR /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    # /MIR mirrors bin\Debug, which has no DraftLeagueLive.exe — re-create the
    # renamed apphost (a byte copy of the exe; it loads DraftLeague.Web.dll by the
    # DLL's embedded name, so the rename is purely the process name).
    Copy-Item (Join-Path $dst 'DraftLeague.Web.exe') (Join-Path $dst 'DraftLeagueLive.exe') -Force
    Write-Host "Promoted bin\Debug -> run\ (DraftLeagueLive.exe)."
}
finally {
    Remove-Item $pause -Force -ErrorAction SilentlyContinue  # watchdog relaunches run\
}
Write-Host "Watchdog will relaunch the live server from run\ within ~5s."
