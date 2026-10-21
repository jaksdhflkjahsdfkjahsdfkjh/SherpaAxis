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

Preparation validates the image/tool, controller identity, settings and USB
location. It rejects existing or ambiguous DFU targets. Flashing repeats
preflight, stops normal input/output, stages the image, backs up hardware
settings and durably journals recovery before sending the bootloader command.

Programming requires exactly one compatible bootloader at the original port.
Keep other DFU devices disconnected. DfuSe uses device index 0 after this check;
an external hotplug race inside the vendor tool remains possible.

The tool runs with separate process arguments:
`-c --de 0 --al 0 -d --v --fn <staged-file>`. Exit zero must be accompanied by
exact programming and verification success lines. The staged image/tool are
protected against ordinary replacement during programming. Once the bootloader
transition occurs, cancellation does not terminate programming. Slow operation
continues while the UI reports the delay.

## Desktop workflow

1. Open Settings → Firmware update. **Check firmware** reads the controller version;
   **Backup calibration** saves a settings backup without starting calibration mode.
2. Choose **Update firmware**, select a firmware from the dropdown and choose **Flash**.
   The app scans only its `Firmwares` folder and omits invalid or unreviewed images.
   Image/tool, identity and USB-port checks run automatically before programming.
3. After programming verifies, unplug/reconnect and choose **Check firmware**.
4. Review changed calibration before explicitly accepting it. Settings are never
   restored automatically after flashing.

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
programming. Retry or explicitly accept its detected firmware and valid calibration.
Logs remain after resolution; only the blocking journal is cleared. No automatic
retry, downgrade or HTTP firmware download is performed.

Automated tests use fake transports, bootloaders and tool processes. Physical
bootloader entry, native DfuSe programming and reconnect acceptance remain
unverified and require a coordinated hardware test before public release. The
bundled tool's help command and driver signatures are checked separately; these
checks do not establish that physical flashing works on a particular Windows setup.
