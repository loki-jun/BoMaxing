using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BoMaxing.Core.Communication;
using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;

namespace BoMaxing.Core.Tests;

public sealed class CommunicationTests
{
    [Fact]
    public void Protocol_frame_template_roundtrips_and_validates_crc()
    {
        var template = new ProtocolFrameTemplate(
            [0xAA],
            [0x55],
            ProtocolChecksum.Crc16Modbus,
            minimumPayloadLength: 1,
            maximumPayloadLength: 8);
        var frame = ProtocolFrameCodec.Encode(template, [1, 2, 3]);

        Assert.Equal([1, 2, 3], ProtocolFrameCodec.Decode(template, frame));
        frame[2] ^= 0x10;
        Assert.Throws<InvalidDataException>(() => ProtocolFrameCodec.Decode(template, frame));
    }

    [Fact]
    public void Protocol_frame_template_rejects_invalid_length()
    {
        var template = new ProtocolFrameTemplate([0x02], [0x03], minimumPayloadLength: 2);

        Assert.Throws<InvalidDataException>(() => ProtocolFrameCodec.Encode(template, [1]));
    }

    [Fact]
    public void Protocol_frame_template_encodes_xor8_as_one_byte()
    {
        var template = new ProtocolFrameTemplate([0x02], [0x03], ProtocolChecksum.Xor8);
        var frame = ProtocolFrameCodec.Encode(template, [0x10, 0x20]);

        Assert.Equal(5, frame.Length);
        Assert.Equal([0x10, 0x20], ProtocolFrameCodec.Decode(template, frame));
    }

    [Fact]
    public async Task Industrial_protocol_contracts_support_opcua_plc_and_robot_operations()
    {
        await using var session = new InMemoryIndustrialProtocolSession();
        await session.ConnectAsync();
        await session.WriteAsync("ns=2;s=Ready", IndustrialValueType.Boolean, true);

        var values = await session.ReadManyAsync(["ns=2;s=Ready", "ns=2;s=Missing"]);
        Assert.Equal(true, values[0].Value);
        Assert.Equal("BadNotFound", values[1].Quality);
        Assert.True(await session.ExecuteCommandAsync("reset"));
        Assert.True(await session.StartProgramAsync("PickAndPlace"));
        Assert.True(await session.StopProgramAsync());
    }

    [Fact]
    public async Task Industrial_protocol_tools_execute_through_workflow_services()
    {
        var runtime = BoMaxing.Application.BoMaxingRuntime.CreateDefault();
        await runtime.IndustrialProtocols.RegisterAndConnectAsync(
            "line",
            new InMemoryIndustrialProtocolSession());

        var workflow = new WorkflowDefinition { Name = "Industrial IO" };
        var write = workflow.AddNode(BuiltInTools.IndustrialWrite, "Write");
        write.Parameters["sessionId"] = JsonSerializer.SerializeToElement("line");
        write.Parameters["address"] = JsonSerializer.SerializeToElement("Ready");
        write.Parameters["valueType"] = JsonSerializer.SerializeToElement("Boolean");
        write.Parameters["value"] = JsonSerializer.SerializeToElement(true);
        var read = workflow.AddNode(BuiltInTools.IndustrialRead, "Read");
        read.Parameters["sessionId"] = JsonSerializer.SerializeToElement("line");
        read.Parameters["address"] = JsonSerializer.SerializeToElement("Ready");

        var result = await runtime.WorkflowEngine.ExecuteAsync(workflow);

        Assert.True(result.Succeeded);
        var readValue = Assert.IsType<IndustrialVariable>(
            result.Outputs[$"{read.Id}.value"].Value);
        Assert.Equal(true, readValue.Value);
    }

    [Fact]
    public async Task Retrying_plc_command_reconnects_and_raises_retry_event()
    {
        await using var inner = new FlakyPlcSession();
        await using var session = new RetryingPlcSession(
            inner,
            new CommunicationRetryOptions(
                maxAttempts: 3,
                initialDelay: TimeSpan.Zero,
                maximumDelay: TimeSpan.Zero));
        var retries = new List<CommunicationRetryAttempt>();
        session.RetryAttempted += (_, attempt) => retries.Add(attempt);

        var succeeded = await session.ExecuteCommandAsync("reset");

        Assert.True(succeeded);
        Assert.Equal(2, inner.CommandAttempts);
        Assert.Equal(2, inner.ConnectCount);
        Assert.Equal(1, inner.DisconnectCount);
        Assert.Single(retries);
        Assert.Equal(1, retries[0].Attempt);
        Assert.Equal(3, retries[0].MaxAttempts);
    }
    [Fact]
    public async Task Tcp_channel_connects_sends_and_receives()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var serverTask = EchoOnceAsync(listener);
        await using var channel = new TcpClientChannel(
            new TcpConnectionOptions(
                IPAddress.Loopback.ToString(),
                endpoint.Port,
                connectTimeout: TimeSpan.FromSeconds(2)));

