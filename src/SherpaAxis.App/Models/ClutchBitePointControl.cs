namespace SherpaAxis.App.Models;

internal sealed class ClutchBitePointControl(TimeProvider clock)
{
    internal static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan DoubleTapInterval = TimeSpan.FromMilliseconds(350);
    private bool down, secondTap;
    private long? pressedAt, tapReleasedAt;
    public bool Enabled { get; private set; }
    public bool Holding { get; private set; }
    public void SetEnabled(bool value)
    {
        Enabled = value;
        Holding = value && down && pressedAt.HasValue && clock.GetElapsedTime(pressedAt.Value) >= HoldDelay;
    }
    public void Update(bool pressed)
    {
        var now = clock.GetTimestamp();
        if (pressed && !down)
        {
            secondTap = tapReleasedAt.HasValue && clock.GetElapsedTime(tapReleasedAt.Value, now) <= DoubleTapInterval;
            if (secondTap) { SetEnabled(!Enabled); }
            tapReleasedAt = null;
            pressedAt = now;
        }
        else if (!pressed && down)
        {
            tapReleasedAt = !secondTap && pressedAt.HasValue && clock.GetElapsedTime(pressedAt.Value, now) < HoldDelay ? now : null;
            secondTap = false;
            pressedAt = null;
        }
        down = pressed;
        Holding = Enabled && down && pressedAt.HasValue && clock.GetElapsedTime(pressedAt.Value, now) >= HoldDelay;
        if (Holding) { tapReleasedAt = null; }
    }
    public void Release()
    {
        down = secondTap = Holding = false;
        pressedAt = tapReleasedAt = null;
    }
}
