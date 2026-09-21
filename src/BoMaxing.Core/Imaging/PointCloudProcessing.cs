namespace BoMaxing.Core.Imaging;

public readonly record struct PointCloudBounds(
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ);

public static class PointCloudProcessing
{
    public static PointCloudBounds GetBounds(PointCloud3D pointCloud)
    {
        ArgumentNullException.ThrowIfNull(pointCloud);
        var first = pointCloud.GetPoint(0);
        var minX = first.X;
        var minY = first.Y;
        var minZ = first.Z;
        var maxX = first.X;
        var maxY = first.Y;
        var maxZ = first.Z;
        for (var index = 1; index < pointCloud.Count; index++)
        {
            var point = pointCloud.GetPoint(index);
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            minZ = Math.Min(minZ, point.Z);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
            maxZ = Math.Max(maxZ, point.Z);
        }

        return new PointCloudBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    public static PointCloud3D FilterByZ(
        PointCloud3D pointCloud,
        float minimum,
        float maximum)
    {
        ArgumentNullException.ThrowIfNull(pointCloud);
        if (minimum > maximum)
        {
            throw new ArgumentException("Minimum Z cannot exceed maximum Z.");
        }

        var positions = new List<float>();
        var intensities = pointCloud.Intensities.IsEmpty ? null : new List<float>();
        for (var index = 0; index < pointCloud.Count; index++)
        {
            var point = pointCloud.GetPoint(index);
            if (point.Z < minimum || point.Z > maximum)
            {
                continue;
            }

            positions.Add(point.X);
            positions.Add(point.Y);
            positions.Add(point.Z);
            if (intensities is not null)
            {
                intensities.Add(pointCloud.Intensities.Span[index]);
            }
        }

        if (positions.Count == 0)
        {
            throw new InvalidDataException("Point cloud filter produced no points.");
        }

        return new PointCloud3D(
            positions.ToArray(),
            intensities?.ToArray() ?? [],
            pointCloud.CoordinateSystem);
    }

    public static Region2D Threshold(
        DepthMap depthMap,
        double minimum,
        double maximum)
    {
        ArgumentNullException.ThrowIfNull(depthMap);
        if (minimum > maximum)
        {
            throw new ArgumentException("Minimum depth cannot exceed maximum depth.");
        }

        var mask = new bool[depthMap.Width * depthMap.Height];
        var values = depthMap.Values.Span;
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index] * depthMap.UnitScale;
            mask[index] = value >= minimum && value <= maximum;
        }

        return new Region2D(depthMap.Width, depthMap.Height, mask);
    }
}
