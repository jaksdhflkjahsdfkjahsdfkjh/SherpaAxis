using System.Collections.ObjectModel;
using SherpaAxis.App.Services;

namespace SherpaAxis.App.ViewModels;

public sealed class StartupRecoveryViewModel : ObservableObject
{
    public ObservableCollection<PendingHardwareRecovery> Records { get; } = [];
    private PendingHardwareRecovery? selected;
    private bool busy, blocked;
    private string status = "";
    public PendingHardwareRecovery? Selected { get => selected; set { if (Set(ref selected, value)) { Notify(); } } }
    public bool IsBlocked { get => blocked; private set => Set(ref blocked, value); }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Notify(); } } }
    public bool CanCheck => !Busy;
    public bool CanRestore => !Busy && Selected?.CanRestore == true;
    public bool CanSave => !Busy && Selected?.CanSave == true;
    public bool ShowSave => Selected?.Snapshot?.SaveUncertain == true;
    public string Detail => Selected?.Detail ?? "";
    public string FilePath => Selected?.Path ?? "";
    public string Status { get => status; set => Set(ref status, value); }
    public void Checking() { IsBlocked = true; Busy = true; Status = "Checking interrupted calibration records… Virtual output is paused."; }
    public void Apply(IReadOnlyList<PendingHardwareRecovery> records)
    {
        var path = Selected?.Path;
        Records.Clear(); foreach (var item in records) { Records.Add(item); }
        Selected = Records.FirstOrDefault(item => item.Path == path) ?? Records.FirstOrDefault();
        IsBlocked = Records.Count > 0;
        Status = IsBlocked ? "Interrupted calibration needs attention. Pedal input and virtual output are paused." : "Hardware recovery check completed.";
        Busy = false; Notify();
    }
    public void Fail(string detail) { IsBlocked = true; Busy = false; Status = $"Recovery check could not finish: {detail}"; Notify(); }
    private void Notify()
    {
        foreach (var property in new[] { nameof(CanCheck), nameof(CanRestore), nameof(CanSave), nameof(ShowSave), nameof(Detail), nameof(FilePath) }) { Changed(property); }
    }
}
