<#
.SYNOPSIS
  Warns when accounts other than this user, SYSTEM and Administrators can change a folder of the checkout.
.DESCRIPTION
  Anyone who can change the checkout can replace the binaries in deps\ or add a git hook, which then run as the developer
  in the next build, test or commit. A checkout on a second NTFS drive usually inherits Authenticated Users Modify from
  the drive's root. Changes nothing: it prints who can write and the icacls command that limits the folder to this
  user, SYSTEM and Administrators (existing files and folders inside inherit the change). Run by setup-dev.ps1.
.PARAMETER Path
  The folders to check: the checkout, and its git folder when that lies elsewhere (a worktree).
#>
param([Parameter(Mandatory)][string[]]$Path)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$me = [Security.Principal.WindowsIdentity]::GetCurrent().User
$trusted = @(
    $me,
    [Security.Principal.SecurityIdentifier]'S-1-5-18', # SYSTEM
    [Security.Principal.SecurityIdentifier]'S-1-5-32-544', # Administrators
    [Security.Principal.SecurityIdentifier]'S-1-3-0', # CREATOR OWNER: whoever creates a file, who must be able to write here
    [Security.Principal.SecurityIdentifier]'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') # TrustedInstaller

# Rights that let someone change, replace or delete what is in the folder, or take it over. The same list as
# InstallLocation.WriteRights in Flevert.Core.
$writeRights = [int](
    [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership') -bor
    0x10000000 -bor 0x40000000 # GENERIC_ALL, GENERIC_WRITE

function Get-Name([Security.Principal.SecurityIdentifier]$Sid) {
    try { $Sid.Translate([Security.Principal.NTAccount]).Value } catch { $Sid.Value }
}

foreach ($folder in $Path) {
    $acl = Get-Acl -LiteralPath $folder
    $writers = @()
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($trusted -notcontains $owner) { $writers += "$(Get-Name $owner) (owner)" }
    # Entries that apply only to what is created inside count too: every file of the checkout was created inside.
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ([int]$rule.FileSystemRights -band $writeRights) -and
            $trusted -notcontains $rule.IdentityReference) {
            $writers += Get-Name $rule.IdentityReference
        }
    }
    if ($writers.Count -eq 0) { continue }

    Write-Warning (("Other accounts can change {0}: {1}. They could replace the binaries in deps\ or add a git hook that " +
        "runs as you. To allow only you, SYSTEM and Administrators (files inside follow), run:") -f $folder, (($writers | Sort-Object -Unique) -join ', '))
    # SIDs rather than names, which differ in other Windows languages.
    Write-Host ('  icacls "{0}" /inheritance:r /grant:r "*{1}:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"' -f $folder, $me.Value)
}
