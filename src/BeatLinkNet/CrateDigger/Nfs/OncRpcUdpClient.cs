using System.Net;
using System.Net.Sockets;

namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// A minimal ONC RPC (RFC 5531, originally RFC 1057) client which sends call messages over UDP and
/// waits for the matching reply, retransmitting with exponential backoff if no response arrives.
/// Replaces the Remote Tea <c>OncRpcUdpClient</c> used by Deep Symmetry's crate-digger.
/// </summary>
/// <remarks>
/// <para>Calls are made with <c>AUTH_NONE</c> credentials and verifier, which is what the Remote Tea
/// client sends by default, and is all that the Pioneer players require (the Java
/// <c>FileFetcher</c> never configures any other authentication).</para>
/// <para>Calls are serialized: the client holds an internal lock for the duration of each call, so a
/// single instance may safely be shared by multiple threads, one call at a time.</para>
/// </remarks>
public sealed class OncRpcUdpClient : IDisposable
{
    /// <summary>
    /// The version of the ONC RPC protocol that we speak.
    /// </summary>
    public const uint RpcVersion = 2;

    /// <summary>
    /// The <c>msg_type</c> value identifying a call message.
    /// </summary>
    private const uint CallMessageType = 0;

    /// <summary>
    /// The <c>msg_type</c> value identifying a reply message.
    /// </summary>
    private const uint ReplyMessageType = 1;

    /// <summary>
    /// Used to allocate a distinct transaction identifier for each call, across all client instances.
    /// </summary>
    private static int _nextTransactionId = Random.Shared.Next();

    private readonly Socket _socket;
    private readonly byte[] _receiveBuffer = new byte[ushort.MaxValue];
    private readonly Lock _lock = new();
    private int _retransmitTimeout = 250;
    private int _timeout = 30_000;

    /// <summary>
    /// The address of the server this client talks to.
    /// </summary>
    public IPAddress Host { get; }

    /// <summary>
    /// The UDP port of the server this client talks to.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The ONC RPC program number this client calls.
    /// </summary>
    public uint Program { get; }

    /// <summary>
    /// The version of the ONC RPC program this client calls.
    /// </summary>
    public uint Version { get; }

