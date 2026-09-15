#ifndef MyAppVersion
  #define MyAppVersion "7.23.8"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\installer\v" + MyAppVersion + "\package"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer\v" + MyAppVersion + "\output"
#endif

; Reject a plain dotnet build directory even when ISCC is invoked directly.
; The build script additionally validates both runtimeconfig.json files.
#if !FileExists(SourceDir + "\hostfxr.dll") || !FileExists(SourceDir + "\hostpolicy.dll") || !FileExists(SourceDir + "\coreclr.dll") || !FileExists(SourceDir + "\PresentationFramework.dll")
  #error "Missing bundled .NET runtime. Use a self-contained dotnet publish output, not dotnet build."
#endif
#if !FileExists(SourceDir + "\bin\xray\xray.exe") || !FileExists(SourceDir + "\bin\sing_box\sing-box.exe")
  #error "Proxy cores are missing from the installer payload."
#endif

#define MyAppName "freedom"
#define LegacyAppName "v2rayN"
#define MyAppExeName "freedom.exe"
#define MyAppPublisher "qhs200312"
#define MyAppUrl "https://github.com/qhs200312/freedom-winui"

[Setup]
AppId={{BC0ED823-5089-4F4D-9E42-4739BFE3D562}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
VersionInfoProductName={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=freedom-windows-64-setup
SetupIconFile=..\v2rayN.WinUI\Assets\freedom.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName},v2rayN.exe,AmazTool.exe,ProxiFyre.exe
RestartApplications=no
UsePreviousAppDir=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.LegacyInstallFound=An existing portable installation was found at:%n%n%s%n%nSetup will upgrade this directory. Existing profiles, settings, logs, and databases will be preserved.
chinesesimplified.LegacyInstallFound=检测到已有便携版目录：%n%n%s%n%n安装程序将覆盖升级此目录，现有节点、设置、日志和数据库都会保留。

[Tasks]
Name: "udpdriver"; Description: "Install UDP interception prerequisites (NDISRD driver and VC++ runtime; may interrupt networking or require restart)"; GroupDescription: "UDP interception"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "prerequisites\*,guiConfigs\*,guiLogs\*,binConfigs\*,*.db,*.db-shm,*.db-wal"; Flags: ignoreversion recursesubdirs createallsubdirs overwritereadonly
Source: "{#SourceDir}\prerequisites\VC_redist.x64.exe"; Flags: dontcopy
Source: "{#SourceDir}\prerequisites\Windows.Packet.Filter.3.6.2.1.x64.msi"; Flags: dontcopy

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; postinstall otherwise launches with the original unelevated token via CreateProcess.
; Use the elevated installer token and ShellExecute for requireAdministrator executables.
Filename: "{app}\{#MyAppExeName}"; Verb: "runas"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: shellexec runascurrentuser nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  CurrentAppKey = '{BC0ED823-5089-4F4D-9E42-4739BFE3D562}_is1';

var
  PrerequisitesReady: Boolean;
  PrerequisitesNeedRestart: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  DriverPresent: Boolean;
begin
  Result := '';
  if PrerequisitesReady or not WizardIsTaskSelected('udpdriver') then
    Exit;
  if not IsAdminInstallMode then
  begin
    Result := 'UDP prerequisites require administrator privileges.';
    Exit;
  end;
  ExtractTemporaryFile('VC_redist.x64.exe');
  if not Exec(ExpandConstant('{tmp}\VC_redist.x64.exe'), '/install /quiet /norestart', '',
    SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Unable to start the Visual C++ runtime installer.';
    Exit;
  end;
  { 1638 means a newer runtime is already installed. }
  if (ExitCode <> 0) and (ExitCode <> 3010) and (ExitCode <> 1638) then
  begin
    Result := Format('Visual C++ installation failed (%d).', [ExitCode]);
    Exit;
  end;
  PrerequisitesNeedRestart := ExitCode = 3010;
  DriverPresent := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\NDISRD');
  if DriverPresent then
    DriverPresent := FileExists(ExpandConstant('{sys}\drivers\ndisrd.sys'));
  if not DriverPresent then
  begin
    ExtractTemporaryFile('Windows.Packet.Filter.3.6.2.1.x64.msi');
    if not Exec(ExpandConstant('{sys}\msiexec.exe'),
      '/i "' + ExpandConstant('{tmp}\Windows.Packet.Filter.3.6.2.1.x64.msi') +
      '" /qn /norestart /L*v "' + ExpandConstant('{tmp}\freedom-ndisrd-install.log') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    begin
      Result := 'Unable to start the NDISRD installer.';
      Exit;
    end;
    if (ExitCode <> 0) and (ExitCode <> 3010) then
    begin
      Result := Format('NDISRD installation failed (%d). See freedom-ndisrd-install.log in the temporary directory.', [ExitCode]);
      Exit;
    end;
    PrerequisitesNeedRestart := PrerequisitesNeedRestart or (ExitCode = 3010);
  end;
  PrerequisitesReady := True;
end;

function NeedRestart: Boolean;
begin
  Result := PrerequisitesNeedRestart;
end;

function IsExistingInstallDirectory(const Directory: String): Boolean;
begin
  Result := (Directory <> '')
    and (FileExists(AddBackslash(Directory) + '{#MyAppExeName}')
      or FileExists(AddBackslash(Directory) + 'v2rayN.exe'))
    and DirExists(AddBackslash(Directory) + 'guiConfigs');
end;

function FindRegisteredInstall(const RootKey: Integer): String;
var
  KeyNames: TArrayOfString;
  DisplayName: String;
  InstallLocation: String;
  Index: Integer;
begin
  Result := '';
  if not RegGetSubkeyNames(RootKey, UninstallKey, KeyNames) then
    Exit;

  for Index := 0 to GetArrayLength(KeyNames) - 1 do
  begin
    if RegQueryStringValue(RootKey, UninstallKey + '\' + KeyNames[Index], 'DisplayName', DisplayName)
      and ((CompareText(Trim(DisplayName), '{#MyAppName}') = 0)
        or (Pos(Lowercase('{#MyAppName} '), Lowercase(Trim(DisplayName))) = 1)
        or (CompareText(Trim(DisplayName), '{#LegacyAppName}') = 0)
        or (Pos(Lowercase('{#LegacyAppName} '), Lowercase(Trim(DisplayName))) = 1))
      and RegQueryStringValue(RootKey, UninstallKey + '\' + KeyNames[Index], 'InstallLocation', InstallLocation)
      and IsExistingInstallDirectory(InstallLocation) then
    begin
      Result := InstallLocation;
      Exit;
    end;
  end;
end;

function FindPortableInstall: String;
var
  Candidate: String;
  Drive: String;
  DriveCode: Integer;
  CurrentInstallLocation: String;
begin
  Result := '';

  Candidate := ExpandConstant('{param:LEGACYDIR|}');
  if IsExistingInstallDirectory(Candidate) then
  begin
    Result := Candidate;
    Exit;
  end;

  if RegQueryStringValue(HKCU, UninstallKey + '\' + CurrentAppKey, 'InstallLocation', CurrentInstallLocation)
    and IsExistingInstallDirectory(CurrentInstallLocation) then
  begin
    Result := CurrentInstallLocation;
    Exit;
  end;

  Candidate := FindRegisteredInstall(HKCU);
  if Candidate = '' then
    Candidate := FindRegisteredInstall(HKLM64);
  if Candidate = '' then
    Candidate := FindRegisteredInstall(HKLM32);
  if Candidate <> '' then
  begin
    Result := Candidate;
    Exit;
  end;

  Candidate := ExpandConstant('{src}');
  if IsExistingInstallDirectory(Candidate) then
  begin
    Result := Candidate;
    Exit;
  end;

  for DriveCode := Ord('C') to Ord('Z') do
  begin
    Drive := Chr(DriveCode) + ':\';
    if not DirExists(Drive) then
      Continue;

    Candidate := Drive + 'v2';
    if IsExistingInstallDirectory(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;

    Candidate := Drive + '{#MyAppName}';
    if IsExistingInstallDirectory(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;

    Candidate := Drive + '{#LegacyAppName}';
    if IsExistingInstallDirectory(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;

    Candidate := Drive + 'Apps\{#MyAppName}';
    if IsExistingInstallDirectory(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;

    Candidate := Drive + 'Apps\{#LegacyAppName}';
    if IsExistingInstallDirectory(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;
  end;
end;

procedure MigrateOwnedLegacyShortcut(const ShortcutPath, NewShortcutPath: String);
var
  Shell: Variant;
  Shortcut: Variant;
  NewShortcut: Variant;
  Target: String;
begin
  if not FileExists(ShortcutPath) then
    Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    Target := ExpandFileName(Shortcut.TargetPath);
    if (CompareText(Target, ExpandConstant('{app}\{#MyAppExeName}')) <> 0)
      and (CompareText(Target, ExpandConstant('{app}\{#LegacyAppName}.exe')) <> 0) then
      Exit;
    NewShortcut := Shell.CreateShortcut(NewShortcutPath);
    if FileExists(NewShortcutPath) then
    begin
      Target := ExpandFileName(NewShortcut.TargetPath);
      if CompareText(Target, ExpandConstant('{app}\{#MyAppExeName}')) <> 0 then
        Exit;
    end;
    NewShortcut.TargetPath := ExpandConstant('{app}\{#MyAppExeName}');
    NewShortcut.WorkingDirectory := ExpandConstant('{app}');
    NewShortcut.Description := '{#MyAppName}';
    NewShortcut.IconLocation := ExpandConstant('{app}\{#MyAppExeName},0');
    NewShortcut.Save;
    DeleteFile(ShortcutPath);
  except
    Log('Could not inspect legacy shortcut: ' + ShortcutPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  { Only migrate old shortcuts that belong to this installation. }
  MigrateOwnedLegacyShortcut(ExpandConstant('{autoprograms}\{#LegacyAppName}\{#LegacyAppName}.lnk'),
    ExpandConstant('{group}\{#MyAppName}.lnk'));
  MigrateOwnedLegacyShortcut(ExpandConstant('{userprograms}\{#LegacyAppName}\{#LegacyAppName}.lnk'),
    ExpandConstant('{group}\{#MyAppName}.lnk'));
  RemoveDir(ExpandConstant('{autoprograms}\{#LegacyAppName}'));
  RemoveDir(ExpandConstant('{userprograms}\{#LegacyAppName}'));
  MigrateOwnedLegacyShortcut(ExpandConstant('{autodesktop}\{#LegacyAppName}.lnk'),
    ExpandConstant('{autodesktop}\{#MyAppName}.lnk'));
  MigrateOwnedLegacyShortcut(ExpandConstant('{userdesktop}\{#LegacyAppName}.lnk'),
    ExpandConstant('{userdesktop}\{#MyAppName}.lnk'));
end;

procedure InitializeWizard;
var
  LegacyDirectory: String;
begin
  { Respect an explicit /DIR used by administrators and automated deployments. }
  if ExpandConstant('{param:DIR|}') <> '' then
    Exit;

  LegacyDirectory := FindPortableInstall;
  if LegacyDirectory = '' then
    Exit;

  WizardForm.DirEdit.Text := LegacyDirectory;
  SuppressibleMsgBox(
    Format(CustomMessage('LegacyInstallFound'), [LegacyDirectory]),
    mbInformation,
    MB_OK,
    IDOK);
end;
