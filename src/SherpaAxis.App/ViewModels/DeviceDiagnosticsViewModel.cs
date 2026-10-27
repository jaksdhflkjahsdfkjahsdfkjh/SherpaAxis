using System.Collections.ObjectModel;
using SherpaAxis.App.Services.Diagnostics;

namespace SherpaAxis.App.ViewModels;

public sealed class DeviceDiagnosticsViewModel : ObservableObject
{
    public DeviceDiagnosticsViewModel() => Checks.CollectionChanged += (_, _) => Changed(nameof(HasIssues));
    public bool HasIssues => Checks.Any(check => check.NeedsAttention);
    public ObservableCollection<DeviceCheck> Checks { get; } = [];
    private bool busy;
    private bool firmwareWarning;
    private string status = "Checks run automatically at startup and when devices change.";
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Changed(nameof(CanCheck)); } } }
    public bool CanCheck => !Busy;
    public bool FirmwareWarning { get => firmwareWarning; set => Set(ref firmwareWarning, value); }
    private string firmwareNotice = "";
    public string FirmwareNotice { get => firmwareNotice; private set => Set(ref firmwareNotice, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public void Apply(DeviceDiagnosticsResult result)
    {
        Checks.Clear(); foreach (var check in result.Checks) { Checks.Add(check); }
        FirmwareWarning = result.FirmwareWarning;
        FirmwareNotice = result.FirmwareWarning ? result.Checks.FirstOrDefault(check => check.Title == "Firmware")?.Detail ?? "" : "";
        Status = result.Checks.Any(check => check.NeedsAttention) ? "Setup needs attention. See the checks above." : "Checks complete.";
    }
}
