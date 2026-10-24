; Inno Setup script for The Garden (compiled by package.ps1 after it builds artifacts\package\TheGarden).
; Per-user install (no administrator prompt) into %LOCALAPPDATA%\Programs\The Garden; Start menu entry,
; optional desktop icon, uninstall from Windows Settings. A calculated climate is kept inside the saved map file; the app's
; scratch folder (%TEMP%\Map) goes when it closes. Uninstalling removes the settings (%APPDATA%\TheGarden), the climate
; results older versions kept in %LOCALAPPDATA%\TheGarden and the scratch folder, so nothing is left behind; the user's own
; saved maps stay. Installing a new version over the old one keeps the settings.
#ifndef Src
  #define Src "..\artifacts\package\Map"
#endif
#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef OutDir
  #define OutDir "..\artifacts\package"
#endif

[Setup]
AppId={{6C1E0F7A-3B9D-4E52-9A41-7D2C5B8E9F10}
AppName=Map
AppVersion={#Version}
AppVerName=Map {#Version}
AppPublisher=Map
DefaultDirName={autopf}\Map
DefaultGroupName=Map
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutDir}
OutputBaseFilename=Map-Setup-{#Version}
SetupIconFile=..\src\TheGarden.Desktop\Map.ico
UninstallDisplayIcon={app}\Map.exe
; ultra64 runs the 32-bit compiler out of memory; max in the separate 64-bit compressor process is enough
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=2
CloseApplications=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Src}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; the app was TheGarden.exe ("The Garden" shortcuts) before it was named Map
Type: files; Name: "{app}\TheGarden.exe"
Type: files; Name: "{autoprograms}\The Garden.lnk"
Type: files; Name: "{autodesktop}\The Garden.lnk"

[Icons]
Name: "{autoprograms}\Map"; Filename: "{app}\Map.exe"
Name: "{autodesktop}\Map"; Filename: "{app}\Map.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Map.exe"; Description: "{cm:LaunchProgram,Map}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; __pycache__ folders the bundled Python writes next to the model
Type: filesandordirs; Name: "{app}\climate"
; climate results of older versions (tens of MB per run), settings, the scratch folder and its font cache
Type: filesandordirs; Name: "{localappdata}\TheGarden"
Type: filesandordirs; Name: "{userappdata}\TheGarden"
Type: filesandordirs; Name: "{%TEMP}\Map"
