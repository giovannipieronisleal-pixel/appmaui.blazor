#ifndef MyAppVersion
  #define MyAppVersion "1.0.11"
#endif

#ifndef PackageDir
  #define PackageDir "..\dist\YouLink-Package\YouLink"
#endif

#ifndef OutputSetupDir
  #define OutputSetupDir "..\dist-installer"
#endif

[Setup]
AppId={{8F6C8D4A-2A3E-4A79-9F6A-8D1A7E2B4C11}
AppName=YouLink
AppVersion={#MyAppVersion}
AppVerName=YouLink v{#MyAppVersion}
AppPublisher=YouLink
AppPublisherURL=https://github.com/giovannipieronisleal-pixel/yourlink
AppSupportURL=https://github.com/giovannipieronisleal-pixel/yourlink/releases
DefaultDirName={localappdata}\Programs\YouLink
DefaultGroupName=YouLink
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputSetupDir}
OutputBaseFilename=YouLink-Setup-x64
SetupIconFile={#PackageDir}\App\appicon.ico
UninstallDisplayIcon={app}\YouLink.exe
UninstallDisplayName=YouLink
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=YouLink.exe,appmaui.blazor.exe
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na &Área de Trabalho"; GroupDescription: "Atalhos adicionais:"

[Files]
Source: "{#PackageDir}\YouLink.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\YouLink"; Filename: "{app}\YouLink.exe"; WorkingDir: "{app}\App"
Name: "{group}\Desinstalar YouLink"; Filename: "{uninstallexe}"
Name: "{autodesktop}\YouLink"; Filename: "{app}\YouLink.exe"; WorkingDir: "{app}\App"; Tasks: desktopicon

[Run]
Filename: "{app}\YouLink.exe"; WorkingDir: "{app}\App"; Description: "Abrir o YouLink agora"; Flags: nowait postinstall skipifsilent
