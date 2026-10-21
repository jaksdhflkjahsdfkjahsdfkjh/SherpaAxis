using System.IO;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Firmware;

public sealed record FirmwareChoice(string Path, string Version, string FileName)
{
    public string DisplayName => $"{FileName} · {Version}";
}

public sealed record FirmwareCatalog(IReadOnlyList<FirmwareChoice> Images, IReadOnlyList<string> RejectedFiles)
{
    public static FirmwareCatalog Scan(string directory)
    {
        if (!Directory.Exists(directory)) { return new([], []); }
        var images = new List<FirmwareChoice>();
        var rejected = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.dfu").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(path).Length > PedalFirmwareImage.MaximumLength) { throw new InvalidDataException("Firmware file is too large."); }
                var image = PedalFirmwareImage.Validate(File.ReadAllBytes(path));
                images.Add(new(Path.GetFullPath(path), image.Version, Path.GetFileName(path)));
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
            { rejected.Add(Path.GetFileName(path)); }
        }
        return new(images, rejected);
    }
}
