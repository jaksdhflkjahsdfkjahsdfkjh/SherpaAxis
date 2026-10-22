using SherpaAxis.App.Services.Firmware;

namespace SherpaAxis.App.ViewModels;

public sealed class FirmwareUpdateViewModel : ObservableObject
{
    private string status = "Firmware files and DfuSe are included with SherpaAxis Setup.";
    private FirmwareChoice? selectedImage;
    private IReadOnlyList<FirmwareChoice> images = [];
    private bool blocked, running, canAccept, completed;
    public IReadOnlyList<FirmwareChoice> Images { get => images; set => Set(ref images, value); }
    public FirmwareChoice? SelectedImage { get => selectedImage; set { if (Set(ref selectedImage, value)) { Changed(nameof(CanFlash)); } } }
    public bool Blocked { get => blocked; set { if (Set(ref blocked, value)) { Notify(); } } }
    public bool Running { get => running; set { if (Set(ref running, value)) { Notify(); } } }
    public bool Completed { get => completed; set { if (Set(ref completed, value)) { Notify(); } } }
    public bool CanAccept { get => canAccept; set { if (Set(ref canAccept, value)) { Changed(nameof(CanUseDetected)); } } }
    public bool CanPrepare => !Running && !Blocked && !Completed;
    public bool CanOpenUpdater => !Running;
    public bool CanReview => !Running && Blocked;
    public bool CanUseDetected => CanReview && CanAccept;
    public bool CanFlash => !Running && (Completed || Blocked || SelectedImage is not null);
    public string FlashLabel => Completed ? "OK" : Blocked ? "Retry flash" : "Flash";
    public string Status { get => status; set => Set(ref status, value); }
    private void Notify() { Changed(nameof(CanPrepare)); Changed(nameof(CanOpenUpdater)); Changed(nameof(CanReview)); Changed(nameof(CanUseDetected)); Changed(nameof(CanFlash)); Changed(nameof(FlashLabel)); }
}
