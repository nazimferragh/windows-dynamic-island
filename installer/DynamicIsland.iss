; Inno Setup script for Dynamic Island.
; Built by build.ps1, which passes /DAppVersion=x.y.z.

#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif

#define AppName "Dynamic Island"
#define AppExe "DynamicIsland.exe"
#define AppPublisher "Nazim Abderahman"
#define AppUrl "https://github.com/nazimferragh/windows-dynamic-island"

[Setup]
AppId={{8C3F2B8E-5D1A-4E7B-9C4F-2A6D1E9B7F30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Show the welcome page (modern wizard hides it by default); it's where the author is shown.
DisableWelcomePage=no
; Per-user install, no admin prompt and no install-mode chooser, so the author page is the first screen.
PrivilegesRequired=lowest
UsedUserAreasWarning=no
OutputDir=..\dist
OutputBaseFilename=DynamicIsland-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; We close the running island ourselves in PrepareToInstall.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
; Author photo shown on the first page; not installed, only used by the wizard.
Source: "author.bmp"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Starting with Windows is handled by a Task Scheduler task the app registers (see the "Always
; running" page below). The app also adds a Run entry so it's listed in Windows' Startup apps (it
; only hands off to the task); uninstalling removes it and its on/off state.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DynamicIsland"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "DynamicIsland"; Flags: uninsdeletevalue

; The app turns off Windows 11's drag-to-top snap layouts bar on first run (it sits right where the
; island is). Uninstalling removes the value, which restores Windows' default.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"; ValueName: "EnableSnapBar"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\DynamicIsland"; Flags: uninsdeletekey

[Run]
; Starts on its own as soon as install finishes (no checkbox to leave it unchecked).
Filename: "{app}\{#AppExe}"; Flags: nowait skipifsilent

[UninstallRun]
; Ask the island (and its watchdog) to stop for good, remove the start-with-Windows task (asks for
; admin rights if it was set up in high priority), then make sure nothing is left running.
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitDynamicIsland"
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAutostart"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe} /im DynamicIslandGuard.exe"; Flags: runhidden; RunOnceId: "StopDynamicIsland"
; Give Windows its notification pop-ups back (the island turned them off while it ran).
Filename: "{app}\{#AppExe}"; Parameters: "--restore-banners"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreBanners"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\DynamicIsland"
; The watchdog's exe, a hard link the app creates next to itself.
Type: files; Name: "{app}\DynamicIslandGuard.exe"

[Code]
var
  PriorityPage: TInputOptionWizardPage;

procedure InitializeWizard;
var
  Avatar: TBitmapImage;
  NameLabel, RoleLabel: TNewStaticText;
  Top: Integer;
begin
  ExtractTemporaryFile('author.bmp');

  WizardForm.WelcomeLabel1.Caption := 'Dynamic Island for Windows';

  Top := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(20);

  Avatar := TBitmapImage.Create(WizardForm);
  Avatar.Parent := WizardForm.WelcomePage;
  Avatar.Bitmap.LoadFromFile(ExpandConstant('{tmp}\author.bmp'));
  Avatar.Stretch := True;
  Avatar.Left := WizardForm.WelcomeLabel1.Left;
  Avatar.Top := Top;
  Avatar.Width := ScaleX(76);
  Avatar.Height := ScaleY(76);

  NameLabel := TNewStaticText.Create(WizardForm);
  NameLabel.Parent := WizardForm.WelcomePage;
  NameLabel.AutoSize := True;
  NameLabel.Left := Avatar.Left + Avatar.Width + ScaleX(16);
  NameLabel.Top := Top + ScaleY(18);
  NameLabel.Font.Style := [fsBold];
  NameLabel.Font.Size := 12;
  NameLabel.Caption := 'Nazim Abderahman';

  RoleLabel := TNewStaticText.Create(WizardForm);
  RoleLabel.Parent := WizardForm.WelcomePage;
  RoleLabel.AutoSize := True;
  RoleLabel.Left := NameLabel.Left;
  RoleLabel.Top := NameLabel.Top + NameLabel.Height + ScaleY(4);
  RoleLabel.Font.Color := clGray;
  RoleLabel.Caption := 'Developer';

  WizardForm.WelcomeLabel2.Top := Top + Avatar.Height + ScaleY(20);
  WizardForm.WelcomeLabel2.Caption :=
    'This will install Dynamic Island on your computer.' + #13#10#13#10 +
    'It sits at the top of your screen, shows what''s playing, holds windows in its black hole, and starts automatically with Windows.' + #13#10#13#10 +
    'Click Next to continue.';

  // "Always running": like Wallpaper Engine's high-priority startup. High priority asks Windows for
  // administrator permission once (Windows shows its own prompt when setup finishes).
  PriorityPage := CreateInputOptionPage(wpWelcome,
    'Keep Dynamic Island always running',
    'Choose how Dynamic Island starts with Windows.',
    'Dynamic Island is meant to feel like part of Windows. With either option it starts when you sign in, ' +
    'and it comes back on its own if it crashes, freezes or is closed from Task Manager.' + #13#10#13#10 +
    'High priority also starts it before your other apps, gives it extra CPU priority so it stays smooth, ' +
    'and lets it work with apps running as administrator. Windows will ask for your permission once.',
    True, False);
  PriorityPage.Add('High priority (recommended)');
  PriorityPage.Add('Normal: no administrator permission needed');
  PriorityPage.Values[0] := True;
end;

// After the files are in place: set up start-with-Windows. The app registers the task itself; for
// high priority it runs Task Scheduler with admin rights, which shows Windows' permission prompt.
// If that's declined it falls back to normal on its own.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Mode: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  // Silent installs/updates keep whatever was chosen before ("auto"); the wizard asks.
  Mode := 'auto';
  if not WizardSilent then
  begin
    if PriorityPage.Values[0] then Mode := 'high' else Mode := 'normal';
  end;
  if ExpandConstant('{param:HIGHPRIORITY|0}') = '1' then Mode := 'high';
  Exec(ExpandConstant('{app}\{#AppExe}'), '--register-autostart ' + Mode, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Upgrading while the island is running would fail to overwrite the exe. Ask it to stop first (the
  // only way that works for a high-priority island, which this setup has no rights to end), then
  // make sure.
  if FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Exec(ExpandConstant('{app}\{#AppExe}'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe} /im DynamicIslandGuard.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
