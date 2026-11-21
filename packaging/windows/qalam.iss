; Inno Setup script for Qalam (per-user install, no admin rights needed)
#define AppVersion GetEnv("QALAM_VERSION")
#if AppVersion == ""
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6C2B7C1E-5B7A-4C8E-9F3D-1A2B3C4D5E6F}
AppName=Qalam
AppVersion={#AppVersion}
AppPublisher=Asia-Plus
DefaultDirName={localappdata}\Programs\Qalam
DefaultGroupName=Qalam
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\out
OutputBaseFilename=Qalam-Setup-{#AppVersion}
SetupIconFile=..\..\src\Qalam.App\Assets\qalam.ico
UninstallDisplayIcon={app}\Qalam.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\..\out\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Qalam"; Filename: "{app}\Qalam.exe"
Name: "{userdesktop}\Qalam"; Filename: "{app}\Qalam.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Qalam.exe"; Description: "Запустить Qalam"; Flags: nowait postinstall skipifsilent
; silent update from inside Qalam: start the new version right away
Filename: "{app}\Qalam.exe"; Parameters: "--tray"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Qalam.exe /F"; Flags: runhidden; RunOnceId: "KillQalam"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Qalam"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Qalam"; Flags: uninsdeletevalue dontcreatekey
