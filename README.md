# SherpaAxis

SherpaAxis is a Windows utility for FEEL-VR Pedals One. It combines hardware
calibration, pedal tuning and virtual controller output in one application.

## What it helps with

Adjust how each pedal responds, smooth input jitter and keep different setups
for different cars or games. Each pedal has its own editable response curve,
deadzones and filter settings. Live graphs let you compare the input, filtered
signal and final output while tuning.

Save your settings as profiles, switch between them from the app or system tray,
and choose one profile to load automatically at startup. The Calibration panel
sets the controller's pedal bounds and provides hardware backup and restoration.
The interface is available in English and Ukrainian.

A short interactive tour highlights the real controls on first launch. You can
try them while following the tour; tuning changes apply to your current profile.
Replay it from **Settings → Help → Getting started**, or open **Setup guide** there
for device and game setup instructions.

## Installation

Requirements: Windows 10 22H2 or Windows 11 (64-bit Intel/AMD) and FEEL-VR
Pedals One. Native hardware calibration supports the PE0 controller with firmware 017.

Installation uses a single offline `Setup.exe`. It includes the .NET runtime and
the vJoy driver, HidHide, ST DfuSe firmware tool and USB driver, and runtime libraries; you do not need to install developer tools
or run scripts.

1. Close FEEL-VR CONTROL, including its tray instance, games and other vJoy feeders.
2. Run `Setup.exe`, allow the administrator prompt and follow the wizard.
3. Setup installs SherpaAxis into `C:\Program Files\SherpaAxis`, adds a Start menu
   shortcut and offers an optional desktop shortcut. It installs vJoy when
   missing and configures or reuses a dedicated virtual pedal device. Existing
   vJoy device configurations are preserved.
4. Restart Windows if requested. Device configuration finishes automatically
   after the restart.
5. Connect your pedals and open **SherpaAxis** from the Start menu. The app runs
   with normal user permissions; live input and virtual output start automatically.

In your game's controls, bind the selected vJoy device's **X** axis to throttle,
**Y** to brake and **Z** to clutch. Remove duplicate bindings to the physical
pedals. SherpaAxis remembers the virtual device so these bindings stay consistent.

**SherpaAxis must stay running to send processed pedal input to your game.**
Minimizing it or keeping it in the system tray continues output. Choosing **Exit**
stops output; the virtual controller may still be listed in Windows.

When SherpaAxis uses the only configured vJoy device, Windows Game Controllers
shows **FEEL-VR Pedals One (SherpaAxis)**. If other vJoy slots are configured,
the shared name remains unchanged to preserve their names. Some games use the
driver's product string and may still show **vJoy**. Reopen the controller list
after starting SherpaAxis to refresh the displayed name.

**Hide physical pedals from games** is enabled by default in Settings. HidHide
hides only the FEEL-VR pedal joystick while SherpaAxis retains access to input
and calibration. The physical Windows HID driver stays installed and must not
be removed or disabled. Existing HidHide entries for other devices are preserved.
SherpaAxis restores its visibility changes on exit or when this setting is turned
off. For existing shared HidHide configurations, follow any status shown
in Settings; an inverted application list or disabled shared configuration needs
manual review. Close games before setup; reconnect the pedals if a game still
sees both devices. Keep FEEL-VR Control closed while using SherpaAxis.

If virtual output is unavailable, open **Settings** and run **Automatic vJoy
setup**. Close other vJoy feeders first. An incompatible existing vJoy driver
requires repair; Setup preserves it rather than replacing it silently.

Use **Tuning** for curves, filters and profile deadzones, **Calibration** for
hardware calibration, and **Settings** for language, startup preferences and
device checks. Tuning changes apply automatically. The saved/unsaved indicator
shows whether they are stored in your profile. Save your profile to keep them
after restart; enable **Auto-load** if you want
it applied the next time the app starts.

For sensor drift, close FEEL-VR CONTROL and choose a **Pedal drift correction** mode in
Tuning. First calibrate each affected pedal once in SherpaAxis so it can retain
the original raw range separately from the hardware deadzone margins; correction
is blocked if that reference is missing. **Auto** waits for five consistent press/release cycles and only corrects
downward drift during stable release. **By button** checks a stable second while
you hold your binding with your foot off. Hold **Fix drift** for the same check
without a binding and keep holding until correction finishes; release to stop.
Manual correction supports drift in either
direction. Correction moves both
range endpoints together, preserving sensitivity, hardware margins and profile deadzones. It is
temporary until you explicitly choose **Save corrected calibration**; the original
hardware calibration is backed up first. If readings disagree, no new correction
is applied. Keep your foot completely off for manual checks: stability alone
cannot distinguish a released pedal from steady foot pressure.

SherpaAxis checks GitHub once at each app startup for a newer stable release.
It does not repeat checks while running. An update link appears in the footer;
when the app is hidden, it also sends a tray notification. Open **Settings → Updates**
to **Check now** or disable automatic checks. Checks send no pedal data or profiles;
they contact GitHub for public release metadata. Downloads and installation are manual.

To update, open the release link, download its `Setup.exe`, exit SherpaAxis from the
tray, close FEEL-VR Control and games, then run Setup. Your
profiles and settings are retained. Enable **Start with Windows** in Settings
if you want the app to launch when you sign in.

To uninstall, open Windows **Settings → Apps**, select **SherpaAxis** and choose
**Uninstall**. Your profiles and settings are retained. The shared vJoy driver
stays installed for other applications that use it.
HidHide also stays installed. SherpaAxis releases its own visibility entries and
restores the previous controller display name during uninstall. After an abnormal
termination, reopen SherpaAxis and exit normally to complete visibility cleanup.

For pedal firmware updates, open **Settings → Firmware update → Update firmware**,
select a bundled firmware and choose **Flash**. Setup includes firmware 017 in
`Firmwares` and the DfuSe tool in `Tools\DfuSe` inside the SherpaAxis installation.
**Check firmware** provides a manual version check; **Backup calibration** saves a
separate settings backup. **Open update logs** opens retained logs and automatic backups.
After **Flash**, SherpaAxis waits for the pedals to restart, restores and saves
the original calibration if needed, and checks the controller, USB port, firmware
and calibration automatically. Click **OK** when the update finishes. If a step
fails, output stays paused and the backup and logs remain available for recovery.
This checks the connected controller and calibration; firmware byte readback is
not performed.
See the [firmware guide](docs/firmware.md) for recovery details. Setup prepares the
USB driver; it does not flash pedal firmware.

## Bug reports

Report problems through [GitHub Issues](https://github.com/jaksdhflkjahsdfkjahsdfkjh/SherpaAxis/issues).
Include the SherpaAxis version, Windows version, controller firmware, steps to
reproduce the problem, and what you expected to happen. Settings → Export diagnostic
report provides a ZIP you can review and attach to the issue.

## License

SherpaAxis is licensed under the [MIT License](LICENSE).
Bundled ST DfuSe components retain their [ST license](vendor/dfuse/SLA0044.txt).
