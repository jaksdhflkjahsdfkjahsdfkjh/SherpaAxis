using System.Windows;
using System.Windows.Controls;

namespace SherpaAxis.App.Controls;

public partial class TourOverlay : UserControl
{
    public event RoutedEventHandler? NextRequested;
    public event RoutedEventHandler? BackRequested;
    public event RoutedEventHandler? DismissRequested;
    public Rect TargetBounds => Spotlight.TargetBounds;
    public Rect CardBounds => new(Canvas.GetLeft(TourCard), Canvas.GetTop(TourCard), TourCard.ActualWidth, TourCard.ActualHeight);

    public TourOverlay() => InitializeComponent();
    public void ClearHighlight() => Spotlight.SetTarget(Rect.Empty);
    public void ResetContent() => TourBody.ScrollToTop();

    public void Place(Rect target)
    {
        if (ActualWidth < 1 || ActualHeight < 1) { return; }
        Spotlight.SetTarget(target);
        var width = Math.Min(390, Math.Max(310, ActualWidth * .29));
        var sideSpace = Math.Max(target.Left - 26, ActualWidth - target.Right - 26);
        if (sideSpace >= 300) { width = Math.Min(width, sideSpace); }
        TourCard.Width = Math.Min(width, ActualWidth - 24);
        TourCard.MaxHeight = Math.Min(510, ActualHeight - 24);
        TourCard.Measure(new Size(TourCard.Width, TourCard.MaxHeight));
        var height = TourCard.DesiredSize.Height;
        var size = new Size(TourCard.Width, height);
        var position = PlaceCard(new Size(ActualWidth, ActualHeight), target, size);
        Canvas.SetLeft(TourCard, position.X);
        Canvas.SetTop(TourCard, position.Y);
    }

    internal static Point PlaceCard(Size viewport, Rect target, Size card)
    {
        const double gap = 14;
        double x, y;
        if (viewport.Width - target.Right >= card.Width + gap + 12)
        { x = target.Right + gap; y = target.Top; }
        else if (target.Left >= card.Width + gap + 12)
        { x = target.Left - card.Width - gap; y = target.Top; }
        else if (viewport.Height - target.Bottom >= card.Height + gap)
        { x = target.Left; y = target.Bottom + gap; }
        else
        { x = target.Left; y = target.Top - card.Height - gap; }
        return new(Math.Clamp(x, 12, Math.Max(12, viewport.Width - card.Width - 12)),
            Math.Clamp(y, 12, Math.Max(12, viewport.Height - card.Height - 12)));
    }

    public void FocusNavigation() => TourNextButton.Focus();
    private void NextClick(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, e);
    private void BackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, e);
    private void SkipClick(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, e);
}
