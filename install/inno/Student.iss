; KIBERone Student — requires Administrator (VPN Windows service).
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
AppId={{A8F3C2B1-4D5E-4A6F-9B0C-1D2E3F4A5B6C}}
AppName=KIBERone Student
AppVersion={#MyAppVersion}
AppPublisher=KIBERone
AppPublisherURL=https://github.com/mmmLoer/kiberone
DefaultDirName={autopf}\KIBERone\Student
DefaultGroupName=KIBERone
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=KIBERoneStudent-Setup-{#MyAppVersion}-win-x64
SetupIconFile=
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName=KIBERone Student {#MyAppVersion}
CloseApplications=force
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#DistRoot}\Student-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\scripts\install-student-vpn-service.ps1"; DestDir: "{app}\service"; Flags: ignoreversion
Source: "..\Repair-Student-Vpn.cmd"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\KIBERone Student"; Filename: "{app}\Kiberone.Student.exe"; WorkingDir: "{app}"
Name: "{group}\Repair Student VPN"; Filename: "{app}\Repair-Student-Vpn.cmd"; WorkingDir: "{app}"
Name: "{autodesktop}\KIBERone Student"; Filename: "{app}\Kiberone.Student.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\service\install-student-vpn-service.ps1"" -SourceDir ""{app}"" -InPlace"; \
  WorkingDir: "{app}"; Flags: runhidden waituntilterminated; \
  StatusMsg: "Установка VPN-службы KIBERoneStudentVpn…"
Filename: "{app}\Kiberone.Student.exe"; Description: "Запустить Student"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

[UninstallRun]
Filename: "{cmd}"; \
  Parameters: "/c net stop KIBERoneStudentVpn >nul 2>&1 & sc delete KIBERoneStudentVpn >nul 2>&1"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveVpnService"
