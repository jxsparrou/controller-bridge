using System;
using System.Buffers.Binary;
using System.IO;
using SBridge.Steam;

namespace SBridge.Artwork;

internal enum ArtworkKind { Portrait, Hero, Logo, Icon }
internal sealed record ArtworkRequest(SteamAccount Account, string Name, uint AppId, string Exe, string LaunchOptions, string OriginalIcon);
internal sealed record ArtworkImage(byte[] Bytes, string Extension);
internal sealed record ArtworkResult(int Saved, string? IconPath, string[] Warnings, bool Cancelled = false);

internal static class ArtworkImageLimits
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxPixels = 16 * 1024 * 1024;
    public static string Inspect(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Artwork exceeds the 16 MiB limit.");
        int width = 0, height = 0;
        string extension;
        if (bytes.Length >= 33 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) == 13)
        {
            width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
            height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
            extension = ".png";
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[^2] == 0xFF && bytes[^1] == 0xD9)
        {
            extension = ".jpg";
            int index = 2;
            while (index + 3 < bytes.Length)
            {
                if (bytes[index++] != 0xFF) throw new InvalidDataException("Invalid JPEG artwork marker.");
                while (index < bytes.Length && bytes[index] == 0xFF) index++;
                if (index >= bytes.Length) break;
                int marker = bytes[index++];
                if (marker == 0xDA || marker == 0xD9) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index, 2));
                if (length < 2 || index + length > bytes.Length) throw new InvalidDataException("Truncated JPEG artwork.");
                if (marker is 0xC0 or 0xC1 or 0xC2)
                {
                    if (length < 8) throw new InvalidDataException("Invalid JPEG dimensions.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 5, 2));
                    break;
                }
                index += length;
            }
        }
        else throw new InvalidDataException("Artwork must be a PNG or JPEG image.");
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > MaxPixels)
            throw new InvalidDataException("Artwork dimensions exceed the bounded pixel budget.");
        return extension;
    }
}
