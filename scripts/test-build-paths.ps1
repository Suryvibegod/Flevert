<#
.SYNOPSIS
Fails when one of Flevert's own assemblies names the folder it was built in (docs/specs/pdf.md, v0.2.1, P7).

.DESCRIPTION
A release binary should not say where it was built: the path of the build machine's checkout ends up in the PDB path
of every assembly and in the file names of CallerFilePath. CI builds with ContinuousIntegrationBuild set, so the
compiler writes /_/ for the checkout instead; this checks that it did, in UTF-8 and in UTF-16.

.PARAMETER Folder
Where the assemblies are, searched with its subfolders: Flevert*.dll and flevert.dll.

.PARAMETER BuildPath
The path that must not appear; defaults to the root of this repository.
#>
param(
    [Parameter(Mandatory)][string]$Folder,
    [string]$BuildPath = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$files = @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Include 'Flevert*.dll', 'flevert.dll')
if ($files.Count -eq 0) { throw "No Flevert assemblies in $Folder." }

# Bytes compared as Latin-1 text, one character a byte: the path in UTF-8 and in UTF-16, in any case.
$latin1 = [Text.Encoding]::GetEncoding(28591)
$needles = @(
    $latin1.GetString([Text.Encoding]::UTF8.GetBytes($BuildPath.TrimEnd('\'))),
    $latin1.GetString([Text.Encoding]::Unicode.GetBytes($BuildPath.TrimEnd('\')))
)
$named = @(foreach ($file in $files) {
    $text = $latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
    if ($needles | Where-Object { $text.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) { $file.FullName }
})
if ($named.Count -gt 0) {
    throw "These assemblies name the folder they were built in ($BuildPath): $($named -join ', '). Build with -p:ContinuousIntegrationBuild=true."
}
"No build path in $($files.Count) assemblies."
