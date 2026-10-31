# What each live process was started from. Session-0 command lines can't be read
# from a normal shell, so keep-server-up.ps1 stamps every launch with a hash of the
# code it launched, and deploy-live.ps1 compares that with what is on disk.
$liveDir      = Join-Path $PSScriptRoot '.live'
$battleDir    = Join-Path (Split-Path $PSScriptRoot) 'battle-server'
$tunnelConfig = Join-Path $env:USERPROFILE '.cloudflared\config.yml'
$showdownFiles = 'scripts\showdown.js', 'lib\engine.js', 'lib\showdown-config.js', 'client\caches\pokemon-showdown\dist\sim\battle.js'
$clientFiles   = 'scripts\serve-client.js', 'lib\client-shell.js', 'lib\seed-teams.js', 'lib\roster-teambuilder.js',
    'lib\roster-tera.js', 'lib\auto-login.js', 'showdown-config\custom-megas.js'

function Get-LiveSources([string]$name) {
    switch ($name) {
        'watchdog' { foreach ($file in 'keep-server-up.ps1', 'port-check.ps1', 'live-code.ps1') { Get-Item (Join-Path $PSScriptRoot $file) } }
        'tunnel'   { Get-Item $tunnelConfig }
        '8787' {
            foreach ($file in $showdownFiles) { Get-Item (Join-Path $battleDir $file) }
            Get-ChildItem (Join-Path $battleDir 'showdown-config') -Filter '*.js' -Recurse
        }
        '8791'     { foreach ($file in $clientFiles) { Get-Item (Join-Path $battleDir $file) } }
        default    { throw "No live process is called $name." }
    }
}

function Get-CodeHash([string]$name) {
    $hashes = Get-LiveSources $name | Sort-Object FullName | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($hashes -join ''))) -replace '-', ''
    } finally {
        $sha.Dispose()
    }
}

function Get-LiveStamp([string]$name) {
    $path = Join-Path $liveDir "$name.json"
    if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json }
}

function Get-LiveProcess($processId) {
    Get-CimInstance Win32_Process -Filter "ProcessId=$processId"
}

function Write-LiveStamp([string]$name, $processId, [string]$hash) {
    $null = New-Item -ItemType Directory -Path $liveDir -Force
    $started = (Get-LiveProcess $processId).CreationDate
    $stamp = @{ processId = $processId; hash = $hash; started = $started.ToString('o') }
    $stamp | ConvertTo-Json | Set-Content (Join-Path $liveDir "$name.json") -Encoding ascii
}

function Test-StampReused([string]$name) {
    $stamp = Get-LiveStamp $name
    if (-not $stamp) { return $false }
    $process = Get-LiveProcess $stamp.processId
    return [bool]($process -and $process.CreationDate.ToString('o') -ne $stamp.started)
}

function Get-StaleReason([string]$name) {
    $stamp = Get-LiveStamp $name
    if (-not $stamp) { return 'it has no stamp' }
    if (-not (Get-LiveProcess $stamp.processId)) { return "pid $($stamp.processId) is gone" }
    if (Test-StampReused $name) { return "pid $($stamp.processId) was reused" }
    if ($stamp.hash -ne (Get-CodeHash $name)) { return 'its code changed on disk' }
}
