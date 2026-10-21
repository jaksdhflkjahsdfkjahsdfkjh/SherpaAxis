using System.IO;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Core;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class WorkbenchEditingTests
{
    [Theory]
    [InlineData("Linear")]
    [InlineData("Progressive")]
    [InlineData("Aggressive")]
    [InlineData("S curve")]
    [InlineData("Late bite")]
    [InlineData("Early bite")]
    public void EveryPresetIsMonotonicBoundedAndHasExactEndpoints(string name)
    {
        var processor = new AxisProfile(new(0, 1), curve: CurvePresets.Create(name)).CreateProcessor();
        var previous = 0d;
        Assert.Equal(0, processor.Process(0).Output);
        for (var i = 0; i <= 1000; i++)
        {
            var current = processor.Process(i / 1000d).Output;
            Assert.InRange(current, previous, 1);
            previous = current;
        }
        Assert.Equal(1, processor.Process(1).Output);
    }

    [Fact]
    public void DragCannotCrossNeighboursOrMoveEndpointsAndInsertRemoveRemainValid()
    {
        CurvePoint[] points = [new(0, 0), new(.3, .2), new(.6, .7), new(1, 1)];
        var moved = CurveEditing.Move(points, 1, .9, .95);
        Assert.InRange(moved[1].Input, .001, .5999);
        Assert.Equal(.7, moved[1].Output);
        _ = ProfileCurve.PiecewiseLinear(moved);
        Assert.Equal(points, CurveEditing.Move(points, 0, .5, .5));
        Assert.Equal(points, CurveEditing.Remove(points, points.Length - 1));
        var inserted = CurveEditing.Insert(points, .45, .99);
        Assert.Equal(new CurvePoint(.45, .7), inserted[2]);
        Assert.Equal(points, CurveEditing.Remove(inserted, 2));
        Assert.Equal(points, CurveEditing.Insert(points, .30001, .5));
        CurvePoint[] close = [new(0, 0), new(Math.BitDecrement(.5), .2), new(.5, .3), new(Math.BitIncrement(.5), .4), new(1, 1)];
        _ = ProfileCurve.PiecewiseLinear(CurveEditing.Move(close, 2, .9, .9));
    }

    [Fact]
    public void SwitchingTabsPreservesInvalidDraftsAndOtherAxisEdits()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        var first = model.SelectedProfile!;
        model.ProfileName = "First";
        model.Editor.Minimum = "invalid";
        model.SelectedAxis = PedalAxis.Throttle;
        model.Editor.FilterMilliseconds = "10";
        model.Load(DashboardEngine.DefaultProfile());
        var second = model.SelectedProfile!;
        model.Editor.Maximum = "12000";
        model.SwitchProfile(first);
        Assert.Equal("First", model.ProfileName);
        Assert.Equal("10", model.Editor.FilterMilliseconds);
        model.SelectedAxis = PedalAxis.Brake;
        Assert.Equal("invalid", model.Editor.Minimum);
        Assert.Throws<ArgumentException>(model.Apply);
        model.SwitchProfile(second);
        model.SelectedAxis = PedalAxis.Throttle;
        Assert.Equal("12000", model.Editor.Maximum);
    }

    [Fact]
    public void RangeCalibrationMapsMinimumToZeroAndRejectsNoMovementAndReconnects()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 500, 0)));
        model.AdvanceCalibration();
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        Assert.True(model.IsCalibrating);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.004), 1, new(0, 500, 0)));
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.01), 1, new(0, 100, 0)));
        model.AdvanceCalibration();
        Assert.Equal("100", model.Editor.Minimum);
        Assert.Equal("500", model.Editor.Maximum);
        Assert.False(model.Editor.Inverted);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.02), 1, new(0, 100, 0)));
        Assert.Equal(0, engine.Snapshot(PedalAxis.Brake).Axes.Single(a => a.Axis == PedalAxis.Brake).Result!.Value.Processing.Output);
        model.AdvanceCalibration();
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.03), 2, new(0, 200, 0)));
        Assert.Throws<InvalidOperationException>(model.AdvanceCalibration);
        Assert.False(model.IsCalibrating);
    }

    [Fact]
    public async Task LocalLibraryPersistsFilterSessionAndSelectionAndSkipsMalformedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(directory);
        try
        {
            Assert.Empty(await library.LoadAsync());
            var model = new DashboardViewModel(new DashboardEngine());
            model.Editor.FilterKind = "Median";
            model.Editor.FilterMilliseconds = "10";
            model.Editor.FilterWindowSamples = "7";
            model.Editor.CorrectionRate = "120";
            model.Editor.CurveKind = "Early bite";
            var (profile, _) = model.ValidateEditors();
            var path = await library.SaveAsync(profile);
            var loaded = Assert.Single(await library.LoadAsync());
            Assert.Equal(path, loaded.Path);
            var restored = AxisEditor.FromProfile(loaded.Profile.Axes[PedalAxis.Brake]);
            Assert.Equal("Median", restored.FilterKind);
            Assert.Equal("7", restored.FilterWindowSamples);
            Assert.Equal("120", restored.CorrectionRate);
            Assert.Equal(path, await library.SaveAsync(profile, path));
            Assert.Equal(path, await library.SelectedPathAsync());
            Assert.Single(await library.LoadAsync());
            await File.WriteAllTextAsync(Path.Combine(directory, "broken.json"), "{bad json");
            Assert.Single(await library.LoadAsync());
            Assert.Single(library.LoadErrors);
            await File.WriteAllTextAsync(Path.Combine(directory, "active-profile"), "../outside.json");
            Assert.Null(await library.SelectedPathAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory)) { File.Delete(file); }
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public void CurveDragUpdatesActiveOutputImmediately()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.EditCurve([new(0, 0), new(.5, .8), new(1, 1)]);
        Assert.Equal("Custom", model.Editor.CurveKind);
        Assert.Equal(.8, model.DraftPoints[1].Output);
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 8388608, 0)));
        Assert.Equal(.8, engine.Snapshot(PedalAxis.Brake).Axes.Single(a => a.Axis == PedalAxis.Brake).Result!.Value.Processing.Output);
        model.Apply();
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.01), 1, new(0, 8388608, 0)));
        Assert.Equal(.8, engine.Snapshot(PedalAxis.Brake).Axes.Single(a => a.Axis == PedalAxis.Brake).Result!.Value.Processing.Output);
    }
}
