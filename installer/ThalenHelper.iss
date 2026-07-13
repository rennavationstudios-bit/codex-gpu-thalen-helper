#ifndef MyAppVersion
  #define MyAppVersion "0.1.0-beta.1"
#endif

#define MyAppName "Codex GPU Thalen Helper"
#define MyAppPublisher "Codex GPU Thalen Helper contributors"
#define MyAppURL "https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper"

[Setup]
AppId={{C1055BDA-DB1A-490F-B45A-F381568F8B5D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\Codex GPU Thalen Helper
DefaultGroupName=Codex GPU Thalen Helper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir=..\.artifacts\installer
OutputBaseFilename=Codex-GPU-Thalen-Helper-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayIcon={app}\ThalenHelper.ControlCenter.exe
VersionInfoVersion=0.1.0.1
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} community beta installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion=0.1.0.1
VersionInfoCopyright=Copyright (c) 2026 Codex GPU Thalen Helper contributors
LicenseFile=..\LICENSE
InfoBeforeFile=..\installer-notice.txt
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\.artifacts\stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Codex GPU Thalen Helper"; Filename: "{app}\ThalenHelper.ControlCenter.exe"; WorkingDir: "{app}"
Name: "{group}\Documentation"; Filename: "{app}\README.md"
Name: "{userdesktop}\Codex GPU Thalen Helper"; Filename: "{app}\ThalenHelper.ControlCenter.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\ThalenHelper.ControlCenter.exe"; Description: "Open setup and Control Center"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\thalen-helper.exe"; Parameters: "uninstall --yes"; Flags: runhidden waituntilterminated; RunOnceId: "ThalenHelperManagedCleanup"; Check: ShouldRunManagedUninstall

[UninstallDelete]
Type: files; Name: "{app}\.package-lifecycle-test"

[Code]
function GetSwitchValue(const Name: String): String;
var
  Index: Integer;
  Prefix: String;
begin
  Result := '';
  Prefix := '/' + Uppercase(Name) + '=';
  for Index := 1 to ParamCount do
  begin
    if Pos(Prefix, Uppercase(ParamStr(Index))) = 1 then
    begin
      Result := Copy(ParamStr(Index), Length(Prefix) + 1, MaxInt);
      Exit;
    end;
  end;
end;

function CountSwitchOccurrences(const Name: String): Integer;
var
  Index: Integer;
  Prefix: String;
begin
  Result := 0;
  Prefix := '/' + Uppercase(Name) + '=';
  for Index := 1 to ParamCount do
  begin
    if Pos(Prefix, Uppercase(ParamStr(Index))) = 1 then
      Result := Result + 1;
  end;
end;

function IsTrueSwitch(const Name: String): Boolean;
var
  Value: String;
begin
  Value := Lowercase(GetSwitchValue(Name));
  Result := (Value = '1') or (Value = 'true') or (Value = 'yes');
end;

function IsBooleanSwitchValue(const Value: String): Boolean;
var
  Normalized: String;
begin
  Normalized := Lowercase(Value);
  Result := (Normalized = '1') or (Normalized = 'true') or (Normalized = 'yes') or
            (Normalized = '0') or (Normalized = 'false') or (Normalized = 'no');
end;

function IsTrueSwitchValue(const Value: String): Boolean;
var
  Normalized: String;
begin
  Normalized := Lowercase(Value);
  Result := (Normalized = '1') or (Normalized = 'true') or (Normalized = 'yes');
end;

function HasUnsafeQuotedArgumentCharacters(const Value: String): Boolean;
begin
  Result := (Pos('"', Value) > 0) or (Pos(#13, Value) > 0) or (Pos(#10, Value) > 0);
end;

function ShouldRunSilentConfiguration: Boolean;
begin
  Result := WizardSilent and not IsTrueSwitch('NOCONFIGURE');
end;

function ShouldRunManagedUninstall: Boolean;
begin
  Result := not FileExists(ExpandConstant('{app}\.package-lifecycle-test'));
end;

function SilentConfigurationArguments(Param: String): String;
var
  Model: String;
  ModelsDir: String;
  CodexHome: String;
  StateDir: String;
  AutoStart: String;
  Pull: String;
begin
  Model := GetSwitchValue('MODEL');
  ModelsDir := GetSwitchValue('MODELSDIR');
  CodexHome := GetSwitchValue('CODEXHOME');
  StateDir := GetSwitchValue('STATEDIR');
  AutoStart := GetSwitchValue('AUTOSTART');
  Pull := GetSwitchValue('PULLANDVALIDATE');
  if StateDir = '' then
    StateDir := ExpandConstant('{localappdata}\CodexGPUThalenHelper');
  Result := 'install --yes --install-dir "' + ExpandConstant('{app}') + '" --state-dir "' + StateDir + '" --codex-home "' + CodexHome + '"';
  if (Model <> '') and (Lowercase(Model) <> 'auto') then
    Result := Result + ' --model "' + Model + '"';
  if ModelsDir <> '' then
    Result := Result + ' --models-dir "' + ModelsDir + '"';
  if IsTrueSwitchValue(AutoStart) then
    Result := Result + ' --auto-start true'
  else
    Result := Result + ' --auto-start false';
  if IsTrueSwitchValue(Pull) then
    Result := Result + ' --pull-and-validate';
end;

function InitializeSetup(): Boolean;
var
  CodexHome: String;
  StateDir: String;
  ModelsDir: String;
  Model: String;
  AutoStart: String;
  Pull: String;
begin
  Result := True;
  if WizardSilent and IsTrueSwitch('NOCONFIGURE') then
  begin
    if (CountSwitchOccurrences('NOCONFIGURE') <> 1) or
       (CountSwitchOccurrences('CODEXHOME') <> 0) or
       (CountSwitchOccurrences('STATEDIR') <> 0) or
       (CountSwitchOccurrences('MODELSDIR') <> 0) or
       (CountSwitchOccurrences('MODEL') <> 0) or
       (CountSwitchOccurrences('AUTOSTART') <> 0) or
       (CountSwitchOccurrences('PULLANDVALIDATE') <> 0) then
    begin
      Log('Package-only /NOCONFIGURE=1 cannot be duplicated or combined with configuration switches.');
      Result := False;
    end;
  end
  else if WizardSilent then
  begin
    CodexHome := GetSwitchValue('CODEXHOME');
    StateDir := GetSwitchValue('STATEDIR');
    ModelsDir := GetSwitchValue('MODELSDIR');
    Model := GetSwitchValue('MODEL');
    AutoStart := GetSwitchValue('AUTOSTART');
    Pull := GetSwitchValue('PULLANDVALIDATE');
    if (CountSwitchOccurrences('CODEXHOME') <> 1) or
       (CountSwitchOccurrences('MODELSDIR') <> 1) or
       (CountSwitchOccurrences('MODEL') <> 1) or
       (CountSwitchOccurrences('AUTOSTART') <> 1) or
       (CountSwitchOccurrences('PULLANDVALIDATE') <> 1) or
       (CountSwitchOccurrences('STATEDIR') > 1) or
       (CodexHome = '') or (ModelsDir = '') or (Model = '') or
       (AutoStart = '') or (Pull = '') then
    begin
      Log('Configured silent setup requires explicit /CODEXHOME, /MODELSDIR, /MODEL, /AUTOSTART, and /PULLANDVALIDATE choices, or /NOCONFIGURE=1 for package-only lifecycle testing.');
      Result := False;
    end
    else if (not IsBooleanSwitchValue(AutoStart)) or (not IsBooleanSwitchValue(Pull)) then
    begin
      Log('Silent setup requires explicit Boolean /AUTOSTART and /PULLANDVALIDATE values.');
      Result := False;
    end
    else if HasUnsafeQuotedArgumentCharacters(CodexHome) or
            HasUnsafeQuotedArgumentCharacters(StateDir) or
            HasUnsafeQuotedArgumentCharacters(ModelsDir) or
            HasUnsafeQuotedArgumentCharacters(Model) then
    begin
      Log('Silent setup rejected an unsafe quoted argument value.');
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if IsTrueSwitch('NOCONFIGURE') then
      SaveStringToFile(ExpandConstant('{app}\.package-lifecycle-test'), 'Package-only lifecycle test; managed cleanup intentionally skipped.', False)
    else if ShouldRunSilentConfiguration then
    begin
      if not Exec(
        ExpandConstant('{app}\thalen-helper.exe'),
        SilentConfigurationArguments(''),
        ExpandConstant('{app}'),
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) then
        RaiseException('Windows could not start the explicit silent configuration command.')
      else if ResultCode <> 0 then
        RaiseException(Format('Explicit silent configuration failed with exit code %d.', [ResultCode]));
    end;
  end;
end;
