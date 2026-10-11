namespace SherpaAxis.Profiles;

public enum BaselineCorrectionMode { Off, Auto, ByButton }

public sealed record CorrectionButton
{
    public CorrectionButton(string kind, int code, string? device = null)
    {
        if (kind is not ("keyboard" or "controller") || code is < 1 or > 65535
            || kind == "keyboard" && (code > 254 || device is not null)
            || kind == "controller" && (string.IsNullOrWhiteSpace(device) || device.Length > 1024))
        { throw new ArgumentException("Invalid correction button binding."); }
        Kind = kind; Code = code; Device = device?.ToUpperInvariant();
    }
    public string Kind { get; }
    public int Code { get; }
    public string? Device { get; }
}
