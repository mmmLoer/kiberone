; KIBERone Student — requires Administrator (VPN Windows service).
#ifndef MyAppVersion
  #define MyAppVersion "0.10.18"
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
Filename: "{app}\Kiberone.Student.exe"; Description: "Запустить Student"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

[UninstallRun]
Filename: "{cmd}"; \
  Parameters: "/c net stop KIBERoneStudentVpn >nul 2>&1 & sc delete KIBERoneStudentVpn >nul 2>&1"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveVpnService"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Script: String;
begin
  if CurStep = ssPostInstall then
  begin
    Script := ExpandConstant('{app}\service\install-student-vpn-service.ps1');
    if not FileExists(Script) then
    begin
      MsgBox('Student установлен, но не найден скрипт VPN:'#13#10 + Script + #13#10#13#10 +
        'Запустите Repair-Student-Vpn.cmd от администратора.', mbError, MB_OK);
      exit;
    end;
    if not Exec(
        'powershell.exe',
        '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '" -SourceDir "' + ExpandConstant('{app}') + '" -InPlace',
        ExpandConstant('{app}'),
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) then
    begin
      MsgBox('Student установлен, но не удалось запустить установку VPN-службы.'#13#10 +
        'Для связи с Tutor VPN не обязателен. Позже: Repair-Student-Vpn.cmd', mbInformation, MB_OK);
      exit;
    end;
    if ResultCode <> 0 then
      MsgBox('Student установлен. VPN-служба не поднялась (код ' + IntToStr(ResultCode) + ').'#13#10#13#10 +
        'На виртуалках часто нет WireGuard — для теста Tutor↔Student это нормально.'#13#10 +
        'Нужен VPN: поставьте WireGuard с https://www.wireguard.com/install/ и запустите Repair-Student-Vpn.cmd.',
        mbInformation, MB_OK);
  end;
end;
