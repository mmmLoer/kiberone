; KIBERone combined setup — always elevates (Student VPN needs admin).
; Wizard: choose Student / Tutor / both.
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
AppId={{C0F5E4D3-6F70-4C81-9D2E-3F4A5B6C7D8E}}
AppName=KIBERone
AppVersion={#MyAppVersion}
AppPublisher=KIBERone
AppPublisherURL=https://github.com/mmmLoer/kiberone
DefaultDirName={autopf}\KIBERone
DefaultGroupName=KIBERone
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=KIBERone-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName=KIBERone {#MyAppVersion}
CloseApplications=force

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Student и Tutor"
Name: "student"; Description: "Только Student"
Name: "tutor"; Description: "Только Tutor"
Name: "custom"; Description: "Выборочно"; Flags: iscustom

[Components]
Name: "student"; Description: "KIBERone Student (класс + VPN)"; Types: full student custom; Flags: disablenouninstallwarning
Name: "tutor"; Description: "KIBERone Tutor (преподаватель)"; Types: full tutor custom; Flags: disablenouninstallwarning

[Tasks]
Name: "desktopicon"; Description: "Ярлыки на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#DistRoot}\Student-win-x64\*"; DestDir: "{app}\Student"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: student
Source: "..\..\scripts\install-student-vpn-service.ps1"; DestDir: "{app}\Student\service"; Flags: ignoreversion; Components: student
Source: "..\Repair-Student-Vpn.cmd"; DestDir: "{app}\Student"; Flags: ignoreversion; Components: student
Source: "{#DistRoot}\Tutor-win-x64\*"; DestDir: "{app}\Tutor"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: tutor; Excludes: "updates\*"

[Icons]
Name: "{group}\KIBERone Student"; Filename: "{app}\Student\Kiberone.Student.exe"; WorkingDir: "{app}\Student"; Components: student
Name: "{group}\Repair Student VPN"; Filename: "{app}\Student\Repair-Student-Vpn.cmd"; WorkingDir: "{app}\Student"; Components: student
Name: "{group}\KIBERone Tutor"; Filename: "{app}\Tutor\Kiberone.Tutor.exe"; WorkingDir: "{app}\Tutor"; Components: tutor
Name: "{autodesktop}\KIBERone Student"; Filename: "{app}\Student\Kiberone.Student.exe"; WorkingDir: "{app}\Student"; Tasks: desktopicon; Components: student
Name: "{autodesktop}\KIBERone Tutor"; Filename: "{app}\Tutor\Kiberone.Tutor.exe"; WorkingDir: "{app}\Tutor"; Tasks: desktopicon; Components: tutor

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Student\service\install-student-vpn-service.ps1"" -SourceDir ""{app}\Student"" -InPlace"; \
  WorkingDir: "{app}\Student"; Flags: runhidden waituntilterminated; Components: student; \
  StatusMsg: "Установка VPN-службы…"
Filename: "{app}\Student\Kiberone.Student.exe"; Description: "Запустить Student"; Flags: nowait postinstall skipifsilent unchecked; Components: student; WorkingDir: "{app}\Student"
Filename: "{app}\Tutor\Kiberone.Tutor.exe"; Description: "Запустить Tutor"; Flags: nowait postinstall skipifsilent unchecked; Components: tutor; WorkingDir: "{app}\Tutor"

[UninstallRun]
Filename: "{cmd}"; \
  Parameters: "/c net stop KIBERoneStudentVpn >nul 2>&1 & sc delete KIBERoneStudentVpn >nul 2>&1"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveVpnService"
