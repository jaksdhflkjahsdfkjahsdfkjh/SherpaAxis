SherpaAxis - Windows x64 ZIP package

This archive includes the application and .NET runtime, the vJoy and HidHide
installers, ST DfuSe tools and USB driver, and supported pedal firmware.
Windows drivers still need to be installed; extracting the ZIP does not install them.

Automatic first-time setup

1. Extract the entire archive into a permanent location. "Extract here" creates
   a SherpaAxis folder containing all app files. Open that folder; do not run the
   app from inside the ZIP. Keep its subfolders alongside SherpaAxis.App.exe.

2. Close SherpaAxis and FEEL-VR Control completely, including their tray instances.
   Close games and other vJoy feeders before installing or configuring drivers.

3. Run SherpaAxis.App.exe and choose English or Ukrainian. Your choice is saved
   for setup and the app. Its first-run setup window checks the bundled files,
   installs missing drivers and configures the virtual pedals.
   Approve the Windows administrator prompt. No internet connection is needed.

4. If setup requests a restart, close the setup window, restart Windows, then
   open SherpaAxis.App.exe again to finish device setup.

5. Follow the app's setup guide and bind the selected virtual pedal device in
   your game: X = throttle, Y = brake, Z = clutch.

The app opens automatically when setup completes. Already installed components
are reused. Setup never flashes or calibrates the pedals. If a driver installer
takes too long, the window explains the failure and offers setup logs. It does not
force-stop a driver installer or start another installation while it is running.

Manual repair if automatic setup fails

If vJoy 2.2.2 is not installed, run:
   Prerequisites\vJoySetup_v2.2.2.0_Win10_Win11.exe
   Allow the administrator prompt and install the driver and configuration apps.
   Existing vJoy configurations should be preserved. For an incompatible installed
   version, follow the vJoy installer's repair or upgrade instructions.

If HidHide is not installed, run:
   Prerequisites\HidHide_1.5.230_x64.exe
   Allow the administrator prompt. HidHide prevents games receiving duplicate
   input from the physical pedals and SherpaAxis's virtual device.

Restart Windows if either installer requests it.

To prepare firmware updates manually, open PowerShell as administrator. Change to the
   extracted SherpaAxis folder, then run:

   pnputil.exe /add-driver ".\Tools\DfuSe\Driver\STtube.inf"

   This stages the USB driver; it does not flash the pedals. The DfuSe command
   tool is already in Tools\DfuSe and requires no separate DfuSe installation.

Connect the pedals and run SherpaAxis.App.exe. If virtual output is unavailable,
   open Settings and choose Automatic vJoy setup. Allow administrator approval and
   restart Windows if requested.

Keep SherpaAxis running while playing. Output continues when minimized to the
system tray and stops when you choose Exit. Keep FEEL-VR Control closed while
using SherpaAxis.

If vJoy and HidHide are already installed and working, their installers can be
skipped. Profiles, language, bindings and settings are stored in your Windows user
profile, separately from the extracted app folder. Exit SherpaAxis before replacing
that folder with a newer ZIP. When moving it to a different location, review the
Start with Windows setting and physical-pedal hiding configuration in Settings.

Need help?

Guide: https://github.com/jaksdhflkjahsdfkjahsdfkjh/SherpaAxis#readme
Bugs: https://github.com/jaksdhflkjahsdfkjahsdfkjh/SherpaAxis/issues

SherpaAxis: MIT license (LICENSE).
vJoy and HidHide licenses: licenses\.
ST DfuSe license: Tools\DfuSe\SLA0044.txt.
