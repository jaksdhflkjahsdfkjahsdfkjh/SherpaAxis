using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Input;
using SherpaAxis.App.ViewModels;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;
using Forms = System.Windows.Forms;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class UiRefinementTests
{
    [Fact]
    public Task FooterAndWindowTitleShowTheReleaseVersionAndBugReportLink() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new DashboardEngine(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var label = Assert.IsType<TextBlock>(main.FindName("AppVersionLabel"));
        var footer = Assert.IsType<Border>(main.FindName("FooterBar"));
        var link = Assert.IsType<System.Windows.Documents.Hyperlink>(main.FindName("BugReportLink"));
        Assert.Equal("v1.4.0", label.Text);
        Assert.Equal("https://github.com/jaksdhflkjahsdfkjahsdfkjh/SherpaAxis/issues", link.NavigateUri.AbsoluteUri);
        Assert.Contains(label, Descendants(footer));
        try
        {
            Localizer.Instance.Language = "en";
            Assert.Equal("SherpaAxis — Pedals Utility · v1.4.0", main.Title);
            Assert.Equal("Report a bug", Assert.IsType<System.Windows.Documents.Run>(link.Inlines.FirstInline).Text);
            main.ShowInTaskbar = false; main.Opacity = 0; main.Show(); main.UpdateLayout();
            CapturePreview(footer, "release-footer-en.png");
            Localizer.Instance.Language = "uk";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal("SherpaAxis — утиліта педалей · v1.4.0", main.Title);
            Assert.Equal("Повідомити про помилку", Assert.IsType<System.Windows.Documents.Run>(link.Inlines.FirstInline).Text);
            main.UpdateLayout();
            CapturePreview(footer, "release-footer-uk.png");
        }
        finally { Localizer.Instance.Language = "en"; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AppDriftButtonRequiresAHoldAndCancelsOnReleaseOrCaptureLoss(bool captureLoss) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var engine = new DashboardEngine();
        await using var main = new MainWindow(engine, desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        PrepareDrift(engine, model);
        using var buttons = new ButtonStateBuffer();
        var fix = Assert.IsType<Button>(main.FindName("FixDriftButton"));
        double time = 0;
        void Samples(int count)
        {
            for (var index = 0; index < count; index++)
            {
                engine.ProcessCorrectionButtons(buttons);
                engine.Accept(new(PedalConnectionState.Connected, TimeSpan.FromSeconds(time), 1, new(1000, 2900500, 1000)));
                time += .01;
            }
        }
        Samples(2);
        fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Samples(50);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        if (captureLoss) { fix.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent }); }
        else { fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent }); }
        Samples(400);
        Assert.Equal(0, engine.DriftState(PedalAxis.Brake).Offset);
        fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Samples(400);
        Assert.Equal(-300000, engine.DriftState(PedalAxis.Brake).Offset, 5);
        fix.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Assert.True(engine.DriftState(PedalAxis.Brake).CanSave);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public Task ModeTabsFillThePanelAndDisabledButtonsDoNotFadeDuringSwitching(string language) => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        await using var main = new MainWindow(new(), desktopServices: new SettingsAndTrayTests.FakeDesktop().Services);
        var model = Assert.IsType<DashboardViewModel>(main.DataContext);
        try
        {
            Localizer.Instance.Language = language;
            var root = Assert.IsAssignableFrom<FrameworkElement>(main.Content);
            root.Measure(new Size(1100, 760)); root.Arrange(new Rect(0, 0, 1100, 760)); root.UpdateLayout();
            var tabs = Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(main.FindName("PanelModeTabs"));
            var items = tabs.Children.OfType<Button>().ToArray();
            Assert.Equal(3, items.Length);
            Assert.All(items, item => Assert.Equal(items[0].ActualWidth, item.ActualWidth, 1));
            Assert.InRange(items.Sum(item => item.ActualWidth + item.Margin.Left + item.Margin.Right), tabs.ActualWidth - 2, tabs.ActualWidth + 2);
            CapturePreview(tabs, $"mode-tabs-{language}.png");
            var profile = Assert.IsType<Border>(main.FindName("ProfileBar"));
            var save = Descendants(profile).OfType<Button>().First();
            model.IsSwitchingMode = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(save.IsEnabled);
            Assert.Equal(1, Assert.IsType<Border>(save.Template.FindName("Surface", save)).Opacity);
            model.IsSwitchingMode = false;
        }
        finally { Localizer.Instance.Language = "en"; }
    });

    [Fact]
    public Task TransitionKeepsTheGraphSnapshotUntilTheNewSourceIsCommitted() => HardwarePedalUiTests.OnUiAsync(() =>
    {
        var engine = new DashboardEngine();
        var model = new DashboardViewModel(engine);
        engine.Begin();
        engine.Accept(new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(100, 500, 200)));
        model.Refresh();
        var before = model.DisplayHistories;
        model.IsSwitchingMode = true;
        engine.Begin();
        model.Refresh(force: true);
        Assert.Same(before, model.DisplayHistories);
        model.CommitDisplayTransition();
        model.IsSwitchingMode = false;
        Assert.NotSame(before, model.DisplayHistories);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TrayUsesAppColorsAndOnlyChecksTheSelectedProfile() => HardwarePedalUiTests.OnUiAsync(() =>
    {
        var profile = new ProfileTab { Name = "Racing" };
        using var tray = new ApplicationTray(() => new([profile], profile, SidePanelMode.Tuning, true, true));
        tray.RebuildMenu();
        var menu = Assert.IsType<Forms.ContextMenuStrip>(typeof(ApplicationTray).GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray));
        Assert.False(menu.ShowCheckMargin);
        Assert.All(menu.Items.OfType<Forms.ToolStripMenuItem>(), item => Assert.False(item.Checked));
        Assert.DoesNotContain(menu.Items.OfType<Forms.ToolStripMenuItem>(), item => item.Text?.StartsWith("Fix drift", StringComparison.Ordinal) == true);
        var profiles = menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "Switch profile");
        var dropDown = Assert.IsType<Forms.ToolStripDropDownMenu>(profiles.DropDown);
        Assert.True(dropDown.ShowCheckMargin);
        Assert.False(dropDown.ShowImageMargin);
        var renderer = Assert.IsAssignableFrom<Forms.ToolStripProfessionalRenderer>(dropDown.Renderer);
        Assert.Equal(menu.BackColor, renderer.ColorTable.ImageMarginGradientBegin);
        var selected = Assert.Single(profiles.DropDownItems.OfType<Forms.ToolStripMenuItem>());
        using var bitmap = new System.Drawing.Bitmap(24, 24);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(menu.BackColor);
            renderer.DrawItemCheck(new Forms.ToolStripItemImageRenderEventArgs(graphics, selected, null, new System.Drawing.Rectangle(3, 3, 16, 16)));
        }
        Assert.Equal(System.Drawing.Color.FromArgb(13, 23, 40).ToArgb(), bitmap.GetPixel(7, 7).ToArgb());
        Assert.NotEqual(menu.BackColor.ToArgb(), bitmap.GetPixel(10, 14).ToArgb());
        menu.PerformLayout();
        profiles.Select();
        Assert.True(profiles.Selected);
        using var highlight = new System.Drawing.Bitmap(menu.Width, menu.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(highlight))
        {
            renderer.DrawMenuItemBackground(new Forms.ToolStripItemRenderEventArgs(graphics, profiles));
            Assert.Equal(System.Drawing.Color.FromArgb(41, 66, 85).ToArgb(), highlight.GetPixel(4, 4).ToArgb());
            var text = new Forms.ToolStripItemTextRenderEventArgs(graphics, profiles, profiles.Text!,
                new System.Drawing.Rectangle(4, 2, profiles.Width - 8, profiles.Height - 4), System.Drawing.Color.Black, profiles.Font, Forms.TextFormatFlags.Left);
            renderer.DrawItemText(text);
            Assert.Equal(menu.ForeColor, text.TextColor);
            profiles.Enabled = false;
            renderer.DrawItemText(text);
            Assert.Equal(System.Drawing.Color.FromArgb(149, 169, 193), text.TextColor);
            profiles.Enabled = true;
        }
        if (Environment.GetEnvironmentVariable("SHERPAAXIS_UI_CAPTURE_DIR") is { Length: > 0 } folder)
        {
            System.IO.Directory.CreateDirectory(folder);
            using var mainPreview = new System.Drawing.Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(mainPreview, new System.Drawing.Rectangle(System.Drawing.Point.Empty, mainPreview.Size));
            mainPreview.Save(System.IO.Path.Combine(folder, "tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
            dropDown.PerformLayout();
            selected.Select();
            using var preview = new System.Drawing.Bitmap(dropDown.Width, dropDown.Height);
            dropDown.DrawToBitmap(preview, new System.Drawing.Rectangle(System.Drawing.Point.Empty, preview.Size));
            preview.Save(System.IO.Path.Combine(folder, "tray-profiles.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        return Task.CompletedTask;
    });

    private static void PrepareDrift(DashboardEngine engine, DashboardViewModel model)
    {
        model.SetCorrectionMode("By button");
        model.BindCorrection(new("keyboard", 65));
        var bounds = new PedalCalibration(new(1000, 15000), new(3200500, 4700500), new(1000, 15000));
        engine.Begin();
        engine.SetSensorCalibration(new("test-controller", bounds,
            new Dictionary<PedalAxis, HardwareCalibrationRange> { [PedalAxis.Brake] = bounds.Brake }));
    }
    internal static void CapturePreview(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("SHERPAAXIS_UI_CAPTURE_DIR") is not { Length: > 0 } folder) { return; }
        System.IO.Directory.CreateDirectory(folder);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 18, 32)), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name));
        encoder.Save(file);
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
