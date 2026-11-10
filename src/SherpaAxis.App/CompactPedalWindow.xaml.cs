using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

internal sealed partial class CompactPedalWindow : Window
{
    private const long FrameInterval = TimeSpan.TicksPerSecond / 144;
    private readonly DashboardEngine engine;
    private readonly Func<string?> pauseReason;
    private readonly CompactPedalViewModel model;
    private HwndSource? nativeSource;
    private bool rendering, closed;
    private long lastFrame = -1, nextFrame, nextGraphFrame;

    internal CompactPedalWindow(DashboardEngine engine, Func<string?> pauseReason, PedalOverlaySettingsViewModel settings)
    {
        this.engine = engine; this.pauseReason = pauseReason; model = new(settings);
        InitializeComponent(); DataContext = model;
        settings.PropertyChanged += OverlaySettingsChanged;
        SourceInitialized += (_, _) =>
        {
            WindowTheme.Apply(this);
            nativeSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            nativeSource?.AddHook(PreventMouseActivation);
        };
        IsVisibleChanged += (_, _) => UpdateRendering();
        StateChanged += (_, _) => UpdateRendering();
        Closed += (_, _) =>
        {
            closed = true; UpdateRendering(); engine.SetTelemetryCapture(false);
            settings.PropertyChanged -= OverlaySettingsChanged;
            nativeSource?.RemoveHook(PreventMouseActivation);
        };
        ApplySize();
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - Width - 20); Top = Math.Max(area.Top, area.Bottom - Height - 20);
        engine.SetTelemetryCapture(true);
        RefreshNow(); RefreshGraph();
    }

    internal static nint PreventMouseActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) { return 0; }
        handled = true;
        return 3; // MA_NOACTIVATE: moving the display must not take keyboard focus from the game.
    }

    private void MoveOverlay(object sender, MouseButtonEventArgs args)
    {
        if (args.LeftButton == MouseButtonState.Pressed) { DragMove(); args.Handled = true; }
    }

    private void OverlaySettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (closed) { return; }
        if (!updatingSize && args.PropertyName is (nameof(PedalOverlaySettingsViewModel.Width) or nameof(PedalOverlaySettingsViewModel.Height))) { ApplySize(); }
        if (args.PropertyName == nameof(PedalOverlaySettingsViewModel.GraphPaused) && model.Settings.GraphPaused)
        { model.GraphFrame = engine.TelemetrySnapshot(60, maximumPoints: 24008); }
        else if (args.PropertyName is nameof(PedalOverlaySettingsViewModel.GraphPaused) or nameof(PedalOverlaySettingsViewModel.WindowSeconds)) { RefreshGraph(); }
    }

    private void ApplySize()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(model.Settings.Width, area.Width); Height = Math.Min(model.Settings.Height, area.Height);
        if (double.IsFinite(Left)) { Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)); }
        if (double.IsFinite(Top)) { Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)); }
    }

    private void RefreshGraph()
    {
        if (closed) { return; }
        var frame = engine.TelemetrySnapshot(model.Settings.WindowSeconds, model.Settings.GraphPaused ? model.GraphFrame?.Generation : null);
        if (frame is not null) { model.GraphFrame = frame; }
    }

    private void UpdateRendering()
    {
        var enabled = !closed && IsVisible && WindowState != WindowState.Minimized;
        if (enabled == rendering) { return; }
        rendering = enabled; lastFrame = -1; nextFrame = nextGraphFrame = 0;
        if (enabled) { RefreshNow(); RefreshGraph(); CompositionTarget.Rendering += Render; }
        else { CompositionTarget.Rendering -= Render; }
    }

    private void Render(object? sender, EventArgs args) { if (args is RenderingEventArgs frame) { RefreshFrame(frame.RenderingTime); } }

    internal void RefreshFrame(TimeSpan time)
    {
        if (!rendering || closed) { return; }
        var now = time.Ticks;
        if (lastFrame == now) { return; }
        if (now < lastFrame) { nextFrame = nextGraphFrame = 0; }
        lastFrame = now;
        if (now < nextFrame) { return; }
        nextFrame = nextFrame == 0 || now - nextFrame > FrameInterval * 2 ? now + FrameInterval : nextFrame + FrameInterval;
        RefreshNow();
        if (now >= nextGraphFrame) { nextGraphFrame = now + TimeSpan.TicksPerSecond / 30; RefreshGraph(); }
    }

    internal void RefreshNow() { if (!closed) { model.Refresh(engine.CompactSnapshot(), pauseReason()); } }
}
