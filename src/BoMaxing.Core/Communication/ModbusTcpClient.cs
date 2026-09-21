using System.Buffers.Binary;

namespace BoMaxing.Core.Communication;

public sealed class ModbusException : CommunicationException
{
    public ModbusException(
        byte functionCode,
        byte exceptionCode,
        string message)
        : base(message)
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    public byte FunctionCode { get; }
    public byte ExceptionCode { get; }
}

public sealed class ModbusTcpClient : IAsyncDisposable
{
    private readonly TcpClientChannel _channel;
    private int _transactionId;
    private bool _disposed;

    public ModbusTcpClient(TcpClientChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    public ConnectionState State => _channel.State;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _channel.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _channel.DisconnectAsync(cancellationToken);

    public async Task<ushort[]> ReadHoldingRegistersAsync(
        ushort startAddress,
        ushort quantity,
        byte unitId = 1,
        CancellationToken cancellationToken = default)
    {
        if (quantity is < 1 or > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        var requestPayload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(requestPayload, startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(requestPayload.AsSpan(2), quantity);
        var readResponse = await SendRequestAsync(
            unitId,
            functionCode: 3,
            requestPayload,
            cancellationToken);
        var expectedByteCount = quantity * 2;
        if (readResponse.Payload.Length < 1 ||
            readResponse.Payload[0] != expectedByteCount ||
            readResponse.Payload.Length != expectedByteCount + 1)
        {
            throw new CommunicationException("Modbus response byte count is invalid.");
        }

        var registers = new ushort[quantity];
        for (var index = 0; index < registers.Length; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(
                readResponse.Payload.AsSpan(1 + (index * 2), 2));
        }

        return registers;
    }

    public async Task<ushort> WriteSingleRegisterAsync(
        ushort address,
        ushort value,
        byte unitId = 1,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload, address);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), value);
        var response = await SendRequestAsync(
            unitId,
            functionCode: 6,
            payload,
            cancellationToken);
        if (response.Payload.Length != 4)
        {
            throw new CommunicationException("Modbus write response length is invalid.");
        }

        var responseAddress = BinaryPrimitives.ReadUInt16BigEndian(response.Payload);
        var responseValue = BinaryPrimitives.ReadUInt16BigEndian(response.Payload[2..]);
        if (responseAddress != address || responseValue != value)
        {
            throw new CommunicationException("Modbus write response does not match the request.");
        }

        return responseValue;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _channel.DisposeAsync();
    }

    private async Task<ModbusResponse> SendRequestAsync(
        byte unitId,
        byte functionCode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var transactionId = unchecked((ushort)Interlocked.Increment(ref _transactionId));
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(2 + payload.Length));
        frame[6] = unitId;
        frame[7] = functionCode;
        payload.Span.CopyTo(frame.AsSpan(8));

        await _channel.SendAsync(frame, cancellationToken);
        var header = await _channel.ReceiveExactAsync(7, cancellationToken);
        var responseTransactionId = BinaryPrimitives.ReadUInt16BigEndian(header);
        var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var responseUnitId = header[6];
        if (responseTransactionId != transactionId ||
            protocolId != 0 ||
            responseUnitId != unitId ||
            length < 2)
        {
            throw new CommunicationException("Modbus TCP response header is invalid.");
        }

        var pdu = await _channel.ReceiveExactAsync(length - 1, cancellationToken);
        var responseFunctionCode = pdu[0];
        if (responseFunctionCode == (byte)(functionCode | 0x80))
        {
            if (pdu.Length < 2)
            {
                throw new CommunicationException("Modbus exception response is incomplete.");
            }

            throw new ModbusException(
                functionCode,
                pdu[1],
                $"Modbus function {functionCode} failed with exception code {pdu[1]}.");
        }

        if (responseFunctionCode != functionCode)
        {
            throw new CommunicationException("Modbus response function code is invalid.");
        }

        return new ModbusResponse(pdu[1..]);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ModbusResponse(byte[] Payload);
}
