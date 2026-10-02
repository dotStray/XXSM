using System.Buffers.Binary;

namespace Xxsm.Core.Mods;

/// <summary>A picture's size as its header says, read before decoding: PNG, JPEG, WebP, GIF and BMP.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
public readonly record struct PictureSize(int Width, int Height)
{
    /// <summary>The most pixels a picture may have to be shown: 40 million, a 8000 × 5000 photograph.</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>How far into a file its size is looked for: a JPEG's size can follow a long EXIF block.</summary>
    public const int HeaderBytes = 512 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8];

    /// <summary>Whether it has more pixels than <see cref="MaxPixels"/>.</summary>
    public bool IsTooLarge => (long)Width * Height > MaxPixels;

    /// <summary>Reads the size from the start of a stream, and puts the stream back where it was.</summary>
    /// <param name="stream">A stream that can seek, at the picture's first byte.</param>
    /// <param name="size">The size, when the header gives one.</param>
    /// <returns><see langword="true"/> when the header was recognised and gives a size.</returns>
    /// <exception cref="ArgumentException">The stream cannot seek.</exception>
    public static bool TryRead(Stream stream, out PictureSize size)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be able to seek, to be read again afterwards.", nameof(stream));
        }

        var start = stream.Position;
        var buffer = new byte[(int)Math.Min(HeaderBytes, Math.Max(0, stream.Length - start))];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        stream.Position = start;

        return TryRead(buffer.AsSpan(0, read), out size);
    }

    /// <summary>Reads the size from a picture's first bytes.</summary>
    /// <param name="header">The start of the file; more than the header is fine.</param>
    /// <param name="size">The size, when the header gives one.</param>
    /// <returns><see langword="true"/> when the header was recognised and gives a size.</returns>
    public static bool TryRead(ReadOnlySpan<byte> header, out PictureSize size)
    {
        size = default;

        var found = header switch
        {
            _ when header.StartsWith(PngSignature) => Png(header),
            _ when header.StartsWith(JpegSignature) => Jpeg(header),
            _ when header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8) => Gif(header),
            _ when header.StartsWith("BM"u8) => Bmp(header),
            _ when header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) => Webp(header),
            _ => null,
        };

        if (found is not { Width: > 0, Height: > 0 } known)
        {
            return false;
        }

        size = known;
        return true;
    }

    private static PictureSize? Png(ReadOnlySpan<byte> h) =>
        h.Length >= 24 && h[12..16].SequenceEqual("IHDR"u8)
            ? new PictureSize(BinaryPrimitives.ReadInt32BigEndian(h[16..]), BinaryPrimitives.ReadInt32BigEndian(h[20..]))
            : null;

    private static PictureSize? Gif(ReadOnlySpan<byte> h) =>
        h.Length >= 10
            ? new PictureSize(BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), BinaryPrimitives.ReadUInt16LittleEndian(h[8..]))
            : null;

    private static PictureSize? Bmp(ReadOnlySpan<byte> h)
    {
        if (h.Length < 26)
        {
            return null;
        }

        // The OS/2 header gives 16-bit sizes; later ones 32-bit, the height negative when top-down.
        return BinaryPrimitives.ReadInt32LittleEndian(h[14..]) == 12
            ? new PictureSize(BinaryPrimitives.ReadUInt16LittleEndian(h[18..]), BinaryPrimitives.ReadUInt16LittleEndian(h[20..]))
            : new PictureSize(
                BinaryPrimitives.ReadInt32LittleEndian(h[18..]),
                Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(h[22..])));
    }

    private static PictureSize? Webp(ReadOnlySpan<byte> h)
    {
        if (h.Length < 30)
        {
            return null;
        }

        var chunk = h[12..16];

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return new PictureSize(UInt24(h[24..]) + 1, UInt24(h[27..]) + 1);
        }

        if (chunk.SequenceEqual("VP8 "u8))
        {
            return new PictureSize(
                BinaryPrimitives.ReadUInt16LittleEndian(h[26..]) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(h[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8) && h[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(h[21..]);
            return new PictureSize((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        return null;

        static int UInt24(ReadOnlySpan<byte> at) => at[0] | (at[1] << 8) | (at[2] << 16);
    }

    private static PictureSize? Jpeg(ReadOnlySpan<byte> h)
    {
        var i = 2;

        while (i + 9 < h.Length)
        {
            if (h[i] != 0xFF)
            {
                return null;
            }

            var marker = h[i + 1];

            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            // Markers with no length: TEM, the restart markers and a stray start-of-image.
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            // C4, C8 and CC share the start-of-frame range but are not frames.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return new PictureSize(
                    BinaryPrimitives.ReadUInt16BigEndian(h[(i + 7)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(h[(i + 5)..]));
            }

            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(h[(i + 2)..]);
        }

        return null;
    }
}
