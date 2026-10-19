<#
.SYNOPSIS
End-to-end test of the installer on this machine: installs silently without admin, checks files, menu and a real
conversion, installs over itself, uninstalls and checks that nothing ConvertAnything wrote is left. Further scenarios
cover the uninstall safety net, a classic-menu key the user created, a refused install folder, /DIR, and uninstalling
while ca.exe runs.

Uses the real per-user install location and HKCU, so it refuses to run when anything of ConvertAnything is present.

.PARAMETER Setup
The setup executable; defaults to the newest artifacts\installer\ConvertAnything-Setup-*.exe.
#>
param([string]$Setup)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}_is1'
$app = Join-Path $env:LOCALAPPDATA 'Programs\ConvertAnything'
$appData = Join-Path $env:LOCALAPPDATA 'ConvertAnything'
$classes = 'HKCU:\Software\Classes'
$associations = "$classes\SystemFileAssociations"
$classicKey = "$classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"
$heic = Join-Path $root 'tests\ConvertAnything.Image.Tests\fixtures\gradient-320x240.heic'
$exitMenuFailed = 10

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "  ok  $Message"
}

function MenuKeys {
    @(Get-ChildItem $associations -ErrorAction SilentlyContinue |
        Where-Object { Test-Path "$($_.PSPath)\shell\ConvertAnything" }) +
    @(Get-ChildItem $classes -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'ConvertAnything.Menu*' })
}

function AssociationNames { @(Get-ChildItem $associations -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName }) }

function RunningCa { @(Get-Process ca -ErrorAction SilentlyContinue) }

function Setup([string[]]$Arguments) {
    $log = Join-Path $work "setup-$([guid]::NewGuid()).log"
    $all = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $Arguments
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Setup -ArgumentList $all -Wait -PassThru
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Log = $log; Seconds = $timer.Elapsed.TotalSeconds }
}

function Install([string]$ClassicMenu = '') {
    $arguments = @()
    if ($ClassicMenu) { $arguments += "/CLASSICMENU=$ClassicMenu" }
    $run = Setup $arguments
    Assert ($run.ExitCode -eq 0) "setup exits with 0 (got $($run.ExitCode); log $($run.Log))"
    Write-Host ("      installed in {0:N0} s" -f $run.Seconds)
    return $run
}

function WaitForUninstaller {
    # unins000.exe hands over to a copy of itself in %TEMP%\is-*-uninstall.tmp and returns at once.
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Process | Where-Object { $_.Path -like '*-uninstall.tmp\_unins.tmp' }) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
}

function Uninstall {
    Assert (Test-Path "$app\unins000.exe") 'the uninstaller is installed'
    Start-Process "$app\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep -Milliseconds 500
    WaitForUninstaller
}

function AssertClean {
    Assert (-not (Test-Path $app)) "no install folder ($app)"
    Assert (-not (Test-Path $appData)) "no app data ($appData)"
    Assert (-not (Test-Path $arpKey)) 'no entry in Installed apps'
    $left = @(MenuKeys)
    Assert ($left.Count -eq 0) "no menu keys left ($(($left | ForEach-Object { $_.PSChildName }) -join ', '))"
    $added = @(AssociationNames | Where-Object { $associationsBefore -notcontains $_ })
    Assert ($added.Count -eq 0) "no SystemFileAssociations keys left behind ($($added -join ', '))"
    Assert (-not (Test-Path $classicKey) -or $userClassicKey) 'no classic menu key left'
}

function RunCaHeadless([string[]]$Arguments, [switch]$NoWait) {
    $env:CONVERTANYTHING_HEADLESS = '1'
    try { return Start-Process "$app\ca.exe" -ArgumentList $Arguments -PassThru -WindowStyle Hidden -Wait:(-not $NoWait) }
    finally { Remove-Item Env:\CONVERTANYTHING_HEADLESS }
}

if (-not $Setup) {
    $Setup = Get-ChildItem (Join-Path $root 'artifacts\installer\ConvertAnything-Setup-*.exe') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Setup -or -not (Test-Path $Setup)) { throw 'No installer found. Run scripts\build-installer.ps1 first.' }
if (Test-Path $arpKey) { throw 'ConvertAnything is installed on this machine; uninstall it before running this test.' }
if (Test-Path $app) { throw "$app exists; remove it before running this test." }
if (Test-Path $appData) { throw "$appData exists (logs of a development build?); remove it before running this test." }
if (@(MenuKeys).Count -gt 0) { throw 'A ConvertAnything menu is registered (ca.exe install-menu?); run ca.exe uninstall-menu first.' }
if (Test-Path $classicKey) { throw "The classic-menu key exists already ($classicKey); this test needs to create and remove it." }
if (@(RunningCa).Count -gt 0) { throw 'ca.exe is running; a running batch leader would take over the conversions this test checks.' }
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($elevated) { throw 'Run this test without administrator rights: it checks that setup needs none.' }

