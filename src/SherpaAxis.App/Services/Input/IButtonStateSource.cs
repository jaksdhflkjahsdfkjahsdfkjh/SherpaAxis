using SherpaAxis.Profiles;

namespace SherpaAxis.App.Services.Input;

public interface IButtonStateSource : IDisposable
{
    event Action<CorrectionButton>? Pressed;
    bool IsDown(CorrectionButton button);
    long ConnectionRevision => 0;
    long GetConnectionRevision(CorrectionButton button) => ConnectionRevision;
}

public sealed class ButtonStateBuffer : IButtonStateSource
{
    private readonly object sync = new();
    private readonly HashSet<CorrectionButton> down = [];
    private long connectionRevision;
    private readonly Dictionary<string, long> deviceRevisions = new(StringComparer.OrdinalIgnoreCase);
    public long GetConnectionRevision(CorrectionButton button)
    { lock (sync) { return deviceRevisions.GetValueOrDefault(button.Device ?? button.Kind); } }
    public long ConnectionRevision { get { lock (sync) { return connectionRevision; } } }
    public event Action<CorrectionButton>? Pressed;
    public bool IsDown(CorrectionButton button) { lock (sync) { return down.Contains(button); } }
    internal void Set(CorrectionButton button, bool pressed)
    {
        bool changed;
        lock (sync) { changed = pressed ? down.Add(button) : down.Remove(button); }
        if (changed && pressed) { Pressed?.Invoke(button); }
    }
    internal void RemoveDevice(string device)
    { lock (sync) { down.RemoveWhere(button => string.Equals(button.Device, device, StringComparison.OrdinalIgnoreCase)); deviceRevisions[device] = ++connectionRevision; } }
    internal void NotifyDeviceRemoved() { lock (sync) { deviceRevisions["keyboard"] = ++connectionRevision; } }
    public void Dispose() { lock (sync) { down.Clear(); } GC.SuppressFinalize(this); }
}
