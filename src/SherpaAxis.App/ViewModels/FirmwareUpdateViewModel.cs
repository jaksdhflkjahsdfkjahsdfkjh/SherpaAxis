using SherpaAxis.App.Models;
using SherpaAxis.App.Services;

namespace SherpaAxis.App.ViewModels;

public sealed class FirmwareUpdateViewModel : ObservableObject
{
    private string imagePath = "", toolPath = "", status = "Select the vendor firmware file and ST DfuSe tool, then prepare the update.";
    private FirmwarePlan? plan;
    private bool blocked, running, canAccept;
    public string ImagePath { get => imagePath; set { if (Set(ref imagePath, value)) { Plan = null; } } }
    public string ToolPath { get => toolPath; set { if (Set(ref toolPath, value)) { Plan = null; } } }
    public FirmwarePlan? Plan { get => plan; set { if (Set(ref plan, value)) { Changed(nameof(PlanInfo)); Changed(nameof(CanFlash)); } } }
    public string PlanInfo => Plan is null ? "" : $"Controller: {Plan.SerialNumber}\nFirmware: {Plan.OriginalVersion} → {Plan.Image.Version}\nSHA-256: {Plan.Image.Sha256}";
    public bool Blocked { get => blocked; set { if (Set(ref blocked, value)) { Notify(); } } }
    public bool Running { get => running; set { if (Set(ref running, value)) { Notify(); } } }
    public bool CanAccept { get => canAccept; set { if (Set(ref canAccept, value)) { Changed(nameof(CanAcceptCurrent)); } } }
    public bool CanPrepare => !Running && !Blocked;
    public bool CanFlash => CanPrepare && Plan is not null;
    public bool CanReview => !Running && Blocked;
    public bool CanAcceptCurrent => CanReview && CanAccept;
    public string Status { get => status; set => Set(ref status, value); }
    private void Notify() { Changed(nameof(CanPrepare)); Changed(nameof(CanFlash)); Changed(nameof(CanReview)); Changed(nameof(CanAcceptCurrent)); }
}
