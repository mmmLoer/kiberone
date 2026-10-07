; KIBERone Student — requires Administrator (VPN Windows service).
#ifndef MyAppVersion
  #define MyAppVersion "0.10.41"
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
Name: "{autodesktop}\KIBERone Student"; Filename: "{app}\Kiberone.Student.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Setup is already elevated (PrivilegesRequired=admin) — install VPN service here, no extra .cmd / UAC.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\service\install-student-vpn-service.ps1"" -SourceDir ""{app}"" -InPlace"; \
  WorkingDir: "{app}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Установка VPN-службы KIBERoneStudentVpn…"
Filename: "{app}\Kiberone.Student.exe"; Description: "Запустить Student"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  StopCommand: String;
begin
  if CurStep = ssPostInstall then
  begin
    if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -Command "if((Get-Service -Name KIBERoneStudentVpn -ErrorAction SilentlyContinue).Status -ne ''Running''){exit 1}"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
      RaiseException('VPN-служба KIBERoneStudentVpn не запустилась. Проверьте ProgramData\KIBERone\Student\vpn\service-install.log.');
    Exit;
  end;
  if CurStep <> ssInstall then Exit;
  { The bridge may run from the installed EXE; stop it before file replacement. }
  StopCommand := '$s=Get-Service -Name KIBERoneStudentVpn -ErrorAction SilentlyContinue; ' +
    'if($s -and $s.Status -ne ''Stopped''){Stop-Service -Name KIBERoneStudentVpn -ErrorAction Stop; ' +
    '$s.WaitForStatus(''Stopped'',[TimeSpan]::FromSeconds(30))}';
  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + StopCommand + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    RaiseException('Не удалось остановить службу KIBERoneStudentVpn перед обновлением.');
end;

[UninstallRun]
Filename: "{cmd}"; \
  Parameters: "/c net stop KIBERoneStudentVpn >nul 2>&1 & sc delete KIBERoneStudentVpn >nul 2>&1"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveVpnService"
