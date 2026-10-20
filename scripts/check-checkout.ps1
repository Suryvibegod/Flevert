<#
.SYNOPSIS
  Warns when accounts other than this user, SYSTEM and Administrators can change the checkout, and prints the icacls
  commands that fix it.
.DESCRIPTION
  Anyone who can change the checkout can replace the binaries in deps\ or add a git hook, which then run as the developer
  in the next build, test or commit. A checkout on a second NTFS drive usually inherits Authenticated Users Modify from
  the drive's root. Checked, for each folder given:
  - the folder itself: its owner, and every entry that grants a write right, inherit-only ones included (every file
    inside inherits them);
  - deps\, .git\hooks and hooks\ inside it, where a file or folder with entries of its own keeps them after the fix;
  - the folders above it: whoever can rename one of them can put a checkout of their own in its place.
  Changes nothing: setup-dev.ps1 and fetch-deps.ps1 run it, and the developer runs the printed commands.
.PARAMETER Path
  The folders to check: the checkout, and its git folder when that lies elsewhere (a worktree).
.PARAMETER StopAt
  Tests only: the walk up the parents stops before this folder.
#>
param([Parameter(Mandatory)][string[]]$Path, [string]$StopAt)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$me = [Security.Principal.WindowsIdentity]::GetCurrent().User
$trusted = @(
    $me,
    [Security.Principal.SecurityIdentifier]'S-1-5-18', # SYSTEM
    [Security.Principal.SecurityIdentifier]'S-1-5-32-544', # Administrators
    [Security.Principal.SecurityIdentifier]'S-1-3-0', # CREATOR OWNER: whoever creates a file, who must be able to write here
    [Security.Principal.SecurityIdentifier]'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') # TrustedInstaller

$GenericAll = 0x10000000
$GenericWrite = 0x40000000
# Rights that let someone change, replace or delete what is in a folder, or take it over: InstallLocation.WriteRights in
# Flevert.Core.
$writeRights = [int][Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership' -bor $GenericAll -bor $GenericWrite
# Rights on a folder above that let someone rename it away and rebuild the path: InstallLocation.ParentRights. A drive's
# root cannot be renamed, so there only the right to delete what is in it counts.
$parentRights = [int][Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership' -bor $GenericAll
$rootRights = [int][Security.AccessControl.FileSystemRights]'DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership' -bor $GenericAll

function Get-Label([Security.Principal.SecurityIdentifier]$Sid) {
    try { "$($Sid.Value) ($($Sid.Translate([Security.Principal.NTAccount]).Value))" } catch { $Sid.Value }
}

# The untrusted accounts that own $Item or hold one of $Rights on it, as SIDs.
function Get-Writers($Item, [int]$Rights, [switch]$CountInheritOnly, [switch]$OwnOnly) {
    $acl = $Item.GetAccessControl()
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    $writers = [ordered]@{}
    if ($trusted -notcontains $owner) { $writers[$owner.Value] = 'owner' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or $trusted -contains $rule.IdentityReference) { continue }
        if ($OwnOnly -and $rule.IsInherited) { continue }
        if (-not $CountInheritOnly -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
        if ([int]$rule.FileSystemRights -band $Rights) { $writers[$rule.IdentityReference.Value] = 'entry' }
    }
    return $writers
}

function Write-Fix([string]$Folder, $Writers, [string]$Why) {
    $labels = @($Writers.Keys | ForEach-Object { Get-Label ([Security.Principal.SecurityIdentifier]$_) })
    Write-Warning (("$Why {0}. To allow only you, SYSTEM and Administrators (files inside follow unless they have " +
        "entries of their own), run:") -f ($labels -join ', '))
    if ($Writers.Values -contains 'owner') { Write-Host ('  icacls "{0}" /setowner "*{1}"' -f $Folder, $me.Value) }
    # SIDs rather than names, which differ in other Windows languages. /remove:g drops the entries the folder has of its
    # own; /inheritance:r the ones it inherits.
    $remove = @($Writers.Keys | ForEach-Object { "*$_" }) -join ' '
    Write-Host ('  icacls "{0}" /inheritance:r /grant:r "*{1}:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /remove:g {2}' -f $Folder, $me.Value, $remove)
}

foreach ($folder in $Path) {
    $root = Get-Item -LiteralPath $folder -Force
    $writers = Get-Writers $root $writeRights -CountInheritOnly
    if ($writers.Count -gt 0) {
        Write-Fix $root.FullName $writers "Other accounts can change $($root.FullName), so they could replace the binaries in deps\ or add a git hook that runs as you:"
    }

    # Files and folders with entries of their own, which the fix above leaves as they are.
    foreach ($inner in 'deps', '.git\hooks', 'hooks') {
        $top = Join-Path $root.FullName $inner
        if (-not (Test-Path -LiteralPath $top -PathType Container)) { continue }
        $own = [ordered]@{}
        foreach ($entry in @(Get-Item -LiteralPath $top -Force) + @(Get-ChildItem -LiteralPath $top -Recurse -Force)) {
            $found = Get-Writers $entry $writeRights -CountInheritOnly -OwnOnly
            if ($found.Count -gt 0) { $own[$entry.FullName.Substring($root.FullName.TrimEnd('\').Length + 1)] = $found }
        }
        if ($own.Count -eq 0) { continue }
        $labels = @($own.Values | ForEach-Object { $_.Keys } | Sort-Object -Unique | ForEach-Object { Get-Label ([Security.Principal.SecurityIdentifier]$_) })
        Write-Warning (("{0} has entries of its own for other accounts ({1}) in {2}. After fixing the folder, give them " +
            "back the folder's permissions:") -f $top, ($labels -join ', '), ($own.Keys -join ', '))
        Write-Host ('  icacls "{0}" /reset /T /C /Q' -f $top)
    }

    for ($parent = $root.Parent; $parent; $parent = $parent.Parent) {
        if ($StopAt -and $parent.FullName.TrimEnd('\') -eq $StopAt.TrimEnd('\')) { break }
        # Inherit-only entries do not apply to the parent itself; what they pass down is checked where it lands.
        $rights = if ($parent.Parent) { $parentRights } else { $rootRights }
        $found = Get-Writers $parent $rights
        if ($found.Count -gt 0) {
            Write-Fix $parent.FullName $found "Other accounts can rename or take over $($parent.FullName), which holds $($root.FullName), and put a folder of their own in its place:"
        }
    }
}
