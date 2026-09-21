using System.Buffers.Binary;

namespace BoMaxing.Core.Imaging;

public enum ImageSampleType
{
    Gray8,
    Gray16,
    Float32
}

public sealed class Image2D
{
    private readonly byte[] _rawPixels;
    private readonly byte[] _displayPixels;

    public Image2D(int width, int height, ReadOnlySpan<byte> pixels)
        : this(width, height, pixels, ImageSampleType.Gray8)
    {
    }

    private Image2D(
        int width,
        int height,
        ReadOnlySpan<byte> pixels,
        ImageSampleType sampleType)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var bytesPerPixel = GetBytesPerPixel(sampleType);
        if (pixels.Length != checked(width * height * bytesPerPixel))
        {
            throw new ArgumentException(
                "Pixel data size must equal width multiplied by height multiplied by bytes per pixel.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        SampleType = sampleType;
        _rawPixels = pixels.ToArray();
        _displayPixels = CreateDisplayPixels(width, height, _rawPixels, sampleType);
    }

    public int Width { get; }
    public int Height { get; }
    public ImageSampleType SampleType { get; }
    public int BytesPerPixel => GetBytesPerPixel(SampleType);
    public int Stride => Width;
    public int StrideBytes => checked(Width * BytesPerPixel);
    public ReadOnlyMemory<byte> Pixels => _displayPixels;
    public ReadOnlyMemory<byte> RawPixels => _rawPixels;

    public byte GetPixel(int x, int y)
    {
        ValidateCoordinate(x, y);
        return _displayPixels[(y * Stride) + x];
    }

    public ushort GetUInt16(int x, int y)
    {
        ValidateCoordinate(x, y);
        if (SampleType != ImageSampleType.Gray16)
        {
            throw new InvalidOperationException("The image does not contain Gray16 samples.");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(
            _rawPixels.AsSpan((y * StrideBytes) + (x * 2), 2));
    }

    public float GetFloat32(int x, int y)
    {
        ValidateCoordinate(x, y);
        if (SampleType != ImageSampleType.Float32)
        {
            throw new InvalidOperationException("The image does not contain Float32 samples.");
        }

        return BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(
                _rawPixels.AsSpan((y * StrideBytes) + (x * 4), 4)));
    }

    public Image2D Clone() => new(Width, Height, _rawPixels, SampleType);

    public Image2D Crop(Rect2D rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0 ||
            rectangle.X < 0 || rectangle.Y < 0 ||
            rectangle.X + rectangle.Width > Width ||
            rectangle.Y + rectangle.Height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rectangle));
        }

        var pixels = new byte[checked(rectangle.Width * rectangle.Height * BytesPerPixel)];
        for (var y = 0; y < rectangle.Height; y++)
        {
            _rawPixels.AsSpan(
                    ((rectangle.Y + y) * StrideBytes) + (rectangle.X * BytesPerPixel),
                    rectangle.Width * BytesPerPixel)
                .CopyTo(pixels.AsSpan(y * rectangle.Width * BytesPerPixel));
        }

        return new Image2D(rectangle.Width, rectangle.Height, pixels, SampleType);
    }

    public static Image2D From8Bit(int width, int height, byte[] pixels) =>
        new(width, height, pixels);

    public static Image2D From16Bit(int width, int height, ReadOnlySpan<ushort> pixels)
    {
        if (pixels.Length != checked(width * height))
        {
            throw new ArgumentException(
                "Pixel count must equal width multiplied by height.",
                nameof(pixels));
        }

        var raw = new byte[checked(pixels.Length * sizeof(ushort))];
        for (var index = 0; index < pixels.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                raw.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                pixels[index]);
        }

        return new Image2D(width, height, raw, ImageSampleType.Gray16);
    }

    public static Image2D FromFloat32(int width, int height, ReadOnlySpan<float> pixels)
    {
        if (pixels.Length != checked(width * height))
        {
            throw new ArgumentException(
                "Pixel count must equal width multiplied by height.",
                nameof(pixels));
        }

        var raw = new byte[checked(pixels.Length * sizeof(float))];
        for (var index = 0; index < pixels.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                raw.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(pixels[index]));
        }

        return new Image2D(width, height, raw, ImageSampleType.Float32);
    }

    private static int GetBytesPerPixel(ImageSampleType sampleType) => sampleType switch
    {
        ImageSampleType.Gray8 => 1,
        ImageSampleType.Gray16 => 2,
        ImageSampleType.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleType))
    };

    private static byte[] CreateDisplayPixels(
        int width,
        int height,
        byte[] rawPixels,
        ImageSampleType sampleType)
    {
        if (sampleType == ImageSampleType.Gray8)
        {
            return rawPixels;
        }

        var display = new byte[checked(width * height)];
        for (var index = 0; index < display.Length; index++)
        {
            display[index] = sampleType switch
            {
                ImageSampleType.Gray16 => (byte)Math.Clamp(
                    Math.Round(
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            rawPixels.AsSpan(index * 2, 2)) / 257d),
                    0,
                    255),
                ImageSampleType.Float32 => ToDisplayByte(
                    BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(
                            rawPixels.AsSpan(index * 4, 4)))),
                _ => throw new ArgumentOutOfRangeException(nameof(sampleType))
            };
        }

        return display;
    }

    private static byte ToDisplayByte(float value)
    {
        if (float.IsNaN(value) || float.IsNegativeInfinity(value))
        {
            return 0;
        }

        if (float.IsPositiveInfinity(value))
        {
            return byte.MaxValue;
        }

        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private void ValidateCoordinate(int x, int y)
    {
        if (x < 0 || x >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }
    }
}
