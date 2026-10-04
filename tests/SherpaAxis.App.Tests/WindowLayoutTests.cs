using System.Windows;
using System.Windows.Threading;

namespace SherpaAxis.App.Tests;

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
                var window = new MainWindow();
                try
                {
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, height));
                    content.UpdateLayout();
                    Assert.NotNull(window.DataContext);
                    Assert.True(content.ActualWidth > 0);
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
}
