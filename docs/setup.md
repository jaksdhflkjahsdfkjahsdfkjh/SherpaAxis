# Installation and device checks

The main window shows Connected or Not connected at the top right. Firmware
warnings appear there only when the detected hardware/firmware is outside the
analyzed PE0/017 combination. Firmware 017 is the supported controller firmware,
not a Windows driver version. Older PE0 firmware gets an update suggestion;
newer or unknown firmware gets a compatibility warning, not an automatic downgrade.

Settings → Device and output status checks controller identity, the matching
vJoy 2.2.2 driver/runtime SDK, a dedicated virtual device and its X/Y/Z ranges,
and the startup calibration-recovery gate. These checks read identity/descriptors;
they do not calibrate, save controller settings, acquire a virtual device or send
pedal reports. Checks start in the background at launch, when the live connection
changes and when Settings opens. They can be repeated with Check again.

The first successful automatic acquisition picks the lowest free pedal-only ID
from 1–16 and stores it per user in `virtual-device.txt`. A busy device 1 does not
prevent initial use of 2 or later. Reconnects keep the remembered ID, even when it
is busy, to avoid invalidating simulator bindings. Other feeders must close to
release it. A changed descriptor is never silently replaced.

Automatic vJoy setup pauses live output, runs an elevated helper, and resumes only
after the helper finishes and the recovery gate permits it. It creates an unused
ID with X/Y/Z, zero buttons/POVs and no force feedback. A missing remembered ID is
recreated at that same number. Existing descriptors are preserved, including if
another process creates the ID between inspection and creation: the upstream
helper is called without its overwrite flag. Driver state is queried again after
creation; helper exit code zero alone is insufficient evidence of success.
Setup helper returns 0 for verified readiness, 3010 when configuration is not yet
active and a restart/recheck is required, and 1 for other setup failures. No
normal single-instance activation or pedal input/output runs in the helper.

Settings also offers Configure vJoy, Windows controller test (`joy.cpl`) and a
localized setup guide. Game axis bindings still require the game's own controls
screen: X throttle, Y brake, Z clutch from the selected vJoy device. SherpaAxis
does not edit games' configuration or hide physical devices.

## Firmware updates

Settings → Firmware update now supports the reviewed PE0017 vendor image through
bundled ST DfuSe tools, with file validation, controller/USB-port checks,
hardware-settings backup, durable recovery journal, programming verification and
reconnect checks. Setup includes DfuSe and stages its signed USB driver. It does
not download firmware through the vendor's old HTTP endpoint or downgrade newer
firmware. See [firmware updates](firmware.md) for operation, recovery and the
outstanding physical acceptance test. No firmware is flashed during startup/setup.

## Build and test the installer

```powershell
./tools/Build-SetupPackage.ps1
```

The script publishes a self-contained Windows x64 application, validates the
signed upstream vJoy installer and compiles `publish/Setup.exe` with Inno Setup
6.7.3. Firmware and DfuSe payload hashes and the USB driver's publisher signatures
are checked before publishing. The build-only compiler is extracted from a pinned NuGet archive and its
publisher signature is checked; no compiler is installed on the user's PC.
Supply `-VJoyInstaller` to reuse a downloaded prerequisite, `-CompilerPath` to use
an existing compiler, and `-Version` to set the application and setup version.
Building does not install or configure drivers.

Run `Setup.exe` on Windows 10 22H2 or Windows 11 on x64 Intel/AMD hardware. Setup
requests administrator approval, installs the app into Program Files, creates a
Start menu shortcut and registers a Windows uninstaller. The .NET runtime is
included. An existing Windows startup entry for the installing user is updated
to the installed executable; startup is not enabled automatically. vJoy is
installed when missing; an existing incompatible driver is
preserved and reported for repair. Existing virtual devices are preserved.

Setup runs the app's configuration helper without starting normal pedal input
or output. If the driver or device setup requires a restart, a one-time SYSTEM
boot task finishes configuration after Windows restarts. The task executes only
the installed helper from the protected Program Files directory, removes itself
on success and is removed on uninstall. It does not restart Windows by itself.
Failures are logged to `%PROGRAMDATA%/SherpaAxis/device-setup.log`. A failed
configuration returns a nonzero setup exit code and explains recovery through
Settings; the installed app remains available with its uninstaller.

Uninstall through Windows Settings → Apps. Profiles and settings are retained,
and the shared vJoy driver is left installed. The setup wizard supports English
and Ukrainian and can create an optional desktop shortcut. Automated deployments
can use `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /LOG`.

~~~powershell
./tools/Test-SetupPackage.ps1
~~~

Installer checks compile a separate fixture with mock executables and use a
private directory and an isolated per-user uninstall entry. They cover fresh
installation, upgrades, profile preservation, uninstall, existing-driver reuse,
prerequisite integrity and installation failures, DfuSe driver staging, and restart paths. They
do not run the real driver installer, change virtual devices, or register a
SYSTEM task. Native SDK startup inspection has a separate opt-in regression
check that reads device metadata and verifies one hidden SDK window under
concurrent calls. The full input/output hardware test remains opt-in.

The generated setup executable is unsigned until a publisher signing certificate
is supplied. Fresh-machine installation, Windows restart behavior and physical
hardware acceptance remain release checks.
