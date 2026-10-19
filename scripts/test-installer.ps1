<#
.SYNOPSIS
End-to-end test of the installer on this machine: installs silently, checks files, menu and a real conversion,
installs over itself, uninstalls and checks that nothing is left. Also checks the uninstaller's registry safety net and
that a classic-menu key the user created survives.

Uses the real per-user install location and HKCU, so it refuses to run when ConvertAnything is already installed.

.PARAMETER Setup
The setup executable; defaults to the newest artifacts\installer\ConvertAnything-Setup-*.exe.
#>
param([string]$Setup)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$appId = '{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}_is1'
$arpKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$app = Join-Path $env:LOCALAPPDATA 'Programs\ConvertAnything'
$appData = Join-Path $env:LOCALAPPDATA 'ConvertAnything'
$classes = 'HKCU:\Software\Classes'
$classicKey = "$classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"
$heic = Join-Path $root 'tests\ConvertAnything.Image.Tests\fixtures\gradient-320x240.heic'

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "  ok  $Message"
}

function MenuKeys {
    @(Get-ChildItem "$classes\SystemFileAssociations" -ErrorAction SilentlyContinue |
        Where-Object { Test-Path "$($_.PSPath)\shell\ConvertAnything" }) +
    @(Get-ChildItem $classes -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'ConvertAnything.Menu*' })
}

function Install([string]$ClassicMenu = '') {
    $log = Join-Path $work "install-$([guid]::NewGuid()).log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"")
    if ($ClassicMenu) { $arguments += "/CLASSICMENU=$ClassicMenu" }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Setup -ArgumentList $arguments -Wait -PassThru
    Assert ($p.ExitCode -eq 0) "setup exits with 0 (got $($p.ExitCode); log $log)"
    Write-Host "      installed in $([int]$timer.Elapsed.TotalSeconds) s"
    return $timer.Elapsed
}

function Uninstall {
    Assert (Test-Path "$app\unins000.exe") 'the uninstaller is installed'
    $p = Start-Process "$app\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Assert ($p.ExitCode -eq 0) "uninstaller exits with 0 (got $($p.ExitCode))"
    # The uninstaller hands over to a copy of itself in %TEMP% and returns at once; wait for that copy to finish.
    $deadline = (Get-Date).AddSeconds(120)
    while (((Test-Path $arpKey) -or (Test-Path $app)) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}

function AssertClean {
    Assert (-not (Test-Path $app)) "no install folder ($app)"
    Assert (-not (Test-Path $appData)) "no app data ($appData)"
    Assert (-not (Test-Path $arpKey)) 'no entry in Installed apps'
    $left = @(MenuKeys)
    Assert ($left.Count -eq 0) "no menu keys left ($(($left | ForEach-Object { $_.PSChildName }) -join ', '))"
}

function RunCa([string[]]$Arguments) {
    $p = Start-Process "$app\ca.exe" -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

if (-not $Setup) {
    $Setup = Get-ChildItem (Join-Path $root 'artifacts\installer\ConvertAnything-Setup-*.exe') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Setup -or -not (Test-Path $Setup)) { throw 'No installer found. Run scripts\build-installer.ps1 first.' }
if (Test-Path $arpKey) { throw 'ConvertAnything is installed on this machine; uninstall it before running this test.' }
if (Test-Path $classicKey) { throw "The classic-menu key exists already ($classicKey); this test needs to create and remove it." }

$work = Join-Path $env:TEMP "ca-installer-test-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
Write-Host "Testing $Setup"
try {
    Write-Host 'Scenario 1: install, convert, install again, uninstall'
    $elapsed = Install '1'
    Assert ($elapsed.TotalSeconds -lt 30) 'installs in under 30 s'
    Assert (Test-Path "$app\ca.exe") 'ca.exe is installed'
    Assert (Test-Path "$app\LICENSE.txt") 'LICENSE.txt is installed'
    $notices = Get-Content "$app\THIRD-PARTY-NOTICES.txt" -Raw -ErrorAction SilentlyContinue
    Assert ($notices -and $notices.Contains('ImageMagick License') -and $notices.Contains('.NET Foundation')) 'third-party notices cover ImageMagick and .NET'
    Assert (Test-Path $arpKey) 'listed in Installed apps'
    $command = (Get-ChildItem "$classes\ConvertAnything.Menu.heic" -Recurse |
        Where-Object { $_.PSChildName -eq 'command' } | Select-Object -First 1).GetValue('')
    Assert ($command -like "`"$app\ca.exe`" run *") "the .heic menu runs the installed ca.exe ($command)"
    Assert (Test-Path "$classes\SystemFileAssociations\.jpg\shell\ConvertAnything") 'menu entry for .jpg'
    Assert ((Get-ItemProperty $classicKey).ConvertAnything -eq '1') 'classic menu key created and marked'
    $menuCount = @(MenuKeys).Count

    Copy-Item $heic "$work\photo.heic"
    $env:CONVERTANYTHING_HEADLESS = '1'
    try { $code = RunCa @('run', 'image.to-jpg', "`"$work\photo.heic`"") } finally { Remove-Item Env:\CONVERTANYTHING_HEADLESS }
    Assert ($code -eq 0) "installed ca.exe converts HEIC to JPG (exit $code)"
    Assert (Test-Path "$work\photo.jpg") 'photo.jpg written'

    Install | Out-Null
    Assert (@(MenuKeys).Count -eq $menuCount) 'installing again leaves the same menu'
    Assert (Test-Path $classicKey) 'installing again without /CLASSICMENU keeps the earlier choice'
    Install '0' | Out-Null
    Assert (-not (Test-Path $classicKey)) 'installing again with /CLASSICMENU=0 removes the classic menu key'

    Uninstall
    AssertClean

    Write-Host 'Scenario 2: the registry is cleaned even when ca.exe cannot run'
    Install '1' | Out-Null
    Remove-Item "$app\ca.exe"
    Uninstall
    AssertClean
    Assert (-not (Test-Path $classicKey)) 'marked classic menu key removed without ca.exe'

    Write-Host "Scenario 3: a classic menu key the user created survives"
    New-Item "$classicKey\InprocServer32" -Force | Out-Null
    Set-ItemProperty "$classicKey\InprocServer32" -Name '(default)' -Value ''
    Install '1' | Out-Null
    Assert ($null -eq (Get-ItemProperty $classicKey).PSObject.Properties['ConvertAnything']) 'user key left unmarked'
    Uninstall
    AssertClean
    Assert (Test-Path "$classicKey\InprocServer32") "user's classic menu key kept"

    Write-Host 'PASS'
}
finally {
    if (Test-Path "$app\unins000.exe") { Uninstall }
    if (Test-Path $classicKey) { Remove-Item $classicKey -Recurse -Force }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
