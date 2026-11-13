; Inno Setup 6 – NICHT gebaut/getestet. Voraussetzung: dotnet publish nach ..\publish (self-contained win-x64).
#define AppName "NEXUS Optimizer AI"
#define AppVersion "0.1.0"

[Setup]
AppId={{6F2C8D1E-4B7A-4C55-9E3A-1A2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=NexusOptimizerAI-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
UninstallDisplayIcon={app}\NexusOptimizer.App.exe
; Keine Signatur hinterlegt: SignTool bewusst nicht konfiguriert.

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\NexusOptimizer.App.exe"

[Run]
Filename: "{app}\NexusOptimizer.App.exe"; Description: "{#AppName} starten"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'NexusOptimizerAI');
    Dir := ExpandConstant('{localappdata}\NexusOptimizerAI');
    if DirExists(Dir) then
      if MsgBox('Sollen auch die gespeicherten Anwendungsdaten (Verlauf, Sicherungen, Einstellungen) gelöscht werden?' + #13#10 +
                'Hinweis: Ohne diese Daten lassen sich bereits angewendete Änderungen nicht mehr über die Anwendung zurücknehmen.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Dir, True, True, True);
  end;
end;
