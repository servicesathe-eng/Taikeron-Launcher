#define MyAppName "Taikeron Launcher"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef BuildRoot
  #error BuildRoot must be provided
#endif
#ifndef OutputDir
  #error OutputDir must be provided
#endif

[Setup]
AppId={{8D4A2C2F-65F5-4DB8-A0E8-8BE6F2DE9B41}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Taikeron
DefaultDirName={localappdata}\Programs\Taikeron Launcher
DefaultGroupName=Taikeron
UninstallFilesDir={localappdata}\Taikeron\Launcher\Uninstall
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Taikeron-Launcher-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=yes

[Tasks]
Name: "desktopicon"; Description: "Créer une icône sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "{#BuildRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Taikeron Launcher"; Filename: "{app}\TaikeronLauncher.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Taikeron Launcher"; Filename: "{app}\TaikeronLauncher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TaikeronLauncher.exe"; Description: "Lancer Taikeron Launcher"; Flags: nowait postinstall skipifsilent


[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Taikeron\Runtime\Lab"
Type: filesandordirs; Name: "{localappdata}\Taikeron\Lab"
Type: filesandordirs; Name: "{userappdata}\Taikeron\Lab"
Type: filesandordirs; Name: "{localappdata}\Taikeron Lab"
Type: filesandordirs; Name: "{userappdata}\Taikeron Lab"
Type: filesandordirs; Name: "{localappdata}\taikeron-lab-cycling-os"
Type: filesandordirs; Name: "{userappdata}\taikeron-lab-cycling-os"
Type: filesandordirs; Name: "{localappdata}\com.taikeronlab.cyclingos"
Type: filesandordirs; Name: "{userappdata}\com.taikeronlab.cyclingos"
Type: filesandordirs; Name: "{tmp}\Taikeron\Lab"
Type: filesandordirs; Name: "{tmp}\Taikeron Lab"
Type: filesandordirs; Name: "{tmp}\Taikeron\LauncherSelfUpdate"
Type: filesandordirs; Name: "{localappdata}\Taikeron\Launcher\Jobs"
Type: filesandordirs; Name: "{localappdata}\Taikeron\Launcher\Downloads"
Type: files; Name: "{localappdata}\Taikeron\Launcher\launcher-settings.json"
Type: files; Name: "{localappdata}\CrashDumps\Taikeron Lab*.dmp"
Type: files; Name: "{localappdata}\CrashDumps\TaikeronLab*.dmp"
