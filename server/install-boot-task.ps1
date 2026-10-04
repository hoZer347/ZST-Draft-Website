# Boot-time task that runs start-server.ps1 before sign-in, so the sites survive a
# Windows Update restart (the Startup .vbs only fires at sign-in). S4U: no stored
# password, no network shares. Self-elevates; re-run to update.

$ErrorActionPreference = 'Stop'
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$me).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList `
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    exit
}

$start     = Join-Path $PSScriptRoot 'start-server.ps1'
$action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$start`"" `
    -WorkingDirectory $PSScriptRoot
$trigger   = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId $me.Name -LogonType S4U -RunLevel Limited
$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName 'DraftLeagueBoot' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
