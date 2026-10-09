using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;

namespace SBridge.Artwork;

[SupportedOSPlatform("windows")]
internal static class WindowsArtworkImages
{
    public static ArtworkImage Process(byte[] bytes, bool icon)
    {
        string extension = ArtworkImageLimits.Inspect(bytes);
        try
        {
            using var input = new MemoryStream(bytes);
            using var image = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > ArtworkImageLimits.MaxPixels)
                throw new InvalidDataException("Decoded artwork exceeds the pixel budget.");
            // Force decoding before persisting compressed bytes; header recognition
            // alone is not validation of an image payload.
            using var decoded = new Bitmap(image);
            if (!icon) return new ArtworkImage(bytes, extension);
            using var output = new BoundedImageOutput(); decoded.Save(output, ImageFormat.Png);
            byte[] png = output.ToArray(); ArtworkImageLimits.Inspect(png);
            return new ArtworkImage(png, ".png");
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        { throw new InvalidDataException("Artwork image decoding failed.", ex); }
    }

    private sealed class BoundedImageOutput : MemoryStream
    {
        private void Check(long length)
        { if (length > ArtworkImageLimits.MaxBytes) throw new InvalidDataException("Converted artwork exceeds the byte budget."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
    }
}
