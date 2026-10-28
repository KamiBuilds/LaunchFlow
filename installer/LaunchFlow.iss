#define AppVersion "0.7.24"
#define AppName "LaunchFlow"
[Setup]
AppId={{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=LaunchFlow
AppCopyright=LaunchFlow
AppSupportURL=https://github.com
DefaultDirName={localappdata}\Programs\LaunchFlow
DefaultGroupName=LaunchFlow
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=LaunchFlow-Setup-v{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\LaunchFlow\Assets\LaunchFlow.ico
UninstallDisplayIcon={app}\LaunchFlow.exe
UninstallDisplayName=LaunchFlow
; Existing installations with the same AppId are updated IN PLACE. Never change AppId.
AppMutex=LaunchFlow.Windows.SingleInstance
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
InfoBeforeFile=
ShowLanguageDialog=no
; Authenticode: BUILD-INSTALLER.ps1 passes /DLfSign=1 and /Slfsign="signtool ... $f" when a cert is set.
; Unsigned local builds omit /DLfSign=1 so ISCC does not require SignTool.
#ifdef LfSign
SignTool=lfsign
SignedUninstaller=yes
#endif

[Messages]
WelcomeLabel1=Welcome to LaunchFlow Setup
WelcomeLabel2=This installs LaunchFlow on your PC.%n%nLaunchFlow keeps your website workflows and passwords in a local encrypted database. No Microsoft account is required.%n%nThe .NET runtime and Playwright driver are included; no SDK is needed. Browser setup is offered separately in the app and never runs silently.%n%nClick Next to continue.
FinishedLabel=LaunchFlow is installed.%n%nClick Finish to open it and create your master password. Your data stays on this computer.

[Files]
; Keep Playwright's private driver tree, including bundled Node and its licenses.
Source: "..\publish\*"; DestDir: "{app}"; Excludes: ".playwright\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\.playwright\*"; DestDir: "{app}\.playwright"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LaunchFlow"; Filename: "{app}\LaunchFlow.exe"; Comment: "Website workspace automation"
Name: "{group}\Uninstall LaunchFlow"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LaunchFlow"; Filename: "{app}\LaunchFlow.exe"; Tasks: desktopicon; Comment: "Website workspace automation"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: checkedonce

[Registry]
Root: HKCU; Subkey: "Software\Classes\.launchflow"; ValueType: string; ValueName: ""; ValueData: "LaunchFlow.Backup"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\LaunchFlow.Backup"; ValueType: string; ValueName: ""; ValueData: "LaunchFlow Encrypted Backup"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\LaunchFlow.Backup\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\LaunchFlow.exe,0"
Root: HKCU; Subkey: "Software\Classes\LaunchFlow.Backup\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LaunchFlow.exe"" ""%1"""

[Run]
Filename: "{app}\LaunchFlow.exe"; Description: "Open LaunchFlow"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; Vault wipe is optional — see InitializeUninstall / CurUninstallStepChanged.

[Code]
var
  InstalledVersion: String;
  UpgradeInfoPage: TOutputMsgWizardPage;
  WipeUserData: Boolean;

function InitializeSetup(): Boolean;
var
  InstallDir: String;
  Executable: String;
  ResultCode: Integer;
  Attempt: Integer;
begin
  Result := True;
  // Inno calls this before its AppMutex warning. Ask the installed version
  // to seal its browser profiles and exit; never terminate it forcefully.
  if not CheckForMutexes('LaunchFlow.Windows.SingleInstance') then Exit;

  InstallDir := '';
  RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}_is1',
    'InstallLocation', InstallDir);
  if InstallDir = '' then
    InstallDir := ExpandConstant('{localappdata}\Programs\LaunchFlow');
  Executable := AddBackslash(InstallDir) + 'LaunchFlow.exe';

  if not FileExists(Executable) then
  begin
    MsgBox('LaunchFlow is running, but Setup cannot find its installed executable. ' +
      'Exit LaunchFlow from the tray and start Setup again.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if not Exec(Executable, '--exit-for-install', '', SW_HIDE, ewNoWait, ResultCode) then
  begin
    MsgBox('LaunchFlow could not receive the installer shutdown request. ' +
      'Exit it manually, then run Setup again.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  // Wait for the original process to fully release the mutex, including
  // browser-close and encrypted-profile cleanup. No force-kill fallback.
  for Attempt := 1 to 120 do
  begin
    if not CheckForMutexes('LaunchFlow.Windows.SingleInstance') then Exit;
    Sleep(250);
  end;

  MsgBox('LaunchFlow did not finish a safe shutdown. Your encrypted data is ' +
    'untouched by Setup. Check LaunchFlow for a cleanup or save warning, ' +
    'then exit it and retry the installation.', mbError, MB_OK);
  Result := False;
end;

procedure InitializeWizard;
var
  Heading: String;
  Explanation: String;
begin
  if RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}_is1',
      'DisplayVersion', InstalledVersion) then
  begin
    if CompareText(InstalledVersion, '{#AppVersion}') = 0 then
    begin
      Heading := 'Repair LaunchFlow';
      Explanation := 'Version {#AppVersion} is already installed. Setup will repair program files.';
    end
    else
    begin
      Heading := 'Update LaunchFlow';
      Explanation := 'LaunchFlow ' + InstalledVersion + ' will be updated to {#AppVersion}.';
    end;
    UpgradeInfoPage := CreateOutputMsgPage(wpWelcome, Heading,
      'Your data is kept',
      Explanation + #13#10 + #13#10 +
      'Workflows and encrypted passwords stay in your local vault.' + #13#10 +
      'Setup requests a safe LaunchFlow shutdown automatically. If the old ' +
      'installed version does not support it, exit it from the tray and retry.');
  end;
end;

function InitializeUninstall(): Boolean;
var
  DataDir: String;
  Answer: Integer;
begin
  Result := True;
  WipeUserData := False;
  DataDir := ExpandConstant('{localappdata}\LaunchFlow');
  if not DirExists(DataDir) then
    Exit;

  Answer := MsgBox(
    'Uninstall LaunchFlow program files from this PC.' + #13#10 + #13#10 +
    'Your encrypted vault (workflows, passwords, backups) is stored separately at:' + #13#10 +
    DataDir + #13#10 + #13#10 +
    'Also permanently delete that vault and all backups?' + #13#10 + #13#10 +
    'Yes = uninstall AND delete vault/passwords/backups (cannot be undone)' + #13#10 +
    'No = uninstall the app only; keep encrypted data (recommended if you may reinstall)' + #13#10 +
    'Cancel = do not uninstall',
    mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);

  if Answer = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;
  WipeUserData := (Answer = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and WipeUserData then
  begin
    DataDir := ExpandConstant('{localappdata}\LaunchFlow');
    if DirExists(DataDir) then
      DelTree(DataDir, True, True, True);
  end;
end;
