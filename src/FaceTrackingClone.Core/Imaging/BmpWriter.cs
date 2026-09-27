namespace FaceTrackingClone.Imaging;

/// <summary>
/// Minimal 8-bit greyscale BMP writer. Exists so probe output can be eyeballed in any
/// Windows image viewer without pulling in an imaging dependency.
/// </summary>
internal static class BmpWriter
{
    public static void WriteGrey8(string path, ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (pixels.Length < width * height)
            throw new ArgumentException("Pixel buffer smaller than width*height.", nameof(pixels));

        // Rows are padded to 4-byte boundaries and stored bottom-up.
        int rowStride = (width + 3) & ~3;
        int paletteBytes = 256 * 4;
        int pixelOffset = 14 + 40 + paletteBytes;
        int imageBytes = rowStride * height;

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        // BITMAPFILEHEADER
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(pixelOffset + imageBytes);
        w.Write((short)0); w.Write((short)0);
        w.Write(pixelOffset);

        // BITMAPINFOHEADER
        w.Write(40);
        w.Write(width);
        w.Write(height);
        w.Write((short)1);
        w.Write((short)8);
        w.Write(0);              // BI_RGB
        w.Write(imageBytes);
        w.Write(2835); w.Write(2835);
        w.Write(256); w.Write(256);

        // Greyscale palette
        for (int i = 0; i < 256; i++)
        {
            w.Write((byte)i); w.Write((byte)i); w.Write((byte)i); w.Write((byte)0);
        }

        Span<byte> row = stackalloc byte[rowStride];
        for (int y = height - 1; y >= 0; y--)
        {
            row.Clear();
            pixels.Slice(y * width, width).CopyTo(row);
            w.Write(row);
        }
    }
}
