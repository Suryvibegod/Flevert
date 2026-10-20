<#
.SYNOPSIS
Gets the setup of the previous release, for the upgrade scenario of test-installer.ps1 (docs\RELEASING.md, step 5):
downloads it from its GitHub release, or copies the file given with -Path, into a new folder that only this user, SYSTEM
and Administrators can change, and checks its SHA256 against the pin below before anyone runs it. Prints the path of
the checked copy; the caller deletes its folder.

After each release, pin that release's setup here (its URL and the SHA256 in its SHA256SUMS.txt).

.PARAMETER Path
A copy downloaded earlier (no network). It is copied and checked like a download.

.PARAMETER Destination
The folder to create for the copy; it must not exist yet. Defaults to a new folder in %TEMP%.

.PARAMETER Sha256
The expected hash; defaults to the pin. Tests pass their own file's.
#>
param(
    [string]$Path,
    [string]$Destination,
    [string]$Sha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower

$version = '0.1.0'
# 0.1.0 was released as Flevert (ADR-0009); GitHub redirects the old repository path to Flevert.
$url = "https://github.com/Suryvibegod/Flevert/releases/download/v$version/Flevert-Setup-$version.exe"
if (-not $Sha256) { $Sha256 = 'aa85392b25b34aed58d6bc9f1e514d8feed38cb0f28b86c5901a91f396bf92e3' }
if (-not $Destination) { $Destination = Join-Path ([IO.Path]::GetTempPath()) "flevert-previous-setup-$([guid]::NewGuid())" }
# .NET resolves relative paths against its own current folder, not PowerShell's.
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)

# The same as in fetch-deps.ps1: %TEMP% may let other accounts change what is created in it (on the maintainer's machine
# it grants a sandbox Modify), and they could swap the setup between the hash check and the test running it.
function New-PrivateFolder([string]$Folder) {
    if (Test-Path -LiteralPath $Folder) { throw "$Folder exists already; give a folder that does not exist yet." }
    $trusted = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]'S-1-5-18', # SYSTEM
        [Security.Principal.SecurityIdentifier]'S-1-5-32-544') # Administrators
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false) # inherits nothing
    foreach ($sid in $trusted) {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    # Created with that ACL, so the entries of the parent never apply to it, not even for a moment.
    $aclExtensions = 'System.IO.FileSystemAclExtensions' -as [type]
    # [void]: the script's only output is the setup's path.
    if ($aclExtensions) { [void]$aclExtensions::Create([IO.DirectoryInfo]::new($Folder), $security) } # PowerShell 7 (.NET)
    else { [void][IO.Directory]::CreateDirectory($Folder, $security) } # Windows PowerShell (.NET Framework)
    # Both return quietly when the folder appeared in between: make sure it is the private, empty folder asked for.
    $acl = Get-Acl -LiteralPath $Folder
    $others = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $trusted -notcontains $_.IdentityReference })
    if (-not $acl.AreAccessRulesProtected -or $others.Count -gt 0 -or
        $trusted -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]) -or
        @(Get-ChildItem -LiteralPath $Folder -Force).Count -gt 0) {
        throw "$Folder is not a new folder of this user's own."
    }
}

New-PrivateFolder $Destination
$setup = Join-Path $Destination "Flevert-Setup-$version.exe"
try {
    if ($Path) { Copy-Item -LiteralPath $Path $setup }
    else { Invoke-WebRequest $url -OutFile $setup -UseBasicParsing }
    $actual = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        throw "SHA256 mismatch for the Flevert $version setup: expected $Sha256, got $actual. It was not kept."
    }
}
catch {
    Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
    throw
}
$setup
