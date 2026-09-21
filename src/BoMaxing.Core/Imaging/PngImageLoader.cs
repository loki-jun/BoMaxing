using System.Buffers.Binary;
using System.IO.Compression;

namespace BoMaxing.Core.Imaging;

public static class PngImageLoader
{
    private static readonly byte[] Signature =
        [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task<Image2D> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (bytes.Length < Signature.Length || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("PNG signature is invalid.");
        }

        var position = 8;
        var idat = new MemoryStream();
        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        while (position + 12 <= bytes.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4)));
            var dataOffset = position + 8;
            var next = checked(dataOffset + length + 4);
            if (next > bytes.Length)
            {
                throw new InvalidDataException("PNG chunk is incomplete.");
            }

            if (ChunkTypeEquals(bytes, position + 4, "IHDR"u8))
            {
                if (length != 13)
                {
                    throw new InvalidDataException("PNG IHDR is invalid.");
                }

                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset, 4)));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset + 4, 4)));
                bitDepth = bytes[dataOffset + 8];
                colorType = bytes[dataOffset + 9];
                if (bytes[dataOffset + 10] != 0 || bytes[dataOffset + 11] != 0)
                {
                    throw new InvalidDataException("PNG compression or filter method is unsupported.");
                }

                interlace = bytes[dataOffset + 12];
            }
            else if (ChunkTypeEquals(bytes, position + 4, "IDAT"u8))
            {
                idat.Write(bytes, dataOffset, length);
            }
            else if (ChunkTypeEquals(bytes, position + 4, "IEND"u8))
            {
                break;
            }

            position = next;
        }

        if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0 ||
            colorType is not (0 or 2 or 6))
        {
            throw new InvalidDataException(
                "Only non-interlaced 8-bit grayscale, RGB and RGBA PNG files are supported.");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            6 => 4,
            _ => 0
        };
        var rowBytes = checked(width * channels);
        var expectedBytes = checked((rowBytes + 1) * height);
        var inflated = new byte[expectedBytes];
        idat.Position = 0;
        await using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            await zlib.ReadExactlyAsync(inflated, cancellationToken);
        }

        var raw = new byte[rowBytes * height];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var inputOffset = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = inflated[inputOffset++];
            Array.Copy(inflated, inputOffset, current, 0, rowBytes);
            inputOffset += rowBytes;
            Unfilter(current, previous, filter, channels);
            Array.Copy(current, 0, raw, y * rowBytes, rowBytes);
            (current, previous) = (previous, current);
        }

        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = x * channels;
                pixels[(y * width) + x] = colorType switch
                {
                    0 => raw[(y * rowBytes) + offset],
                    2 or 6 => ToGray(
                        raw[(y * rowBytes) + offset],
                        raw[(y * rowBytes) + offset + 1],
                        raw[(y * rowBytes) + offset + 2]),
                    _ => throw new InvalidDataException("Unsupported PNG color type.")
                };
            }
        }

        return new Image2D(width, height, pixels);
    }

    private static void Unfilter(byte[] row, byte[] previous, byte filter, int bytesPerPixel)
    {
        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= bytesPerPixel ? row[index - bytesPerPixel] : (byte)0;
            var above = previous[index];
            var upperLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : (byte)0;
            row[index] = filter switch
            {
                0 => row[index],
                1 => (byte)(row[index] + left),
                2 => (byte)(row[index] + above),
                3 => (byte)(row[index] + ((left + above) / 2)),
                4 => (byte)(row[index] + Paeth(left, above, upperLeft)),
                _ => throw new InvalidDataException($"PNG filter type {filter} is unsupported.")
            };
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte ToGray(byte red, byte green, byte blue) =>
        (byte)Math.Clamp(Math.Round((0.299 * red) + (0.587 * green) + (0.114 * blue)), 0, 255);

    private static bool ChunkTypeEquals(byte[] bytes, int offset, ReadOnlySpan<byte> expected)
    {
        if (offset < 0 || offset + expected.Length > bytes.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (bytes[offset + index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }
}
