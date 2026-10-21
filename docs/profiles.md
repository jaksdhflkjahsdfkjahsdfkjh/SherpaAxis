# Processing profiles

`SherpaAxis.Profiles` stores immutable per-axis settings as UTF-8 JSON. New
files use schema 3. Earlier schemas 1 through 2 remain readable.

A profile has `schemaVersion`, a display `name` and an `axes` object keyed by
`throttle`, `brake` or `clutch`. Axis entries store `calibration` (minimum,
maximum, inverted), `deadzone` (lower, upper), `curve`, `filter` and
`maximumBaselineCorrection`. Calibration units must match the input stream.

Curve kinds are `linear`, `power` with `exponent`, and `piecewiseLinear`
with input/output `points`. A curve allows at most 1,024 points. Disabled filters
are null; enabled filters store `timeConstantSeconds` and `resetAfterSeconds`.

Schema 2 adds filter `kind` and `windowSamples`, plus optional axis `session`
settings: `maximumCorrectionPerSecond`, `maximumCorrectionOutputPerSecond`
and `resetAfterSeconds`. Schema 1 rejects these extra fields. Filter kinds:
`Exponential`, `TwoStageExponential`, `MovingAverage`, `Median`, `WeightedMovingAverage`, `MedianExponential`.

Schema 3 adds `correction` with `mode` (`Off`, `Auto`, `ByButton`) and an
optional keyboard/controller `button` binding. Older schemas default to Off.
Unknown modes and incompatible bindings are rejected.

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

## CLI

```powershell
dotnet run --project src/SherpaAxis.Cli -c Release -- profile create --json example.json --name "Example" --axis brake --minimum 100 --maximum 1000
dotnet run --project src/SherpaAxis.Cli -c Release -- profile validate --input example.json
dotnet run --project src/SherpaAxis.Cli -c Release -- replay --input capture.csv --csv processed.csv --profile example.json --axis brake
```

`set-axis` replaces one complete axis configuration. `export` validates and
writes a canonical copy. Profile output paths require new files unless
`--overwrite true` is explicit; parent directories must exist. Replay rejects
processing overrides when a profile is supplied.

## Desktop library

The desktop library keeps named profiles separate from runtime state.
Valid tuning changes apply automatically; saving persists the profile.
The saved/unsaved indicator compares effective profile settings, ignoring numeric
formatting and unused editor fields. Invalid values retain the last valid processing
configuration and show a validation message. Display names
are not trusted as filesystem paths.

Auto-load designates one saved startup profile. Selecting another removes
the previous designation. Creating or editing a profile does not apply it
automatically on a future startup without that selection.

Unfinished editor text is kept in a separate bounded draft file, not saved as
a valid processing profile. Recovering or undeleting a draft applies its valid
settings automatically. The last
deleted profile can be restored during the session without overwriting a file.
