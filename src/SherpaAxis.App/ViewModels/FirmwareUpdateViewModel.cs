using SherpaAxis.App.Services.Firmware;

namespace SherpaAxis.App.ViewModels;

public sealed class FirmwareUpdateViewModel : ObservableObject
{
    private string status = "Firmware files and DfuSe are included with SherpaAxis Setup.";
    private FirmwareChoice? selectedImage;
    private IReadOnlyList<FirmwareChoice> images = [];
    private bool blocked, running, canAccept;
    public IReadOnlyList<FirmwareChoice> Images { get => images; set => Set(ref images, value); }
    public FirmwareChoice? SelectedImage { get => selectedImage; set { if (Set(ref selectedImage, value)) { Changed(nameof(CanFlash)); } } }
    public bool Blocked { get => blocked; set { if (Set(ref blocked, value)) { Notify(); } } }
    public bool Running { get => running; set { if (Set(ref running, value)) { Notify(); } } }
    public bool CanAccept { get => canAccept; set => Set(ref canAccept, value); }
    public bool CanPrepare => !Running && !Blocked;
    public bool CanOpenUpdater => !Running;
    public bool CanFlash => !Running && (Blocked || SelectedImage is not null);
    public string FlashLabel => Blocked ? "Retry flash" : "Flash";
    public string Status { get => status; set => Set(ref status, value); }
    private void Notify() { Changed(nameof(CanPrepare)); Changed(nameof(CanOpenUpdater)); Changed(nameof(CanFlash)); Changed(nameof(FlashLabel)); }
}
