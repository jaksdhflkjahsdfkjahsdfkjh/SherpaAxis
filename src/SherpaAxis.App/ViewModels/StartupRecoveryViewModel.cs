using System.Collections.ObjectModel;
using SherpaAxis.App.Services.Hardware;

namespace SherpaAxis.App.ViewModels;

public sealed class StartupRecoveryViewModel : ObservableObject
{
    public ObservableCollection<PendingHardwareRecovery> Records { get; } = [];
    private PendingHardwareRecovery? selected;
    private bool busy, blocked, needsAttention;
    private string status = "";
    public PendingHardwareRecovery? Selected { get => selected; set { if (Set(ref selected, value)) { Notify(); } } }
    public bool IsBlocked { get => blocked; private set => Set(ref blocked, value); }
    public bool NeedsAttention { get => needsAttention; private set => Set(ref needsAttention, value); }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Notify(); } } }
    public bool CanCheck => !Busy;
    public bool CanRestore => !Busy && Selected?.CanRestore == true;
    public bool CanSave => !Busy && Selected?.CanSave == true;
    public bool ShowSave => Selected?.Snapshot?.SaveUncertain == true;
    public string Detail => Selected?.Detail ?? "";
    public string FilePath => Selected?.Path ?? "";
    public string Status { get => status; set => Set(ref status, value); }
    public void Checking() { IsBlocked = true; Busy = true; Status = "Checking pedal settings recovery records… Virtual output is paused."; }
    public void Apply(IReadOnlyList<PendingHardwareRecovery> records)
    {
        var path = Selected?.Path;
        Records.Clear(); foreach (var item in records) { Records.Add(item); }
        Selected = Records.FirstOrDefault(item => item.Path == path) ?? Records.FirstOrDefault();
        IsBlocked = Records.Count > 0;
        NeedsAttention = IsBlocked;
        Status = !IsBlocked ? "Hardware recovery check completed."
            : Records.All(record => record.IsDriftMonitoring) ? "Unfinished drift fix needs recovery. Pedal input and virtual output are paused."
            : "Pedal settings need recovery. Pedal input and virtual output are paused.";
        Busy = false; Notify();
    }
    public void Fail(string detail) { IsBlocked = true; NeedsAttention = true; Busy = false; Status = $"Recovery check could not finish: {detail}"; Notify(); }
    private void Notify()
    {
        foreach (var property in new[] { nameof(CanCheck), nameof(CanRestore), nameof(CanSave), nameof(ShowSave), nameof(Detail), nameof(FilePath) }) { Changed(property); }
    }
}
