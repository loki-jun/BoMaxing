namespace BoMaxing.Core.Imaging;

public sealed record ConnectedComponent(
    int Label,
    int Area,
    Rect2D BoundingBox,
    double CenterX,
    double CenterY);

public sealed record ConnectedComponentsResult(
    IReadOnlyList<ConnectedComponent> Components);
