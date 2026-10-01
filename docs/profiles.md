# Processing profiles

`SherpaAxis.Profiles` stores immutable per-axis settings as UTF-8 JSON. New
files use schema 1.

A profile has `schemaVersion`, a display `name` and an `axes` object keyed by
`throttle`, `brake` or `clutch`. Axis entries store `calibration` (minimum,
maximum, inverted), `deadzone` (lower, upper), `curve`, `filter` and
`maximumBaselineCorrection`. Calibration units must match the input stream.

Curve kinds are `linear`, `power` with `exponent`, and `piecewiseLinear`
with input/output `points`. A curve allows at most 1,024 points. Disabled filters
are null; enabled filters store `timeConstantSeconds` and `resetAfterSeconds`.

## Validation and persistence

Required zero/false/null fields must be present. Unknown or duplicate fields,
unsupported versions, invalid axes/numbers and incompatible curve fields fail
validation. Files are limited to one MiB and 16 JSON nesting levels. Names are
trimmed, limited to 80 UTF-16 code units and reject invalid Unicode/control characters.

Saving validates before opening the destination, writes and flushes a unique temporary
file beside it and promotes it by rename. Existing files require explicit overwrite.
Concurrent creates cannot overwrite the winner; concurrent replacements use the
last successful writer. Interruption may leave a temporary file.

Profiles contain settings only: no baseline offset, filter history, device path
or release permission. Loading creates fresh processing state. See
[processing](processing.md) for runtime ownership.
