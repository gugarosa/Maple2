#include "build-defines.iss"

[Setup]
AppId={#AppId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName=MapleTime MS2 {#BuildLabel}
AppPublisher=MapleTime community
AppPublisherURL=https://ms2.mapletime.dev
AppSupportURL=https://github.com/gugarosa/Maple2
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
MinVersion=10.0
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
WizardImageFile={#ArtworkDirectory}\wizard-light.png
WizardImageFileDynamicDark={#ArtworkDirectory}\wizard-dark.png
WizardSmallImageFile={#ArtworkDirectory}\wizard-small-light.png
WizardSmallImageFileDynamicDark={#ArtworkDirectory}\wizard-small-dark.png
SetupIconFile={#ArtworkDirectory}\setup.ico
UninstallDisplayIcon={app}\setup.ico
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=MapleTime MS2 private pilot setup
LicenseFile={#PayloadDirectory}\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=Set up official Mushroom Launcher for the MapleTime MS2 private pilot.%n%nYou need an existing compatible MS2 client. Game files are not included, and access is limited to approved networks.%n%nClose Mushroom before continuing.
FinishedLabel=MapleTime MS2 is configured.%n%nCreate your account, then select {#ServerName} in Mushroom and launch the game.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PayloadDirectory}\Configure-Client.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\CLIENT_SETUP.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\SOURCE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\SOURCE.zip"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ArtworkDirectory}\setup.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{#LauncherPath}"; WorkingDir: "{code:GetClientDirectory}"; IconFilename: "{app}\setup.ico"; Comment: "Select {#ServerName} in Mushroom"
Name: "{autodesktop}\{#ProductName}"; Filename: "{#LauncherPath}"; WorkingDir: "{code:GetClientDirectory}"; IconFilename: "{app}\setup.ico"; Tasks: desktopicon
Name: "{group}\Create an account"; Filename: "{#RegistrationUrl}"
Name: "{group}\Setup guide"; Filename: "{app}\README.txt"
Name: "{group}\Uninstall MapleTime MS2 setup"; Filename: "{uninstallexe}"

[Run]
Filename: "{#RegistrationUrl}"; Description: "Create a MapleTime MS2 account"; Flags: shellexec postinstall skipifsilent unchecked
Filename: "{#LauncherPath}"; Description: "Open Mushroom Launcher"; WorkingDir: "{code:GetClientDirectory}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  ClientPage: TInputDirWizardPage;
  DownloadPage: TDownloadWizardPage;

function GetClientDirectory(Param: String): String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(ClientPage.Values[0]));
end;

function IsWithin(const Path, Parent: String): Boolean;
begin
  Result := Pos(Lowercase(AddBackslash(ExpandFileName(Parent))),
    Lowercase(AddBackslash(ExpandFileName(Path)))) = 1;
end;

function ValidateHelperDirectory: String;
var
  Directory, Client, LauncherDirectory: String;
  Found: TFindRec;
begin
  Result := '';
  Directory := WizardDirValue;
  Client := GetClientDirectory('');
  LauncherDirectory := ExtractFileDir(ExpandConstant('{#LauncherPath}'));
  if IsWithin(Directory, Client) or IsWithin(Client, Directory) or
     IsWithin(Directory, LauncherDirectory) or IsWithin(LauncherDirectory, Directory) then begin
    Result := 'Choose a separate helper folder, outside the client and Mushroom installation.';
    exit;
  end;
  if (CompareText(GetPreviousData('HelperDirectory', ''), Directory) = 0) and
     FileExists(Directory + '\unins000.exe') then
    exit;
  if FindFirst(AddBackslash(Directory) + '*', Found) then
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          Result := 'Choose an empty helper folder. Setup will not replace unrelated files.';
          exit;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
end;

function ValidateClient: String;
var
  Directory: String;
begin
  Result := '';
  Directory := GetClientDirectory('');
  if (Directory = '') or
     not FileExists(Directory + '\x64\MapleStory2.exe') or
     not FileExists(Directory + '\x64\NxCharacter64.dll') or
     not FileExists(Directory + '\Data\Xml.m2d') or
     not FileExists(Directory + '\Data\Xml.m2h') then begin
    Result := 'Select an existing client root containing Data and x64. Do not select the x64 folder itself.';
    exit;
  end;
  if CompareText(GetSHA256OfFile(Directory + '\x64\MapleStory2.exe'), '{#ClientExeSha256}') <> 0 then
    Result := 'This is not the tested x64/NA client executable. Use the compatible original client described in the setup guide.';
end;

function RuntimeInstalled: Boolean;
var
  High, Low: Cardinal;
  I: Integer;
  Names: TArrayOfString;
begin
  Result := False;
  Names := ['msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'];
  for I := 0 to GetArrayLength(Names) - 1 do begin
    if not GetVersionNumbers(ExpandConstant('{#RuntimeDirectory}\') + Names[I], High, Low) then
      exit;
    if (High < {#RuntimeVersionHigh}) or
       ((High = {#RuntimeVersionHigh}) and (Low < {#RuntimeVersionLow})) then
      exit;
  end;
  Result := True;
end;

function ConfigureClient(ValidateOnly: Boolean; const Script: String): Boolean;
var
  Params: String;
  Code: Integer;
begin
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script +
    '" -ClientPath "' + GetClientDirectory('') +
    '" -LoginHost "{#LoginHost}" -LoginPort {#LoginPort} -ServerName "{#ServerName}"' +
    ' -LauncherPath "' + ExpandConstant('{#LauncherPath}') +
    '" -ConfigPath "' + ExpandConstant('{#LauncherConfigPath}') + '" -NoShortcut';
  if ValidateOnly then
    Params := Params + ' -ValidateOnly';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then
    Log('Client configuration helper failed. Exit code: ' + IntToStr(Code));
end;

procedure InitializeWizard;
begin
  ClientPage := CreateInputDirPage(wpSelectDir, 'Select your MS2 client',
    'Use an existing compatible installation.',
    'Choose the folder containing Data and x64. Setup will not copy, download or replace the game files.',
    False, '');
  ClientPage.Add('Client folder:');
  ClientPage.Values[0] := ExpandConstant('{param:CLIENTDIR|}');
  if ClientPage.Values[0] = '' then
    ClientPage.Values[0] := GetPreviousData('ClientPath', '');
  DownloadPage := CreateDownloadPage('Preparing required software',
    'Downloads are verified against their publisher-source checksums.', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if CurPageID = ClientPage.ID then begin
    Error := ValidateClient;
    Result := Error = '';
    if not Result then
      SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  NeedRuntime, NeedLauncher: Boolean;
  Code: Integer;
begin
  Result := ValidateClient;
  if Result <> '' then
    exit;
  Result := ValidateHelperDirectory;
  if Result <> '' then
    exit;
  NeedRuntime := not RuntimeInstalled;
  NeedLauncher := not FileExists(ExpandConstant('{#LauncherPath}'));
  DownloadPage.Clear;
  if NeedRuntime then
    DownloadPage.Add('{#RuntimeUrl}', 'vc_redist.x64.exe', '{#RuntimeSha256}');
  if NeedLauncher then
    DownloadPage.Add('{#MushroomUrl}', 'Mushroom-Setup.exe', '{#MushroomSha256}');
  if NeedRuntime or NeedLauncher then begin
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        Result := 'Required software could not be downloaded or verified: ' + GetExceptionMessage;
        exit;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
  if NeedRuntime then begin
    if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'),
      '/install /quiet /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) or
      ((Code <> 0) and (Code <> 3010)) then begin
      Result := 'Microsoft Visual C++ x64 runtime installation failed. Administrator approval is required when the runtime is missing.';
      exit;
    end;
    NeedsRestart := Code = 3010;
    if NeedsRestart then begin
      Result := 'Restart Windows to finish installing the Microsoft runtime, then run setup again.';
      exit;
    end;
    if not RuntimeInstalled then begin
      Result := 'The required Microsoft Visual C++ x64 runtime is still unavailable. Repair it before retrying setup.';
      exit;
    end;
  end;
  if NeedLauncher then begin
    if not Exec(ExpandConstant('{tmp}\Mushroom-Setup.exe'), '--silent', '',
      SW_SHOWNORMAL, ewWaitUntilTerminated, Code) or (Code <> 0) or
      not FileExists(ExpandConstant('{#LauncherPath}')) then begin
      Result := 'Official Mushroom Launcher did not finish installing. No client files have been changed.';
      exit;
    end;
  end;
  ExtractTemporaryFile('Configure-Client.ps1');
  if not ConfigureClient(True, ExpandConstant('{tmp}\Configure-Client.ps1')) then
    Result := 'Close Mushroom and check that its settings are valid and writable, then retry. Client configuration was not applied.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not ConfigureClient(False, ExpandConstant('{app}\Configure-Client.ps1')) then
      RaiseException('Mushroom configuration failed. Close the launcher and retry setup. Game files were not changed.');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'ClientPath', GetClientDirectory(''));
  SetPreviousData(PreviousDataKey, 'HelperDirectory', WizardDirValue);
end;
