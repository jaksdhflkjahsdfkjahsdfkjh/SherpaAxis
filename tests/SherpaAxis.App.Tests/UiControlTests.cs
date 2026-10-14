using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class UiControlTests
{
    private static readonly string[] InvalidGraphWindows = ["", "0", "-1", "NaN", "61", "abc"];
    [Fact]
    public Task NewProfileButtonCreatesAndSelectsDefaultsWithoutDiscardingThePreviousDraft() => OnSta(window =>
    {
        var model = Assert.IsType<DashboardViewModel>(window.DataContext);
        var previous = model.SelectedProfile!;
        model.Editor.Minimum = "unfinished draft";
        var count = model.ProfileTabs.Count;
        Assert.IsType<Button>(window.FindName("NewProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(count + 1, model.ProfileTabs.Count);
        var created = model.SelectedProfile!;
        Assert.NotSame(previous, created);
        Assert.Equal("new profile", created.Name);
        Assert.Equal("0", model.Editor.Minimum);
        Assert.Equal("Off", model.FilterPreset);
        Assert.Null(created.FilePath);
        model.SwitchProfile(previous);
        Assert.Equal("unfinished draft", model.Editor.Minimum);
        model.SwitchProfile(created);
        Assert.Equal("new profile", model.ProfileName);
        Assert.Equal("0", model.Editor.Minimum);
    });

    [Fact]
    public Task GraphWindowAcceptsKeyboardInputAndKeepsTheLastValidValue() => OnSta(window =>
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1340, 900)); content.Arrange(new Rect(0, 0, 1340, 900)); content.UpdateLayout();
        var model = Assert.IsType<DashboardViewModel>(window.DataContext);
        var input = Assert.IsType<TextBox>(window.FindName("GraphWindowInput"));
        Assert.Equal("10", input.Text);
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Graph keyboard test")
        { Width = 1, Height = 1, WindowStyle = 0 });
        void Enter(string value)
        {
            input.SetCurrentValue(TextBox.TextProperty, value);
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        }
        Enter("2.5"); Assert.Equal(2.5, model.GraphWindow);
        foreach (var value in InvalidGraphWindows)
        {
            Enter(value); Assert.Equal(2.5, model.GraphWindow); Assert.True(Validation.GetHasError(input));
        }
        Enter("20"); Assert.Equal(20, model.GraphWindow); Assert.False(Validation.GetHasError(input));
        var pause = Assert.IsType<Button>(window.FindName("GraphPauseButton"));
        var reset = Assert.IsType<Button>(window.FindName("GraphResetButton"));
        var pausePosition = pause.TransformToAncestor(content).Transform(new Point());
        var inputPosition = input.TransformToAncestor(content).Transform(new Point());
        Assert.Equal(inputPosition.Y, pausePosition.Y, 1);
        Assert.True(pausePosition.X > inputPosition.X);
        Assert.True(reset.TransformToAncestor(content).Transform(new Point()).X > pausePosition.X);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FocusedClosedDropdownIgnoresWheelAndLetsThePanelReceiveIt(bool editable) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var combo = new SherpaAxis.App.Controls.StableComboBox
        { ItemsSource = Enumerable.Range(0, 30).Select(index => $"Choice {index}").ToArray(), SelectedIndex = 10, IsEditable = editable, MaxDropDownHeight = 100 };
        var parent = new StackPanel(); parent.Children.Add(combo);
        var host = new Window { Content = parent, Width = 300, Height = 200, Opacity = 0, ShowInTaskbar = false };
        try
        {
            host.Show(); combo.Focus();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(combo.IsKeyboardFocusWithin);
            var panelReceivedWheel = 0;
            parent.MouseWheel += (_, _) => panelReceivedWheel++;
            foreach (var delta in new[] { 120, -120 })
            {
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = combo };
                InputManager.Current.ProcessInput(wheel);
                Assert.True(wheel.Handled);
                Assert.Equal(10, combo.SelectedIndex);
            }
            Assert.Equal(2, panelReceivedWheel);
            combo.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
            var scroll = Descendants(popup.Child).OfType<ScrollViewer>().First();
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(scroll.VerticalOffset > 0);
            Assert.Equal(10, combo.SelectedIndex);
        }
        finally { combo.IsDropDownOpen = false; host.Close(); }
    });

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
            Assert.All(controls.OfType<ComboBox>(), combo => { combo.ApplyTemplate(); Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo)); });
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
            var name = Find<TextBox>(controls, "Software minimum");
            name.SetCurrentValue(TextBox.TextProperty, "1.23456");
            name.CaretIndex = 5;
            name.Select(2, 4);
            for (var i = 0; i < 100; i++) { model.Refresh(); }
            Assert.Equal("1.23456", name.Text);
            Assert.Equal(2, name.SelectionStart);
            Assert.Equal(4, name.SelectionLength);
            var first = model.SelectedProfile;
            var profileSelector = Find<ComboBox>(controls, "Selected profile");
            model.Load(StarterProfiles.Create()[1]);
            Assert.Same(model.SelectedProfile, profileSelector.SelectedItem);
            profileSelector.SetCurrentValue(Selector.SelectedItemProperty, first);
            Assert.Same(first, model.SelectedProfile);
            Assert.Equal("1.23456", model.Editor.Minimum);
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
            Assert.Equal("Calibration", Assert.IsType<Button>(window.FindName("CalibrationModeButton")).Content);
            Assert.False(model.IsCalibrationMode);
            Assert.Null(window.FindName("WorkspaceTabs"));
            Assert.DoesNotContain(controls.OfType<GroupBox>(), group => Equals(group.Header, "Calibration"));
            Assert.Null(window.FindName("CalibrationButton"));
            Assert.Null(window.FindName("SoftwareCalibrationButton"));
            Assert.Null(window.FindName("SoftwareInputMapping"));
            Assert.DoesNotContain(controls.OfType<Button>(), button => button.Content is "Calibrate software" or "Cancel calibration" or "Use current" or "Use observed range");
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
    public Task ResettingAdvancedInputMappingPreservesThePedalsOtherProfileSettings()
    {
        var engine = new DashboardEngine();
        return OnSta(engine, window =>
        {
            var mapping = Assert.IsType<Expander>(window.FindName("AdvancedInputMapping"));
            Assert.False(mapping.IsExpanded);
            mapping.IsExpanded = true;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1340, 900));
            content.Arrange(new Rect(0, 0, 1340, 900));
            content.UpdateLayout();
            var model = Assert.IsType<DashboardViewModel>(window.DataContext);
            model.Editor.Minimum = "100";
            model.Editor.Maximum = "500";
            model.Editor.Inverted = true;
            model.Editor.LowerDeadzone = "5";
            model.SetCurvePreset(PedalAxis.Brake, "Progressive");
            model.SetFilterPreset("Smooth");
            var before = model.ValidateEditors().Profile;
            Descendants(content).OfType<Button>().Single(button => Equals(button.Content, "Reset input mapping"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var after = model.ValidateEditors().Profile;
            var brake = after.Axes[PedalAxis.Brake];
            Assert.Equal(0, brake.Calibration.Minimum);
            Assert.Equal(DashboardEngine.DeviceMaximum(PedalAxis.Brake), brake.Calibration.Maximum);
            Assert.False(brake.Calibration.Inverted);
            Assert.Equal(before.Axes[PedalAxis.Brake].Deadzone.Lower, brake.Deadzone.Lower);
            Assert.Equal(before.Axes[PedalAxis.Brake].Deadzone.Upper, brake.Deadzone.Upper);
            Assert.Equal("Progressive", model.Editor.CurveKind);
            Assert.Equal("Smooth", model.FilterPreset);
            foreach (var axis in new[] { PedalAxis.Throttle, PedalAxis.Clutch })
            {
                Assert.Equal(before.Axes[axis].Calibration.Minimum, after.Axes[axis].Calibration.Minimum);
                Assert.Equal(before.Axes[axis].Calibration.Maximum, after.Axes[axis].Calibration.Maximum);
                Assert.Equal(before.Axes[axis].Calibration.Inverted, after.Axes[axis].Calibration.Inverted);
            }
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
                var window = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
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
