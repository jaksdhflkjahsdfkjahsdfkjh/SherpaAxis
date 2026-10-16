SherpaAxis setup preview (Windows x64)

Extract this entire ZIP and run Install.cmd. Windows requests administrator
approval for installation. The installed app runs with normal user permissions.
The self-contained application includes its .NET runtime.

Setup installs the unmodified signed Brunner vJoy 2.2.2.0 driver and runtime SDK
when missing, then creates an unused virtual device with X/Y/Z axes and no buttons,
POVs or force feedback. Existing device configurations are preserved. If a free
pedal-only device is already configured it is reused. Close other vJoy feeders
before setup. Restart Windows when requested; open SherpaAxis from the Start menu.
An incompatible existing vJoy installation requires repair, not silent replacement.

The application remembers its selected vJoy ID per user so simulator bindings stay
stable. Bind X=throttle, Y=brake and Z=clutch from that vJoy device in your game.
Settings includes diagnostics, Automatic vJoy setup and Windows controller test.
Firmware updates in Settings support the reviewed PE0017 vendor image only.
Install ST DfuSe and its USB driver separately; these are not bundled here.
Firmware is not redistributed and no firmware is flashed by this installer.
Physical flashing still requires acceptance testing before public release.

Preview status: installer script is not signed and fresh-machine installation
requires acceptance testing before public release. No driver changes are made
when building this package. Install logs: %PROGRAMDATA%\SherpaAxis\install.log.
Application/settings/profiles are retained when reinstalling. This preview does
not register an uninstaller. Remove C:\Program Files\SherpaAxis and the Start menu
shortcut to remove the app. Uninstall shared vJoy separately through Windows only
when no other application needs it.

vJoy upstream release/source: https://github.com/BrunnerInnovation/vJoy/tree/v2.2.2.0
Release: https://github.com/BrunnerInnovation/vJoy/releases/tag/v2.2.2.0
The bundled installer is unchanged. vJoy license is included alongside this file.