    /// <summary>
    /// How long to wait, in milliseconds, for a response before retransmitting a request. Each
    /// retransmission doubles the wait (exponential backoff), matching the retransmission mode
    /// crate-digger configures on its Remote Tea clients, until <see cref="Timeout"/> is exhausted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">if the value is not positive</exception>
    public int RetransmitTimeout
    {
        get => _retransmitTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _retransmitTimeout = value;
        }
    }

    /// <summary>
    /// The overall time limit, in milliseconds, for a call including all retransmissions, after
    /// which the call fails with an <see cref="RpcException"/>. Defaults to thirty seconds, the
    /// Remote Tea default used by the Java crate-digger.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">if the value is not positive</exception>
    public int Timeout
    {
        get => _timeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _timeout = value;
        }
    }

    /// <summary>
    /// Create a client for making calls to the specified ONC RPC program over UDP.
    /// </summary>
    /// <param name="host">the address of the server to call</param>
    /// <param name="port">the UDP port on which the program is listening (usually discovered
    /// through the portmapper, see <see cref="PortmapperClient"/>)</param>
    /// <param name="program">the ONC RPC program number to call</param>
    /// <param name="version">the version of the program to call</param>
    public OncRpcUdpClient(IPAddress host, int port, uint program, uint version)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        Port = port;
        Program = program;
        Version = version;
        _socket = new Socket(host.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Connect(new IPEndPoint(host, port));
    }

    /// <summary>
    /// Build the bytes of an ONC RPC call message with <c>AUTH_NONE</c> credentials and verifier,
    /// as laid out in RFC 1057 section 8.
    /// </summary>
    /// <param name="transactionId">the transaction identifier which will match the reply</param>
    /// <param name="program">the remote program number</param>
    /// <param name="version">the remote program version</param>
    /// <param name="procedure">the procedure within the program to call</param>
    /// <param name="writeArguments">writes the procedure arguments, or <c>null</c> if the procedure takes none</param>
    internal static byte[] BuildCall(uint transactionId, uint program, uint version, uint procedure,
        Action<XdrWriter>? writeArguments)
    {
        var writer = new XdrWriter();
        writer.WriteUInt32(transactionId);
        writer.WriteUInt32(CallMessageType);
        writer.WriteUInt32(RpcVersion);
        writer.WriteUInt32(program);
        writer.WriteUInt32(version);
        writer.WriteUInt32(procedure);
        writer.WriteUInt32(0);  // Credentials: AUTH_NONE flavor.
        writer.WriteUInt32(0);  // Credentials: zero-length body.
        writer.WriteUInt32(0);  // Verifier: AUTH_NONE flavor.
        writer.WriteUInt32(0);  // Verifier: zero-length body.
        writeArguments?.Invoke(writer);
        return writer.ToArray();
    }

    /// <summary>
    /// Call a remote procedure and wait for its reply, retransmitting with exponential backoff
    /// starting at <see cref="RetransmitTimeout"/> until a reply arrives or <see cref="Timeout"/>
    /// elapses.
    /// </summary>
    /// <param name="procedure">the procedure within the program to call</param>
    /// <param name="writeArguments">writes the procedure arguments, or <c>null</c> if the procedure takes none</param>
    /// <param name="cancellationToken">allows the call to be interrupted</param>
    /// <returns>a reader positioned at the start of the procedure results</returns>
    /// <exception cref="RpcException">if the call times out, is rejected by the server, or is
    /// accepted with a status other than success</exception>
    /// <exception cref="OperationCanceledException">if the call is cancelled</exception>
    public XdrReader Call(uint procedure, Action<XdrWriter>? writeArguments, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var transactionId = unchecked((uint)Interlocked.Increment(ref _nextTransactionId));
            var request = BuildCall(transactionId, Program, Version, procedure, writeArguments);
            var overallDeadline = Environment.TickCount64 + Timeout;
            long interval = RetransmitTimeout;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _socket.Send(request);
                var intervalDeadline = Math.Min(Environment.TickCount64 + interval, overallDeadline);

                while (true)
                {
                    var now = Environment.TickCount64;
                    if (now >= intervalDeadline)
                    {
                        break;
                    }
                    // Wait in short slices so cancellation stays responsive even as backoff grows.
                    _socket.ReceiveTimeout = (int)Math.Min(intervalDeadline - now, 250);
                    int received;
                    try
                    {
                        received = _socket.Receive(_receiveBuffer);
                    }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }
                    catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                    {
                        // An ICMP port-unreachable response; keep waiting, we will retransmit.
                        cancellationToken.ThrowIfCancellationRequested();
                        Thread.Sleep(10);
                        continue;
                    }
                    var reply = ParseReply(_receiveBuffer, received, transactionId, procedure);
                    if (reply != null)
                    {
                        return reply;
                    }
                }

                if (Environment.TickCount64 >= overallDeadline)
                {
                    throw new RpcException($"RPC call to program {Program} version {Version} procedure {procedure} " +
                        $"on {Host}:{Port} timed out after {Timeout} ms.");
                }
                interval *= 2;
            }
        }
    }

    /// <summary>
    /// Validate a datagram as the reply to our call, returning a reader positioned at the procedure
    /// results, or <c>null</c> if the datagram should be ignored (wrong transaction identifier, or
    /// not a reply at all, for example a duplicate response to an earlier retransmitted call).
    /// </summary>
    /// <exception cref="RpcException">if the reply reports that the call failed</exception>
    private XdrReader? ParseReply(byte[] buffer, int length, uint expectedTransactionId, uint procedure)
    {
        if (length < 12)
        {
            return null;  // Far too short to be an RPC reply.
        }
        var reader = new XdrReader(buffer.AsMemory(0, length).ToArray());
        if (reader.ReadUInt32() != expectedTransactionId || reader.ReadUInt32() != ReplyMessageType)
        {
            return null;
        }

        var context = $"RPC call to program {Program} version {Version} procedure {procedure} on {Host}:{Port}";
        var replyStatus = reader.ReadUInt32();
        if (replyStatus == 1)  // MSG_DENIED
        {
            var rejectStatus = reader.ReadUInt32();
            if (rejectStatus == 0)  // RPC_MISMATCH
            {
                var low = reader.ReadUInt32();
                var high = reader.ReadUInt32();
                throw new RpcException($"{context} was denied: server supports RPC versions {low} through {high}.");
            }
            var authStatus = reader.ReadUInt32();
            throw new RpcException($"{context} was denied: authentication error {authStatus}.");
        }
        if (replyStatus != 0)  // Neither MSG_ACCEPTED nor MSG_DENIED.
        {
            throw new RpcException($"{context} received a reply with unknown status {replyStatus}.");
        }

        reader.ReadUInt32();  // Verifier flavor, which we do not use.
        reader.ReadOpaque();  // Verifier body, which we do not use.
        var acceptStatus = reader.ReadUInt32();
        switch (acceptStatus)
        {
            case 0:  // SUCCESS
                return reader;
            case 1:
                throw new RpcException($"{context} failed: program unavailable.");
            case 2:
                var low = reader.ReadUInt32();
                var high = reader.ReadUInt32();
                throw new RpcException($"{context} failed: program version mismatch, server supports versions {low} through {high}.");
            case 3:
                throw new RpcException($"{context} failed: procedure unavailable.");
            case 4:
                throw new RpcException($"{context} failed: server could not decode the arguments.");
            case 5:
                throw new RpcException($"{context} failed: server reported a system error.");
            default:
                throw new RpcException($"{context} failed with unknown accepted status {acceptStatus}.");
        }
    }

    /// <summary>
    /// Close the UDP socket used by this client. Any call in progress will fail.
    /// </summary>
    public void Dispose()
    {
        _socket.Dispose();
    }
}
