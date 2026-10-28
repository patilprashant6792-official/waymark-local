<#
.SYNOPSIS
    Installs, updates, starts, stops and removes Waymark for the current Windows user.

.DESCRIPTION
    install    builds Waymark from this repository into %LOCALAPPDATA%\Waymark\app, starts it when you sign in, and opens it.
    update     pulls the latest version of the repository and installs it. Your data is not touched.
    uninstall  stops and removes Waymark. Your data stays unless -RemoveData is given.
    start, stop, open, status   do what they say.
    run        is what the sign-in task calls: it starts the program and keeps a short last-run log.

    Your data (database, keys, secrets) lives in %LOCALAPPDATA%\Waymark and is never part of the app folder.
    Waymark runs as you, not as a Windows service: its keys, folders and git credentials belong to your account.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install', 'update', 'uninstall', 'start', 'stop', 'open', 'status', 'run')]
    [string] $Command = 'status',

    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'

$AdminPort = if ($env:Waymark__AdminPort) { [int] $env:Waymark__AdminPort } else { 47100 }
$McpPort = if ($env:Waymark__McpPort) { [int] $env:Waymark__McpPort } else { 47101 }
$AdminUrl = 'http://localhost:' + $AdminPort

$TaskName = 'Waymark'
$DataDir = Join-Path $env:LOCALAPPDATA 'Waymark'
$AppDir = Join-Path $DataDir 'app'
$LogDir = Join-Path $DataDir 'logs'
$Exe = Join-Path $AppDir 'Waymark.Host.exe'
$InstalledScript = Join-Path $DataDir 'waymark.ps1'
$RepoFile = Join-Path $DataDir 'repo.path'
$VersionFile = Join-Path $DataDir 'version.txt'
$Programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$MenuShortcut = Join-Path $Programs 'Waymark.lnk'
$StartupShortcut = Join-Path $Programs 'Startup\Waymark.lnk'
$HiddenArguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File'

function Write-Step([string] $Text) { Write-Host ('==> ' + $Text) -ForegroundColor Cyan }
function Write-Good([string] $Text) { Write-Host ('    ' + $Text) -ForegroundColor Green }
function Write-Note([string] $Text) { Write-Host ('    ' + $Text) -ForegroundColor Yellow }

function Assert-Windows {
    if ([Environment]::OSVersion.Platform -ne 'Win32NT') {
        throw 'Waymark runs on Windows only.'
    }
}

# Any answer from the admin address, even an error page, means the program is up.
function Test-Up {
    try {
        $null = Invoke-WebRequest -Uri $AdminUrl -UseBasicParsing -TimeoutSec 2
        return $true
    }
    catch [System.Net.WebException] {
        return ($null -ne $_.Exception.Response)
    }
    catch {
        return $false
    }
}

function Wait-Up([int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Up) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-RunningInstall {
    return @(Get-Process -Name 'Waymark.Host' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe })
}

function Assert-PortsFree {
    foreach ($port in @($AdminPort, $McpPort)) {
        $connection = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $connection) { continue }
        $owner = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
        if ($null -ne $owner -and $owner.Path -eq $Exe) { continue }
        $name = if ($null -ne $owner) { $owner.ProcessName } else { 'another program' }
        throw ('Port ' + $port + ' is already used by ' + $name + '. Close it, or give Waymark other ports with the user environment variables Waymark__AdminPort and Waymark__McpPort, and run this again.')
    }
}

function Test-Sdk {
    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool] ($sdks | Where-Object { $_ -match '^10\.' })
}

function Install-Sdk {
    if (Test-Sdk) {
        Write-Good '.NET 10 SDK found.'
        return
    }

    if ($null -eq (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'The .NET 10 SDK is missing and winget is not available. Install the SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.'
    }

    Write-Note 'The .NET 10 SDK is missing. Installing it with winget (this can take a few minutes)...'
    & winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-package-agreements --accept-source-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Test-Sdk)) {
        throw 'The .NET 10 SDK was installed but is not visible yet. Close this window, open a new one and run this again.'
    }

    Write-Good '.NET 10 SDK installed.'
}

function Find-Repo {
    $candidates = @()
    if ($PSScriptRoot) { $candidates += (Split-Path -Parent $PSScriptRoot) }
    if (Test-Path $RepoFile) { $candidates += (Get-Content $RepoFile -Raw).Trim() }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'src\Waymark.Host\Waymark.Host.csproj'))) { return $candidate }
    }

    throw 'The Waymark repository was not found. Run install.cmd from the cloned repository.'
}

