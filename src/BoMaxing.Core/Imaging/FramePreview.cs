namespace BoMaxing.Core.Imaging;

public sealed record ImagePreviewSummary(
    int Width,
    int Height,
    byte Minimum,
    byte Maximum,
    double Mean);

public sealed record DepthPreviewSummary(
    int Width,
    int Height,
    double Minimum,
    double Maximum,
    double Mean,
    string Unit);

public sealed record PointCloudPreviewSummary(
    int PointCount,
    PointCloudBounds Bounds,
    CoordinateSystem CoordinateSystem);

public static class FramePreview
{
    public static ImagePreviewSummary Summarize(Image2D image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pixels = image.Pixels.Span;
        if (pixels.IsEmpty)
        {
            return new ImagePreviewSummary(image.Width, image.Height, 0, 0, 0);
        }

        var minimum = byte.MaxValue;
        var maximum = byte.MinValue;
        var sum = 0d;
        foreach (var pixel in pixels)
        {
            minimum = Math.Min(minimum, pixel);
            maximum = Math.Max(maximum, pixel);
            sum += pixel;
        }

        return new ImagePreviewSummary(
            image.Width,
            image.Height,
            minimum,
            maximum,
            sum / pixels.Length);
    }

    public static DepthPreviewSummary Summarize(DepthMap depthMap)
    {
        ArgumentNullException.ThrowIfNull(depthMap);
        var values = depthMap.Values.Span;
        if (values.IsEmpty)
        {
            return new DepthPreviewSummary(
                depthMap.Width,
                depthMap.Height,
                0,
                0,
                0,
                "unit");
        }

        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        var sum = 0d;
        foreach (var value in values)
        {
            var scaled = value * depthMap.UnitScale;
            minimum = Math.Min(minimum, scaled);
            maximum = Math.Max(maximum, scaled);
            sum += scaled;
        }

        return new DepthPreviewSummary(
            depthMap.Width,
            depthMap.Height,
            minimum,
            maximum,
            sum / values.Length,
            "unit");
    }

    public static PointCloudPreviewSummary Summarize(PointCloud3D pointCloud)
    {
        ArgumentNullException.ThrowIfNull(pointCloud);
        return new PointCloudPreviewSummary(
            pointCloud.Count,
            PointCloudProcessing.GetBounds(pointCloud),
            pointCloud.CoordinateSystem);
    }
}