$associationsBefore = AssociationNames
$userClassicKey = $false
$work = Join-Path $env:TEMP "ca-installer-test-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
Write-Host "Testing $Setup"
try {
    Write-Host 'Scenario 1: install, convert, install again, uninstall'
    $run = Install '1'
    Assert ($run.Seconds -lt 30) 'installs in under 30 s'
    Assert ((Get-Content $run.Log -Raw).Contains('Administrative install mode: No')) 'installed without administrator rights'
    Assert (Test-Path "$app\ca.exe") 'ca.exe is installed'
    Assert (Test-Path "$app\LICENSE.txt") 'LICENSE.txt is installed'
    $notices = Get-Content "$app\THIRD-PARTY-NOTICES.txt" -Raw -ErrorAction SilentlyContinue
    Assert ($notices -and $notices.Contains('ImageMagick License') -and $notices.Contains('.NET Foundation')) 'third-party notices cover ImageMagick and .NET'
    Assert (@(Get-ChildItem $app -Recurse -Filter *.pdb).Count -eq 0) 'no debug symbols shipped'
    Assert (Test-Path $arpKey) 'listed in Installed apps'
    $command = (Get-ChildItem "$classes\ConvertAnything.Menu.heic" -Recurse |
        Where-Object { $_.PSChildName -eq 'command' } | Select-Object -First 1).GetValue('')
    Assert ($command -like "`"$app\ca.exe`" run *") "the .heic menu runs the installed ca.exe ($command)"
    Assert (Test-Path "$associations\.jpg\shell\ConvertAnything") 'menu entry for .jpg'
    Assert ((Get-ItemProperty $classicKey).ConvertAnything -eq '1') 'classic menu key created and marked'
    $menuCount = @(MenuKeys).Count

    Copy-Item $heic "$work\photo.heic"
    $code = (RunCaHeadless @('run', 'image.to-jpg', "`"$work\photo.heic`"")).ExitCode
    Assert ($code -eq 0) "installed ca.exe converts HEIC to JPG (exit $code)"
    $jpg = [Drawing.Image]::FromFile("$work\photo.jpg")
    try { Assert ($jpg.RawFormat.Equals([Drawing.Imaging.ImageFormat]::Jpeg) -and $jpg.Width -eq 320 -and $jpg.Height -eq 240) 'photo.jpg is a 320x240 JPEG' }
    finally { $jpg.Dispose() }
    Assert (Test-Path "$appData\logs") 'the conversion wrote its log under app data'

    Install | Out-Null
    Assert (@(MenuKeys).Count -eq $menuCount) 'installing again leaves the same menu'
    Assert (Test-Path $classicKey) 'installing again without /CLASSICMENU keeps the earlier choice'
    Install '0' | Out-Null
    Assert (-not (Test-Path $classicKey)) 'installing again with /CLASSICMENU=0 removes the classic menu key'

    Uninstall
    AssertClean

    Write-Host 'Scenario 2: the registry is cleaned even when ca.exe cannot run'
    Install '1' | Out-Null
    [IO.File]::WriteAllBytes("$app\ca.exe", [byte[]](1..64))
    Uninstall
    AssertClean

    Write-Host 'Scenario 3: a classic menu key the user created survives'
    New-Item "$classicKey\InprocServer32" -Force | Out-Null
    Set-ItemProperty "$classicKey\InprocServer32" -Name '(default)' -Value ''
    $userClassicKey = $true
    Install '1' | Out-Null
    Assert ($null -eq (Get-ItemProperty $classicKey).PSObject.Properties['ConvertAnything']) 'user key left unmarked'
    Uninstall
    AssertClean
    Assert (Test-Path "$classicKey\InprocServer32") "user's classic menu key kept"
    Remove-Item $classicKey -Recurse -Force
    $userClassicKey = $false

    Write-Host 'Scenario 4: an install folder other accounts can change is refused, and setup says so'
    New-Item -ItemType Directory $app | Out-Null
    $acl = Get-Acl $app
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl $app $acl
    $run = Setup @('/CLASSICMENU=0')
    Assert ($run.ExitCode -eq $exitMenuFailed) "setup exits with $exitMenuFailed when the menu is refused (got $($run.ExitCode))"
    Assert ((Get-Content $run.Log -Raw).Contains('other accounts can modify its folder')) "ca.exe's reason reaches setup"
    Assert (@(MenuKeys).Count -eq 0) 'no menu registered'
    Uninstall
    AssertClean

    Write-Host 'Scenario 5: /DIR cannot move the install elsewhere'
    $elsewhere = Join-Path $work 'elsewhere'
    $run = Setup @("/DIR=`"$elsewhere`"")
    Assert ($run.ExitCode -ne 0) "setup refuses /DIR (exit $($run.ExitCode))"
    Assert (-not (Test-Path "$elsewhere\ca.exe")) 'nothing installed there'
    AssertClean

    Write-Host 'Scenario 6: uninstalling while ca.exe runs never leaves a partial install'
    Install '0' | Out-Null
    $inputs = 1..6 | ForEach-Object { $f = "$work\slow$_.txt"; Set-Content $f 'x'; "`"$f`"" }
    $job = RunCaHeadless (@('run', 'dev.slow-copy') + $inputs) -NoWait
    Start-Sleep -Seconds 1
    Assert (-not $job.HasExited) 'ca.exe is running'
    Uninstall
    $job.WaitForExit()
    # Either the uninstaller waited for ca.exe and removed everything, or it declined and left everything; never half.
    if ((Test-Path $arpKey) -and (Test-Path "$app\ca.exe") -and @(MenuKeys).Count -gt 0) {
        Write-Host '      the uninstaller declined while ca.exe ran; uninstalling again'
        Uninstall
    }
    AssertClean

    Write-Host 'PASS'
}
finally {
    WaitForUninstaller
    if (Test-Path "$app\unins000.exe") { Uninstall }
    if (Test-Path $classicKey) { Remove-Item $classicKey -Recurse -Force }
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
