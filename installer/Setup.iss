#ifndef AppVersion
  #define AppVersion "1.4.2"
#endif
#ifndef AppSource
  #define AppSource "..\publish\v" + AppVersion + "\app"
#endif
#ifndef VJoyInstaller
  #define VJoyInstaller "..\research\tools\setup-prerequisites\vJoySetup_v2.2.2.0_Win10_Win11.exe"
#endif
#ifndef VJoyHash
  #define VJoyHash "EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE"
#endif
#ifndef VJoyLibrary
  #define VJoyLibrary "{commonpf64}\vJoy\x64\vJoyInterface.dll"
#endif
#ifndef SetupOutput
  #define SetupOutput "..\publish\v" + AppVersion
#endif
#ifndef HidHideInstaller
  #define HidHideInstaller "..\research\tools\setup-prerequisites\HidHide_1.5.230_x64.exe"
#endif
#ifndef HidHideHash
  #define HidHideHash "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6"
#endif
#ifndef HidHideCli
  #define HidHideCli "{commonpf64}\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"
#endif
#ifndef VendorControlProcess
  #define VendorControlProcess "FEEL_VR_Control.exe"
#endif

[Setup]
#ifdef InstallerTest
AppId=SherpaAxis-Installer-Test
AppName=SherpaAxis Installer Test
PrivilegesRequired=lowest
DefaultDirName={localappdata}\SherpaAxis-Installer-Test
AppMutex=Local\SherpaAxis.InstallerTest
SetupMutex=SherpaAxis.InstallerTest.Setup
#else
AppId={{48CBF14C-6F5A-43EE-8F67-AE44052BA58B}
AppName=SherpaAxis
PrivilegesRequired=admin
DefaultDirName={autopf}\SherpaAxis
DisableDirPage=yes
UsePreviousAppDir=no
AppMutex=Local\SherpaAxis.PedalsUtility
SetupMutex=SherpaAxis.Setup
#endif
AppVersion={#AppVersion}
AppPublisher=SherpaAxis contributors
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
WizardStyle=modern dark polar includetitlebar
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\LICENSE
SetupIconFile=..\src\SherpaAxis.App\Assets\SherpaAxis.ico
UninstallDisplayIcon={app}\SherpaAxis.App.exe
OutputDir={#SetupOutput}
OutputBaseFilename=Setup
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallLogging=yes
VersionInfoDescription=SherpaAxis Setup
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[CustomMessages]
english.CloseVendorControl=FEEL-VR Control is running. Exit it completely, including its tray instance, then click Retry to continue installation.
ukrainian.CloseVendorControl=FEEL-VR Control запущено. Повністю закрийте програму, зокрема її значок у системному треї, а потім натисніть «Повторити», щоб продовжити встановлення.
english.VendorControlCheckFailed=Could not check whether FEEL-VR Control is running. Restart Windows and run Setup again. Details: %1
ukrainian.VendorControlCheckFailed=Не вдалося перевірити, чи запущено FEEL-VR Control. Перезавантажте Windows і запустіть інсталятор знову. Подробиці: %1
english.RemoveOwnedDrivers=Also remove vJoy, HidHide and the DfuSe USB driver installed by SherpaAxis?%n%nOnly recorded, unchanged components are eligible. Pre-existing drivers and profiles are preserved. Other applications may need these shared drivers.%n%nChoose No to keep drivers, or Cancel to stop uninstalling.
ukrainian.RemoveOwnedDrivers=Також видалити vJoy, HidHide та USB-драйвер DfuSe, встановлені SherpaAxis?%n%nВидаляються лише записані та незмінені компоненти. Наявні раніше драйвери та профілі зберігаються. Ці спільні драйвери можуть бути потрібні іншим програмам.%n%nОберіть «Ні», щоб залишити драйвери, або «Скасувати», щоб припинити видалення.
english.UninstallReleaseFailed=Input configuration could not be released. Close SherpaAxis and other controller apps, then retry uninstalling.
ukrainian.UninstallReleaseFailed=Не вдалося скасувати налаштування вводу. Закрийте SherpaAxis та інші програми контролерів і повторіть видалення.
english.DriverRemovalStartFailed=Could not start optional driver removal. Retry uninstalling.
ukrainian.DriverRemovalStartFailed=Не вдалося запустити додаткове видалення драйверів. Повторіть спробу.
english.DriverRemovalFailed=Optional driver removal did not finish. The app is kept so you can retry, or choose No to preserve the drivers. Logs: ProgramData\SherpaAxis\Prerequisites.
ukrainian.DriverRemovalFailed=Видалення драйверів не завершено. Програму збережено для повторної спроби; також можна обрати «Ні», щоб залишити драйвери. Журнали: ProgramData\SherpaAxis\Prerequisites.
english.RemoveSavedData=Permanently delete all saved SherpaAxis data?%n%nThis removes this Windows account's profiles, settings, bindings, logs and calibration/firmware recovery backups, plus shared SherpaAxis setup records. Complete pending recovery or export needed backups first.%n%nFiles exported elsewhere and Windows driver configurations are kept. Choose No to keep saved data, or Cancel to stop uninstalling.
ukrainian.RemoveSavedData=Назавжди видалити всі збережені дані SherpaAxis?%n%nБуде видалено профілі, налаштування, призначення кнопок, журнали та резервні копії відновлення калібрування/прошивки цього облікового запису Windows, а також спільні записи налаштування SherpaAxis. Спочатку завершіть відновлення або експортуйте потрібні резервні копії.%n%nФайли, експортовані в інші папки, та налаштування драйверів Windows зберігаються. Оберіть «Ні», щоб залишити дані, або «Скасувати», щоб припинити видалення.
english.SavedDataRemovalFailed=Saved data could not be completely removed. The app is kept so you can retry, or uninstall without deleting saved data.
ukrainian.SavedDataRemovalFailed=Не вдалося повністю видалити збережені дані. Програму залишено для повторної спроби; також можна видалити її зі збереженням даних.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VJoyInstaller}"; DestDir: "{tmp}"; DestName: "vJoySetup.exe"; Flags: dontcopy
Source: "{#HidHideInstaller}"; DestDir: "{tmp}"; DestName: "HidHideSetup.exe"; Flags: dontcopy
Source: "Complete-VJoySetup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "vJoy-LICENSE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "HidHide-LICENSE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion; AfterInstall: ConfigureDevice

[Icons]
#ifdef InstallerTest
Name: "{autoprograms}\SherpaAxis Installer Test"; Filename: "{app}\SherpaAxis.App.exe"
#else
Name: "{autoprograms}\SherpaAxis"; Filename: "{app}\SherpaAxis.App.exe"
Name: "{autodesktop}\SherpaAxis"; Filename: "{app}\SherpaAxis.App.exe"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\SherpaAxis.App.exe"; Description: "{cm:LaunchProgram,SherpaAxis}"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: CanLaunchApplication

[Code]
const
  DeferredTaskName = 'SherpaAxis.DeviceSetup.48CBF14C';
#ifdef InstallerTest
  StartupValueName = 'SherpaAxis-Installer-Test';
  StartupRegistryKey = 'Software\SherpaAxisInstallerTest\Startup';
#else
  StartupValueName = 'SherpaAxis';
  StartupRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
#endif
var
  DriverInstalled: Boolean;
  RestartRequired: Boolean;
  ConfigurationFailed: Boolean;
  DfuConfigurationFailed: Boolean;
  DeferredSetupCreated: Boolean;
  InstallationCompleted: Boolean;
  RemoveOwnedDrivers: Boolean;
  DriverRemovalRestart: Boolean;
  RemoveSavedData: Boolean;

function UninstallNeedRestart: Boolean;
begin
  Result := DriverRemovalRestart;
end;

function InitializeUninstall: Boolean;
var
  Choice: Integer;
begin
  Result := True;
  RemoveOwnedDrivers := ExpandConstant('{param:REMOVEOWNEDDRIVERS|0}') = '1';
  RemoveSavedData := ExpandConstant('{param:REMOVESAVEDDATA|0}') = '1';
  if UninstallSilent then Exit;
  Choice := MsgBox(CustomMessage('RemoveOwnedDrivers'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
  Result := Choice <> IDCANCEL;
  RemoveOwnedDrivers := Choice = IDYES;
  if not Result then Exit;
  Choice := MsgBox(CustomMessage('RemoveSavedData'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
  Result := Choice <> IDCANCEL;
  RemoveSavedData := Choice = IDYES;
end;

function DriverPackageExists(InfName: String): Boolean;
var
  Found: TFindRec;
begin
#ifdef InstallerTest
  Result := False;
#else
  Result := FindFirst(ExpandConstant('{sys}\DriverStore\FileRepository\') + InfName + '_*', Found);
  if Result then FindClose(Found);
#endif
end;

procedure RecordNewDriver(Component: String);
#ifndef InstallerTest
var
  Found: TFindRec;
  InfName, InfPath, Hash: String;
#endif
begin
#ifndef InstallerTest
  if Component = 'VJoy' then InfName := 'vjoy.inf'
  else if Component = 'HidHide' then InfName := 'hidhide.inf'
  else InfName := 'sttube.inf';
  if not FindFirst(ExpandConstant('{sys}\DriverStore\FileRepository\') + InfName + '_*', Found) then Exit;
  InfPath := ExpandConstant('{sys}\DriverStore\FileRepository\') + Found.Name + '\' + InfName;
  FindClose(Found);
  Hash := GetSHA256OfFile(InfPath);
  if not RegWriteStringValue(HKLM64, 'SOFTWARE\SherpaAxis\Prerequisites', 'Pending' + Component + 'Hash', Hash) then
    RaiseException('Could not record the installed driver fingerprint.');
  if Component = 'VJoy' then
    if not RegWriteStringValue(HKLM64, 'SOFTWARE\SherpaAxis\Prerequisites', 'PendingVJoyUninstallerHash',
      GetSHA256OfFile(ExpandConstant('{commonpf64}\vJoy\unins000.exe'))) then
      RaiseException('Could not record the vJoy uninstaller fingerprint.');
  if not RegWriteDWordValue(HKLM64, 'SOFTWARE\SherpaAxis\Prerequisites', 'Pending' + Component, 1) then
    RaiseException('Could not record the newly installed driver. Review the Setup log.');
#endif
end;

procedure RemoveDeferredSetup;
#ifndef InstallerTest
var
  Scheduler, Folder: Variant;
#endif
begin
#ifndef InstallerTest
  try
    Scheduler := CreateOleObject('Schedule.Service');
    Scheduler.Connect;
    Folder := Scheduler.GetFolder('\');
    if Folder.GetTasks(0).Count > 0 then
      Folder.DeleteTask(DeferredTaskName, 0);
  except
    Log('No deferred device setup task was removed: ' + GetExceptionMessage);
  end;
#endif
end;

procedure RegisterDeferredSetup;
#ifdef InstallerTest
begin
  SaveStringToFile(ExpandConstant('{#VJoyLibrary}') + '.deferred', 'pending', False);
end;
#else
var
  Scheduler, Folder, Task, Trigger, Action: Variant;
begin
  Scheduler := CreateOleObject('Schedule.Service');
  Scheduler.Connect;
  Folder := Scheduler.GetFolder('\');
  Task := Scheduler.NewTask(0);
  Task.RegistrationInfo.Description := 'Finish the SherpaAxis virtual pedal device setup after a driver restart.';
  Task.Principal.UserId := 'SYSTEM';
  Task.Principal.LogonType := 5;
  Task.Principal.RunLevel := 1;
  Task.Settings.StartWhenAvailable := True;
  Task.Settings.DisallowStartIfOnBatteries := False;
  Task.Settings.StopIfGoingOnBatteries := False;
  Task.Settings.ExecutionTimeLimit := 'PT2M';
  Trigger := Task.Triggers.Create(8);
  Trigger.Delay := 'PT15S';
  Action := Task.Actions.Create(0);
  Action.Path := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Action.Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' +
    ExpandConstant('{app}\Complete-VJoySetup.ps1') + '"';
  Action.WorkingDirectory := ExpandConstant('{app}');
  Folder.RegisterTaskDefinition(DeferredTaskName, Task, 6, 'SYSTEM', '', 5);
end;
#endif

function HidHideAvailable: Boolean;
begin
  Result := FileExists(ExpandConstant('{#HidHideCli}'));
#ifndef InstallerTest
  Result := Result or FileExists(ExpandConstant('{commonpf64}\Nefarius Software Solutions\HidHide\HidHideCLI.exe')) or
    FileExists(ExpandConstant('{commonpf64}\Nefarius Software Solutions e.U.\HidHide\x64\HidHideCLI.exe')) or
    FileExists(ExpandConstant('{commonpf64}\Nefarius Software Solutions e.U.\HidHide\HidHideCLI.exe'));
#endif
end;

function PrepareHidHide: String;
var
  InstallerPath: String;
  ExitCode: Integer;
  PreExisting: Boolean;
begin
  Result := '';
  if HidHideAvailable then Exit;
  PreExisting := DriverPackageExists('hidhide.inf') or RegKeyExists(HKLM64,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{01E0AB21-D1CC-42B4-9DFF-84FFE4F26DAF}');
  WizardForm.PreparingLabel.Caption := 'Installing HidHide duplicate-input protection...';
  ExtractTemporaryFile('HidHideSetup.exe');
  InstallerPath := ExpandConstant('{tmp}\HidHideSetup.exe');
  if CompareText(GetSHA256OfFile(InstallerPath), '{#HidHideHash}') <> 0 then
  begin
    Result := 'The bundled HidHide installer could not be verified. Download SherpaAxis Setup again.';
    Exit;
  end;
  if not Exec(InstallerPath, '/exenoui /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Could not start HidHide installation: ' + SysErrorMessage(ExitCode);
    Exit;
  end;
  Log(Format('HidHide installation returned %d', [ExitCode]));
  if (ExitCode <> 0) and (ExitCode <> 3010) then
  begin
    Result := Format('HidHide installation failed (code %d). Restart Windows and run Setup again.', [ExitCode]);
    Exit;
  end;
  RestartRequired := RestartRequired or (ExitCode = 3010);
  if not PreExisting then RecordNewDriver('HidHide');
  if (ExitCode = 0) and not HidHideAvailable then
    Result := 'HidHide installation did not provide its configuration tool. Restart Windows and run Setup again.';
end;

function CheckVendorControl: String;
var
  Locator, Services, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''{#VendorControlProcess}''');
    if Processes.Count > 0 then
      Result := CustomMessage('CloseVendorControl');
  except
    Result := FmtMessage(CustomMessage('VendorControlCheckFailed'), [GetExceptionMessage]);
  end;
  if Result <> '' then Log(Result);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  InstallerPath: String;
  PreExisting: Boolean;
begin
  Result := CheckVendorControl;
  if Result <> '' then Exit;
#ifndef InstallerTest
  if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue)),
    ExpandConstant('{autopf}\SherpaAxis')) <> 0 then
  begin
    Result := 'SherpaAxis must be installed in Program Files so its driver setup tools remain protected.';
    Exit;
  end;
#endif
  if not (DriverInstalled or FileExists(ExpandConstant('{#VJoyLibrary}'))) then
  begin
  PreExisting := DriverPackageExists('vjoy.inf') or
    FileExists(ExpandConstant('{commonpf64}\vJoy\unins000.exe')) or
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\vjoy');
  WizardForm.PreparingLabel.Caption := 'Installing the vJoy driver and runtime...';
  ExtractTemporaryFile('vJoySetup.exe');
  InstallerPath := ExpandConstant('{tmp}\vJoySetup.exe');
  if CompareText(GetSHA256OfFile(InstallerPath), '{#VJoyHash}') <> 0 then
  begin
    Result := 'The bundled vJoy installer could not be verified. Download SherpaAxis Setup again.';
    Exit;
  end;
  if not Exec(InstallerPath,
    '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /COMPONENTS="Apps,Apps\vJoyConf"',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Could not start vJoy installation: ' + SysErrorMessage(ExitCode);
    Exit;
  end;
  Log(Format('vJoy installation returned %d', [ExitCode]));
  if (ExitCode <> 0) and (ExitCode <> 3010) then
  begin
    Result := Format('vJoy installation failed (code %d). Restart Windows and run Setup again.', [ExitCode]);
    Exit;
  end;
  RestartRequired := RestartRequired or (ExitCode = 3010);
  if not RestartRequired and not FileExists(ExpandConstant('{#VJoyLibrary}')) then
  begin
    Result := 'vJoy installation did not provide its runtime. Restart Windows and run Setup again.';
    Exit;
  end;
  DriverInstalled := True;
  if not PreExisting then RecordNewDriver('VJoy');
  end;
  Result := PrepareHidHide;
end;

procedure ConfigureDfuDriver;
var
  ExitCode: Integer;
  PreExisting: Boolean;
begin
  PreExisting := DriverPackageExists('sttube.inf');
  WizardForm.StatusLabel.Caption := 'Preparing the pedal firmware USB driver...';
#ifdef InstallerTest
  if not Exec(ExpandConstant('{app}\SherpaAxis.App.exe'), '--configure-dfuse',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
#else
  if not Exec(ExpandConstant('{sys}\pnputil.exe'),
    '/add-driver "' + ExpandConstant('{app}\Tools\DfuSe\Driver\STtube.inf') + '"',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
#endif
    ExitCode := 1;
  Log(Format('DfuSe driver staging returned %d', [ExitCode]));
  DfuConfigurationFailed := (ExitCode <> 0) and (ExitCode <> 3010);
  RestartRequired := RestartRequired or (ExitCode = 3010);
  if not DfuConfigurationFailed and not PreExisting then RecordNewDriver('DfuSe');
end;

procedure ConfigureDevice;
var
  ExitCode: Integer;
  StartupCommand: String;
begin
  if RegQueryStringValue(HKCU, StartupRegistryKey, StartupValueName, StartupCommand) and
    (StartupCommand <> '') then
    if not RegWriteStringValue(HKCU, StartupRegistryKey, StartupValueName,
      '"' + ExpandConstant('{app}\SherpaAxis.App.exe') + '" --startup') then
      Log('Could not update the existing Windows startup entry. Review Start with Windows in Settings.');
  if not RestartRequired then
  begin
    WizardForm.StatusLabel.Caption := 'Configuring the virtual pedal device...';
    if not Exec(ExpandConstant('{app}\SherpaAxis.App.exe'), '--configure-vjoy',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      ExitCode := 1;
    Log(Format('Virtual pedal setup returned %d', [ExitCode]));
    RestartRequired := ExitCode = 3010;
    ConfigurationFailed := (ExitCode <> 0) and not RestartRequired;
  end;
  if RestartRequired then
  begin
    RegisterDeferredSetup;
    DeferredSetupCreated := True;
  end
  else if not ConfigurationFailed then
    RemoveDeferredSetup;
  ConfigureDfuDriver;
  if not Exec(ExpandConstant('{app}\SherpaAxis.App.exe'), '--record-prerequisite-ownership',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    Log('Driver ownership could not be finalized. Automatic driver removal will remain unavailable for unrecorded components.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
    InstallationCompleted := True;
end;

procedure DeinitializeSetup;
begin
  if DeferredSetupCreated and not InstallationCompleted then
    RemoveDeferredSetup;
end;

function NeedRestart: Boolean;
begin
  Result := RestartRequired;
end;

function CanLaunchApplication: Boolean;
begin
  Result := not RestartRequired and not ConfigurationFailed and not DfuConfigurationFailed;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if ConfigurationFailed or DfuConfigurationFailed then
    Result := 1;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and ConfigurationFailed then
    WizardForm.FinishedLabel.Caption :=
      'SherpaAxis is installed, but vJoy needs attention. Close other vJoy applications, open SherpaAxis Settings and select Automatic vJoy setup. An incompatible driver may need repair.';
  if (CurPageID = wpFinished) and DfuConfigurationFailed then
    WizardForm.FinishedLabel.Caption :=
      'SherpaAxis is installed, but Windows could not prepare the DfuSe USB driver. Firmware updates are unavailable. Review the Setup log and run Setup again to repair it.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  ExitCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  RemoveDeferredSetup;
  if not Exec(ExpandConstant('{app}\SherpaAxis.App.exe'), '--release-input-configuration',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    RaiseException(CustomMessage('UninstallReleaseFailed'));
  if RemoveOwnedDrivers then
  begin
    if not Exec(ExpandConstant('{app}\SherpaAxis.App.exe'), '--remove-owned-prerequisites',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException(CustomMessage('DriverRemovalStartFailed'));
    DriverRemovalRestart := ExitCode = 3010;
    if (ExitCode <> 0) and not DriverRemovalRestart then
      RaiseException(CustomMessage('DriverRemovalFailed'));
  end;
  if RemoveSavedData then
  begin
    if not Exec(ExpandConstant('{app}\SherpaAxis.App.exe'), '--remove-saved-data',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
      RaiseException(CustomMessage('SavedDataRemovalFailed'));
  end;
  if RegQueryStringValue(HKCU, StartupRegistryKey, StartupValueName, Command) and
    (Pos('"' + ExpandConstant('{app}\SherpaAxis.App.exe') + '"', Command) = 1) then
    RegDeleteValue(HKCU, StartupRegistryKey, StartupValueName);
end;
