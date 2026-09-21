using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Runtime;

public enum DataType
{
    Unknown,
    Boolean,
    Number,
    Text,
    Image2D,
    DepthMap,
    PointCloud,
    Bytes,
    Region2D,
    Geometry,
    Pose,
    Measurement,
    Record
}

public sealed record DataValue(DataType Type, object? Value)
{
    public static DataValue Boolean(bool value) => new(DataType.Boolean, value);
    public static DataValue Number(double value) => new(DataType.Number, value);
    public static DataValue Text(string value) => new(DataType.Text, value);
    public static DataValue Image(Image2D value) => new(DataType.Image2D, value);
    public static DataValue Depth(DepthMap value) => new(DataType.DepthMap, value);
    public static DataValue PointCloud(PointCloud3D value) =>
        new(DataType.PointCloud, value);
    public static DataValue Region(Region2D value) => new(DataType.Region2D, value);
    public static DataValue Measurement(Measurement value) =>
        new(DataType.Measurement, value);
    public static DataValue Bytes(byte[] value) => new(DataType.Bytes, value);
    public static DataValue Record(object value) => new(DataType.Record, value);

    public bool TryGetNumber(out double value)
    {
        switch (Value)
        {
            case double doubleValue:
                value = doubleValue;
                return true;
            case float floatValue:
                value = floatValue;
                return true;
            case decimal decimalValue:
                value = (double)decimalValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case long longValue:
                value = longValue;
                return true;
            default:
                value = default;
                return false;
        }
    }
}
