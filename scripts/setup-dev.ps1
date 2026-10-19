<#
.SYNOPSIS
  Prepares a Windows machine to work on Flevert and checks that the repository builds and its tests pass.
.DESCRIPTION
  Safe to run repeatedly; each step is skipped when already done.
  1. Installs the .NET 10 SDK per user (no admin rights needed) if missing; warns if the GitHub CLI is missing or signed out.
  2. On the maintainer's machines only (signed in to GitHub as account 79949229): sets this repository's commit
     identity, repairs a known-broken global git rewrite that breaks plugin installs, and installs the agent-skills
     plugin for Claude Code. Contributors keep their own identity and global settings; the script only prints advice.
  3. Builds the solution and runs each test project in turn.
#>
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repo

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# Windows PowerShell 5.1 turns a native command's redirected stderr into a terminating error under
# ErrorActionPreference=Stop. gh writes to stderr when signed out or offline, which must not stop the build and tests.
function Invoke-Tolerant([scriptblock]$Command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } catch { $null } finally { $ErrorActionPreference = $saved }
}

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
    # A fresh folder of its own: a fixed name in %TEMP% could be swapped between the download and the run.
    $download = Join-Path $env:TEMP ("dotnet-install-" + [guid]::NewGuid())
    New-Item -ItemType Directory $download | Out-Null
    try {
        $installer = Join-Path $download 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
        # Microsoft signs the script; run it only if that signature is intact.
        $signature = Get-AuthenticodeSignature $installer
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
            throw "dotnet-install.ps1 is not validly signed by Microsoft ($($signature.Status)); not running it."
        }
        & $installer -Channel 10.0 -InstallDir $userDotnet -NoPath
    }
    finally {
        Remove-Item $download -Recurse -Force -ErrorAction SilentlyContinue
    }
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
    Write-Warning 'GitHub CLI missing. Run:  winget install --id GitHub.cli -e   then  gh auth login --web --scopes workflow  (account Suryvibegod).'
} else {
    $auth = Invoke-Tolerant { & gh auth status 2>&1 | Out-String }
    if ($auth -notmatch 'Logged in') {
        Write-Warning 'Not signed in to GitHub. Run:  gh auth login --web --scopes workflow   (account Suryvibegod), then push works.'
    } elseif ($auth -match 'Token scopes' -and $auth -notmatch 'workflow') {
        # Pushing changes under .github/workflows needs the workflow scope.
        Write-Warning 'GitHub token lacks the workflow scope. Run:  gh auth refresh -h github.com -s workflow'
    }
}

Step 'Inno Setup 6.7.3 (builds the installer)'
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" | Where-Object { Test-Path $_ }
if (-not $iscc) {
    # Per-user install: no UAC prompt.
    winget install --id JRSoftware.InnoSetup -e --version 6.7.3 --scope user --silent --accept-package-agreements --accept-source-agreements
    # Only the installer needs it; build and tests go on without it.
    if ($LASTEXITCODE -ne 0) { Write-Warning "Installing Inno Setup failed ($LASTEXITCODE); scripts\build-installer.ps1 will not work until it is installed." }
} else {
    $iscc
}

# The maintainer is recognised by GitHub account id, which survives a change of user name (the account was renamed
# once already). Everything that changes git or Claude Code settings beyond this script's own build runs only for them.
$account = if (Get-Command gh -ErrorAction SilentlyContinue) { Invoke-Tolerant { gh api user --jq '.id,.login' 2>$null } } else { $null }
$isMaintainer = [bool]($account -and $account[0] -eq '79949229')

Step 'Commit identity (this repository only)'
if ($isMaintainer) {
    # The GitHub no-reply address keeps personal email out of the public history; GitHub links it to the account by
    # the number in front.
    git config user.name $account[1]
    git config user.email "79949229+$($account[1])@users.noreply.github.com"
} else {
    'Left as is. Commit with an address you are happy to publish, for example your GitHub no-reply address.'
}
"{0} <{1}>" -f (git config user.name), (git config user.email)

Step 'Global git URL rewrite'
if (-not $isMaintainer) {
    'Skipped (changes global git settings; maintainer machines only).'
} else {
    $rewrites = git config --global --get-regexp '^url\..*\.insteadof$' 2>$null
    if ($rewrites -match '^url\.https://github\.com\.insteadof git@github\.com:$') {
        # Missing trailing slash: git@github.com:owner/repo became https://github.comowner/repo.
        git config --global --unset 'url.https://github.com.insteadof'
        git config --global 'url.https://github.com/.insteadOf' 'git@github.com:'
        'Repaired url.https://github.com/.insteadOf git@github.com:'
    } else {
        'OK'
    }
}

Step 'Claude Code plugin: agent-skills@addy-agent-skills'
if (-not $isMaintainer) {
    'Skipped. To follow the same process with Claude Code: /plugin install agent-skills@addy-agent-skills'
} elseif (Get-Command claude -ErrorAction SilentlyContinue) {
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
& dotnet build Flevert.sln -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# One project at a time: both start real flevert.exe processes and time them, which is flaky when they run together.
foreach ($project in 'tests\Flevert.Core.Tests', 'tests\Flevert.Image.Tests') {
    & dotnet test $project --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed in $project." }
}
Write-Host "`nReady." -ForegroundColor Green
