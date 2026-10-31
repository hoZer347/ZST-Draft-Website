# Tests for the watchdog, restart-node.ps1, deploy-live.ps1 and start-server.ps1. Run by WatchdogScriptTests in
# server.Tests, or alone: powershell -ExecutionPolicy Bypass -File server\test-watchdog.ps1
#
# Everything that could stop a process or start the boot task is replaced by a
# stub first, and the script refuses to go on unless every stub took effect.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$checks = 0
$liveSweepLoops = 50
$fakeProcessId = 999999
$stubbed = @(
    'taskkill', 'taskkill.exe', 'Stop-Process', 'Get-WmiObject', 'Get-CimInstance', 'Get-Process',
    'Get-ScheduledTask', 'Start-ScheduledTask', 'Start-Sleep', 'Get-NetTCPConnection', 'netstat', 'node'
)

#region Helpers

function Assert-That($ok, [string]$what) {
    if (-not $ok) { throw "FAILED: $what" }
    $script:checks++
}

function Assert-Throws([scriptblock]$body, [string]$like, [string]$what) {
    try {
        & $body
    } catch {
        Assert-That ("$_" -like $like) "$what (threw: $_)"
        return
    }
    Assert-That $false "$what (did not throw)"
}

function Get-ScriptAst([string]$path) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert-That ($errors.Count -eq 0) "$path parses"
    return $ast
}

function Get-FunctionText([string]$path, [string]$name) {
    (Get-ScriptAst $path).FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)[0].Extent.Text
}

function Get-AssignmentText([string]$path, [string]$name) {
    (Get-ScriptAst $path).FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq "`$$name"
    }, $true)[0].Extent.Text
}

function Get-CodeText([string]$path) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    ($tokens | Where-Object { $_.Kind -ne 'Comment' } | ForEach-Object Text) -join ' '
}

function Test-SourcesPortCheck([string]$path) {
    @((Get-ScriptAst $path).FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.InvocationOperator -eq 'Dot' -and $node.Extent.Text -like '*port-check.ps1*'
    }, $true)).Count -gt 0
}

function Test-DefinesProbe([string]$path) {
    @((Get-ScriptAst $path).FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-Listening'
    }, $true)).Count -gt 0
}

#endregion

#region Stubs

$global:stub = @{ Killed = @(); ExitCode = 0 }

function global:taskkill { $global:stub.Killed += ,$args[-1]; $global:LASTEXITCODE = $global:stub.ExitCode }
function global:taskkill.exe { taskkill @args }
function global:Stop-Process { $global:stub.Killed += ,'Stop-Process' }
function global:Get-WmiObject { $global:stub.Nodes }
function global:Get-CimInstance { $global:stub.Nodes }
function global:Get-Process {
    param($Id, $ErrorAction, $Name)
    if ($Name) { return @($global:stub.Processes | Where-Object { $_.Name -eq $Name }) }
    if ($Id -eq $PID) { return [pscustomobject]@{ SessionId = $global:stub.Session } }
    if ($Id) { return @($global:stub.Processes | Where-Object { $_.Id -eq $Id }) }
}
function global:Get-ScheduledTask { [pscustomobject]@{ State = 'Ready' } }
function global:Start-ScheduledTask { param($TaskName) & $global:stub.StartTask }
function global:Start-Sleep { }
function global:Get-NetTCPConnection { throw 'read the TCP table' }
function global:netstat { throw 'read the TCP table' }
function global:node { & $global:stub.Node @args }

#endregion

