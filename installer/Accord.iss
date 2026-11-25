#define AppName "Accord"
#define AppVersion "2.0.2"
#define AppPublisher "Accord"
#define AppExeName "Accord.exe"
#define PublishDir "..\publish"

[Setup]
AppId={{8F6B2D9E-3C4A-4A5E-9A7B-1F8E5D4C2B91}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=Accord-Setup-{#AppVersion}
SetupIconFile=..\assets\accord.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis supplémentaires :"; Flags: unchecked
Name: "addtopath"; Description: "Ajouter Accord au PATH système (accès CLI à Accord.Watcher et Accord.Admin)"; GroupDescription: "Options avancées :"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\{#AppName}"; Permissions: users-modify
Name: "{commonappdata}\{#AppName}\logs"; Permissions: users-modify
Name: "{commonappdata}\{#AppName}\ipc"; Permissions: users-modify

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "settings.json,Accord.backup.txt,watcher-status.json,*.log,ipc\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\settings.json"; DestDir: "{commonappdata}\{#AppName}"; Flags: onlyifdoesntexist skipifsourcedoesntexist
Source: "{#PublishDir}\Accord.backup.txt"; DestDir: "{commonappdata}\{#AppName}"; Flags: onlyifdoesntexist skipifsourcedoesntexist

[Icons]
; Directement à la racine du Menu Démarrer, sans sous-dossier et sans raccourci Désinstaller
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Lancer {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  EnvRegKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

procedure StopRunningAccordInstances;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Accord.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Accord.Watcher.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Accord.Admin.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function NormalizePathSegment(const Value: String): String;
begin
  Result := Trim(Value);
  while (Length(Result) > 1) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

procedure UpdateSystemPath(const TargetDir: String; const AddEntry: Boolean);
var
  CurrentPath, Entry, NormalizedTarget, NewPath: String;
  PosSemi: Integer;
  Found: Boolean;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvRegKey, 'Path', CurrentPath) then
    CurrentPath := '';

  NormalizedTarget := Lowercase(NormalizePathSegment(TargetDir));
  NewPath := '';
  Found := False;

  while CurrentPath <> '' do
  begin
    PosSemi := Pos(';', CurrentPath);
    if PosSemi > 0 then
    begin
      Entry := Copy(CurrentPath, 1, PosSemi - 1);
      Delete(CurrentPath, 1, PosSemi);
    end
    else
    begin
      Entry := CurrentPath;
      CurrentPath := '';
    end;

    if Trim(Entry) = '' then
      Continue;

    if Lowercase(NormalizePathSegment(Entry)) = NormalizedTarget then
    begin
      Found := True;
      if not AddEntry then
        Continue;
    end;

    if NewPath <> '' then
      NewPath := NewPath + ';';
    NewPath := NewPath + Entry;
  end;

  if AddEntry and (not Found) then
  begin
    if NewPath <> '' then
      NewPath := NewPath + ';';
    NewPath := NewPath + NormalizePathSegment(TargetDir);
  end;

  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvRegKey, 'Path', NewPath);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningAccordInstances;
  Result := '';
end;

procedure MigrateExistingBootRestoreTask;
var
  ResultCode: Integer;
  TaskRun, Args: String;
begin
  // Si la tâche AccordRestauration existe déjà (ex. v1.x .vbs ou ancien chemin),
  // la met automatiquement à jour vers Accord.Admin.exe --restore-boot.
  if Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "AccordRestauration"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    TaskRun := '\"' + ExpandConstant('{app}\Accord.Admin.exe') + '\" --restore-boot --data-dir \"' + ExpandConstant('{commonappdata}\{#AppName}') + '\"';
    Args := '/Create /TN "AccordRestauration" /TR "' + TaskRun + '" /SC ONLOGON /RL HIGHEST /F';
    Exec(ExpandConstant('{sys}\schtasks.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  OldLegacyLnk: String;
begin
  if CurStep = ssPostInstall then
  begin
    UpdateSystemPath(ExpandConstant('{app}'), WizardIsTaskSelected('addtopath'));
    MigrateExistingBootRestoreTask;
    OldLegacyLnk := ExpandConstant('{userstartup}\AudioRename Bascule.lnk');
    if FileExists(OldLegacyLnk) then
      DeleteFile(OldLegacyLnk);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupLnk: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningAccordInstances;
    UpdateSystemPath(ExpandConstant('{app}'), False);
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "AccordRestauration" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    StartupLnk := ExpandConstant('{userstartup}\Accord.lnk');
    if FileExists(StartupLnk) then
      DeleteFile(StartupLnk);
  end;
end;
