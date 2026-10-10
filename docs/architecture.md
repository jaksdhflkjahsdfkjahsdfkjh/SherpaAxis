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

## Input and native configuration

FEEL-VR Pedals One uses VID `0483`, PID `A2EA`. The joystick collection is
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

## Build and validation

Run `dotnet restore --locked-mode` and `dotnet build -c Release --no-restore`.

Run `dotnet test -c Release --no-build` for the available automated tests.

Native vJoy output tests are opt-in and require an available physical driver/device.
Ordinary automated tests use fake transports and do not establish physical acceptance.
