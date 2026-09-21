namespace BoMaxing.Core.Imaging;

public readonly record struct Rect2D(int X, int Y, int Width, int Height)
{
    public int Area => Width * Height;
}

public sealed class Region2D
{
    private readonly bool[] _mask;

    public Region2D(int width, int height, ReadOnlySpan<bool> mask)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (mask.Length != width * height)
        {
            throw new ArgumentException(
                "Mask count must equal width multiplied by height.",
                nameof(mask));
        }

        Width = width;
        Height = height;
        _mask = mask.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<bool> Mask => _mask;
    public int Area => _mask.Count(value => value);

    public Rect2D BoundingBox
    {
        get
        {
            var minX = Width;
            var minY = Height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    if (!IsSet(x, y))
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            return maxX < 0
                ? new Rect2D(0, 0, 0, 0)
                : new Rect2D(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
    }

    public (double X, double Y) Centroid
    {
        get
        {
            var area = Area;
            if (area == 0)
            {
                return (double.NaN, double.NaN);
            }

            var sumX = 0d;
            var sumY = 0d;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    if (!IsSet(x, y))
                    {
                        continue;
                    }

                    sumX += x;
                    sumY += y;
                }
            }

            return (sumX / area, sumY / area);
        }
    }

    public bool IsSet(int x, int y)
    {
        if (x < 0 || x >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return _mask[(y * Width) + x];
    }

    public static Region2D Threshold(Image2D image, byte minimum, byte maximum)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (minimum > maximum)
        {
            throw new ArgumentException("Minimum threshold cannot exceed maximum threshold.");
        }

        var mask = new bool[image.Width * image.Height];
        var pixels = image.Pixels.Span;
        for (var index = 0; index < pixels.Length; index++)
        {
            mask[index] = pixels[index] >= minimum && pixels[index] <= maximum;
        }

        return new Region2D(image.Width, image.Height, mask);
    }
}

