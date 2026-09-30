; Inno Setup script for Dynamic Island.
; Built by build.ps1, which passes /DAppVersion=x.y.z.

#ifndef AppVersion
  #define AppVersion "0.3.0"
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
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Author photo shown on the first page; not installed, only used by the wizard.
Source: "author.bmp"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Always starts with Windows, like a built-in component.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DynamicIsland"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

; The app turns off Windows 11's drag-to-top snap layouts bar on first run (it sits right where the
; island is). Uninstalling removes the value, which restores Windows' default.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"; ValueName: "EnableSnapBar"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\DynamicIsland"; Flags: uninsdeletekey

[Run]
; Starts on its own as soon as install finishes (no checkbox to leave it unchecked).
Filename: "{app}\{#AppExe}"; Flags: nowait skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopDynamicIsland"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\DynamicIsland"

[Code]
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
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Upgrading while the island is running would fail to overwrite the exe.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
