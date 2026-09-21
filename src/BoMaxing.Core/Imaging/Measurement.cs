namespace BoMaxing.Core.Imaging;

public sealed record Measurement(
    string Name,
    double Value,
    string Unit,
    double? LowerLimit = null,
    double? UpperLimit = null)
{
    public bool IsWithinTolerance =>
        (!LowerLimit.HasValue || Value >= LowerLimit.Value) &&
        (!UpperLimit.HasValue || Value <= UpperLimit.Value);
}

