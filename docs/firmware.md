# Firmware

The approved image is the vendor's `PE0017.dfu` for PE0 / firmware 017,
VID `0483`, PID `A2EA`. The firmware is not redistributed.

```text
SHA-256 5D525D43D7EF434E61E0DD077379629C420F95CD863E6C607201752368157757
Length  80181 bytes
Data    79872 bytes at 0x0800C000, ending at 0x0801F800
```

`PedalFirmwareImage` validates the DfuSe container, one target/element,
alternate 0, VID/PID suffix, reflected CRC, application address range and
whole-file hash. The hash identifies a reviewed file; it is not a digital
signature or a claim about the newest available firmware.
