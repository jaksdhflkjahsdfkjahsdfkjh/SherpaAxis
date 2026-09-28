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
