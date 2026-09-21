namespace BoMaxing.Core.Imaging;

public enum MorphologyOperation
{
    Dilate,
    Erode
}

public static class RegionProcessing
{
    public static Region2D Morphology(
        Region2D source,
        MorphologyOperation operation,
        int iterations = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (iterations is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        var current = source.Mask.ToArray();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var next = new bool[current.Length];
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var value = operation == MorphologyOperation.Dilate ? false : true;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var sampleX = x + dx;
                            var sampleY = y + dy;
                            var sample = sampleX >= 0 && sampleX < source.Width &&
                                         sampleY >= 0 && sampleY < source.Height &&
                                         current[(sampleY * source.Width) + sampleX];
                            value = operation == MorphologyOperation.Dilate
                                ? value || sample
                                : value && sample;
                        }
                    }

                    next[(y * source.Width) + x] = value;
                }
            }

            current = next;
        }

        return new Region2D(source.Width, source.Height, current);
    }

    public static ConnectedComponentsResult FindConnectedComponents(
        Region2D source,
        int minimumArea = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (minimumArea < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumArea));
        }

        var visited = new bool[source.Width * source.Height];
        var components = new List<ConnectedComponent>();
        var label = 0;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var start = (y * source.Width) + x;
                if (visited[start] || !source.IsSet(x, y))
                {
                    continue;
                }

                label++;
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((x, y));
                visited[start] = true;
                var points = new List<(int X, int Y)>();
                while (queue.Count > 0)
                {
                    var point = queue.Dequeue();
                    points.Add(point);
                    foreach (var neighbor in Neighbors(point.X, point.Y))
                    {
                        if (neighbor.X < 0 || neighbor.X >= source.Width ||
                            neighbor.Y < 0 || neighbor.Y >= source.Height)
                        {
                            continue;
                        }

                        var index = (neighbor.Y * source.Width) + neighbor.X;
                        if (!visited[index] && source.IsSet(neighbor.X, neighbor.Y))
                        {
                            visited[index] = true;
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                if (points.Count < minimumArea)
                {
                    continue;
                }

                var minX = points.Min(point => point.X);
                var minY = points.Min(point => point.Y);
                var maxX = points.Max(point => point.X);
                var maxY = points.Max(point => point.Y);
                components.Add(new ConnectedComponent(
                    label,
                    points.Count,
                    new Rect2D(minX, minY, maxX - minX + 1, maxY - minY + 1),
                    points.Average(point => point.X),
                    points.Average(point => point.Y)));
            }
        }

        return new ConnectedComponentsResult(components);
    }

    private static IEnumerable<(int X, int Y)> Neighbors(int x, int y)
    {
        yield return (x - 1, y);
        yield return (x + 1, y);
        yield return (x, y - 1);
        yield return (x, y + 1);
    }
}