function Stop-Waymark {
    if ($null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    }

    foreach ($process in Get-RunningInstall) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $null = $process.WaitForExit(15000)
    }
}

# Builds into a side folder first, so a failed build leaves the working install exactly as it was.
function Publish-Waymark([string] $Repo) {
    $project = Join-Path $Repo 'src\Waymark.Host\Waymark.Host.csproj'
    $staging = Join-Path $DataDir 'app.new'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    Write-Step 'Building Waymark (the first time takes a minute or two)'
    Push-Location $Repo
    try {
        # Warnings are not errors here: a newer SDK may report warnings the project was not written against.
        # Out-Host keeps dotnet's own output out of this function's return value, which must be the staging path only.
        & dotnet publish $project -c Release -o $staging --nologo -v minimal -p:TreatWarningsAsErrors=false | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw 'The build failed. Nothing was changed: the installed version, if there is one, still works.'
    }

    return $staging
}

function New-Shortcut([string] $Path, [string] $Arguments, [string] $Description) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $link.Arguments = $Arguments
    $link.WorkingDirectory = $DataDir
    $link.Description = $Description
    $link.WindowStyle = 7
    $link.IconLocation = $Exe + ',0'
    $link.Save()
}

# A task that runs as you when you sign in and restarts the program if it fails. If tasks cannot be created, the Startup folder does the same job.
function Register-Autostart {
    $argument = $HiddenArguments + ' "' + $InstalledScript + '" run'
    $user = $env:USERDOMAIN + '\' + $env:USERNAME
    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argument
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Seconds 0)
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description 'Starts Waymark when you sign in.' -Force | Out-Null
        if (Test-Path $StartupShortcut) { Remove-Item $StartupShortcut -Force }
        Write-Good 'Waymark will start when you sign in (scheduled task named Waymark).'
    }
    catch {
        Write-Note ('A scheduled task could not be created (' + $_.Exception.Message + '). Using the Startup folder instead.')
        New-Shortcut -Path $StartupShortcut -Arguments ($HiddenArguments + ' "' + $InstalledScript + '" run') -Description 'Starts Waymark when you sign in.'
        Write-Good 'Waymark will start when you sign in (shortcut in your Startup folder).'
    }
}

function Start-Waymark {
    if (Test-Up) { return }
    Assert-PortsFree
    if ($null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Start-ScheduledTask -TaskName $TaskName
    }
    else {
        Start-Process -FilePath 'powershell.exe' -ArgumentList ($HiddenArguments + ' "' + $InstalledScript + '" run') -WindowStyle Hidden
    }
}

function Show-Log {
    foreach ($name in @('host.err.log', 'host.log')) {
        $file = Join-Path $LogDir $name
        if ((Test-Path $file) -and ((Get-Item $file).Length -gt 0)) {
            Write-Host ('--- ' + $name + ' (last lines) ---') -ForegroundColor Yellow
            Get-Content $file -Tail 20
        }
    }
}

function Invoke-Install([bool] $Pull) {
    Assert-Windows
    $repo = Find-Repo

    Write-Step 'Checking the .NET 10 SDK'
    Install-Sdk

    if ($Pull) {
        Write-Step 'Getting the latest version'
        if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is needed to update.' }
        & git -C $repo pull --ff-only
        if ($LASTEXITCODE -ne 0) { throw 'git pull did not finish cleanly (local changes?). Fix that and run update again.' }
    }

    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    $staging = Publish-Waymark $repo

    Write-Step 'Installing'
    # The tunnel is a separate program that outlives Waymark and would still hold the public address; free it before the app is replaced.
    # Skipped when ngrok is not running or not installed: this step must never fail the install.
    if ($null -ne (Get-Process -Name ngrok -ErrorAction SilentlyContinue)) {
        Write-Step 'Freeing the ngrok tunnel'
        try { & taskkill /IM ngrok.exe /F 2>&1 | Out-Null } catch { Write-Note 'ngrok could not be stopped; continuing.' }
        $global:LASTEXITCODE = 0
    }
    Stop-Waymark
    if (Test-Path $AppDir) { Remove-Item $AppDir -Recurse -Force }
    Move-Item $staging $AppDir
    Copy-Item (Join-Path $repo 'scripts\waymark.ps1') $InstalledScript -Force
    Set-Content -Path $RepoFile -Value $repo -Encoding ASCII

    $version = 'unknown'
    if ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) {
        $short = & git -C $repo rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $short) { $version = $short }
    }
    Set-Content -Path $VersionFile -Value $version -Encoding ASCII

    Register-Autostart
    New-Shortcut -Path $MenuShortcut -Arguments ($HiddenArguments + ' "' + $InstalledScript + '" open') -Description 'Open Waymark'
    Write-Good 'Start menu shortcut added: Waymark.'

    Write-Step 'Starting Waymark'
    Start-Waymark
    if (-not (Wait-Up 90)) {
        Show-Log
        throw ('Waymark did not answer at ' + $AdminUrl + ' within 90 seconds. The last lines of its log are above (folder: ' + $LogDir + ').')
    }

    Write-Good ('Waymark is running at ' + $AdminUrl + ' (version ' + $version + ').')
    Start-Process $AdminUrl
}

