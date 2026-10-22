using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Input;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class DriftCompensationTests
{
    private static readonly PedalCalibration Hardware = new(new(1000, 15000), new(3200500, 4700500), new(1000, 15000));

    [Fact]
    public void RepeatedReleaseJitterDefinesANoiseBandWithoutBeingReportedAsUpwardDrift()
    {
        var engine = Configured(BaselineCorrectionMode.Auto);
        engine.SetSensorCalibration(new("test-controller", Hardware.WithRange(PedalChannel.Brake,
            PedalHardwareSettings.ApplyDeadzones(Hardware.Brake, 5, 5)),
            new Dictionary<PedalAxis, HardwareCalibrationRange> { [PedalAxis.Brake] = Hardware.Brake }));
        double time = 0;
        Hold(engine, ref time, Hardware.Brake.Minimum + 1000, 200);
        Assert.Contains("Waiting for five", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        for (var cycle = 0; cycle < 5; cycle++)
        {
            Hold(engine, ref time, Hardware.Brake.Maximum, 10);
            for (var sample = 0; sample < 120; sample++)
            { Sample(engine, ref time, Hardware.Brake.Minimum + 1000 + (sample % 2 == 0 ? 600 : -600)); }
            if (cycle < 4) { Assert.Contains($"{cycle + 1}/5", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal); }
        }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.Contains("within measured sensor noise", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
    }

    [Theory]
    [InlineData(200000)]
    [InlineData(-400000)]
    public void DirectionAndMagnitudeAreAssessedOnlyAfterFiveConsistentCycles(int drift)
    {
        var engine = Configured(BaselineCorrectionMode.Auto);
        double time = 0;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            Cycle(engine, ref time, drift);
            Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
            if (cycle < 4) { Assert.Contains($"{cycle + 1}/5", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal); }
        }
        var status = engine.DriftState(PedalAxis.Brake).Status;
        Assert.Contains("Five cycles agree", status, StringComparison.Ordinal);
        Assert.Contains(drift > 0 ? "could be foot pressure" : "automatic limit", status, StringComparison.Ordinal);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
    }

    [Fact]
    public void AChangingReleaseCannotWidenItsOwnNoiseBandUntilItLooksStable()
    {
        var engine = Configured(BaselineCorrectionMode.Auto);
        double time = 0;
        for (var cycle = 0; cycle < 8; cycle++)
        {
            Hold(engine, ref time, Hardware.Brake.Maximum - 200000, 10);
            for (var sample = 0; sample < 120; sample++)
            { Sample(engine, ref time, Hardware.Brake.Minimum - 200000 + sample * 100); }
        }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.Contains("Waiting for five", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
    }

    [Fact]
    public void IdleInputNeverStartsACheckAndReleasingTheBindingCancelsItsProgress()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        using var buttons = new ButtonStateBuffer();
        var button = new CorrectionButton("keyboard", 65);
        double time = 0;
        Hold(engine, ref time, Hardware.Brake.Minimum - 200000, 200);
        Assert.StartsWith("Ready.", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        engine.ProcessCorrectionButtons(buttons);
        buttons.Set(button, true);
        for (var sample = 0; sample < 50; sample++)
        { engine.ProcessCorrectionButtons(buttons); Sample(engine, ref time, Hardware.Brake.Minimum - 200000); }
        Assert.Contains("0.5/1.0", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        buttons.Set(button, false); engine.ProcessCorrectionButtons(buttons);
        Hold(engine, ref time, Hardware.Brake.Minimum - 200000, 200);
        Assert.Contains("cancelled", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        buttons.Set(button, true);
        for (var sample = 0; sample < 50; sample++)
        { engine.ProcessCorrectionButtons(buttons); Sample(engine, ref time, Hardware.Brake.Minimum - 200000); }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnstableChecksTimeOutForBothFixDriftAndAHeldBinding(bool held)
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        using var buttons = new ButtonStateBuffer();
        double time = 0;
        Sample(engine, ref time, Hardware.Brake.Minimum - 200000);
        if (held)
        {
            engine.ProcessCorrectionButtons(buttons);
            buttons.Set(new("keyboard", 65), true);
        }
        else { engine.RequestFixDrift(PedalAxis.Brake); }
        for (var sample = 0; sample < 1200; sample++)
        {
            engine.ProcessCorrectionButtons(buttons);
            Sample(engine, ref time, Hardware.Brake.Minimum - 200000 + sample % 2 * 10000);
        }
        Assert.Contains("not stable", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
    }

    [Fact]
    public void TuningChangesDoNotCarryAStaleCheckingMessageIntoAnIdleSession()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        double time = 0;
        Sample(engine, ref time, Hardware.Brake.Minimum);
        engine.RequestFixDrift(PedalAxis.Brake);
        Hold(engine, ref time, Hardware.Brake.Minimum, 50);
        Assert.Contains("Checking", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        var (profile, limits) = Settings(BaselineCorrectionMode.ByButton, new(.01, .01));
        engine.Configure(profile, limits);
        Sample(engine, ref time, Hardware.Brake.Minimum);
        Assert.StartsWith("Ready.", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        engine.RequestFixDrift(PedalAxis.Brake);
        Hold(engine, ref time, Hardware.Brake.Minimum, 150);
        Assert.Contains("no correction needed", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BaselineCorrectionMode.Auto, 0)]
    [InlineData(BaselineCorrectionMode.ByButton, 0)]
    [InlineData(BaselineCorrectionMode.Auto, -200000)]
    [InlineData(BaselineCorrectionMode.ByButton, -300000)]
    [InlineData(BaselineCorrectionMode.ByButton, 200000)]
    public void HardwareDeadzoneMarginsAreNeverMistakenForDrift(BaselineCorrectionMode mode, int drift)
    {
        var engine = Configured(mode);
        var raw = Hardware.Brake;
        var trimmed = PedalHardwareSettings.ApplyDeadzones(raw, 5, 5);
        engine.SetSensorCalibration(new("test-controller", Hardware.WithRange(PedalChannel.Brake, trimmed),
            new Dictionary<PedalAxis, HardwareCalibrationRange> { [PedalAxis.Brake] = raw }));
        double time = 0;
        Sample(engine, ref time, raw.Minimum + drift);
        if (mode == BaselineCorrectionMode.ByButton) { engine.RequestFixDrift(PedalAxis.Brake); }
        else { for (var cycle = 0; cycle < 5; cycle++) { Cycle(engine, ref time, drift); } }
        Hold(engine, ref time, raw.Minimum + drift, 400);
        var state = engine.DriftState(PedalAxis.Brake);
        Assert.Equal(drift, state.Offset, 5);
        Assert.Equal(raw.Minimum, state.RawMinimum);
        Assert.Equal(raw.Maximum, state.RawMaximum);
        Assert.Equal(trimmed.Minimum + drift, state.Minimum);
        Assert.Equal(trimmed.Maximum + drift, state.Maximum);
        Assert.Equal(0, Brake(engine).Processing.Output);
        if (drift != 0)
        {
            Assert.True(state.CanSave);
            var proposal = engine.CorrectedHardwareCalibration(PedalAxis.Brake);
            Assert.Equal(raw, proposal.RawOriginal);
            Assert.Equal(trimmed.Minimum - raw.Minimum, proposal.Corrected.Brake.Minimum - (raw.Minimum + drift));
            Assert.Equal(raw.Maximum - trimmed.Maximum, raw.Maximum + drift - proposal.Corrected.Brake.Maximum);
        }
        Sample(engine, ref time, (raw.Minimum + raw.Maximum) / 2 + drift);
        Assert.Equal(.5, Brake(engine).Processing.Output, 10);
    }

    [Theory]
    [InlineData(BaselineCorrectionMode.Auto)]
    [InlineData(BaselineCorrectionMode.ByButton)]
    public void MissingOriginalRawRangeNeverFallsBackToTrimmedHardwareBounds(BaselineCorrectionMode mode)
    {
        var engine = Configured(mode);
        engine.SetSensorCalibration(new("test-controller", Hardware));
        double time = 0;
        if (mode == BaselineCorrectionMode.ByButton)
        { Assert.Throws<InvalidOperationException>(() => engine.RequestFixDrift(PedalAxis.Brake)); }
        for (var cycle = 0; cycle < 8; cycle++) { Cycle(engine, ref time, -75000); }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
        Assert.Null(engine.DriftState(PedalAxis.Brake).RawMinimum);
        Assert.Contains("Original raw calibration is unavailable", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => engine.CorrectedHardwareCalibration(PedalAxis.Brake));
    }

    [Theory]
    [InlineData(BaselineCorrectionMode.Auto)]
    [InlineData(BaselineCorrectionMode.ByButton)]
    public void ReleasedSensorJitterUsesTheUpperEnvelopeWithoutCreatingIdleOutput(BaselineCorrectionMode mode)
    {
        var engine = Configured(mode);
        double time = 0;
        const int drift = -72000;
        Sample(engine, ref time, Hardware.Brake.Minimum + drift);
        if (mode == BaselineCorrectionMode.ByButton) { engine.RequestFixDrift(PedalAxis.Brake); }
        for (var cycle = 0; cycle < 5; cycle++)
        {
            if (mode == BaselineCorrectionMode.Auto) { Hold(engine, ref time, Hardware.Brake.Maximum + drift, 10); }
            for (var sample = 0; sample < 120; sample++)
            { Sample(engine, ref time, Hardware.Brake.Minimum + drift + (sample % 2 == 0 ? 750 : -750)); }
        }
        for (var sample = 0; sample < 400; sample++)
        {
            Sample(engine, ref time, Hardware.Brake.Minimum + drift + (sample % 2 == 0 ? 750 : -750));
            Assert.Equal(0, Brake(engine).Processing.Output);
        }
        Assert.Equal(drift + 750, engine.DriftState(PedalAxis.Brake).Offset, 5);
    }

    [Theory]
    [InlineData(-100500)]
    [InlineData(-200000)]
    [InlineData(-300000)]
    public void FiveMatchingCyclesTranslateBothEndpointsWithoutChangingSensitivity(int drift)
    {
        var engine = Configured(BaselineCorrectionMode.Auto);
        double time = 0;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            Cycle(engine, ref time, drift);
            Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        }
        Cycle(engine, ref time, drift);
        Hold(engine, ref time, Hardware.Brake.Minimum + drift, 400);
        var state = engine.DriftState(PedalAxis.Brake);
        Assert.Equal(drift, state.Offset, 5);
        Assert.Equal(Hardware.Brake.Minimum + drift, state.Minimum);
        Assert.Equal(Hardware.Brake.Maximum + drift, state.Maximum);
        Assert.True(state.CanSave);
        var proposal = engine.CorrectedHardwareCalibration(PedalAxis.Brake);
        Assert.Equal(Hardware.Throttle, proposal.Corrected.Throttle);
        Assert.Equal(Hardware.Clutch, proposal.Corrected.Clutch);
        Sample(engine, ref time, Hardware.Brake.Minimum + drift + 750000);
        Assert.Equal(.5, Brake(engine).Processing.Output, 10);
    }

    [Fact]
    public void ConstantFootPressureAndMismatchedCyclesNeverApplyACorrection()
    {
        var engine = Configured(BaselineCorrectionMode.Auto);
        double time = 0;
        Hold(engine, ref time, Hardware.Brake.Minimum + 25000, 1000);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        for (var cycle = 0; cycle < 12; cycle++) { Cycle(engine, ref time, cycle % 2 == 0 ? -200000 : -220000); }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.Contains("disagree", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
    }

    [Fact]
    public void UpwardDriftDoesNotLearnARestingFootAcrossRepeatableCycles()
    {
        var engine = Configured(BaselineCorrectionMode.Auto);
        double time = 0;
        for (var cycle = 0; cycle < 10; cycle++) { Cycle(engine, ref time, 20000); }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("reconnect")]
    public void InterruptedEvidenceRequiresFiveFreshCycles(string reason)
    {
        var engine = Configured(BaselineCorrectionMode.Auto);
        double time = 0;
        for (var cycle = 0; cycle < 4; cycle++) { Cycle(engine, ref time, -300000); }
        if (reason == "gap") { time += 1; }
        else { engine.Accept(new(PedalConnectionState.Disconnected, TimeSpan.FromSeconds(time), 1)); }
        Cycle(engine, ref time, -300000);
        Hold(engine, ref time, Hardware.Brake.Minimum - 300000, 400);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
    }

    [Theory]
    [InlineData(-300000)]
    [InlineData(200000)]
    public void FixDriftRequiresAStableSecondAndSupportsLargeDriftInEitherDirection(int drift)
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        double time = 0;
        Sample(engine, ref time, Hardware.Brake.Minimum + drift);
        engine.RequestFixDrift(PedalAxis.Brake);
        Hold(engine, ref time, Hardware.Brake.Minimum + drift, 99);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Hold(engine, ref time, Hardware.Brake.Minimum + drift, 400);
        Assert.Equal(drift, engine.DriftState(PedalAxis.Brake).Offset, 5);
        Assert.True(engine.DriftState(PedalAxis.Brake).CanSave);
        Sample(engine, ref time, Hardware.Brake.Minimum + drift + 750000);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
        Assert.Equal(.5, Brake(engine).Processing.Output, 10);
    }

    [Fact]
    public void AnUnstableManualCaptureDoesNotChangeCalibrationOrTheCurrentOffset()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        double time = 0;
        Sample(engine, ref time, Hardware.Brake.Minimum - 300000);
        engine.RequestFixDrift(PedalAxis.Brake);
        for (var sample = 0; sample < 1200; sample++) { Sample(engine, ref time, Hardware.Brake.Minimum - 300000 + sample % 2 * 10000); }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
    }

    [Fact]
    public void ClippedInputCannotBeUsedAsRawDriftEvidence()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        engine.SetSensorCalibration(null);
        engine.RequestFixDrift(PedalAxis.Brake);
        double time = 0;
        Hold(engine, ref time, 0, 500);
        Assert.False(engine.DriftState(PedalAxis.Brake).RawAvailable);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.Throws<InvalidOperationException>(() => engine.CorrectedHardwareCalibration(PedalAxis.Brake));
    }

    [Fact]
    public void TuningReconfigurationRetainsAValidatedOffsetButReconnectClearsIt()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        double time = 0;
        Sample(engine, ref time, Hardware.Brake.Minimum - 300000);
        engine.RequestFixDrift(PedalAxis.Brake);
        Hold(engine, ref time, Hardware.Brake.Minimum - 300000, 400);
        Assert.Equal(-300000, engine.DriftState(PedalAxis.Brake).Offset, 5);
        var (profile, limits) = Settings(BaselineCorrectionMode.ByButton, new(.1, .1));
        engine.Configure(profile, limits);
        Sample(engine, ref time, Hardware.Brake.Minimum - 300000 + 750000);
        Assert.Equal(-300000, engine.DriftState(PedalAxis.Brake).Offset, 5);
        Assert.Equal(.5, Brake(engine).Processing.Output, 10);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 2, new(1000, Hardware.Brake.Minimum - 300000, 1000)));
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
    }

    [Fact]
    public void BoundButtonNeedsAReleaseAndAnEntireStableSecond()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        using var buttons = new ButtonStateBuffer();
        var button = new CorrectionButton("keyboard", 65);
        double time = 0;
        buttons.Set(button, true);
        for (var sample = 0; sample < 200; sample++)
        { Sample(engine, ref time, Hardware.Brake.Minimum - 300000); engine.ProcessCorrectionButtons(buttons); }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        buttons.Set(button, false); engine.ProcessCorrectionButtons(buttons);
        buttons.Set(button, true);
        for (var sample = 0; sample < 99; sample++)
        { engine.ProcessCorrectionButtons(buttons); Sample(engine, ref time, Hardware.Brake.Minimum - 300000); }
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        for (var sample = 0; sample < 400; sample++)
        { engine.ProcessCorrectionButtons(buttons); Sample(engine, ref time, Hardware.Brake.Minimum - 300000); }
        Assert.Equal(-300000, engine.DriftState(PedalAxis.Brake).Offset, 5);
        Assert.Contains("Drift fixed", engine.DriftState(PedalAxis.Brake).Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasedBindingDoesNotResetTheIndependentManualCapture()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        using var buttons = new ButtonStateBuffer();
        double time = 0;
        Sample(engine, ref time, Hardware.Brake.Minimum - 300000);
        engine.RequestFixDrift(PedalAxis.Brake);
        for (var sample = 0; sample < 400; sample++)
        { engine.ProcessCorrectionButtons(buttons); Sample(engine, ref time, Hardware.Brake.Minimum - 300000); }
        Assert.Equal(-300000, engine.DriftState(PedalAxis.Brake).Offset, 5);
        Assert.True(engine.DriftState(PedalAxis.Brake).CanSave);
    }

    [Fact]
    public void ReleasingManualHoldBeforeAStableSecondCancelsTheCapture()
    {
        var engine = Configured(BaselineCorrectionMode.ByButton);
        double time = 0;
        Sample(engine, ref time, Hardware.Brake.Minimum - 300000);
        engine.RequestFixDrift(PedalAxis.Brake);
        Hold(engine, ref time, Hardware.Brake.Minimum - 300000, 50);
        engine.CancelFixDrift(PedalAxis.Brake);
        Hold(engine, ref time, Hardware.Brake.Minimum - 300000, 400);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        Assert.False(engine.DriftState(PedalAxis.Brake).CanSave);
    }

    private static DashboardEngine Configured(BaselineCorrectionMode mode)
    {
        var engine = new DashboardEngine();
        var (profile, limits) = Settings(mode);
        engine.Configure(profile, limits);
        engine.Begin(new("live-device-axis"));
        engine.SetSensorCalibration(new("test-controller", Hardware, Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, axis => Hardware.Range((PedalChannel)axis))));
        return engine;
    }
    private static (PedalProfile, Dictionary<PedalAxis, AxisSessionSettings>) Settings(BaselineCorrectionMode mode, AxisDeadzone? deadzone = null)
    {
        var axes = Enum.GetValues<PedalAxis>().ToDictionary(axis => axis, axis => new AxisProfile(new(0, DashboardEngine.DeviceMaximum(axis)),
            deadzone: deadzone, maximumBaselineCorrection: DashboardEngine.DeviceMaximum(axis) * .25,
            correctionMode: axis == PedalAxis.Brake ? mode : BaselineCorrectionMode.Off,
            correctionButton: axis == PedalAxis.Brake ? new("keyboard", 65) : null));
        return (new("Drift", axes), axes.Keys.ToDictionary(axis => axis, axis => new AxisSessionSettings(DashboardEngine.DeviceMaximum(axis) * .1, .05)));
    }
    private static AxisSessionResult Brake(DashboardEngine engine) => engine.Snapshot(PedalAxis.Brake).Axes[1].Result!.Value;
    private static void Sample(DashboardEngine engine, ref double time, int brake)
    {
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1, new(1000, brake, 1000)));
        time += .01;
    }
    private static void Hold(DashboardEngine engine, ref double time, int brake, int samples)
    { for (var sample = 0; sample < samples; sample++) { Sample(engine, ref time, brake); } }
    private static void Cycle(DashboardEngine engine, ref double time, int drift)
    { Hold(engine, ref time, Hardware.Brake.Maximum + drift, 10); Hold(engine, ref time, Hardware.Brake.Minimum + drift, 115); }
}
