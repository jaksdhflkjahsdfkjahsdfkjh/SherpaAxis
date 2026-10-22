#ifndef AppVersion
  #define AppVersion "0.9.1"
#endif
#ifndef AppSource
  #define AppSource "..\publish\setup-package\app"
#endif
#ifndef VJoyInstaller
  #define VJoyInstaller "..\publish\setup-package\prerequisites\vJoySetup_v2.2.2.0_Win10_Win11.exe"
#endif
#ifndef VJoyHash
  #define VJoyHash "EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE"
#endif
#ifndef VJoyLibrary
  #define VJoyLibrary "{commonpf64}\vJoy\x64\vJoyInterface.dll"
#endif
#ifndef SetupOutput
  #define SetupOutput "..\publish"
#endif
#ifndef HidHideInstaller
  #define HidHideInstaller "..\publish\setup-package\prerequisites\HidHide_1.5.230_x64.exe"
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

[UninstallRun]
Filename: "{app}\SherpaAxis.App.exe"; Parameters: "--release-input-configuration"; Flags: runhidden; RunOnceId: "ReleaseSherpaAxisInputConfiguration"

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
begin
  Result := '';
  if HidHideAvailable then Exit;
  WizardForm.PreparingLabel.Caption := 'Installing HidHide duplicate-input protection...';
  ExtractTemporaryFile('HidHideSetup.exe');
  InstallerPath := ExpandConstant('{tmp}\HidHideSetup.exe');
  if CompareText(GetSHA256OfFile(InstallerPath), '{#HidHideHash}') <> 0 then
  begin
    Result := 'The bundled HidHide installer could not be verified. Download SherpaAxis Setup again.';
    Exit;
  end;
  if not Exec(InstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
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
  end;
  Result := PrepareHidHide;
end;

procedure ConfigureDfuDriver;
var
  ExitCode: Integer;
begin
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
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  RemoveDeferredSetup;
  if RegQueryStringValue(HKCU, StartupRegistryKey, StartupValueName, Command) and
    (Pos('"' + ExpandConstant('{app}\SherpaAxis.App.exe') + '"', Command) = 1) then
    RegDeleteValue(HKCU, StartupRegistryKey, StartupValueName);
end;