function Invoke-Uninstall {
    Write-Step 'Stopping Waymark'
    Stop-Waymark

    if ($null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    foreach ($shortcut in @($MenuShortcut, $StartupShortcut)) {
        if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    }
    foreach ($path in @($AppDir, (Join-Path $DataDir 'app.new'))) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    foreach ($file in @($RepoFile, $VersionFile)) {
        if (Test-Path $file) { Remove-Item $file -Force }
    }
    Write-Good 'Waymark was removed.'

    if ($RemoveData) {
        $answer = Read-Host ('This deletes the database, the keys and every stored secret in ' + $DataDir + '. Type DELETE to continue')
        if ($answer -ceq 'DELETE') {
            Remove-Item $DataDir -Recurse -Force
            Write-Good 'Your data was deleted.'
            return
        }
        Write-Note 'Nothing was typed that matches, so your data was kept.'
    }

    if (Test-Path $InstalledScript) { Remove-Item $InstalledScript -Force }
    Write-Good ('Your data is kept in ' + $DataDir + ' (database, keys, secrets). Run uninstall.cmd -RemoveData to delete it as well.')
}

function Invoke-Open {
    if (-not (Test-Path $Exe)) { throw 'Waymark is not installed. Run install.cmd from the repository.' }
    Start-Waymark
    if (-not (Wait-Up 60)) {
        Show-Log
        throw ('Waymark did not answer at ' + $AdminUrl + ' within 60 seconds.')
    }
    Start-Process $AdminUrl
}

# What the sign-in task runs. Only warnings, errors and the start-up lines reach the log, which keeps it small; the Live page is not affected.
function Invoke-Run {
    if (-not (Test-Path $Exe)) { throw 'Waymark is not installed. Run install.cmd from the repository.' }
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    $out = Join-Path $LogDir 'host.log'
    $err = Join-Path $LogDir 'host.err.log'
    foreach ($file in @($out, $err)) {
        if (Test-Path $file) { Move-Item $file ($file -replace '\.log$', '.previous.log') -Force }
    }

    [Environment]::SetEnvironmentVariable('Logging__LogLevel__Default', 'Warning', 'Process')
    [Environment]::SetEnvironmentVariable('Logging__LogLevel__Microsoft.Hosting.Lifetime', 'Information', 'Process')

    $process = Start-Process -FilePath $Exe -WorkingDirectory $AppDir -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $process.Handle
    $process.WaitForExit()
    exit $process.ExitCode
}

function Show-Status {
    $installed = Test-Path $Exe
    Write-Host ('Installed:   ' + $(if ($installed) { 'yes' } else { 'no' }))
    if (Test-Path $VersionFile) { Write-Host ('Version:     ' + (Get-Content $VersionFile -Raw).Trim()) }
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    $autostart = if ($null -ne $task) { 'scheduled task (' + $task.State + ')' } elseif (Test-Path $StartupShortcut) { 'Startup folder' } else { 'none' }
    Write-Host ('Autostart:   ' + $autostart)
    Write-Host ('Running:     ' + $(if (Test-Up) { 'yes, at ' + $AdminUrl } else { 'no' }))
    Write-Host ('Data folder: ' + $DataDir)
}

try {
    switch ($Command) {
        'install' { Invoke-Install $false }
        'update' { Invoke-Install $true }
        'uninstall' { Invoke-Uninstall }
        'start' {
            if (-not (Test-Path $Exe)) { throw 'Waymark is not installed. Run install.cmd from the repository.' }
            Start-Waymark
            if (-not (Wait-Up 60)) { Show-Log; throw 'Waymark did not answer within 60 seconds.' }
            Write-Good ('Waymark is running at ' + $AdminUrl)
        }
        'stop' { Stop-Waymark; Write-Good 'Waymark was stopped.' }
        'open' { Invoke-Open }
        'status' { Show-Status }
        'run' { Invoke-Run }
    }
}
catch {
    Write-Host ''
    Write-Host ('Waymark: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
