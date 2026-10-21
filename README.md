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

## Installation

Requirements: Windows 10 22H2 or Windows 11 (64-bit Intel/AMD) and FEEL-VR
Pedals One. Native hardware calibration supports the PE0 controller with firmware 017.

Installation uses a single offline `Setup.exe`. It includes the .NET runtime and
the vJoy driver and runtime libraries; you do not need to install developer tools
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

If virtual output is unavailable, open **Settings** and run **Automatic vJoy
setup**. Close other vJoy feeders first. An incompatible existing vJoy driver
requires repair; Setup preserves it rather than replacing it silently.

Use **Tuning** for curves, filters and profile deadzones, **Calibration** for
hardware calibration, and **Settings** for language, startup preferences and
device checks. Tuning changes apply automatically. The saved/unsaved indicator
shows whether they are stored in your profile. Save your profile to keep them
after restart; enable **Auto-load** if you want
it applied the next time the app starts.

To update, exit SherpaAxis from the tray and run the newer `Setup.exe`. Your
profiles and settings are retained. Enable **Start with Windows** in Settings
if you want the app to launch when you sign in.

To uninstall, open Windows **Settings → Apps**, select **SherpaAxis** and choose
**Uninstall**. Your profiles and settings are retained. The shared vJoy driver
stays installed for other applications that use it.

Optional firmware updates require ST DfuSe and its USB driver separately; see
the [firmware guide](docs/firmware.md). Setup does not flash pedal firmware.

## License

SherpaAxis is licensed under the [MIT License](LICENSE).
