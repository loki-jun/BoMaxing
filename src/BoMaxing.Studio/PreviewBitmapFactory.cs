using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BoMaxing.Core.Imaging;

namespace BoMaxing.Studio;

public static class PreviewBitmapFactory
{
    public static Bitmap FromImage(Image2D image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        for (var index = 0; index < image.Pixels.Length; index++)
        {
            var gray = image.Pixels.Span[index];
            var target = index * 4;
            pixels[target] = gray;
            pixels[target + 1] = gray;
            pixels[target + 2] = gray;
            pixels[target + 3] = byte.MaxValue;
        }

        return CreateBitmap(image.Width, image.Height, pixels);
    }

    public static Bitmap FromRegion(Region2D region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var pixels = new byte[checked(region.Width * region.Height * 4)];
        for (var index = 0; index < region.Mask.Length; index++)
        {
            var value = region.Mask.Span[index] ? byte.MaxValue : (byte)18;
            var target = index * 4;
            pixels[target] = 40;
            pixels[target + 1] = value;
            pixels[target + 2] = value;
            pixels[target + 3] = byte.MaxValue;
        }

        return CreateBitmap(region.Width, region.Height, pixels);
    }

    public static Bitmap FromDepth(DepthMap depth)
    {
        ArgumentNullException.ThrowIfNull(depth);
        var values = depth.Values.Span;
        ushort minimum = ushort.MaxValue;
        ushort maximum = ushort.MinValue;
        foreach (var value in values)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        var range = Math.Max(1, maximum - minimum);
        var pixels = new byte[checked(depth.Width * depth.Height * 4)];
        for (var index = 0; index < values.Length; index++)
        {
            var gray = (byte)Math.Clamp(
                ((values[index] - minimum) * 255) / range,
                0,
                255);
            var target = index * 4;
            pixels[target] = gray;
            pixels[target + 1] = gray;
            pixels[target + 2] = gray;
            pixels[target + 3] = byte.MaxValue;
        }

        return CreateBitmap(depth.Width, depth.Height, pixels);
    }

    private static Bitmap CreateBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888,
            AlphaFormat.Opaque);
        using var framebuffer = bitmap.Lock();
        var rowBytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(
                pixels,
                row * rowBytes,
                framebuffer.Address + (row * framebuffer.RowBytes),
                rowBytes);
        }

        return bitmap;
    }
}
