# Firmware

The approved image is the vendor's `PE0017.dfu` for PE0 / firmware 017,
VID `0483`, PID `A2EA`. It is included in the application's `Firmwares` folder.

```text
SHA-256 5D525D43D7EF434E61E0DD077379629C420F95CD863E6C607201752368157757
Length  80181 bytes
Data    79872 bytes at 0x0800C000, ending at 0x0801F800
```

`PedalFirmwareImage` validates the DfuSe container, one target/element,
alternate 0, VID/PID suffix, reflected CRC, application address range and
whole-file hash. The hash identifies a reviewed file; it is not a digital
signature or a claim about the newest available firmware.

## Programming backend

The updater supports known PE0 firmware 001–017; it refuses unknown hardware
and newer firmware. Setup includes ST DfuSe 3.0.6 under `Tools\DfuSe`, its private
runtime libraries, and the signed ST USB driver. Setup stages the driver in the
Windows driver store without forcing changes to connected devices. Driver staging
failures are reported by Setup. There is no separate InstallShield installation.
Close FEEL-VR Control, DfuSe applications and games.

When the pedals enter DFU mode, Windows may keep an existing WinUSB binding
instead of selecting the staged DfuSe driver. SherpaAxis prepares the bundled
driver for the single bootloader at the recorded pedal USB port, requesting
Windows administrator approval if needed. It selects that device's compatible
ST driver explicitly; it does not remove WinUSB packages or change other devices.
The executable, driver INF, catalog and driver binaries must match embedded
release hashes. Programming waits for the native DfuSe interface to appear and
rechecks the unique bootloader and USB port after driver preparation.

Before executing DfuSe, the app checks the executable and its required DLLs
against the release hashes embedded in SherpaAxis, then checks its help output
for the required programming options. The bundled ST executable
has no Windows company/version metadata; that metadata is not used to identify it.

Preparation validates the image/tool, controller identity, settings and USB
location. It rejects existing or ambiguous DFU targets. Flashing repeats
preflight, stops normal input/output, stages the image, backs up hardware
settings and durably journals recovery before sending the bootloader command.

Programming requires exactly one compatible bootloader at the original port.
Keep other DFU devices disconnected. DfuSe uses device index 0 after this check;
an external hotplug race inside the vendor tool remains possible.

The pedal may disconnect before Windows finishes the bootloader HID write,
causing a write error even though the command reached it. SherpaAxis records
the uncertain completion and continues only if the expected bootloader appears
at the original USB port with its DfuSe driver ready. An absent, ambiguous or
different-port bootloader keeps the update blocked; no programming begins.

The tool runs with separate process arguments:
`-c --de 0 --al 0 -d --fn <staged-file>`. Exit zero must be accompanied by
the exact `Upgrade successful !` line. PE0 restarts automatically after download;
requesting DfuSe's subsequent upload verification fails after programming because
the controller has left DFU mode. This matches the vendor application's download
sequence. It confirms the tool completed programming, not byte-for-byte readback.
`DownloadCompleted` and `ProgramVerified` are separate journal fields; the latter
stays false for this native workflow. After confirmed programming the app waits
up to 30 seconds for the original controller at its recorded USB port with
firmware 017. If calibration changed, it backs up the post-flash bounds, restores
the pre-update calibration, saves it to the controller and verifies the readback.
Already-matching calibration is not rewritten. Only matching final checks clear
the output block and show the success state with one OK button. This never
claims firmware byte verification. Firmware
tools stop raw-input monitoring and check hardware recovery before reading bounds,
so an unfinished restoration cannot be backed up as calibrated settings. The staged image/tool are
protected against ordinary replacement during programming. Once the bootloader
transition occurs, cancellation does not terminate programming. Slow operation
continues while the UI reports the delay.

## Desktop workflow

1. Open Settings → Firmware update. **Check firmware** reads the controller version;
   **Backup calibration** saves a settings backup without starting calibration mode.
2. Choose **Update firmware**, select a firmware from the dropdown and choose **Flash**.
   The app scans only its `Firmwares` folder and omits invalid or unreviewed images.
   Image/tool, identity and USB-port checks run automatically before programming.
3. Leave the pedals connected. SherpaAxis waits for automatic reconnection,
   restores and saves the backed-up calibration if needed, and checks the
   controller, USB port, firmware and restored bounds. Click **OK** when the
   success message appears. No separate restoration or firmware check is needed.
