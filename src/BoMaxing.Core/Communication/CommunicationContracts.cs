namespace BoMaxing.Core.Communication;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted
}

public sealed record ConnectionStateChanged(
    ConnectionState Previous,
    ConnectionState Current,
    Exception? Exception = null);

public interface IByteChannel : IAsyncDisposable
{
    ConnectionState State { get; }
    event EventHandler<ConnectionStateChanged>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
    ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);
    ValueTask<byte[]> ReceiveExactAsync(
        int byteCount,
        CancellationToken cancellationToken = default);
}

public sealed record SerialPortSettings
{
    public SerialPortSettings(
        string portName,
        int baudRate = 115200,
        int dataBits = 8,
        SerialParity parity = SerialParity.None,
        SerialStopBits stopBits = SerialStopBits.One)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        }

        if (dataBits is < 5 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBits));
        }

        PortName = portName;
        BaudRate = baudRate;
        DataBits = dataBits;
        Parity = parity;
        StopBits = stopBits;
    }

    public string PortName { get; }
    public int BaudRate { get; }
    public int DataBits { get; }
    public SerialParity Parity { get; }
    public SerialStopBits StopBits { get; }
}

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two
}

public interface ISerialPortChannel : IByteChannel
{
    SerialPortSettings Settings { get; }
}

public class CommunicationException : IOException
{
    public CommunicationException(string message)
        : base(message)
    {
    }

    public CommunicationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
