<#
.SYNOPSIS
  Prepares a Windows machine to work on Flevert and checks that the repository builds and its tests pass.
.DESCRIPTION
  Safe to run repeatedly; each step is skipped when already done.
  1. Installs the .NET 10 SDK per user (no admin rights needed) if missing, for this session (and, for the maintainer,
     in the user PATH and DOTNET_ROOT); warns if the GitHub CLI is missing or signed out.
  2. On the maintainer's machines only (signed in to GitHub as account 79949229): installs Inno Setup, sets this
     repository's commit identity, repairs a known-broken global git rewrite that breaks plugin installs, and installs
     the Claude Code plugins pinned in .claude/settings.json. Contributors keep their own tools, identity and global settings; for those
     steps the script only prints advice.
  3. Warns when accounts other than this user, SYSTEM and Administrators can change the checkout (they could replace
     deps\ binaries or add git hooks), with the icacls command that fixes it; it changes no permissions itself.
  4. Fetches pdfium and qpdf, builds the solution and runs each test project in turn.
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

# The maintainer is recognised by GitHub account id, which survives a change of user name (the account was renamed
# once already). Installing tools other than the SDK, and changing user environment variables, git or Claude Code settings, happens
# only for them.
$account = if (Get-Command gh -ErrorAction SilentlyContinue) { Invoke-Tolerant { gh api user --jq '.id,.login' 2>$null } } else { $null }
$isMaintainer = [bool]($account -and $account[0] -eq '79949229')

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
    Use-UserDotnet
    if ($isMaintainer) {
        # Persist for new shells: user PATH and DOTNET_ROOT.
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -notlike "*$userDotnet*") {
            [Environment]::SetEnvironmentVariable('Path', "$userDotnet;$userPath", 'User')
        }
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $userDotnet, 'User')
    } else {
        # A user-wide DOTNET_ROOT holding only .NET 10 can stop other .NET apps from starting, so only this session uses it.
        "Installed to $userDotnet for this session only. In a new shell first run:"
        "  `$env:Path = `"$userDotnet;`$env:Path`"; `$env:DOTNET_ROOT = `"$userDotnet`""
    }
}
& dotnet --list-sdks | Select-String '^10\.'

Step 'GitHub CLI'
# The gh installer is per-machine and would block on a UAC prompt, so only advise here.
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Warning 'GitHub CLI missing. Run:  winget install --id GitHub.cli -e   then  gh auth login --web --scopes workflow'
} else {
    $auth = Invoke-Tolerant { & gh auth status 2>&1 | Out-String }
    if ($auth -notmatch 'Logged in') {
        Write-Warning 'Not signed in to GitHub. Run:  gh auth login --web --scopes workflow'
    } elseif ($auth -match 'Token scopes' -and $auth -notmatch 'workflow') {
        # Pushing changes under .github/workflows needs the workflow scope.
        Write-Warning 'GitHub token lacks the workflow scope. Run:  gh auth refresh -h github.com -s workflow'
    }
}

Step 'Inno Setup 6.7.3 (builds the installer)'
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" | Where-Object { Test-Path $_ }
if (-not $iscc -and -not $isMaintainer) {
    'Not installed. Only needed to build the installer:  winget install JRSoftware.InnoSetup -e --version 6.7.3 --scope user'
} elseif (-not $iscc) {
    # Per-user install: no UAC prompt.
    winget install --id JRSoftware.InnoSetup -e --version 6.7.3 --scope user --silent --accept-package-agreements --accept-source-agreements
    # Only the installer needs it; build and tests go on without it.
    if ($LASTEXITCODE -ne 0) { Write-Warning "Installing Inno Setup failed ($LASTEXITCODE); scripts\build-installer.ps1 will not work until it is installed." }
} else {
    $iscc
}

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

Step 'Claude Code plugins, pinned in .claude/settings.json'
# Both plugins come from marketplaces declared inline in .claude/settings.json, with each plugin pinned to a reviewed
# commit. `claude plugin marketplace add` would register the unpinned default branch instead, so it is not used here.
$pluginIds = 'agent-skills@addy-agent-skills', 'ceo-worker-agent@ceo-worker-agent'
$trustAdvice = 'Open Claude Code in this folder once, sign in if asked and accept the trust prompt (the inline marketplaces apply only in a trusted folder), then run this script again.'
if (-not $isMaintainer) {
    'Skipped. To follow the same process with Claude Code, open it in this folder, accept the trust prompt, then run:'
    $pluginIds | ForEach-Object { "  claude plugin install $_ --scope project" }
} elseif (Get-Command claude -ErrorAction SilentlyContinue) {
    # The CLI cannot register an inline marketplace; a session start in a trusted folder does, and re-registers one whose
    # declared source changed. A slash command runs no model turn; the variable makes the session wait for the fetch.
    $savedSync = $env:CLAUDE_CODE_SYNC_PLUGIN_INSTALL
    $env:CLAUDE_CODE_SYNC_PLUGIN_INSTALL = '1'
    try { Invoke-Tolerant { & claude -p '/reload-plugins' 2>&1 | Out-Null } }
    finally { $env:CLAUDE_CODE_SYNC_PLUGIN_INSTALL = $savedSync }
    $marketplaces = Invoke-Tolerant { & claude plugin marketplace list --json 2>$null | Out-String | ConvertFrom-Json }
    $unpinned = $pluginIds | Where-Object {
        $name = ($_ -split '@')[1]
        -not ($marketplaces | Where-Object { $_.name -eq $name -and $_.source -eq 'settings' })
    }
    if ($unpinned) {
        Write-Warning "Marketplace not registered from .claude/settings.json for $($unpinned -join ', '). $trustAdvice"
    } else {
        $installed = Invoke-Tolerant { & claude plugin list --json 2>$null | Out-String | ConvertFrom-Json }
        foreach ($id in $pluginIds) {
            $here = $installed | Where-Object { $_.id -eq $id -and $_.scope -eq 'project' -and $_.projectPath -eq $repo.Path }
            # update moves an existing install to the pinned commit and does nothing when it is there already.
            $action = if ($here) { 'update' } else { 'install' }
            $output = Invoke-Tolerant { & claude plugin $action $id --scope project 2>&1 | Out-String }
            if ($LASTEXITCODE -ne 0) { Write-Warning "claude plugin $action $id failed ($LASTEXITCODE): $($output.Trim())" }
            else { $output.Trim() }
        }
        'They load in Claude Code after a restart or /reload-plugins; until then read the skills from the plugin cache.'
    }
} else {
    Write-Warning "claude CLI not found. Install Claude Code; $trustAdvice"
}

Step 'Who else can change this checkout'
# Only warns: changing permissions is the developer's call. The git folder holds the hooks; in a worktree it lies elsewhere.
$gitFolder = Invoke-Tolerant { git rev-parse --path-format=absolute --git-common-dir 2>$null }
$folders = @($repo.Path) + @($gitFolder | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { (Resolve-Path $_).Path } |
    Where-Object { -not $_.StartsWith($repo.Path + '\', 'OrdinalIgnoreCase') })
try { & (Join-Path $PSScriptRoot 'check-checkout.ps1') -Path $folders }
catch { Write-Warning "Could not check who can change $($folders -join ', '): $_" }

Step 'Third-party tools (pdfium, qpdf), pinned by SHA256'
& (Join-Path $PSScriptRoot 'fetch-deps.ps1')

Step 'Build and test'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& dotnet build Flevert.sln -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# One project at a time, as on CI: run together, the image encodes starve the process-batching tests of CPU.
foreach ($project in 'tests\Flevert.Core.Tests', 'tests\Flevert.Image.Tests', 'tests\Flevert.Pdf.Tests', 'tests\Flevert.Media.Tests', 'tests\Flevert.Ocr.Tests') {
    & dotnet test $project --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed in $project." }
}
Write-Host "`nReady." -ForegroundColor Green
