using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SherpaAxis.App.ViewModels;

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
            var hint = AutomationProperties.GetHelpText(Find<ComboBox>(controls, "Filter milliseconds"));
            Assert.Contains("milliseconds", hint, StringComparison.Ordinal);
            Assert.Empty(errors.Messages);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); errors.Dispose(); }
    });

    private static T Find<T>(IEnumerable<Control> controls, string name) where T : Control =>
        controls.OfType<T>().Single(c => AutomationProperties.GetName(c) == name);

    private static async Task OnSta(Action<MainWindow> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
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
