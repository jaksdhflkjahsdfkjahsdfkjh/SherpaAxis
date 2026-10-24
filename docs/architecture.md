# Architecture

SherpaAxis uses C# and .NET 10. Windows HID access uses the operating system APIs.
The SDK version is pinned in `global.json`; project references define the dependency boundaries.

## Components

- **Devices** owns HID discovery, capabilities and asynchronous input handles.
  `IInputReportSource` separates transport from consumers.
- **FeelVR** matches the pedal collection, decodes reports and reconnects acquisition.
- **Cli** provides local device diagnostics and capture commands.

  Capture acquisition is independent of console display and CSV storage. Accepted
  rows drain on normal shutdown; a failed writer cancels acquisition.
- **Core** owns axis mapping, input deadzones and response curves. It has no
  vendor, transport, persistence or UI dependencies. See [processing](processing.md).
- **Profiles** validates and persists immutable processing settings. It references
  Core, with no Windows or UI dependency. See [profile format](profiles.md).
- **App** is a WPF desktop application. View models expose editor and graph state;
  device acquisition runs independently of dispatcher refresh. The processing
  model provides bounded snapshots for drawing.
- **Bridge** owns the dynamically loaded vJoy SDK and a latest-output worker.
  Driver writes run outside the processing lock. Source changes, configuration
  changes and stale input disarm output before the device is released.

Native calibration owns a separate held HID session. It stops normal acquisition
and virtual output, verifies identity and journals recovery before changing bounds.
Restoration must complete before output resumes. Persistent Save is a separate command.

`PedalRuntime` owns live input and automatic virtual output independently of graph
refresh. Device checks inspect metadata without acquiring or feeding a virtual device.

The side panels retain their controls and layout between switches. Tuning and
Settings switch without restarting input, resetting the graph or rerunning device
diagnostics. Diagnostics refresh on device events, setup operations and explicit
requests. Calibration reuses its controls but opens a fresh hardware transaction;
controller identity, current bounds and recovery checks are never cached across
sessions. Leaving calibration verifies restoration before resuming output.

`DesktopServices` supplies application dependencies; tests inject substitutes.
Desktop preferences are separate from pedal profiles. Tray actions use the same
mode and profile handlers as the window. A named mutex prevents duplicate instances.

`AtomicFile` writes a temporary file beside its destination, flushes it and promotes
it by replacement. Recovery transactions determine when a durable write is required.

Desktop services are grouped by responsibility under Desktop, Diagnostics,
Firmware, Hardware, Input, Output, Persistence, Recording, Runtime and Updates. Dashboard
view-model and window workflow partials keep editing, graphs and device operations separate.

Diagnostic support ZIPs contain bounded, redacted logs and status. They exclude
continuous input, profile contents, hardware backups and firmware payloads.

Application update checks use the public GitHub latest-release API, with no account
token or telemetry. Stable numeric tags are compared with the assembly version.
One automatic check runs at each app startup, independently of pedal acquisition
and virtual output; there is no recurring timer. Settings provides a manual check
and an opt-out that takes effect at the next startup. Each check requests fresh
metadata. `%LOCALAPPDATA%\SherpaAxis\updates.json` deduplicates tray notices across
restarts without caching release results. Requests have an
eight-second deadline and are cancelled at shutdown. Network failures stay in the
Updates section. Release links are constructed for this repository; downloaded
metadata cannot redirect the user to a different host. The app never downloads or
executes an installer itself.

To notify users, publish a public, stable GitHub Release with a numeric tag such as
`v1.2.0` and attach `Setup.exe`. A commit, a tag alone, a draft or a prerelease does
not trigger a notification. Update the project version before building that release.
New features increment the minor version and reset the patch (for example,
1.1.1 → 1.2.0); fixes increment the patch (1.1.0 → 1.1.1). Set the version in
`Directory.Build.props` and keep the installer's fallback version in sync.
Rebuilding the same version does not increment it.
Users need network access to GitHub; a private or unpublished repository returns no
public release. Windows notification preferences may suppress the tray balloon;
the footer link remains available.

## Input and native configuration

SherpaAxis recognizes the FEEL-VR Pedals One controller reporting VID `0483`, PID
`A2EA`. These are product identifiers shared by matching controllers, not an
individual pedal set's serial number. Other FEEL-VR models or revisions may use
different identifiers and are not covered by that match. The joystick collection is
usage page 1, usage 4. Report 1 is nine bytes: unsigned little-endian throttle
(16 bits), brake (32 bits), clutch (16 bits). Valid logical maxima are 16,384
for throttle/clutch and 16,777,216 for brake. These are firmware-calibrated
device values; they cannot reveal sensor input clipped by hardware calibration.

The configuration collection is usage page `FF00`, usage 1. Feature report 3
is 17 bytes: signed little-endian minimum/maximum pairs, 16 bits each for
throttle and clutch and 32 bits each for brake. Feature reads validate report
identity and length independently of joystick input.

Command report 2 contains a little-endian 32-bit command padded to 17 bytes.
Commands are LED off (0), LED on (1), enter bootloader (2) and Save (3).
Writing bounds and saving them persistently are separate operations.

See [firmware](firmware.md) for image validation and supported hardware.

See [setup](setup.md) for device selection and installation.

## Build and validation

Run `./tools/Verify.ps1` for locked restore, Release build, tests and formatting.

Native vJoy output tests are opt-in and require an available physical driver/device.
Ordinary automated tests use fake transports and do not establish physical acceptance.
