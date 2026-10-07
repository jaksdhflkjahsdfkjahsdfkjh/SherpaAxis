using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class PedalCurveTests
{
    public static TheoryData<string> Presets => new(CurvePresets.Names);

    [Theory]
    [MemberData(nameof(Presets))]
    public void AllPresetsAreBoundedMonotonicAndPreserveEndpoints(string name)
    {
        var processor = new AxisProfile(new(0, 1), curve: CurvePresets.Create(name)).CreateProcessor();
        var previous = 0d;
        for (var i = 0; i <= 1000; i++)
        {
            var output = processor.Process(i / 1000d).Output;
            Assert.InRange(output, previous, 1);
            previous = output;
        }
        Assert.Equal(0, processor.Process(0).Output);
        Assert.Equal(1, processor.Process(1).Output);
        var saved = new PedalProfile("preset", new Dictionary<PedalAxis, AxisProfile>
        {
            [PedalAxis.Brake] = new(new(0, 1), curve: CurvePresets.Create(name))
        });
        Assert.Equal(name, AxisEditor.FromProfile(ProfileJson.Deserialize(ProfileJson.Serialize(saved)).Axes[PedalAxis.Brake]).CurveKind);
    }

    [Theory]
    [InlineData(PedalAxis.Throttle)]
    [InlineData(PedalAxis.Brake)]
    [InlineData(PedalAxis.Clutch)]
    public void EditPresetAndClearTargetOnlyTheirPedalAndRequireApply(PedalAxis target)
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.SelectedAxis = target == PedalAxis.Brake ? PedalAxis.Clutch : PedalAxis.Brake;
        foreach (var axis in DashboardViewModel.AxisNames) { model.SetCurvePreset(axis, "Progressive"); }
        model.Apply();
        model.EditCurve(target, [new(0, 0), new(.5, .8), new(1, 1)]);
        Assert.Equal("Custom", model.PedalCurves.Single(p => p.Axis == target).CurveKind);
        Sample(engine, 0);
        Assert.All(engine.Snapshot(target).Axes, a => Assert.Equal(.25, a.Result!.Value.Processing.Output));
        model.Apply();
        Sample(engine, .01);
        foreach (var a in engine.Snapshot(target).Axes)
        {
            Assert.Equal(a.Axis == target ? .8 : .25, a.Result!.Value.Processing.Output);
        }
        model.SelectedAxis = target;
        model.Editor.Minimum = "10";
        model.Editor.FilterMilliseconds = "10";
        model.Editor.LowerDeadzone = "2";
        model.ClearCurve(target);
        Assert.Equal("Linear", model.Editor.CurveKind);
        Assert.Equal("0:0; 1:1", model.Editor.Points);
        Assert.Equal("10", model.Editor.Minimum);
        Assert.Equal("10", model.Editor.FilterMilliseconds);
        Assert.Equal("2", model.Editor.LowerDeadzone);
        var (draft, _) = model.ValidateEditors();
        Assert.All(draft.Axes.Where(p => p.Key != target), p => Assert.Equal(2, p.Value.Curve.Exponent));
        Assert.Equal(.8, engine.Snapshot(target).Axes.Single(a => a.Axis == target).Result!.Value.Processing.Output);
        var panel = model.PedalCurves.Single(p => p.Axis == target);
        Assert.Equal(.5, panel.Preview[100].Output);
        Assert.Equal(2, panel.Points.Count);
        Assert.Equal(.8, panel.Applied[100].Output);
    }

    [Fact]
    public void InvalidCurveDraftStaysLocalAndClearRepairsIt()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.SetCurvePreset(PedalAxis.Throttle, "Progressive");
        model.Apply();
        model.Editor.CurveKind = "Custom";
        model.Editor.Points = "invalid";
        model.RefreshDraftCurve();
        Assert.Empty(model.PedalCurves.Single(p => p.Axis == PedalAxis.Brake).Points);
        Assert.All(model.PedalCurves.Where(p => p.Axis != PedalAxis.Brake), p => Assert.NotEmpty(p.Points));
        Assert.Throws<ArgumentException>(model.Apply);
        Sample(engine, 0);
        Assert.Equal(.25, engine.Snapshot(PedalAxis.Throttle).Axes[0].Result!.Value.Processing.Output);
        model.ClearCurve(PedalAxis.Brake);
        model.Apply();
        Assert.Equal("Progressive", model.PedalCurves.Single(p => p.Axis == PedalAxis.Throttle).CurveKind);
        Assert.Equal("Linear", model.PedalCurves.Single(p => p.Axis == PedalAxis.Brake).CurveKind);
    }

    [Fact]
    public void ProfileSwitchRestoresThreeDistinctCurvesAndClearDoesNotMutateAnotherTab()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.SetCurvePreset(PedalAxis.Throttle, "Progressive");
        model.SetCurvePreset(PedalAxis.Brake, "Aggressive");
        model.EditCurve(PedalAxis.Clutch, [new(0, 0), new(.5, .7), new(1, 1)]);
        model.Apply();
        var first = model.SelectedProfile!;
        var saved = ProfileJson.Deserialize(ProfileJson.Serialize(model.ValidateEditors().Profile));
        model.Load(saved);
        var second = model.SelectedProfile!;
        model.ClearCurve(PedalAxis.Brake);
        model.Apply();
        model.SwitchProfile(first);
        Sample(engine, 0);
        var output = engine.Snapshot(PedalAxis.Brake).Axes;
        Assert.Equal(.25, output[0].Result!.Value.Processing.Output);
        Assert.Equal(Math.Sqrt(.5), output[1].Result!.Value.Processing.Output, 12);
        Assert.Equal(.7, output[2].Result!.Value.Processing.Output);
        Assert.All(model.PedalCurves, p => Assert.Equal(p.Preview[100].Output, p.Applied[100].Output, 12));
        model.SwitchProfile(second);
        Sample(engine, .01);
        Assert.Equal(.5, engine.Snapshot(PedalAxis.Brake).Axes[1].Result!.Value.Processing.Output);
    }

    private static void Sample(DashboardEngine engine, double time) =>
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1, new(8192, 8388608, 8192)));
}
