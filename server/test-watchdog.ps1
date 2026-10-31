# Tests for the watchdog and restart-node.ps1. Run by WatchdogScriptTests in
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
    'Get-ScheduledTask', 'Start-ScheduledTask', 'Start-Sleep', 'Get-NetTCPConnection', 'netstat'
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
function global:Get-Process { param($Id, $ErrorAction) if ($Id -eq $PID) { [pscustomobject]@{ SessionId = $global:stub.Session } } }
function global:Get-ScheduledTask { [pscustomobject]@{ State = 'Ready' } }
function global:Start-ScheduledTask { param($TaskName) & $global:stub.StartTask }
function global:Start-Sleep { }
function global:Get-NetTCPConnection { throw 'read the TCP table' }
function global:netstat { throw 'read the TCP table' }

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
} finally {
    foreach ($name in $stubbed) {
        Remove-Item "Function:\$name" -ErrorAction SilentlyContinue
    }
    Remove-Variable stub -Scope Global -ErrorAction SilentlyContinue
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

"$checks checks passed"