$sandbox = Join-Path ([IO.Path]::GetTempPath()) "test-watchdog-$PID"
try {
    foreach ($name in $stubbed) {
        Assert-That ((Get-Command $name).CommandType -eq 'Function') "$name is stubbed"
    }

    #region The scripts probe by connecting, through one function

    foreach ($file in Get-ChildItem $root -Filter *.ps1 | Where-Object Name -ne 'test-watchdog.ps1') {
        Assert-That ((Get-CodeText $file.FullName) -notmatch 'NetTCPConnection|netstat|GetActiveTcp') "$($file.Name) stays off the TCP table"
        Assert-That (($file.Name -eq 'port-check.ps1') -or -not (Test-DefinesProbe $file.FullName)) "$($file.Name) has no probe of its own"
    }
    Assert-That ((Get-CodeText (Join-Path $root 'keep-server-up.ps1')) -notmatch 'taskkill|Stop-Process|Kill') 'the watchdog has no way to stop a process'
    foreach ($name in 'keep-server-up.ps1', 'restart-node.ps1') {
        Assert-That (Test-SourcesPortCheck (Join-Path $root $name)) "$name sources port-check.ps1"
    }

    #endregion

    #region Test-Listening answers from a real connect

    . (Join-Path $root 'port-check.ps1')
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $openPort = $listener.LocalEndpoint.Port
    Assert-That (Test-Listening $openPort) 'an open port answers'

    #endregion

    #region Keep-Listening

    $watchdog = Join-Path $root 'keep-server-up.ps1'
    Invoke-Expression (Get-AssignmentText $watchdog 'missLimit')
    $alive = [pscustomobject]@{ Id = $fakeProcessId; HasExited = $false }
    $exited = [pscustomobject]@{ Id = $fakeProcessId; HasExited = $true }
    $launch = { $script:launched++; $alive }

    function Log($message) { $script:logged += $message }
    Invoke-Expression (Get-FunctionText $watchdog 'Keep-Listening')

    function Reset-Watch {
        $script:launches = @{}
        $script:misses = @{}
        $script:logged = @()
        $script:launched = 0
        $global:stub.Killed = @()
    }

    Reset-Watch
    $launches[$openPort] = $alive
    foreach ($i in 1..$liveSweepLoops) { $null = Keep-Listening $openPort 'server' $launch }
    Assert-That ($stub.Killed.Count -eq 0) 'a listening server is left alone'
    Assert-That ($launched -eq 0) 'a listening server is not launched again'
    $listener.Stop()
    Assert-That (-not (Test-Listening $openPort)) 'a closed port does not answer'

    function Test-Listening([int]$port) {
        $script:probe++
        return $script:probes[$script:probe - 1]
    }

    function Invoke-Probes([bool[]]$results) {
        $script:probes = $results
        $script:probe = 0
        foreach ($result in $results) { $null = Keep-Listening 1 'server' $launch }
    }

    Reset-Watch
    Invoke-Probes @($false, $false, $true, $false, $false, $true)
    Assert-That ($launched -eq 0) 'misses that are not consecutive never launch'

    Reset-Watch
    Invoke-Probes @($false, $false, $false)
    Assert-That ($launched -eq 1) 'three misses in a row launch once'
    Assert-That ($stub.Killed.Count -eq 0) 'nothing is killed for a server this watchdog did not launch'

    Invoke-Probes (@($false) * $liveSweepLoops)
    Assert-That ($stub.Killed.Count -eq 0) 'a launch that never answers is not killed'
    Assert-That ($launched -eq 1) 'a launch that is alive is not doubled'
    Assert-That (@($logged -like '*left running*').Count -eq 1) 'that is logged once'

    Reset-Watch
    $launches[1] = $alive
    Invoke-Probes @($false, $false, $false, $true, $false, $false, $false)
    Assert-That ($stub.Killed.Count -eq 0) 'a server that stops answering is not killed'
    Assert-That ($launched -eq 0) 'a server that stops answering is not replaced while alive'
    Assert-That (@($logged -like '*left running*').Count -eq 2) 'each silence is logged'

    Reset-Watch
    $launches[1] = $exited
    Invoke-Probes @($false, $false, $false)
    Assert-That ($launched -eq 1) 'a launch that exited is relaunched'
    Assert-That ($logged -like '*relaunched*') 'that relaunch is logged'
    $launches[1] = $exited
    Invoke-Probes @($false, $false)
    Assert-That ($launched -eq 1) 'a relaunch needs fresh misses'
    Invoke-Probes @($false)
    Assert-That ($launched -eq 2) 'a third fresh miss relaunches'

    #endregion

    #region Start-Showdown captures each launch

    $extraLogs = 3
    $showdownLogs = Join-Path ([IO.Path]::GetTempPath()) "showdown-logs-test-$PID"
    $showdownDir = [IO.Path]::GetTempPath()
    $node = 'node'
    Invoke-Expression (Get-AssignmentText $watchdog 'showdownLogKeep')
    Invoke-Expression (Get-FunctionText $watchdog 'Start-Showdown')
    function Start-Process { $script:launchArguments = $args; [pscustomobject]@{ Id = $fakeProcessId; HasExited = $false } }

    try {
        $showdownRun = @(Start-Showdown)
        Assert-That ($showdownRun.Count -eq 1 -and $showdownRun[0].Id -eq $fakeProcessId) 'Start-Showdown returns only the process'
        Assert-That (Test-Path $showdownLogs) 'the log folder is created'
        $logArguments = @($launchArguments | Where-Object { "$_" -like "$showdownLogs*" })
        Assert-That ($logArguments.Count -eq 2) 'stdout and stderr each go to a file'
        Assert-That ($logArguments[0] -like '*.out.log' -and $logArguments[1] -like '*.err.log') 'the files are named for their stream'

        $oldLogs = $showdownLogKeep + $extraLogs
        Remove-Item (Join-Path $showdownLogs '*') -Force
        foreach ($i in 1..$oldLogs) { Set-Content (Join-Path $showdownLogs ('{0:d4}.out.log' -f $i)) '' }
        $null = Start-Showdown
        $kept = @(Get-ChildItem $showdownLogs -Filter '*.log' | Sort-Object Name)
        Assert-That ($kept.Count -eq $showdownLogKeep) 'old logs are pruned'
        Assert-That ($kept[-1].Name -eq ('{0:d4}.out.log' -f $oldLogs)) 'the newest logs are kept'
    } finally {
        Remove-Item function:Start-Process
        Remove-Item $showdownLogs -Recurse -Force -ErrorAction SilentlyContinue
    }

    #endregion

    #region restart-node.ps1 runs unmodified against stubs

    $null = New-Item -ItemType Directory -Path $sandbox
    Copy-Item (Join-Path $root 'restart-node.ps1') $sandbox
    Set-Content (Join-Path $sandbox 'port-check.ps1') 'function Test-Listening([int]$port) { & $global:stub.PortAnswers $port }'
    $restart = Join-Path $sandbox 'restart-node.ps1'
    $flag = Join-Path $sandbox '.node-restart-requested'

    function Reset-Restart {
        $global:stub.Killed = @()
        $global:stub.ExitCode = 0
        $global:stub.Session = 1
        $global:stub.PortAnswers = { $true }
        $global:stub.Handoffs = @()
        $global:stub.StartTask = {
            $global:stub.Handoffs += (Get-Content $flag -Raw).Trim()
            Remove-Item $flag -Force
        }
        $global:stub.Nodes = @(
            [pscustomobject]@{ ProcessId = 101; CommandLine = '"node.exe" scripts\serve-client.js' },
            [pscustomobject]@{ ProcessId = 102; CommandLine = '"node.exe" scripts\showdown.js' },
            [pscustomobject]@{ ProcessId = 103; CommandLine = '"node.exe" something-else.js' }
        )
        Remove-Item $flag -Force -ErrorAction SilentlyContinue
    }

    Reset-Restart
    & $restart -Port 8791 6>$null
    Assert-That (($stub.Killed -join ',') -eq '101') 'only the named server is stopped'
    Assert-That ($stub.Handoffs.Count -eq 0) 'a stop that works needs no handoff'

    Reset-Restart
    Assert-Throws { & $restart -Port 8080 } '*8080*' 'a port with no server script is refused'
    Assert-That ($stub.Killed.Count -eq 0) 'a refused port stops nothing'

    Reset-Restart
    $stub.Session = 0
    $stub.Nodes = @()
    $stub.PortAnswers = { $false }
    Assert-Throws { & $restart -Port 8791 } '*not running*' 'a port with no server is an error, not a restart'
    Assert-That ($stub.Killed.Count -eq 0) 'a port with no server stops nothing'

    Reset-Restart
    $stub.Session = 0
    $stub.PortAnswers = { $stub.Killed.Count -gt 0 }
    & $restart -Port 8791 6>$null
    Assert-That (($stub.Killed -join ',') -eq '101') 'a hung server is stopped on request'

    Reset-Restart
    $stub.Nodes = @()
    $stub.PortAnswers = { $stub.Handoffs.Count -gt 0 }
    & $restart -Port 8791 6>$null
    Assert-That (($stub.Handoffs -join ',') -eq '8791') 'a hung server out of reach goes to the boot task'

    Reset-Restart
    $stub.Nodes = @()
    & $restart -Port 8791 6>$null
    Assert-That (($stub.Handoffs -join ',') -eq '8791') 'a session 0 stack is handed to the boot task once'
    Assert-That ($stub.Killed.Count -eq 0) 'the handoff stops nothing itself'

    Reset-Restart
    $stub.ExitCode = 1
    & $restart -Port 8791 6>$null
    Assert-That (($stub.Handoffs -join ',') -eq '8791') 'a denied stop is handed to the boot task once'

    Reset-Restart
    $stub.Nodes = @()
    $stub.Session = 0
    Assert-Throws { & $restart -Port 8791 } '*find or stop*' 'session 0 reports what it cannot find'
    Assert-That (-not (Test-Path $flag)) 'session 0 does not hand off to itself'

    Reset-Restart
    $stub.Nodes = @()
    $stub.StartTask = { throw 'task is gone' }
    Assert-Throws { & $restart -Port 8791 } '*DraftLeagueBoot*' 'a task that will not start is an error'
    Assert-That (-not (Test-Path $flag)) 'that failure leaves no flag behind'

    Reset-Restart
    $serverScripts = @{}
    Invoke-Expression (Get-FunctionText $restart 'Get-ServerProcessId')
    Assert-Throws { Get-ServerProcessId 8791 } '*No server script*' 'an unmapped port never matches every node.exe'

    #endregion

    #region deploy-live.ps1 runs unmodified against stubs

    $tree = Join-Path $sandbox 'tree'
    $treeServer = Join-Path $tree 'server'
    $treeBattle = Join-Path $tree 'battle-server'
    foreach ($dir in 'server', 'battle-server\lib', 'battle-server\showdown-config\chat-plugins', 'battle-server\scripts',
        'battle-server\client\caches\pokemon-showdown\dist\sim', '.cloudflared') {
        $null = New-Item -ItemType Directory -Path (Join-Path $tree $dir)
    }
    foreach ($file in 'keep-server-up.ps1', 'port-check.ps1', 'live-code.ps1', 'deploy-live.ps1', 'start-server.ps1') {
        Copy-Item (Join-Path $root $file) $treeServer
    }
    Set-Content (Join-Path $treeServer 'port-check.ps1') 'function Test-Listening([int]$port) { & $global:stub.PortAnswers $port }'
    Set-Content (Join-Path $treeServer 'restart-node.ps1') 'param([int[]]$Port) & $global:stub.Restart $Port'
    $engineFile = 'client\caches\pokemon-showdown\dist\sim\battle.js'
    foreach ($file in 'lib\engine.js', 'lib\showdown-config.js', 'lib\client-shell.js', 'lib\seed-teams.js', 'lib\roster-teambuilder.js',
        'lib\roster-tera.js', 'lib\auto-login.js', 'showdown-config\custom-megas.js', 'showdown-config\chat-plugins\draft-chat.js',
        'scripts\showdown.js', 'scripts\serve-client.js', $engineFile) {
        Set-Content (Join-Path $treeBattle $file) $file
    }
    Set-Content (Join-Path $tree '.cloudflared\config.yml') 'protocol: http2'
    $realProfile = $env:USERPROFILE
    $env:USERPROFILE = $tree
    try {
        . (Join-Path $treeServer 'live-code.ps1')
        $deploy = Join-Path $treeServer 'deploy-live.ps1'
        $treeLog = Join-Path $treeServer 'keep-server-up.log'
        $liveStart = Get-Date '2026-10-31T09:00:00'
        $liveNames = 'watchdog', 'tunnel', '8787', '8791'
        $tunnelRequest = Join-Path $treeServer '.tunnel-restart-requested'

        function Set-Stamps {
            $global:stub.Nodes = @([pscustomobject]@{ ProcessId = $fakeProcessId; CreationDate = $liveStart })
            foreach ($name in $liveNames) { Write-LiveStamp $name $fakeProcessId (Get-CodeHash $name) }
        }

        function Reset-Deploy {
            $global:stub.Killed = @()
            $global:stub.PortAnswers = { $true }
            $global:stub.Battles = 0
            $global:stub.HoldExit = 0
            $global:stub.Tasks = 0
            $global:stub.Restarts = @()
            $global:stub.Holds = 0
            $global:stub.TunnelRequested = $false
            $global:stub.StartTask = { $global:stub.Tasks++; $global:stub.TunnelRequested = Test-Path $tunnelRequest }
            $global:stub.Restart = { param($ports) $global:stub.Restarts += ,($ports -join ',') }
            $global:stub.Node = {
                if ($args[1] -eq 'battles') {
                    $global:LASTEXITCODE = 0
                    return "$($global:stub.Battles)"
                }
                $global:stub.Holds++
                $global:LASTEXITCODE = $global:stub.HoldExit
            }
            Set-Content (Join-Path $treeServer '.watchdog.alive') 'now'
            Set-Content $treeLog 'relaunched server'
            foreach ($file in 'scripts\showdown.js', 'lib\auto-login.js', $engineFile) { Set-Content (Join-Path $treeBattle $file) $file }
            Set-Content $tunnelConfig 'protocol: http2'
            Set-Stamps
        }

        function Invoke-Deploy {
            param([switch]$Force)
            & $deploy -Force:$Force -HoldSeconds 1 -TimeoutSec 1 6>$null
            $LASTEXITCODE
        }

        Reset-Deploy
        Assert-That ((Invoke-Deploy) -eq 0) 'a current stack passes'
        Assert-That (($stub.Tasks -eq 0) -and ($stub.Restarts.Count -eq 0)) 'a current stack restarts nothing'
        Assert-That ($stub.Holds -eq 1) 'a pass holds sockets through the tunnel'

        Reset-Deploy
        Set-Content (Join-Path $treeBattle 'scripts\showdown.js') 'changed'
        Assert-That ((Get-StaleReason '8787') -like '*changed*') 'an edited script makes its server stale'
        Assert-That (-not (Get-StaleReason '8791')) 'a server is not stale over a file it does not load'
        $stub.Battles = 2
        Assert-That ((Invoke-Deploy) -eq 1) 'a live battle refuses the restart'
        Assert-That ($stub.Restarts.Count -eq 0) 'a live battle restarts nothing'

        Assert-That ((Invoke-Deploy -Force) -eq 1) 'a restart that leaves old code running fails the deploy'
        Assert-That (($stub.Restarts -join ';') -eq '8787') 'only the stale server is restarted'
        Assert-That ($stub.Holds -eq 0) 'a stale server is never held'

        Reset-Deploy
        Set-Content (Join-Path $treeBattle 'scripts\showdown.js') 'changed'
        $stub.Restart = { param($ports) Write-LiveStamp '8787' $fakeProcessId (Get-CodeHash '8787') }
        Assert-That ((Invoke-Deploy) -eq 0) 'a restart that loads the new code passes'

        Reset-Deploy
        Remove-Item (Join-Path $liveDir 'tunnel.json')
        Assert-That ((Get-StaleReason 'tunnel') -like '*no stamp*') 'a process with no stamp is stale'
        Assert-That ((Invoke-Deploy) -eq 1) 'a boot task that replaces nothing fails the deploy'
        Assert-That ($stub.Tasks -eq 1) 'a stale tunnel goes to the boot task'
        Assert-That $stub.TunnelRequested 'a stale tunnel is requested of the boot task'
        Assert-That (-not (Test-Path $tunnelRequest)) 'a failed deploy leaves no request behind'

        Reset-Deploy
        Set-Content $tunnelConfig 'protocol: quic'
        Assert-That ((Get-StaleReason 'tunnel') -like '*changed*') 'an edited tunnel config makes the tunnel stale'
        $stub.Battles = 1
        Assert-That ((Invoke-Deploy) -eq 1) 'a live battle refuses a tunnel restart'
        Assert-That ($stub.Tasks -eq 0) 'a live battle never reaches the boot task'

        Reset-Deploy
        Remove-Item (Join-Path $liveDir 'watchdog.json')
        $null = Invoke-Deploy
        Assert-That (($stub.Tasks -eq 1) -and -not $stub.TunnelRequested) 'a stale watchdog alone leaves the tunnel running'

        Reset-Deploy
        Set-Content (Join-Path $treeBattle 'lib\auto-login.js') 'changed'
        Assert-That (-not (Get-StaleReason '8787')) 'a client library leaves the battle server current'
        Assert-That ((Get-StaleReason '8791') -like '*changed*') 'a client library makes the client server stale'

        Reset-Deploy
        Set-Content (Join-Path $treeBattle $engineFile) 'rebuilt'
        Assert-That ((Get-StaleReason '8787') -like '*changed*') 'a rebuilt engine makes the battle server stale'

        Reset-Deploy
        $stub.Nodes = @([pscustomobject]@{ ProcessId = $fakeProcessId; CreationDate = $liveStart.AddSeconds(1) })
        Assert-That ((Get-StaleReason '8791') -like '*reused*') 'a reused pid is stale'
        $stub.Nodes = @()
        Assert-That ((Get-StaleReason '8791') -like '*gone*') 'a dead process is stale'

        Reset-Deploy
        $stub.HoldExit = 1
        Assert-That ((Invoke-Deploy) -eq 1) 'a socket closing during the hold fails the deploy'

        Reset-Deploy
        $stub.Node = {
            $global:LASTEXITCODE = 0
            if ($args[1] -eq 'hold') { Add-Content $treeLog 'relaunched showdown server' }
        }
        Assert-That ((Invoke-Deploy) -eq 1) 'a relaunch during the hold fails the deploy'

        Reset-Deploy
        $stub.Node = {
            $global:LASTEXITCODE = 0
            if ($args[1] -eq 'hold') { Set-Content (Join-Path $treeBattle 'scripts\showdown.js') 'changed' }
        }
        Assert-That ((Invoke-Deploy) -eq 1) 'a save during the hold fails the deploy'

        Reset-Deploy
        $stub.PortAnswers = { param($port) $port -ne 8791 }
        Assert-That ((Invoke-Deploy) -eq 1) 'a port that stops answering fails the deploy'

        #region Start-Stamped never costs the watchdog its pass

        Invoke-Expression (Get-FunctionText (Join-Path $treeServer 'keep-server-up.ps1') 'Start-Stamped')
        $stampedProcess = [pscustomobject]@{ Id = $fakeProcessId; HasExited = $false }

        Reset-Deploy
        Remove-Item (Join-Path $liveDir 'tunnel.json')
        $stamped = @(Start-Stamped 'tunnel' { $stampedProcess })
        Assert-That (($stamped.Count -eq 1) -and ($stamped[0].Id -eq $fakeProcessId)) 'Start-Stamped returns only the process'
        Assert-That (-not (Get-StaleReason 'tunnel')) 'a launch stamps its process'

        Remove-Item (Join-Path $liveDir 'tunnel.json')
        $stub.Nodes = @()
        $logged = @()
        $stamped = @(Start-Stamped 'tunnel' { $stampedProcess })
        Assert-That ($stamped[0].Id -eq $fakeProcessId) 'a process that dies at once is still returned'
        Assert-That ("$logged" -like '*could not stamp tunnel*') 'a failed stamp is logged'

        Reset-Deploy
        Remove-Item $tunnelConfig
        $logged = @()
        $stamped = @(Start-Stamped 'tunnel' { $stampedProcess })
        Assert-That ($stamped[0].Id -eq $fakeProcessId) 'a missing source file does not stop the launch'
        Assert-That ("$logged" -like '*could not hash tunnel*') 'a failed hash is logged'

        #endregion

        #region start-server.ps1 runs unmodified against stubs

        function global:Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }
        function global:Start-Process { $global:stub.Started++ }
        Assert-That ((Get-Command Invoke-WebRequest).CommandType -eq 'Function') 'the web request is stubbed'
        Assert-That ((Get-Command Start-Process).CommandType -eq 'Function') 'the process start is stubbed'
        $boot = Join-Path $treeServer 'start-server.ps1'

        function Reset-Boot {
            Reset-Deploy
            $global:stub.Started = 0
            $global:stub.Processes = @(
                [pscustomobject]@{ Name = 'cloudflared'; Id = $fakeProcessId + 1 },
                [pscustomobject]@{ Name = 'powershell'; Id = $fakeProcessId }
            )
            Set-Content (Join-Path $treeServer '.watchdog.pid') "$fakeProcessId"
        }

        try {
            Reset-Boot
            & $boot 6>$null
            Assert-That (($stub.Killed.Count -eq 0) -and ($stub.Started -eq 0)) 'a current stack is left alone'

            Reset-Boot
            Remove-Item (Join-Path $liveDir 'tunnel.json')
            & $boot 6>$null
            Assert-That ($stub.Killed.Count -eq 0) 'a handoff alone leaves a stale tunnel running'

            Reset-Boot
            Set-Content $tunnelRequest ''
            & $boot 6>$null
            Assert-That ($stub.Killed.Count -eq 1) 'a tunnel request stops the tunnel'
            Assert-That (-not (Test-Path $tunnelRequest)) 'a tunnel request is cleared'

            Reset-Boot
            Set-Content (Join-Path $treeServer 'live-code.ps1') ((Get-Content (Join-Path $treeServer 'live-code.ps1') -Raw) + ' ')
            & $boot 6>$null
            Assert-That (($stub.Killed.Count -eq 1) -and ($stub.Started -eq 1)) 'a stale watchdog is replaced'

            Reset-Boot
            $stub.Nodes = @([pscustomobject]@{ ProcessId = $fakeProcessId; CreationDate = $liveStart.AddSeconds(1) })
            & $boot 6>$null
            Assert-That (($stub.Killed.Count -eq 0) -and ($stub.Started -eq 1)) 'a reused pid is never stopped, and a watchdog starts'
        } finally {
            Remove-Item Function:\Invoke-WebRequest, Function:\Start-Process -ErrorAction SilentlyContinue
        }

        #endregion
    } finally {
        $env:USERPROFILE = $realProfile
    }

    #endregion
} finally {
    foreach ($name in $stubbed) {
        Remove-Item "Function:\$name" -ErrorAction SilentlyContinue
    }
    Remove-Variable stub -Scope Global -ErrorAction SilentlyContinue
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

"$checks checks passed"
