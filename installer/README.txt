SherpaAxis Setup (Windows x64)

Run Setup.exe. The installation wizard requests administrator approval, installs
the application and registers its Windows uninstaller. The installed app runs
with normal user permissions. Its .NET runtime is included.

Setup installs the unmodified signed Brunner vJoy 2.2.2.0 driver and runtime SDK
when missing, then creates an unused virtual device with X/Y/Z axes and no buttons,
POVs or force feedback. Existing device configurations are preserved. If a free
pedal-only device is already configured it is reused. Close other vJoy feeders
before setup. Restart Windows when requested; device setup finishes automatically
after the restart. Open SherpaAxis from the Start menu.
An incompatible existing vJoy installation requires repair, not silent replacement.

The application remembers its selected vJoy ID per user so simulator bindings stay
stable. Bind X=throttle, Y=brake and Z=clutch from that vJoy device in your game.
Settings includes diagnostics, Automatic vJoy setup and Windows controller test.
Firmware updates in Settings support the reviewed PE0017 vendor image only.
Install ST DfuSe and its USB driver separately; these are not bundled here.
Firmware is not redistributed and no firmware is flashed by this installer.
Physical flashing still requires acceptance testing before public release.

Uninstall SherpaAxis from Windows Settings > Apps. Profiles and settings are
retained. Shared vJoy is not removed; uninstall it separately only when no other
application needs it. Setup logs are written to the Windows temporary folder;
post-restart device setup logs go to %PROGRAMDATA%\SherpaAxis\device-setup.log.

Build with tools/Build-SetupPackage.ps1. Output: publish/Setup.exe. The compiler
is a portable build dependency; it is not installed on users' computers. Run
tools/Test-SetupPackage.ps1 to test installation, upgrades, uninstall, prerequisite
validation, failures and restarts using isolated mock drivers.

Setup is unsigned until a publisher signing certificate is supplied. Automated
tests do not replace fresh-machine installation and physical driver acceptance.

vJoy upstream release/source: https://github.com/BrunnerInnovation/vJoy/tree/v2.2.2.0
Release: https://github.com/BrunnerInnovation/vJoy/releases/tag/v2.2.2.0
The bundled installer is unchanged. vJoy license is included alongside this file.
