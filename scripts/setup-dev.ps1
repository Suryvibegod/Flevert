<#
.SYNOPSIS
  Prepares a Windows machine to work on ConvertAnything and checks that the repository builds and its tests pass.
.DESCRIPTION
  Safe to run repeatedly; each step is skipped when already done.
  1. Installs the .NET 10 SDK and the GitHub CLI with winget if missing.
  2. Sets this repository's commit identity.
  3. Repairs a known-broken global git rewrite (https://github.com without the trailing slash) that breaks plugin installs.
  4. Installs the agent-skills plugin for Claude Code (project scope) if the claude CLI is available.
  5. Builds the solution and runs the tests.
#>
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repo

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

Step '.NET 10 SDK'
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks -match '^10\.')) {
    winget install --id Microsoft.DotNet.SDK.10 -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    Refresh-Path
}
& dotnet --list-sdks | Select-String '^10\.'

Step 'GitHub CLI'
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    winget install --id GitHub.cli -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    Refresh-Path
}
$auth = & gh auth status 2>&1 | Out-String
if ($auth -notmatch 'Logged in') {
    Write-Warning 'Not signed in to GitHub. Run:  gh auth login --web   (account Suryvibegod), then push works.'
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
