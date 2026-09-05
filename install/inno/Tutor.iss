; KIBERone Tutor — per-user install, no admin required by default.
#ifndef MyAppVersion
  #define MyAppVersion "0.10.14"
#endif
#ifndef DistRoot
  #define DistRoot "..\..\dist"
#endif
#ifndef OutDir
  #define OutDir "..\..\dist\installers"
#endif

[Setup]
AppId={{B9E4D3C2-5E6F-4B70-8C1D-2E3F4A5B6C7D}}
AppName=KIBERone Tutor
AppVersion={#MyAppVersion}
AppPublisher=KIBERone
AppPublisherURL=https://github.com/mmmLoer/kiberone
DefaultDirName={localappdata}\Programs\KIBERone\Tutor
DefaultGroupName=KIBERone
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=KIBERoneTutor-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName=KIBERone Tutor {#MyAppVersion}
CloseApplications=force

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
; Exclude updates\ (in-app update channel ~215MB) — not needed for first install.
Source: "{#DistRoot}\Tutor-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "updates\*"

[Icons]
Name: "{group}\KIBERone Tutor"; Filename: "{app}\Kiberone.Tutor.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\KIBERone Tutor"; Filename: "{app}\Kiberone.Tutor.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Kiberone.Tutor.exe"; Description: "Запустить Tutor"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"
