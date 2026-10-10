using System.Globalization;

namespace SherpaAxis.FeelVR;

public sealed record PedalDeviceIdentity(string Hardware, string Firmware, string Serial)
{
    public static bool SameController(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal)) { return true; }
        try
        {
            var first = Parse(expected);
            var second = Parse(actual);
            return first.Hardware == second.Hardware && string.Equals(first.Serial, second.Serial, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public static PedalDeviceIdentity Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length < 6) { throw new ArgumentException("Controller identity is missing its hardware/firmware prefix.", nameof(raw)); }
        var suffix = raw[6..].TrimEnd('\0');
        if (suffix.Length % 2 != 0) { suffix += "0"; }
        byte[] bytes;
        try { bytes = Convert.FromHexString(suffix); }
        catch (FormatException failure) { throw new ArgumentException("Controller serial suffix is not hexadecimal.", nameof(raw), failure); }
        byte checksum = 0;
        foreach (var value in bytes)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++) { checksum = (byte)((checksum << 1) ^ ((checksum & 0x80) != 0 ? 0xD5 : 0)); }
        }
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{(char)('A' + checksum / 26)}{(char)('A' + checksum % 26)}");
        return new(raw[..3], raw.Substring(3, 3), $"{prefix}-{suffix}");
    }
}
