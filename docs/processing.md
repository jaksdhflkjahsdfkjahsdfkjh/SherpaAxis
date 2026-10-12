# Axis processing

`SherpaAxis.Core` processes one axis in caller-supplied units. Software mapping
is independent of the controller's persistent calibration. Each processor/session
is mutable and must be used serially by its owner.

`AxisProcessor` subtracts a confirmed baseline, normalizes to 0..1, applies
input deadzones and evaluates a monotonic response curve. Original and intermediate
values are retained in the result. Invalid/nonfinite inputs are rejected.

Curves support linear, positive power exponents and piecewise linear control
points. Custom points require strictly increasing inputs, nondecreasing outputs
and endpoints (0,0) and (1,1). Deadzones rescale the remaining interval to 0..1.

## Baseline correction

`ConfirmReleasedBaseline` requires an explicit caller decision. The offset is
the released input minus the calibration's released endpoint. Subtracting it
translates both endpoints without changing the span or gain. The absolute offset
is bounded by the configured maximum, which defaults to zero. Processing samples
does not infer physical release. `ResetBaseline` clears the correction.

## Filtering

Filtering is disabled when no `LowPassFilterSettings` is supplied. Enabled
filters require finite, nonnegative, nondecreasing monotonic timestamps.
Exponential smoothing uses `1 - exp(-dt / tau)`; the first sample and a gap at
or above `resetAfterSeconds` seed from current input. Equal timestamps do not
advance filter history. Baseline confirmation/reset clears processor filter history.

Available algorithms: `Exponential`, `TwoStageExponential`, `MovingAverage`, `Median`, `WeightedMovingAverage`, `MedianExponential`.

Window sizes are odd, 3..101 reports, with growing windows during warmup.
Pure window filters use the report window; exponential stages use the time
constant. Filtering smooths intentional movement as well as jitter and adds delay.

## Session ownership

`AxisSession` bounds correction speed in input units and final output change
per second. A baseline request changes the target; each sample separately
supplies `releaseConfirmed`. Without permission the target is held. Start,
connection changes and sampling gaps clear correction and filter history.
Duplicate timestamps do not advance transitions.

The session filters original input before subtracting its gradually applied
offset, avoiding filter resets during correction transitions. `AxisProcessor`
applies its immediate offset before filtering. Both normalize, apply deadzones
and evaluate the curve afterwards.

Desktop baseline modes are Off, Auto and By button. Auto requires a substantial
press/release excursion and stable near-zero dwell; its capture band is limited
to 0.5% of the calibration span. It is a heuristic, not evidence of physical
release. By button uses an explicit held keyboard or controller binding.
Neither mode recovers information clipped by firmware.

## Offline replay

Replay opens no HID handles. Use input/output paths and bounds in the selected
axis's units, for example:

```powershell
dotnet run --project src/SherpaAxis.Cli -c Release -- replay --input capture.csv --csv processed.csv --minimum 100 --maximum 1000
```

The complete monitor CSV is validated, including rows outside a selected
interval. Timestamps and positive connection numbers must be nondecreasing.
A new connection clears the explicit baseline. Output files are never overwritten.
Cancellation/storage errors may leave incomplete output and return an error.

`--profile path.json --axis brake` can replace explicit processing overrides.
Use [profiles](profiles.md) to persist settings; runtime offsets are never saved.
