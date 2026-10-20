; Inno Setup 6 script for Flevert. Build with scripts\build-installer.ps1; spec in docs/specs/installer.md.
; Per-user install without admin: the right-click menu lives in HKCU, so a machine-wide install would gain nothing.

#if VER < EncodeVer(6, 7, 3)
  #error Inno Setup 6.7.3 or later is required (RedirectionGuard, random temp folder names).
#endif

#define AppName "Flevert"
#define AppExe "flevert.exe"
#define AppUrl "https://github.com/Suryvibegod/Flevert"
; Held by every running flevert.exe (AppMutex.cs).
#define AppMutexName "Flevert.App"
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
; One place declares the version (Directory.Build.props); read it back from flevert.exe and drop the fourth part.
#define FileVersion GetVersionNumbersString(AddBackslash(SourceDir) + AppExe)
#if FileVersion == ""
  #error flevert.exe not found in SourceDir; run scripts\build-installer.ps1.
#endif
#define AppVersion Copy(FileVersion, 1, RPos(".", FileVersion) - 1)
; [Files] packs the whole folder, pdfium, qpdf and the MSVC runtime included: their licences must go with them.
#if !FileExists(AddBackslash(SourceDir) + "THIRD-PARTY-NOTICES.txt")
  #error THIRD-PARTY-NOTICES.txt not found in SourceDir; run scripts\build-installer.ps1, which writes it.
#endif

