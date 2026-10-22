using SherpaAxis.App.Services.Updates;

namespace SherpaAxis.App.ViewModels;

public sealed class AppUpdateViewModel : ObservableObject
{
    private bool busy;
    private AppRelease? available;
    private string status = "Checks GitHub for stable releases. Downloads and installation are manual.";
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Changed(nameof(CanCheck)); } } }
    public bool CanCheck => !Busy;
    public bool HasUpdate => available is not null;
    public Uri? ReleasePage => available?.Page;
    public string AvailableVersion => available?.DisplayVersion ?? "";
    public string LinkLabel => $"Update available: {AvailableVersion}";
    public string Status { get => status; set => Set(ref status, value); }
    public void Apply(AppRelease? release)
    {
        available = release is not null && AppUpdateChecker.IsNewer(release, typeof(AppUpdateViewModel).Assembly.GetName().Version!) ? release : null;
        Status = HasUpdate ? $"A newer version is available: {AvailableVersion}."
            : release is null ? "No public release found. The repository may be private or have no published releases."
            : $"You're up to date ({DashboardViewModel.VersionLabel}).";
        Changed(nameof(HasUpdate)); Changed(nameof(ReleasePage)); Changed(nameof(AvailableVersion)); Changed(nameof(LinkLabel));
    }
}
