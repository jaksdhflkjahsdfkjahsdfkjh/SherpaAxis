# Architecture

SherpaAxis uses C# and .NET 10. Windows HID access uses the operating system APIs.
The SDK version is pinned in `global.json`; project references define the dependency boundaries.

## Components

- **Devices** owns HID discovery, capabilities and asynchronous input handles.
  `IInputReportSource` separates transport from consumers.

## Build and validation

Run `dotnet restore --locked-mode` and `dotnet build -c Release --no-restore`.
