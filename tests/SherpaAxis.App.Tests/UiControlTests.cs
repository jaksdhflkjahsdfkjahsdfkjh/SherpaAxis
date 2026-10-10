using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class UiControlTests
{
    [Fact]
    public Task HintsTemplatesFilterControlsAndTypingSurviveRefresh() => OnSta(window =>
    {
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        try
        {
            var content = (FrameworkElement)window.Content;
            for (var pass = 0; pass < 3; pass++)
            {
                content.Measure(new Size(1288, 862));
                content.Arrange(new Rect(0, 0, 1288, 862));
                content.UpdateLayout();
                foreach (var expander in Descendants(content).OfType<Expander>()) { expander.IsExpanded = true; }
            }
            var controls = Descendants(content).OfType<Control>().ToArray();
            foreach (var parameter in controls.Where(c => c is TextBox or ComboBox or CheckBox && AutomationProperties.GetName(c).Length > 0))
            {
                Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(parameter.ToolTip)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(parameter)));
                Assert.True(parameter.Focusable);
            }
            Assert.All(controls.OfType<Button>(), button => { Assert.True(button.Focusable); Assert.NotNull(button.FocusVisualStyle); });
            Assert.All(controls.OfType<ComboBox>(), combo => Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo)));
            var graphChoice = Find<ComboBox>(controls, "Graph pedals");
            var graphPopup = Assert.IsType<Popup>(graphChoice.Template.FindName("PART_Popup", graphChoice));
            var popupContent = Assert.IsAssignableFrom<FrameworkElement>(graphPopup.Child);
            popupContent.Measure(new Size(360, 1000));
            popupContent.Arrange(new Rect(popupContent.DesiredSize));
            popupContent.UpdateLayout();
            Assert.InRange(popupContent.DesiredSize.Height, 1, 200);
            var popupScroll = Assert.Single(Descendants(popupContent).OfType<ScrollViewer>());
            Assert.False(popupScroll.CanContentScroll);
            Assert.IsType<SherpaAxis.App.Controls.StableComboBox>(graphChoice);
            var model = Assert.IsType<DashboardViewModel>(window.DataContext);
            var preset = Find<ComboBox>(controls, "Filter preset");
            preset.SetCurrentValue(Selector.SelectedItemProperty, "Smooth");
            Assert.Equal("Smooth", model.FilterPreset);
            var kind = Find<ComboBox>(controls, "Filter type");
            Assert.Equal("TwoStageExponential", kind.SelectedValue);
            Assert.Equal("Two-stage exponential", kind.SelectedItem.ToString());
            preset.SetCurrentValue(Selector.SelectedItemProperty, "Off");
            Assert.False(model.Editor.FilterEnabled);
            Assert.Equal("Off", kind.SelectedValue);
            kind.SetCurrentValue(Selector.SelectedValueProperty, "Exponential");
            Assert.True(model.Editor.FilterEnabled);
            Assert.Equal("Balanced", model.FilterPreset);
            kind.SetCurrentValue(Selector.SelectedValueProperty, "MedianExponential");
            Assert.True(model.Editor.FilterEnabled);
            Assert.True(model.UsesTimeFilter && model.UsesWindowFilter);
            var sampleWindow = Find<ComboBox>(controls, "Filter window samples");
            sampleWindow.SetCurrentValue(ComboBox.TextProperty, "7");
            sampleWindow.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
            Assert.Equal("7", model.Editor.FilterWindowSamples);
            Assert.Equal("Custom", model.FilterPreset);
            sampleWindow.SetCurrentValue(ComboBox.TextProperty, "4");
            sampleWindow.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
            Assert.Throws<ArgumentException>(model.Apply);
            sampleWindow.SetCurrentValue(ComboBox.TextProperty, "7");
            sampleWindow.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
            model.Apply();
            var name = Find<TextBox>(controls, "Profile name");
            name.SetCurrentValue(TextBox.TextProperty, "Still typing");
            name.CaretIndex = 5;
            name.Select(2, 4);
            for (var i = 0; i < 100; i++) { model.Refresh(); }
            Assert.Equal("Still typing", name.Text);
            Assert.Equal(2, name.SelectionStart);
            Assert.Equal(4, name.SelectionLength);
            var first = model.SelectedProfile;
            var profileSelector = Find<ComboBox>(controls, "Selected profile");
            model.Load(StarterProfiles.Create()[1]);
            Assert.Same(model.SelectedProfile, profileSelector.SelectedItem);
            profileSelector.SetCurrentValue(Selector.SelectedItemProperty, first);
            Assert.Same(first, model.SelectedProfile);
            Assert.Equal("Still typing", model.ProfileName);
            Assert.Equal("MedianExponential", model.Editor.FilterKind);
            Assert.Equal("7", model.Editor.FilterWindowSamples);
            Assert.True(model.CanEdit);
            foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
            {
                var bar = new ScrollBar
                {
                    Orientation = orientation,
                    Minimum = 0,
                    Maximum = 100,
                    ViewportSize = 20,
                    Value = 40,
                    Style = (Style)window.FindResource(typeof(ScrollBar))
                };
                var size = new Size(orientation == Orientation.Vertical ? 12 : 240, orientation == Orientation.Vertical ? 240 : 12);
                bar.Measure(size);
                bar.Arrange(new Rect(size));
                bar.UpdateLayout();
                var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
                Assert.Equal(orientation, track.Orientation);
                track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(orientation == Orientation.Horizontal ? 20 : 0, orientation == Orientation.Vertical ? 20 : 0));
                Assert.InRange(bar.Value, 40.1, 100);
                var afterDrag = bar.Value;
                (orientation == Orientation.Vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand).Execute(null, bar);
                Assert.True(bar.Value > afterDrag);
            }
            var hint = AutomationProperties.GetHelpText(Find<ComboBox>(controls, "Filter milliseconds"));
            Assert.Contains("milliseconds", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("Power", DashboardViewModel.CurveKinds);
            Assert.DoesNotContain("Clutch bite", DashboardViewModel.CurveKinds);
            Assert.DoesNotContain(controls.OfType<Expander>(), expander => Equals(expander.Header, "Curve details"));
            Assert.DoesNotContain(controls, c => AutomationProperties.GetName(c) == "Power exponent");
            var calibration = Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Calibrate"));
            calibration.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.IsCalibrating);
            Assert.Contains("fresh readings", model.CalibrationInfo, StringComparison.Ordinal);
            model.CancelCalibration();
            var pause = Assert.IsType<Button>(window.FindName("GraphPauseButton"));
            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(model.GraphPaused);
            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.GraphPaused);
            var help = new FilteringHelpWindow(window.Resources);
            var helpContent = (FrameworkElement)help.Content;
            helpContent.Measure(new Size(680, 680));
            helpContent.Arrange(new Rect(0, 0, 680, 680));
            helpContent.UpdateLayout();
            Assert.Equal(DashboardViewModel.FilterKinds.Select(f => f.Name), FilteringGuide.Algorithms.Select(f => f.Name));
            var imported = DashboardEngine.DefaultProfile().Axes.ToDictionary(pair => pair.Key, pair => pair.Value);
            imported[PedalAxis.Brake] = new(new(0, DashboardEngine.DeviceMaximum(PedalAxis.Brake)), curve: ProfileCurve.Power(2.7));
            model.Load(new("Imported custom curve", imported));
            Assert.Equal("Custom", model.PedalCurves[1].CurveKind);
            Assert.Equal(2.7, model.ValidateEditors().Profile.Axes[PedalAxis.Brake].Curve.Exponent);
            Assert.Empty(errors.Messages);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); errors.Dispose(); }
    });

    [Fact]
    public Task CalibrationButtonCapturesBetweenRefreshesAndDoneActivatesTheRange()
    {
        var engine = new DashboardEngine();
        return OnSta(engine, window =>
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1340, 900));
            content.Arrange(new Rect(0, 0, 1340, 900));
            content.UpdateLayout();
            var model = Assert.IsType<DashboardViewModel>(window.DataContext);
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, 300, 0)));
            model.Refresh();
            model.GraphPaused = true;
            var button = Assert.IsType<Button>(window.FindName("CalibrationButton"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(model.IsCalibrating);
            Assert.Equal("Done", button.Content);
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.002), 1, new(0, 100, 0)));
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.004), 1, new(0, 500, 0)));
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.006), 1, new(0, 300, 0)));
            model.Refresh();
            Assert.Equal("0% = 100\n100% = 500\nInput: 300", model.CalibrationRangeInfo);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Calibrate", button.Content);
            Assert.False(model.IsCalibrating);
            Assert.Equal("0% = 100\n100% = 500", model.CalibrationRangeInfo);
            engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(.008), 1, new(0, 300, 0)));
            model.Refresh();
            Assert.Equal(50, model.Cards[1].OutputPercent);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var cancel = Descendants((FrameworkElement)window.Content).OfType<Button>().Single(b => Equals(b.Content, "Cancel calibration"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.IsCalibrating);
        });
    }

    private static T Find<T>(IEnumerable<Control> controls, string name) where T : Control =>
        controls.OfType<T>().Single(c => AutomationProperties.GetName(c) == name);

    private static Task OnSta(Action<MainWindow> check) => OnSta(new DashboardEngine(), check);

    private static async Task OnSta(DashboardEngine engine, Action<MainWindow> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(engine);
                try { check(window); }
                finally { window.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                done.SetResult();
            }
            catch (Exception exception) { done.SetException(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var item in Descendants(child)) { yield return item; }
        }
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (message?.Contains("Error:", StringComparison.Ordinal) == true) { Messages.Add(message); } }
        public override void WriteLine(string? message) => Write(message);
    }
}
