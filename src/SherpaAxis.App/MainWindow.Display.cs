using System.Windows;
using System.Windows.Media;

namespace SherpaAxis.App;

public partial class MainWindow
{
    private const long PedalPreviewInterval = TimeSpan.TicksPerSecond / 144;
    private bool pedalPreviewRendering;
    private long nextPedalPreviewAt;
    private long lastPedalRenderingTime = -1;

    private void SetPedalPreviewRendering(bool enabled)
    {
        if (pedalPreviewRendering == enabled) { return; }
        pedalPreviewRendering = enabled;
        nextPedalPreviewAt = 0;
        lastPedalRenderingTime = -1;
        if (enabled) { CompositionTarget.Rendering += RenderPedalPreviews; }
        else { CompositionTarget.Rendering -= RenderPedalPreviews; }
    }

    private void RenderPedalPreviews(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs frame) { RefreshPedalPreviews(frame.RenderingTime); }
    }

    internal void RefreshPedalPreviews(TimeSpan renderingTime)
    {
        if (!pedalPreviewRendering || closing || disposed || !IsVisible || WindowState == WindowState.Minimized) { return; }
        var now = renderingTime.Ticks;
        if (now == lastPedalRenderingTime) { return; }
        if (now < lastPedalRenderingTime) { nextPedalPreviewAt = 0; }
        lastPedalRenderingTime = now;
        if (now < nextPedalPreviewAt) { return; }
        nextPedalPreviewAt = nextPedalPreviewAt == 0 || now - nextPedalPreviewAt > PedalPreviewInterval * 2
            ? now + PedalPreviewInterval : nextPedalPreviewAt + PedalPreviewInterval;
        viewModel.RefreshCards();
    }
}
