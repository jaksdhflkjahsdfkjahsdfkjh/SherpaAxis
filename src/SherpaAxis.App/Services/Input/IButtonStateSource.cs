using SherpaAxis.Profiles;

namespace SherpaAxis.App.Services.Input;

public interface IButtonStateSource : IDisposable
{
    event Action<CorrectionButton>? Pressed;
    bool IsDown(CorrectionButton button);
    long ConnectionRevision => 0;
    long GetConnectionRevision(CorrectionButton button) => ConnectionRevision;
    void SetKeyboardEditing(bool editing) { }
    void SetBindingCapture(bool capturing) { }
}

public sealed class ButtonStateBuffer : IButtonStateSource
{
    private readonly object sync = new();
    private readonly HashSet<CorrectionButton> down = [];
    private long connectionRevision;
    private readonly Dictionary<string, long> deviceRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<CorrectionButton> awaitingRelease = [];
    private bool keyboardEditing;
    private bool bindingCapture;
    private long keyboardRevision;
    private long controllerRevision;
    public long GetConnectionRevision(CorrectionButton button)
    { lock (sync) { return Math.Max(deviceRevisions.GetValueOrDefault(button.Device ?? button.Kind), button.Kind == "keyboard" ? keyboardRevision : controllerRevision); } }
    public long ConnectionRevision { get { lock (sync) { return connectionRevision; } } }
    public event Action<CorrectionButton>? Pressed;
    public bool IsDown(CorrectionButton button)
    { lock (sync) { return down.Contains(button) && !Suppressed(button) && !awaitingRelease.Contains(button); } }
    private bool Suppressed(CorrectionButton button) => bindingCapture || keyboardEditing && button.Kind == "keyboard";
    public void SetKeyboardEditing(bool editing)
    {
        lock (sync)
        {
            if (keyboardEditing == editing) { return; }
            keyboardEditing = editing;
            keyboardRevision = ++connectionRevision;
            foreach (var button in down.Where(Suppressed)) { awaitingRelease.Add(button); }
        }
    }
    public void SetBindingCapture(bool capturing)
    {
        lock (sync)
        {
            if (bindingCapture == capturing) { return; }
            bindingCapture = capturing;
            keyboardRevision = controllerRevision = ++connectionRevision;
            foreach (var button in down.Where(Suppressed)) { awaitingRelease.Add(button); }
        }
    }
    internal void Set(CorrectionButton button, bool pressed)
    {
        bool changed;
        lock (sync)
        {
            changed = pressed ? down.Add(button) : down.Remove(button);
            if (!pressed) { awaitingRelease.Remove(button); }
            else if (Suppressed(button)) { awaitingRelease.Add(button); }
        }
        if (changed && pressed) { Pressed?.Invoke(button); }
    }
    internal void RemoveDevice(string device)
    { lock (sync) { down.RemoveWhere(button => string.Equals(button.Device, device, StringComparison.OrdinalIgnoreCase)); deviceRevisions[device] = ++connectionRevision; } }
    internal void NotifyDeviceRemoved() { lock (sync) { deviceRevisions["keyboard"] = ++connectionRevision; } }
    public void Dispose() { lock (sync) { down.Clear(); awaitingRelease.Clear(); } GC.SuppressFinalize(this); }
}
