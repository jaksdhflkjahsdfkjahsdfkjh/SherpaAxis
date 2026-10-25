# Axis processing

`SherpaAxis.Core` processes one axis in caller-supplied units. Software mapping
is independent of the controller's persistent calibration. Each processor/session
is mutable and must be used serially by its owner.

Opening the Calibration panel keeps the live input connection and virtual output
running. Its controls retain draft values between tab switches; cached hardware
bounds are labelled as last known values. Starting a hardware operation pauses
output, restores any temporary drift-monitoring bounds and verifies the controller
before accessing calibration. Changed controller identity or bounds require review
before cached manual limits can be applied. Leaving an active hardware session
restores unsaved controller changes and resumes normal input and output. Cancel
also closes the hardware session and resumes live output while keeping the
Calibration panel open. The display retains its last frame during that handoff;
switching to Tuning afterwards uses the already running input connection.

`AxisProcessor` subtracts a confirmed baseline, normalizes to 0..1, applies
input deadzones and evaluates a monotonic response curve. Original and intermediate
values are retained in the result. Invalid/nonfinite inputs are rejected.

Curves support linear, positive power exponents and piecewise linear control
points. Custom points require strictly increasing inputs, nondecreasing outputs
and endpoints (0,0) and (1,1). Deadzones rescale the remaining interval to 0..1.

## Clutch bite point

The desktop app applies this launch assist after the clutch's normal filtering,
deadzones and response curve. Choose a mode in Tuning → Clutch:

- **Hold bite point** keeps the greater of normal clutch output and the configured
  bite point while the button is held. Pressing the pedal farther takes priority;
  releasing the button restores normal pedal output. Existing profiles keep this mode.
- **Hold full range** gives normal 0–100% pedal output while the button is held.
  Releasing the button scales normal output by the configured percentage. At 40%,
  a fully pressed pedal gives 100% while held and 40% after release; half pedal
  gives 20% after release, and a released pedal gives 0%. Disabling the feature
  restores normal output immediately.

Throttle and brake are unaffected.
No calibration or drift settings are changed, and toggles do not restart vJoy or
reset processing history.

In Tuning, select Clutch, set the output percentage and bind a keyboard or controller
button. Double-tap the binding to enable or disable: each tap must be shorter than
250 ms, and the second press must begin within 350 ms of the first release. A hold
starts after 250 ms; releasing immediately applies the selected mode's released behavior. The second
press can stay held to launch after enabling. Quick taps never apply the bite point.
The Enabled checkbox provides the same session toggle.

Profiles save the mode, percentage and binding; the enabled state starts off when the app
starts. Disconnects or stale input cancel the hold and pending taps but retain the
session toggle. A held binding resumes after 250 ms once healthy input returns,
without another binding, press or game-axis change. Replay never feeds this assist
to a virtual controller. Recorded processing columns keep the normal curve output;
`clutch_final_output` includes launch assistance, and events record toggle/hold changes.

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

Desktop drift correction uses temporary full-range controller bounds to obtain
unclipped sensor counts on PE0 firmware 017. Original bounds are journaled before
the change and restored on mode exit, shutdown or recovery. No device Save command
is sent automatically. Close FEEL-VR Control and bind games only to SherpaAxis's
vJoy device, because physical joystick reports change during raw monitoring.

An interrupted raw-monitoring session can leave a `raw-*.json` recovery record
even if no calibration was started. Startup checks the controller against that
backup and pauses input/output if temporary bounds remain. Close FEEL-VR Control
and games, then choose Restore original calibration. This restores the backed-up
bounds with readback verification; it does not capture a new calibration or send Save.

The controller's stored endpoints already include its hardware deadzone margins.
They are used to reproduce firmware output, never as the original raw release
reference. Completing hardware calibration in SherpaAxis retains the untrimmed
captured range separately, keyed by controller serial, pedal and exact applied
endpoints. Older/vendor calibrations without that record cannot be corrected:
calibrate the pedal once in SherpaAxis rather than guessing its original margins.
Changed or corrupt metadata cannot silently fall back to the trimmed minimum.

Drift is `current released raw value - original raw minimum`. The resulting offset
translates both stored endpoints, preserving their original distance from the raw
endpoints. For raw 3,200,500..4,700,500 and 5% margins, hardware bounds are
3,275,500..4,625,500. A release at 3,000,500 means an offset of -200,000 and hardware
bounds 3,075,500..4,425,500; the two 75,000-count margins remain intact. A release
at the original raw minimum produces zero correction, despite those margins.

Off uses normal firmware-calibrated reports. Auto requires five press/release
cycles within two minutes, each with at least 10% travel and one stable second
at release. Stability allows at most 0.2% peak-to-peak variation. Across qualified
stable windows, jitter is the largest observed peak-to-peak spread. Release estimates
must agree within twice that measured spread, bounded to 0.05%..0.2% of the original
raw span. This tolerance cannot grow beyond the ceiling to accommodate movement.
Differences from the existing baseline within that noise band need no new correction.
Direction and drift magnitude are assessed after five consistent cycles, rather
than rejecting an isolated startup candidate. Mismatches, gaps and reconnects clear evidence.
Automatic adjustment only moves zero downwards and only while both current and
predicted final output are near release. Upward drift requires manual confirmation.
This is a conservative heuristic: a single sensor cannot prove that a foot is off.

By button requires a fresh button press and one stable second with the foot off.
Releasing the binding pauses adjustment; pedal movement requires a new capture.
Fix drift performs that stable-second check without a binding. Both support shifts
up to 25% of the original raw span, subject to sensor limits and the profile's correction
limit. Both endpoints move together, preserving span, hardware margins, profile deadzones and curves.
Offsets are temporary and are cleared on reconnect, rather than saved in profiles.

Save corrected calibration is a separate explicit action, available only after an
accepted correction settles with the pedal released. It verifies controller identity
and unchanged original bounds, retains a hardware backup, writes only the selected
pedal's translated range and sends Save. The matching raw reference is translated
and retained too, so a later session does not apply the same drift twice. Unknown
controller state is never overwritten.

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
