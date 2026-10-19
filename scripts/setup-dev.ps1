<#
.SYNOPSIS
  Prepares a Windows machine to work on ConvertAnything and checks that the repository builds and its tests pass.
.DESCRIPTION
  Safe to run repeatedly; each step is skipped when already done.
  1. Installs the .NET 10 SDK per user (no admin rights needed) if missing; warns if the GitHub CLI is missing or signed out.
  2. Sets this repository's commit identity.
  3. Repairs a known-broken global git rewrite (https://github.com without the trailing slash) that breaks plugin installs.
  4. Installs the agent-skills plugin for Claude Code (project scope) if the claude CLI is available.
  5. Builds the solution and runs the tests.
#>
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repo

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

Step '.NET 10 SDK'
# A per-user install needs no UAC prompt, so the script can run unattended.
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
function Use-UserDotnet {
    if ((Test-Path (Join-Path $userDotnet 'dotnet.exe')) -and ($env:Path -notlike "*$userDotnet*")) {
        $env:Path = "$userDotnet;$env:Path"
        $env:DOTNET_ROOT = $userDotnet
    }
}
Use-UserDotnet
# Under ErrorActionPreference=Stop, invoking a missing dotnet throws, so look it up first.
$sdks = if (Get-Command dotnet -ErrorAction SilentlyContinue) { & dotnet --list-sdks 2>$null } else { @() }
if (-not ($sdks -match '^10\.')) {
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel 10.0 -InstallDir $userDotnet -NoPath
    # Persist for new shells: user PATH and DOTNET_ROOT.
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notlike "*$userDotnet*") {
        [Environment]::SetEnvironmentVariable('Path', "$userDotnet;$userPath", 'User')
    }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $userDotnet, 'User')
    Use-UserDotnet
}
& dotnet --list-sdks | Select-String '^10\.'

Step 'GitHub CLI'
# The gh installer is per-machine and would block on a UAC prompt, so only advise here.
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Warning 'GitHub CLI missing. Run:  winget install --id GitHub.cli -e   then  gh auth login --web  (account Suryvibegod).'
} else {
    $auth = & gh auth status 2>&1 | Out-String
    if ($auth -notmatch 'Logged in') {
        Write-Warning 'Not signed in to GitHub. Run:  gh auth login --web   (account Suryvibegod), then push works.'
    }
}

Step 'Commit identity (this repository only)'
git config user.name 'Suryvibegod'
git config user.email '79949229+Suryvibegod@users.noreply.github.com'
"{0} <{1}>" -f (git config user.name), (git config user.email)

Step 'Global git URL rewrite'
$rewrites = git config --global --get-regexp '^url\..*\.insteadof$' 2>$null
if ($rewrites -match '^url\.https://github\.com\.insteadof git@github\.com:$') {
    # Missing trailing slash: git@github.com:owner/repo became https://github.comowner/repo.
    git config --global --unset 'url.https://github.com.insteadof'
    git config --global 'url.https://github.com/.insteadOf' 'git@github.com:'
    'Repaired url.https://github.com/.insteadOf git@github.com:'
} else {
    'OK'
}

Step 'Claude Code plugin: agent-skills@addy-agent-skills'
if (Get-Command claude -ErrorAction SilentlyContinue) {
    $plugins = & claude plugin list 2>&1 | Out-String
    if ($plugins -notmatch 'agent-skills@addy-agent-skills') {
        & claude plugin marketplace add addyosmani/agent-skills
        & claude plugin install agent-skills@addy-agent-skills --scope project
        'Installed. It loads in Claude Code after a restart or /reload-plugins; until then read the skills from the plugin cache.'
    } else {
        'Already installed'
    }
} else {
    Write-Warning 'claude CLI not found; install the plugin from Claude Code with /plugin install agent-skills@addy-agent-skills'
}

Step 'Build and test'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& dotnet build ConvertAnything.sln -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& dotnet test ConvertAnything.sln --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
Write-Host "`nReady." -ForegroundColor Green