4. If a step fails, output remains paused and the backup and logs are retained.
   Manual reconnection is needed only if programming completed and automatic
   reconnection timed out. Follow the message: unplug the pedal USB cable for
   five seconds, reconnect it to the same USB port and choose **Check firmware**
   to resume the automatic restoration/check sequence without another flash.
   Once programming is confirmed, the main update button becomes **Check firmware**.
   **Restore calibration backup** remains available for manual recovery.

Final controller-check failures are retained in `update-log.json`, including the
reconnection instructions when the controller does not return. A pending update
keeps those instructions after restarting the app. Do not unplug the pedals while
the firmware tool is still programming.

After a confirmed upgrade from firmware 001–016 to 017, SherpaAxis rediscovers the
single supported controller at the original USB port. A changed reported serial
is associated automatically with that update when the pre-update backup matches.
Calibration restoration and final checks continue without a confirmation prompt
or another flash. This also applies when resuming a confirmed pending update with
**Check firmware** after restarting the app. Keep the same physical pedals
connected throughout the update; USB location is not proof of an unchanged
physical controller if pedals are exchanged at that port.

The journal retains both serials and records automatic association explicitly.
The original backup is unchanged. Wrong-port devices, multiple controllers,
unconfirmed programming, invalid backups and subsequent serial changes remain
blocked. Reflashing firmware 017 does not permit an unrelated serial change.
Normal identity matching for other hardware operations is unchanged.

## Recovery and validation

Private records live under `%LOCALAPPDATA%/SherpaAxis/FirmwareUpdates`.
`update.json` blocks normal output until resolved. Each operation retains its
staged image, `hardware-backup.json` and `update-log.json`. These are settings
backups, not a full firmware readback or rollback.

Retry requires the same bootloader location and unchanged staged image/tool.
Open **Update firmware → Retry flash** to retry an interrupted operation. Selection
is locked to the original staged image until that update is resolved. Use
**Open update logs** to inspect the retained operation and calibration backup.
A controller reporting 017 after an uncertain update does not prove verified
programming. For an unconfirmed programming result, Check firmware keeps output
blocked and shows an inline **Use detected controller** recovery action only when
the identity, USB port and calibration match. This explicit recovery does not
claim a confirmed download or byte verification. Otherwise restore calibration
or retry the recorded bootloader operation.
Logs remain after resolution; only the blocking journal is cleared. No automatic
retry, downgrade or HTTP firmware download is performed.

Automated tests use fake transports, bootloaders and tool processes. On October 22,
2026, the maintainer reported successful physical firmware flashing with automatic
calibration restoration and pedal-output checks, clean-PC installation with reboot
and uninstall, longer racing/standby/reconnect drift tests, and upgrades preserving
profiles, auto-load, bindings, language and startup preferences. These are reported
manual acceptance results; additional Windows builds and controllers are not
implied. Firmware byte readback is not performed. Upgrades from older firmware
remain a separate hardware test from reflashing a controller already on 017.

## Physical acceptance test

1. Install the candidate Setup build and restart Windows if requested. Close
   FEEL-VR Control (including its tray instance), games and other DfuSe tools.
   Connect only one supported pedal controller and disconnect other DFU devices.
   Keep the computer awake and power and USB connections stable throughout.
2. In Settings → Firmware update, choose **Check firmware** and record the
   controller identity and firmware version. Choose **Backup calibration** and
   retain that file separately. Record released and pressed readings for all
   three pedals before updating.
3. Choose **Update firmware → PE0017.dfu · 017 → Flash**. Record whether the
   controller enters DFU mode and the tool starts. Do not unplug the pedals or
   close the app during programming. Keep the update logs if
   any step fails; do not start unrelated flashing tools alongside SherpaAxis.
4. After SherpaAxis reports **Firmware programming completed**, let the pedals
   reconnect automatically and wait for calibration restoration and the final
   controller checks. Confirm the success message and click **OK**. The retained log must show
   successful programming. Record byte readback as not performed; detecting 017
   alone does not establish successful programming or matching firmware bytes.
5. Review calibration, then test released, partial and full input on each pedal
   and processed X/Y/Z output in Windows. Restart SherpaAxis and verify normal
   reconnection and that no recovery block remains. Record the build version,
   Windows version, before/after readings and the update log with the result.

Reflashing a controller already on 017 tests the physical programming path, but
does not establish upgrade behavior from older firmware. Test such upgrades on
hardware that actually runs that firmware; do not downgrade to manufacture a test.
Keep interrupted-operation recovery tests simulated until a dedicated recovery
device and recovery procedure are available. Do not deliberately interrupt power
or USB during a first physical acceptance test.
