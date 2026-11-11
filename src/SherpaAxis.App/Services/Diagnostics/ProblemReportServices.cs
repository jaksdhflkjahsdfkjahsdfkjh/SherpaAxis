using System.Diagnostics;
using System.IO;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Services.Diagnostics;

public sealed record ProblemReportServices(string Directory, Action<Uri> OpenIssue, Action<string> RevealArchive)
{
    internal static ProblemReportServices Default() => new(AppDataPaths.SupportReports,
        page => Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }),
        path => Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" }));
}