[Setup]
AppId={{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName} contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#FileVersion}
PrivilegesRequired=lowest
; A fixed folder: flevert.exe refuses to register a menu for a folder other accounts could change (InstallLocation), and
; setup runs flevert.exe from it. Hiding the page is not enough, /DIR still overrides it: PrepareToInstall enforces it.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
; 64-bit mode also makes the Pascal HKCU\Software\Classes\CLSID below the same registry view the x64 flevert.exe writes.
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
SetupIconFile=..\assets\app.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; While flevert.exe runs, Setup and Uninstall ask to wait (OK) or give up (Cancel); with /SUPPRESSMSGBOXES they give up. A per-user
; uninstaller cannot remove files in use, and they would stay behind with no uninstaller left. Restart Manager only
; matters for a flevert.exe that could not open the mutex.
AppMutex={#AppMutexName}
CloseApplications=yes
RestartApplications=no
; Tells Explorer the menu changed, also when only the uninstall safety net below could clean up.
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Shown for AppMutex. The default asks to close the program; a conversion ends by itself, or from its progress window.
SetupAppRunningError=Flevert is still running: a conversion, or its question about a new version.%n%nWait until the conversion has finished (or click Cancel in its progress window) and answer the question, then click OK to continue, or Cancel to exit.
UninstallAppRunningError=Flevert is still running: a conversion, or its question about a new version.%n%nWait until the conversion has finished (or click Cancel in its progress window) and answer the question, then click OK to continue, or Cancel to exit.

; The classic-menu choice is a check box on a custom page, not a [Tasks] entry: Inno Setup 6.7 clips the check box of the
; task list at high DPI (seen at 200%), while a plain check box is drawn by Windows. Silent installs pass /CLASSICMENU=1|0.
; The same goes for the update-check choice (/UPDATECHECK=1|0).

[Files]
; Before copying anything, setup runs flevert.exe check-location from its temporary folder (LocationProblem), and unpacks
; for it the entries whose DestDir is written {app}. What only a conversion loads, the PDF engines with qpdf's MSVC runtime
; and OpenSSL configuration, and ImageMagick's native library (about 40 MB), goes to {code:InstallFolder} instead: the
; same folder, left out of that unpacking. A file in neither list is unpacked, so a new managed assembly flevert.exe
; needs at start cannot be forgotten. InstallerScriptTests keeps this in step with scripts\deps.json. The menu icons
; (assets\menu\*.ico, which the build copies into icons\) go with the first entry to {app}\icons, where install-menu
; points the menu; the uninstaller removes them with the folder.
Source: "{#SourceDir}\*"; Excludes: "*.pdb,pdfium.dll,qpdf.exe,qpdf30.dll,msvcp140.dll,vcruntime140.dll,vcruntime140_1.dll,openssl.cnf,Magick.Native-*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\pdfium.dll"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion
Source: "{#SourceDir}\qpdf.exe"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion
Source: "{#SourceDir}\qpdf30.dll"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion
Source: "{#SourceDir}\msvcp140.dll"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion
Source: "{#SourceDir}\vcruntime140.dll"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion
Source: "{#SourceDir}\vcruntime140_1.dll"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion
Source: "{#SourceDir}\openssl.cnf"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion
Source: "{#SourceDir}\Magick.Native-*.dll"; DestDir: "{code:InstallFolder}"; Flags: ignoreversion

[InstallDelete]
; The runtime names this copy of mscordaccore.dll after its own version, and each release moves to the newest .NET patch:
; Inno never removes what an earlier version installed, so delete the old one before the new files are copied.
Type: files; Name: "{app}\mscordaccore_amd64_amd64_*.dll"

[UninstallDelete]
; What flevert.exe writes under %LOCALAPPDATA%\Flevert: logs, batch keys and the ImageMagick policy. Named one by one,
; so files someone else keeps in a folder of the same name survive.
Type: filesandordirs; Name: "{localappdata}\{#AppName}\logs"
Type: filesandordirs; Name: "{localappdata}\{#AppName}\pipes"
Type: filesandordirs; Name: "{localappdata}\{#AppName}\magick"
Type: filesandordirs; Name: "{localappdata}\{#AppName}\update"
Type: dirifempty; Name: "{localappdata}\{#AppName}"
; Inno removes only folders it created; this also removes an empty install folder that existed before.
Type: dirifempty; Name: "{app}"

[Code]
const
  Classes = 'Software\Classes';
  ClassicMenuKey = 'Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}';
  ClassicMenuMarker = 'Flevert';
  // Inno writes it for AppId; PrepareToInstall uses it to recognise an earlier install. InstallerScriptTests checks it.
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B7A3F52-2C64-4F0B-9E3D-5A1C7E2B9D40}_is1';
  // flevert.exe's exit code for a folder other accounts can change (ExitCodes.RefusedLocation): setup again cannot fix it.
  ExitRefusedLocation = 3;
  // Setup's exit code when it installed the files but could not set up the menu; Inno's own codes stop at 8.
  ExitMenuFailed = 10;
  FileAttributeReparsePoint = $400;
  InvalidFileAttributes = $FFFFFFFF;

var
  ClassicMenuCheck: TNewCheckBox; // nil before Windows 11, where the classic menu is the only one
  UpdateCheck: TNewCheckBox;
  MenuFailed: Boolean;
  ClassicMenuFailed: Boolean;
  FailureHelp: String; // README link for the last failure, repeated on the last page

function GetFileAttributes(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// The install folder, for the [Files] entries that LocationProblem must not unpack: it matches ExtractTemporaryFiles'
// pattern against DestDir as written, so {code:InstallFolder} is left out where {app} is not.
function InstallFolder(Param: String): String;
begin
  Result := ExpandConstant('{app}');
end;

function IsWindows11: Boolean;
begin
  // Major 10, build 22000 or later.
  Result := GetWindowsVersion >= $0A0055F0;
end;

// Runs flevert.exe from Folder and waits. False when it could not start or exited with an error. Reason is the first line
// flevert.exe wrote to stderr, for a message box; the log gets every line.
function RunFlevertIn(const Folder, Params: String; var Started: Boolean; var Code: Integer; var Reason: String): Boolean;
var
  Output: TExecOutput;
  I: Integer;
  Line: String;
begin
  Started := False;
  Code := -1;
  Reason := '';
  // An exception must not end the uninstall step before SweepRegistry, or skip reporting a failed install.
  try
    Started := ExecAndCaptureOutput(AddBackslash(Folder) + '{#AppExe}', Params, '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
    if Started then
      for I := 0 to GetArrayLength(Output.StdErr) - 1 do
      begin
        Line := Trim(Output.StdErr[I]);
        Log('flevert.exe ' + Params + ': ' + Line);
        if (Reason = '') and (Line <> '') then
          Reason := Line;
      end;
  except
    Started := False;
    Log('flevert.exe ' + Params + ': ' + GetExceptionMessage);
  end;
  Log(Format('flevert.exe %s: started=%d code=%d', [Params, Ord(Started), Code]));
  Result := Started and (Code = 0);
end;

function RunFlevert(const Params: String; var Started: Boolean; var Code: Integer; var Reason: String): Boolean;
begin
  Result := RunFlevertIn(ExpandConstant('{app}'), Params, Started, Code, Reason);
end;

procedure ReportFailure(const What: String; Started: Boolean; Code: Integer; const Reason: String);
var
  Text: String;
begin
  Text := Format('Flevert was installed, but %s failed (code %d).', [What, Code]);
  if Reason <> '' then
    Text := Text + #13#10#13#10 + Reason;
  // flevert.exe's own failures exit with 1-3. Anything else means it did not run: not started at all, or stopped while
  // loading, which is how Smart App Control blocking one of its DLLs shows up.
  if Started and (Code = ExitRefusedLocation) then
  begin
    FailureHelp := '{#AppUrl}#setup-refused-the-install-folder';
    Text := Text + #13#10#13#10 + 'Running setup again will not change this. See ' + FailureHelp;
  end
  else if Started and (Code >= 1) and (Code <= 3) then
  begin
    FailureHelp := '{#AppUrl}#if-the-menu-is-missing';
    Text := Text + #13#10#13#10 + 'See ' + FailureHelp;
  end
  else
  begin
    FailureHelp := '{#AppUrl}#smart-app-control';
    Text := Text + #13#10#13#10 + 'Windows blocked Flevert or it stopped unexpectedly. See ' + FailureHelp;
  end;
  SuppressibleMsgBox(Text, mbError, MB_OK, IDOK);
end;

procedure CreateUpdatePage(AfterID: Integer);
var
  Page: TWizardPage;
  Explanation: TNewStaticText;
  Choice: String;
  State: AnsiString;
begin
  Page := CreateCustomPage(AfterID, 'New versions', 'Should Flevert tell you about new versions?');

  Explanation := TNewStaticText.Create(Page);
  Explanation.Parent := Page.Surface;
  Explanation.Width := Page.SurfaceWidth;
  Explanation.WordWrap := True;
  Explanation.Caption := 'After a conversion, at most once a day, Flevert can ask GitHub for the number of the newest ' +
    'version and offer to open its download page. GitHub sees your IP address, your Flevert version and the time; nothing ' +
    'about your files is sent, and nothing is downloaded or installed by itself. This is the only time Flevert uses ' +
    'the network.';

  UpdateCheck := TNewCheckBox.Create(Page);
  UpdateCheck.Parent := Page.Surface;
  UpdateCheck.Top := Explanation.Top + Explanation.Height + ScaleY(16);
  UpdateCheck.Width := Page.SurfaceWidth;
  UpdateCheck.Height := ScaleY(24);
  UpdateCheck.Caption := 'Tell me when a new version is out';

  // /UPDATECHECK=1|0 for silent installs. Otherwise keep the current choice, which flevert.exe update-check off may
  // have changed since the last setup, so read the file it writes; then the previous setup's; on the first time, on.
  Choice := ExpandConstant('{param:UPDATECHECK|}');
  if (Choice <> '') and (Choice <> '0') and (Choice <> '1') then
  begin
    Log('Ignoring /UPDATECHECK=' + Choice + ': only 0 and 1 are understood.');
    Choice := '';
  end;
  if (Choice = '') and LoadStringFromFile(ExpandConstant('{localappdata}\{#AppName}\update\state.json'), State) then
  begin
    if Pos('"enabled":false', State) > 0 then
      Choice := '0'
    else
      Choice := '1';
  end;
  if Choice = '' then
    Choice := GetPreviousData('UpdateCheck', '1');
  UpdateCheck.Checked := Choice <> '0';
end;

procedure InitializeWizard;
var
  Page: TWizardPage;
  Explanation: TNewStaticText;
  Choice: String;
begin
  if not IsWindows11 then
  begin
    CreateUpdatePage(wpWelcome);
    Exit;
  end;
  Page := CreateCustomPage(wpWelcome, 'Right-click menu', 'Where should Flevert appear?');

  Explanation := TNewStaticText.Create(Page);
  Explanation.Parent := Page.Surface;
  Explanation.Width := Page.SurfaceWidth;
  Explanation.WordWrap := True;
  Explanation.Caption := 'Windows 11 puts Flevert under "Show more options". The option below brings back ' +
    'the classic right-click menu, as in Windows 10. It applies to all apps and is undone when you uninstall Flevert.';

  ClassicMenuCheck := TNewCheckBox.Create(Page);
  ClassicMenuCheck.Parent := Page.Surface;
  ClassicMenuCheck.Top := Explanation.Top + Explanation.Height + ScaleY(16);
  ClassicMenuCheck.Width := Page.SurfaceWidth;
  ClassicMenuCheck.Height := ScaleY(24);
  ClassicMenuCheck.Caption := 'Use the classic (Windows 10 style) right-click menu for all apps';

  // /CLASSICMENU=1|0 for silent installs; otherwise keep the choice of the previous install.
  Choice := ExpandConstant('{param:CLASSICMENU|}');
  if Choice = '' then
    Choice := GetPreviousData('ClassicMenu', '0');
  ClassicMenuCheck.Checked := Choice = '1';

  CreateUpdatePage(Page.ID);
end;

function ClassicMenuChosen: Boolean;
begin
  Result := (ClassicMenuCheck <> nil) and ClassicMenuCheck.Checked;
end;

function UpdateCheckChosen: Boolean;
begin
  Result := (UpdateCheck = nil) or UpdateCheck.Checked;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if ClassicMenuChosen then
    SetPreviousData(PreviousDataKey, 'ClassicMenu', '1')
  else
    SetPreviousData(PreviousDataKey, 'ClassicMenu', '0');
  if UpdateCheckChosen then
    SetPreviousData(PreviousDataKey, 'UpdateCheck', '1')
  else
    SetPreviousData(PreviousDataKey, 'UpdateCheck', '0');
end;

function IsLink(const Path: String): Boolean;
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributes(Path);
  Result := (Attributes <> InvalidFileAttributes) and ((Attributes and FileAttributeReparsePoint) <> 0);
end;

// The first folder from Dir up to its drive that is a junction or symbolic link, or '' if there is none.
function LinkOnPath(Dir: String): String;
var
  Parent: String;
begin
  Result := '';
  // UnlessRoot: "C:" without the backslash names the current directory on drive C, not its root.
  Dir := RemoveBackslashUnlessRoot(Dir);
  while Result = '' do
  begin
    if IsLink(Dir) then
      Result := Dir
    else
    begin
      Parent := RemoveBackslashUnlessRoot(ExtractFileDir(Dir));
      if (Parent = '') or (CompareText(Parent, Dir) = 0) then
        Break;
      Dir := Parent;
    end;
  end;
end;

function HoldsAnything(const Dir: String): Boolean;
var
  Found: TFindRec;
  More: Boolean;
begin
  Result := False;
  if not FindFirst(AddBackslash(Dir) + '*', Found) then
    Exit;
  try
    More := True;
    while More and not Result do
    begin
      Result := (Found.Name <> '.') and (Found.Name <> '..');
      More := FindNext(Found);
    end;
  finally
    FindClose(Found);
  end;
end;

// The same check flevert.exe install-menu makes, but before anything is copied or run from the install folder: once flevert.exe
// starts there, a DLL that another account placed beside it has already run. flevert.exe runs from setup's own temporary
// folder instead. The folder is created first, so the check sees the permissions it inherits. '' when it is safe.
// Trusts the user's %TEMP% as Inno itself does for setup.tmp: on a machine where other accounts can change it, they
// could swap the extracted files before they run (../docs/specs/installer.md).
function LocationProblem(const App: String): String;
var
  Created, ProgramsCreated, Started: Boolean;
  Code: Integer;
  Reason, Checker: String;
begin
  Result := '';
  ProgramsCreated := not DirExists(ExtractFileDir(App));
  Created := not DirExists(App);
  if Created and not ForceDirectories(App) then
  begin
    Result := 'Setup could not create ' + App + '.';
    Exit;
  end;
  Checker := AddBackslash(ExpandConstant('{tmp}')) + '{app}';
  try
    // {app} stays unexpanded: this extracts the files whose DestDir is {app} into a folder literally named {tmp}\{app},
    // which leaves out the ones installed through {code:InstallFolder} ([Files]).
    ExtractTemporaryFiles('{app}\*');
  except
    // A second copy of the runtime and assemblies (about 110 MB) goes here; a full drive is the likeliest cause.
    Result := 'Setup could not unpack its files into ' + ExpandConstant('{tmp}') + ' to check the install folder (' +
      GetExceptionMessage + '). Make sure the drive has free space and that security software did not block setup, ' +
      'then run setup again.';
  end;
  if (Result = '') and not RunFlevertIn(Checker, 'check-location "' + App + '"', Started, Code, Reason) then
  begin
    if Started and (Code = ExitRefusedLocation) then
    begin
      if Reason = '' then
        Reason := 'Other accounts may be able to change ' + App + '.';
      Result := Reason + ' See {#AppUrl}#setup-refused-the-install-folder';
    end
    else if Started and (Code >= 1) and (Code <= 2) then
    begin
      if Reason = '' then
        Reason := 'Flevert gave no reason.';
      Result := Format('Setup could not check the install folder (code %d): %s', [Code, Reason]);
    end
    else
      Result := Format('Setup could not check the install folder (code %d). Windows, or a security policy that ' +
        'blocks programs in the temporary folder, stopped Flevert from running. See {#AppUrl}#smart-app-control', [Code]);
  end;
  // Leave nothing behind for a refused install; folders that were there before are not ours to remove.
  if (Result <> '') and Created then
  begin
    RemoveDir(App);
    if ProgramsCreated then
      RemoveDir(ExtractFileDir(App));
  end;
end;

// Refused before anything is copied: files copied through a link land wherever it points, flevert.exe would load a DLL
// that someone placed in the folder beforehand, and other accounts could replace files in a folder they can change.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  App, Link: String;
begin
  Result := '';
  App := RemoveBackslash(ExpandConstant('{app}'));
  if CompareText(App, ExpandConstant('{userpf}\{#AppName}')) <> 0 then
  begin
    Result := 'Flevert installs only into ' + ExpandConstant('{userpf}\{#AppName}') +
      ': another folder may be changeable by other accounts, and the right-click menu would run their code.';
    Exit;
  end;
  Link := LinkOnPath(App);
  if Link <> '' then
    Result := 'Flevert cannot be installed: ' + Link + ' is a link to another folder (a junction or symbolic ' +
      'link), so its files could end up where other accounts can change them. See {#AppUrl}#setup-refused-the-install-folder'
  else if DirExists(App) and HoldsAnything(App) and not RegKeyExists(HKCU, UninstallKey) then
    Result := App + ' already holds files, perhaps left by an earlier Flevert install that did not finish. ' +
      'Delete the folder, then run setup again.'
  else
    Result := LocationProblem(App);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Started, MenuAdded: Boolean;
  Code: Integer;
  Reason: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  MenuAdded := RunFlevert('install-menu', Started, Code, Reason);
  if not MenuAdded then
  begin
    MenuFailed := True;
    ReportFailure('adding the right-click menu', Started, Code, Reason);
  end;
  // Applied both ways, so unticking it on an upgrade undoes it; off only ever removes the key flevert.exe created. Not
  // switched on without a menu to show: it would change every app's menu for nothing.
  if not ClassicMenuChosen then
    RunFlevert('classic-menu off', Started, Code, Reason)
  else if MenuAdded and not RunFlevert('classic-menu on', Started, Code, Reason) then
  begin
    ClassicMenuFailed := True;
    ReportFailure('switching to the classic menu', Started, Code, Reason);
  end;
  // Applied both ways, like the classic menu. A failed "on" only leaves the check off; a failed "off" would leave it on
  // against the user's choice, so that one is reported.
  if UpdateCheckChosen then
    RunFlevert('update-check on', Started, Code, Reason)
  else if not RunFlevert('update-check off', Started, Code, Reason) then
    ReportFailure('turning off the check for new versions (run "flevert.exe update-check off" in ' +
      ExpandConstant('{app}') + ' to try again)', Started, Code, Reason);
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if MenuFailed or ClassicMenuFailed then
    Result := ExitMenuFailed;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Text: String;
begin
  if CurPageID <> wpFinished then
    Exit;
  if MenuFailed then
    Text := 'Setup copied Flevert to your computer, but could not add it to the right-click menu. ' +
      'See ' + FailureHelp
  else if ClassicMenuFailed then
    Text := 'Setup has finished installing Flevert, but could not switch to the classic right-click menu. ' +
      'See ' + FailureHelp + #13#10#13#10 + 'Right-click a picture or a PDF, choose "Show more options", then Flevert.'
  else
  begin
    Text := 'Setup has finished installing Flevert.' + #13#10#13#10;
    if ClassicMenuChosen then
      Text := Text + 'Right-click a picture or a PDF and choose Flevert. The classic menu appears after you sign out and back in, or restart your PC.'
    else if IsWindows11 then
      Text := Text + 'Right-click a picture or a PDF, choose "Show more options", then Flevert.'
    else
      Text := Text + 'Right-click a picture or a PDF and choose Flevert.';
  end;
  WizardForm.FinishedLabel.Caption := Text + #13#10#13#10 + SetupMessage(msgClickFinish);
  // The label was sized for the default text; without this longer text is cut off.
  WizardForm.FinishedLabel.AdjustHeight;
end;

// Safety net for uninstall: removes what flevert.exe uninstall-menu and classic-menu off would, in case flevert.exe cannot run
// (blocked, damaged or deleted). A menu left pointing at a deleted flevert.exe is the worst thing to leave behind.
// Mirrors MenuInstaller.Uninstall (with WindowsRegistry's pruning of empty parents) and ClassicMenu.Off; harmless when
// they already ran. InstallerScriptTests keeps the key names in step with the C# code.
procedure SweepRegistry;
var
  Names: TArrayOfString;
  I: Integer;
  Ext: String;
begin
  if RegGetSubkeyNames(HKCU, Classes + '\SystemFileAssociations', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Ext := Classes + '\SystemFileAssociations\' + Names[I];
      if RegKeyExists(HKCU, Ext + '\shell\{#AppName}') then
      begin
        RegDeleteKeyIncludingSubkeys(HKCU, Ext + '\shell\{#AppName}');
        RegDeleteKeyIfEmpty(HKCU, Ext + '\shell');
        RegDeleteKeyIfEmpty(HKCU, Ext);
      end;
    end;

  if RegGetSubkeyNames(HKCU, Classes, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(Lowercase('{#AppName}.Menu'), Lowercase(Names[I])) = 1 then
        RegDeleteKeyIncludingSubkeys(HKCU, Classes + '\' + Names[I]);

  if RegValueExists(HKCU, ClassicMenuKey, ClassicMenuMarker) then
    RegDeleteKeyIncludingSubkeys(HKCU, ClassicMenuKey);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Started: Boolean;
  Code: Integer;
  Reason: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  RunFlevert('uninstall-menu', Started, Code, Reason);
  RunFlevert('classic-menu off', Started, Code, Reason);
  SweepRegistry;
end;