        await channel.ConnectAsync();
        await channel.SendAsync(Encoding.ASCII.GetBytes("hello"));
        var response = await channel.ReceiveExactAsync(5);

        Assert.Equal("hello", Encoding.ASCII.GetString(response));
        Assert.Equal(ConnectionState.Connected, channel.State);
        await channel.DisconnectAsync();
        await serverTask;
    }

    [Fact]
    public async Task Modbus_tcp_reads_holding_registers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var serverTask = ModbusReadOnceAsync(listener);
        await using var channel = new TcpClientChannel(
            new TcpConnectionOptions(
                IPAddress.Loopback.ToString(),
                endpoint.Port,
                connectTimeout: TimeSpan.FromSeconds(2)));
        await using var modbus = new ModbusTcpClient(channel);
        await modbus.ConnectAsync();

        var registers = await modbus.ReadHoldingRegistersAsync(0x0010, 2);

        Assert.Equal(new ushort[] { 10, 20 }, registers);
        await modbus.DisconnectAsync();
        await serverTask;
    }

    [Fact]
    public async Task Modbus_tcp_writes_single_register()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var serverTask = ModbusWriteOnceAsync(listener);
        await using var channel = new TcpClientChannel(
            new TcpConnectionOptions(
                IPAddress.Loopback.ToString(),
                endpoint.Port,
                connectTimeout: TimeSpan.FromSeconds(2)));
        await using var modbus = new ModbusTcpClient(channel);
        await modbus.ConnectAsync();

        var value = await modbus.WriteSingleRegisterAsync(0x0020, 1234);

        Assert.Equal((ushort)1234, value);
        await modbus.DisconnectAsync();
        await serverTask;
    }

    [Fact]
    public async Task Serial_adapter_exposes_connection_lifecycle()
    {
        await using var channel = new SerialPortChannelAdapter(
            new SerialPortSettings("TEST"),
            static (_, _) => Task.FromResult<Stream>(new MemoryStream()));

        await channel.ConnectAsync();
        Assert.Equal(ConnectionState.Connected, channel.State);
        await channel.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    [Fact]
    public async Task Channel_registry_manages_registered_channels()
    {
        await using var registry = new CommunicationChannelRegistry();
        var channel = new SerialPortChannelAdapter(
            new SerialPortSettings("TEST"),
            static (_, _) => Task.FromResult<Stream>(new MemoryStream()));

        registry.Register("loopback", channel);
        await registry.ConnectAsync("loopback");

        Assert.Equal(["loopback"], registry.GetIds());
        Assert.Same(channel, registry.Get("loopback"));
        Assert.Equal(ConnectionState.Connected, channel.State);

        await registry.DisconnectAsync("loopback");
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    private static async Task EchoOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[5];
        await stream.ReadExactlyAsync(buffer);
        await stream.WriteAsync(buffer);
    }

    private static async Task ModbusReadOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var request = new byte[12];
        await stream.ReadExactlyAsync(request);

        var transactionId = BinaryPrimitives.ReadUInt16BigEndian(request);
        var unitId = request[6];
        var response = new byte[13];
        BinaryPrimitives.WriteUInt16BigEndian(response, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), 7);
        response[6] = unitId;
        response[7] = 3;
        response[8] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), 10);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(11), 20);
        await stream.WriteAsync(response);
    }

    private static async Task ModbusWriteOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var request = new byte[12];
        await stream.ReadExactlyAsync(request);

        Assert.Equal((ushort)0x0020, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)));
        Assert.Equal((ushort)1234, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)));
        request.AsSpan(0, 12).CopyTo(request);
        await stream.WriteAsync(request);
    }

    private sealed class FlakyPlcSession : IPlcSession
    {
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public int CommandAttempts { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            State = ConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisconnectCount++;
            State = ConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<IndustrialVariable> ReadAsync(
            string address,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteAsync(
            string address,
            IndustrialValueType valueType,
            object? value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExecuteCommandAsync(
            string command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandAttempts++;
            if (CommandAttempts == 1)
            {
                throw new CommunicationException("simulated connection drop");
            }

            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
