using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class AutomaticTuningTests
{
    [Fact]
    public void SavedStateIgnoresNumericFormattingAndUnusedEditorFields()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        var saved = model.AddSavedProfile(new("Race", DashboardEngine.DefaultProfile().Axes), "race.json");
        model.SwitchProfile(saved);
        Assert.True(model.IsProfileSaved);
        model.Editor.LowerDeadzone = "0.00";
        model.Editor.Minimum = "0e0";
        model.Editor.Exponent = "3";
        model.Editor.FilterWindowSamples = "7";
        model.ClearCurve(PedalAxis.Brake);
        Assert.True(model.IsProfileSaved);
        Assert.False(saved.HasUnsavedChanges);
        Assert.Equal("Race", saved.DisplayName);
        Assert.Equal("Profile saved", model.DraftStatus);
        model.Editor.LowerDeadzone = "5";
        model.RefreshUnsavedState();
        Assert.False(model.IsProfileSaved);
        Assert.Equal("Race *", saved.DisplayName);
        model.Editor.LowerDeadzone = "0";
        model.RefreshUnsavedState();
        Assert.True(model.IsProfileSaved);
    }

    [Fact]
    public void InvalidEditsRetainActiveProcessingAndRepairAppliesWithoutASave()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.Editor.LowerDeadzone = "10";
        model.RefreshUnsavedState();
        Sample(engine, 0);
        Assert.Equal(4d / 9, BrakeOutput(engine), 12);
        model.Editor.LowerDeadzone = "unfinished";
        model.RefreshUnsavedState();
        Assert.NotEmpty(model.ValidationMessage);
        Assert.Contains("Unsaved", model.DraftStatus, StringComparison.Ordinal);
        Sample(engine, .002);
        Assert.Equal(4d / 9, BrakeOutput(engine), 12);
        model.Editor.LowerDeadzone = "20";
        model.RefreshUnsavedState();
        Assert.Empty(model.ValidationMessage);
        Sample(engine, .004);
        Assert.Equal(.375, BrakeOutput(engine), 12);
        Assert.Null(model.SelectedProfile!.FilePath);
    }

    [Fact]
    public void RepeatedAssessmentsAndEquivalentChangesDoNotResetActiveFilters()
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        model.SetFilterPreset("Balanced");
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 0, 0)));
        Sample(engine, .002);
        var before = engine.Snapshot(PedalAxis.Brake).Axes[1];
        Assert.False(before.Result!.Value.Processing.FilterReset);
        model.Editor.FilterMilliseconds = "10.000";
        for (var index = 0; index < 100; index++) { model.RefreshUnsavedState(); }
        Assert.Equal(before.Result, engine.Snapshot(PedalAxis.Brake).Axes[1].Result);
        Sample(engine, .004);
        var after = engine.Snapshot(PedalAxis.Brake).Axes[1];
        Assert.False(after.Result!.Value.Processing.FilterReset);
        Assert.True(after.Result.Value.Processing.Output > before.Result.Value.Processing.Output);
        Assert.True(after.Result.Value.Processing.Output < .5);
    }

    [Fact]
    public Task UiEditsApplyWithoutAnApplyButtonAndSaveTracksOnlyThePersistedProfile() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-auto-tuning-" + Guid.NewGuid().ToString("N"));
        var library = new ProfileLibrary(directory);
        try
        {
            var path = await library.SaveAsync(new("Race", DashboardEngine.DefaultProfile().Axes));
            await library.SetAutoLoadAsync(path);
            var engine = new DashboardEngine();
            await using var window = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services, profileLibrary: library);
            await window.InitializeForLaunchAsync();
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var model = (DashboardViewModel)window.DataContext;
            Assert.DoesNotContain(Descendants(window).OfType<Button>(), button => Equals(button.Content, "Apply settings"));
            var lower = Descendants(window).OfType<TextBox>().Single(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "Editor.LowerDeadzone");
            lower.SetCurrentValue(TextBox.TextProperty, "10");
            lower.CaretIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Sample(engine, 0);
            Assert.Equal(4d / 9, BrakeOutput(engine), 12);
            Assert.Equal(1, lower.CaretIndex);
            Assert.False(model.IsProfileSaved);
            Assert.Equal(0, (await ProfileStore.LoadAsync(path)).Axes[PedalAxis.Brake].Deadzone.Lower);
            await window.SaveSelectedProfileAsync();
            Assert.True(model.IsProfileSaved);
            Assert.Equal("Profile saved", model.DraftStatus);
            Assert.Equal(.1, (await ProfileStore.LoadAsync(path)).Axes[PedalAxis.Brake].Deadzone.Lower);
            lower.SetCurrentValue(TextBox.TextProperty, "10.00");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(model.IsProfileSaved);
            lower.SetCurrentValue(TextBox.TextProperty, "20");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(model.IsProfileSaved);
            var selected = model.SelectedProfile!;
            selected.FilePath = Path.Combine(directory, "blocked.json");
            Directory.CreateDirectory(selected.FilePath);
            var failure = await Record.ExceptionAsync(window.SaveSelectedProfileAsync);
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.False(model.IsProfileSaved);
            Assert.Equal(.1, (await ProfileStore.LoadAsync(path)).Axes[PedalAxis.Brake].Deadzone.Lower);
            selected.FilePath = path;
            lower.SetCurrentValue(TextBox.TextProperty, "10");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(model.IsProfileSaved);
            foreach (var expander in Descendants((DependencyObject)window.FindName("TuningSidePanel")).OfType<Expander>().ToArray())
            { expander.IsExpanded = true; }
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var inverted = Descendants(window).OfType<CheckBox>().Single(box => BindingOperations.GetBinding(box, CheckBox.IsCheckedProperty)?.Path.Path == "Editor.Inverted");
            inverted.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.002), 1, new(0, 4194304, 0)));
            Assert.Equal(13d / 18, BrakeOutput(engine), 12);
            Assert.False(model.IsProfileSaved);
            inverted.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(model.IsProfileSaved);
            model.SetFilterPreset("Balanced");
            var filterTime = Descendants(window).OfType<ComboBox>().Single(box => BindingOperations.GetBinding(box, ComboBox.TextProperty)?.Path.Path == "Editor.FilterMilliseconds");
            filterTime.SetCurrentValue(ComboBox.TextProperty, "15");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(model.HasFilterDraft);
            Assert.Equal("15", model.Editor.FilterMilliseconds);
            Assert.False(model.IsProfileSaved);
            model.SetFilterPreset("Off");
            Assert.True(model.IsProfileSaved);
            window.Close();
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    });

    private static void Sample(DashboardEngine engine, double time) =>
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1, new(8192, 8388608, 8192)));
    private static double BrakeOutput(DashboardEngine engine) => engine.Snapshot(PedalAxis.Brake).Axes[1].Result!.Value.Processing.Output;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
