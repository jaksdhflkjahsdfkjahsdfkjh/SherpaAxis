using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SherpaAxis.FeelVR;

public sealed record PedalFirmwareImage(string Version, string Sha256, int Length, uint Address, int PayloadLength)
{
    public const string Reviewed017Hash = "5D525D43D7EF434E61E0DD077379629C420F95CD863E6C607201752368157757";
    public const int MaximumLength = 1_048_576;

    public static PedalFirmwareImage Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 309 or > MaximumLength || !bytes[..5].SequenceEqual("DfuSe"u8) || bytes[5] != 1 || bytes[10] != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]) != bytes.Length - 16)
        { throw new InvalidDataException("Invalid DfuSe firmware header or file size."); }
        var suffix = bytes[^16..];
        if (!suffix.Slice(8, 3).SequenceEqual("UFD"u8) || suffix[11] != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(suffix[2..]) != PedalInputReport.ProductId
            || BinaryPrimitives.ReadUInt16LittleEndian(suffix[4..]) != PedalInputReport.VendorId
            || BinaryPrimitives.ReadUInt16LittleEndian(suffix[6..]) != 0x011A)
        { throw new InvalidDataException("This firmware file does not target FEEL-VR Pedals One."); }
        if (Crc(bytes[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]))
        { throw new InvalidDataException("Firmware checksum failed. Select an undamaged vendor file."); }
        var target = bytes[11..285];
        if (!target[..6].SequenceEqual("Target"u8) || target[6] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(target[270..]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(target[266..]) != bytes.Length - 301)
        { throw new InvalidDataException("Unsupported firmware target or element layout."); }
        var address = BinaryPrimitives.ReadUInt32LittleEndian(bytes[285..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[289..]);
        if (address != 0x0800C000 || length != bytes.Length - 309 || length == 0 || (ulong)address + length > 0x0801F800)
        { throw new InvalidDataException("Firmware data lies outside the reviewed pedal application region."); }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, Reviewed017Hash, StringComparison.Ordinal))
        { throw new InvalidDataException("This firmware image has not been reviewed. Currently only the known PE0017 vendor image is supported; renaming a file does not make it compatible."); }
        return new("017", hash, bytes.Length, address, checked((int)length));
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
        }
        return crc;
    }
}
