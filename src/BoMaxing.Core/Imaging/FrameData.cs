namespace BoMaxing.Core.Imaging;

public enum PixelFormat
{
    Gray8,
    Gray16,
    Rgb24,
    Bgr24,
    Depth16,
    Float32
}

public enum CoordinateSystem
{
    Unknown,
    Camera,
    World,
    Robot
}

public sealed record FrameMetadata(
    string DeviceId,
    long Sequence,
    DateTimeOffset Timestamp,
    int Width,
    int Height,
    int Stride,
    PixelFormat PixelFormat,
    double UnitScale = 1.0,
    CoordinateSystem CoordinateSystem = CoordinateSystem.Camera);

public sealed class ImageFrame
{
    public ImageFrame(FrameMetadata metadata, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateMetadata(metadata);

        if (data.Length < metadata.Stride * metadata.Height)
        {
            throw new ArgumentException(
                "Frame data is smaller than the declared stride and height.",
                nameof(data));
        }

        Metadata = metadata;
        Data = data;
    }

    public FrameMetadata Metadata { get; }
    public ReadOnlyMemory<byte> Data { get; }

    public ReadOnlySpan<byte> GetRow(int row)
    {
        if (row < 0 || row >= Metadata.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        return Data.Span.Slice(row * Metadata.Stride, Metadata.Stride);
    }

    private static void ValidateMetadata(FrameMetadata metadata)
    {
        if (metadata.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata.Width));
        }

        if (metadata.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata.Height));
        }

        if (metadata.Stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata.Stride));
        }
    }
}

public sealed class DepthMap
{
    private readonly ushort[] _values;

    public DepthMap(
        int width,
        int height,
        ReadOnlySpan<ushort> values,
        double unitScale = 1.0,
        CoordinateSystem coordinateSystem = CoordinateSystem.Camera)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (values.Length != width * height)
        {
            throw new ArgumentException(
                "Depth value count must equal width multiplied by height.",
                nameof(values));
        }

        Width = width;
        Height = height;
        UnitScale = unitScale;
        CoordinateSystem = coordinateSystem;
        _values = values.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public double UnitScale { get; }
    public CoordinateSystem CoordinateSystem { get; }
    public ReadOnlyMemory<ushort> Values => _values;

    public ushort GetValue(int x, int y)
    {
        ValidateCoordinate(x, y);
        return _values[(y * Width) + x];
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

public sealed class PointCloud3D
{
    private readonly float[] _positions;
    private readonly float[]? _intensities;

    public PointCloud3D(
        ReadOnlySpan<float> positions,
        ReadOnlySpan<float> intensities = default,
        CoordinateSystem coordinateSystem = CoordinateSystem.Camera)
    {
        if (positions.Length == 0 || positions.Length % 3 != 0)
        {
            throw new ArgumentException(
                "Point positions must contain three floats per point.",
                nameof(positions));
        }

        if (!intensities.IsEmpty && intensities.Length != positions.Length / 3)
        {
            throw new ArgumentException(
                "Intensity count must equal point count.",
                nameof(intensities));
        }

        _positions = positions.ToArray();
        _intensities = intensities.IsEmpty ? null : intensities.ToArray();
        CoordinateSystem = coordinateSystem;
    }

    public int Count => _positions.Length / 3;
    public CoordinateSystem CoordinateSystem { get; }
    public ReadOnlyMemory<float> Positions => _positions;
    public ReadOnlyMemory<float> Intensities => _intensities ?? [];

    public (float X, float Y, float Z) GetPoint(int index)
    {
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var offset = index * 3;
        return (_positions[offset], _positions[offset + 1], _positions[offset + 2]);
    }
}

