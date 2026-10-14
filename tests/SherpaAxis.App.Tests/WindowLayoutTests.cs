using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SherpaAxis.App.Controls;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class WindowLayoutTests
{
    [Theory]
    [InlineData(1288, 862)]
    [InlineData(1048, 682)]
    public async Task MainWindowCanInstantiateAndLayOutItsBindings(double width, double height)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(new SherpaAxis.App.Models.DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
                try
                {
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    Assert.True(Assert.IsType<Expander>(window.FindName("CurvesSection")).IsExpanded);
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, height));
                    content.UpdateLayout();
                    Assert.NotNull(window.DataContext);
                    Assert.True(content.ActualWidth > 0);
                    var curves = Descendants(content).OfType<CurveEditor>().ToArray();
                    Assert.Equal(3, curves.Length);
                    Assert.Equal(Enum.GetValues<PedalAxis>(), curves.Select(c => Assert.IsType<PedalCurveViewModel>(c.DataContext).Axis));
                    Assert.All(curves, curve =>
                    {
                        Assert.Equal(2, curve.Points!.Count);
                        Assert.Equal(201, curve.Preview!.Count);
                        Assert.Equal(201, curve.Applied!.Count);
                        Assert.True(curve.ActualWidth >= 100 && curve.ActualHeight >= 115);
                    });
                    var model = Assert.IsType<DashboardViewModel>(window.DataContext);
                    model.SetCurvePreset(PedalAxis.Throttle, "Progressive");
                    var clear = Descendants(content).OfType<Button>().Single(b => Equals(b.Content, "Clear curve")
                        && b.DataContext is PedalCurveViewModel { Axis: PedalAxis.Throttle });
                    clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Linear", model.ValidateEditors().Profile.Axes[PedalAxis.Throttle].Curve.Kind, ignoreCase: true);
                    foreach (var axis in Enum.GetValues<PedalAxis>())
                    {
                        var preset = Descendants(content).OfType<ComboBox>().Single(c => c.DataContext is PedalCurveViewModel panel && panel.Axis == axis);
                        preset.SetCurrentValue(Selector.SelectedItemProperty, "Aggressive");
                        Assert.Equal(axis, model.SelectedAxis);
                        Assert.Equal("Aggressive", model.PedalCurves.Single(p => p.Axis == axis).CurveKind);
                        model.ClearCurve(axis);
                        Assert.Equal("Linear", preset.SelectedItem);
                    }
                }
                finally
                {
                    window.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

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
