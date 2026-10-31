using SherpaAxis.App.Services.Updates;

namespace SherpaAxis.App.ViewModels;

public sealed class AppUpdateViewModel : ObservableObject
{
    private bool busy;
    private AppRelease? available;
    private bool isUpdating;
    private bool isInstalling;
    private int? percent;
    private string status = "Checks GitHub at startup. Click an available update to download, install and restart.";
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Changed(nameof(CanCheck)); NotifyAction(); } } }
    public bool CanCheck => !Busy && !IsUpdating;
    public bool CanAct => HasUpdate && !Busy && !IsInstalling;
    public AppRelease? Release => available;
    public bool IsUpdating { get => isUpdating; set { if (Set(ref isUpdating, value)) { Changed(nameof(CanCheck)); NotifyAction(); } } }
    public bool IsInstalling { get => isInstalling; set { if (Set(ref isInstalling, value)) { NotifyAction(); } } }
    public int? Percent { get => percent; set { if (Set(ref percent, value)) { Changed(nameof(LinkLabel)); } } }
    public bool HasUpdate => available is not null;
    public Uri? ReleasePage => available?.Page;
    public string AvailableVersion => available?.DisplayVersion ?? "";
    public string LinkLabel => IsInstalling ? "Installing update…" : IsUpdating
        ? Percent is { } value ? $"Cancel download · {value}%" : "Cancel update" : $"Update available: {AvailableVersion}";
    public string ActionHint => IsUpdating ? IsInstalling ? "Installing update. SherpaAxis will reopen automatically." : "Click to cancel the update download."
        : "Download the verified update, install it and reopen SherpaAxis. Windows may ask for administrator approval.";
    public string Status { get => status; set => Set(ref status, value); }
    public void Apply(AppRelease? release)
    {
        available = release is not null && AppUpdateChecker.IsNewer(release, typeof(AppUpdateViewModel).Assembly.GetName().Version!) ? release : null;
        Status = HasUpdate ? $"A newer version is available: {AvailableVersion}."
            : release is null ? "No public release found. The repository may be private or have no published releases."
            : $"You're up to date ({DashboardViewModel.VersionLabel}).";
        Changed(nameof(HasUpdate)); Changed(nameof(ReleasePage)); Changed(nameof(AvailableVersion)); NotifyAction();
    }
    private void NotifyAction() { Changed(nameof(CanAct)); Changed(nameof(LinkLabel)); Changed(nameof(ActionHint)); }
}
