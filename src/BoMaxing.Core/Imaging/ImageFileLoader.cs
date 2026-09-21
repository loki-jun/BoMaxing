using System.Buffers.Binary;

namespace BoMaxing.Core.Imaging;

public static class ImageFileLoader
{
    private static readonly ImageDecoderRegistry Registry = CreateRegistry();

    public static Task<Image2D> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Registry.DecodeAsync(filePath, cancellationToken);
    }

    private static ImageDecoderRegistry CreateRegistry()
    {
        var registry = new ImageDecoderRegistry();
        registry.Register(new PgmImageDecoder());
        registry.Register(new BmpImageDecoder());
        registry.Register(new PngImageDecoder());
        registry.Register(new TiffImageDecoder());
        return registry;
    }

    internal static async Task<Image2D> LoadBmpAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
        {
            throw new InvalidDataException("BMP header is invalid.");
        }

        var pixelOffset = ReadInt32(bytes, 10, "pixel offset");
        var dibSize = ReadInt32(bytes, 14, "DIB header size");
        if (dibSize < 40 || bytes.Length < 14 + dibSize)
        {
            throw new InvalidDataException("Only BITMAPINFOHEADER-compatible BMP files are supported.");
        }

        var width = ReadInt32(bytes, 18, "width");
        var signedHeight = ReadInt32(bytes, 22, "height");
        var planes = ReadUInt16(bytes, 26, "planes");
        var bitsPerPixel = ReadUInt16(bytes, 28, "bits per pixel");
        var compression = ReadUInt32(bytes, 30, "compression");
        if (width <= 0 || signedHeight == 0 || planes != 1 || compression != 0)
        {
            throw new InvalidDataException("BMP dimensions, planes or compression are unsupported.");
        }

        if (bitsPerPixel is not (8 or 24 or 32))
        {
            throw new InvalidDataException("Only 8, 24 and 32 bit BMP files are supported.");
        }

        var height = Math.Abs(signedHeight);
        var bytesPerPixel = bitsPerPixel / 8;
        var rowStride = checked(((width * bitsPerPixel + 31) / 32) * 4);
        var requiredLength = checked(pixelOffset + (rowStride * height));
        if (pixelOffset < 0 || requiredLength > bytes.Length)
        {
            throw new InvalidDataException("BMP pixel data is incomplete.");
        }

        var palette = bitsPerPixel == 8
            ? ReadPalette(bytes, 14 + dibSize, pixelOffset)
            : null;
        var pixels = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = signedHeight > 0 ? height - 1 - y : y;
            var rowOffset = pixelOffset + (sourceY * rowStride);
            for (var x = 0; x < width; x++)
            {
                var sourceOffset = rowOffset + (x * bytesPerPixel);
                pixels[(y * width) + x] = bitsPerPixel switch
                {
                    8 => palette is null
                        ? bytes[sourceOffset]
                        : palette[bytes[sourceOffset]],
                    24 => ToGray(bytes[sourceOffset + 2], bytes[sourceOffset + 1], bytes[sourceOffset]),
                    32 => ToGray(bytes[sourceOffset + 2], bytes[sourceOffset + 1], bytes[sourceOffset]),
                    _ => throw new InvalidDataException("Unsupported BMP pixel format.")
                };
            }
        }

        return new Image2D(width, height, pixels);
    }

    private static byte[]? ReadPalette(byte[] bytes, int paletteOffset, int pixelOffset)
    {
        if (paletteOffset >= pixelOffset)
        {
            return null;
        }

        var entryCount = Math.Min(256, (pixelOffset - paletteOffset) / 4);
        if (entryCount <= 0)
        {
            return null;
        }

        var palette = new byte[256];
        for (var index = 0; index < entryCount; index++)
        {
            var offset = paletteOffset + (index * 4);
            palette[index] = ToGray(bytes[offset + 2], bytes[offset + 1], bytes[offset]);
        }

        return palette;
    }

    private static byte ToGray(byte red, byte green, byte blue) =>
        (byte)Math.Clamp(Math.Round((0.299 * red) + (0.587 * green) + (0.114 * blue)), 0, 255);

    private static int ReadInt32(byte[] bytes, int offset, string fieldName)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            throw new InvalidDataException($"BMP {fieldName} is missing.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private static uint ReadUInt32(byte[] bytes, int offset, string fieldName)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            throw new InvalidDataException($"BMP {fieldName} is missing.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private static ushort ReadUInt16(byte[] bytes, int offset, string fieldName)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
        {
            throw new InvalidDataException($"BMP {fieldName} is missing.");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }
}
