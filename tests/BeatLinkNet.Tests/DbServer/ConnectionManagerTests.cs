using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the <see cref="ConnectionManager"/> singleton against fake loopback servers. The port
/// query and dbserver connections are redirected to ephemeral loopback listeners via the internal
/// override seams, so these tests never touch the real DJ Link ports (which may be in use by an
/// actual rekordbox instance on the machine running the tests). Shares the "Network services"
/// collection because it mutates the <see cref="DeviceFinder"/> and <see cref="VirtualCdj"/>
/// singletons.
/// </summary>
[Collection("Network services")]
public sealed class ConnectionManagerTests : IDisposable
{
    private static ConnectionManager Manager => ConnectionManager.Instance;

    private static DeviceFinder Finder => DeviceFinder.Instance;

    private static VirtualCdj Vcdj => VirtualCdj.Instance;

    public ConnectionManagerTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    private static void ResetState()
    {
        Manager.Stop();
        // Give any cancelled port-query tasks a moment to unwind, so their departure cannot race
        // the deduplication of queries spawned by the next test.
        var deadline = Environment.TickCount64 + 5000;
        while (Manager.ActiveQueryCountForTests > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }
        Finder.Stop();
        Vcdj.ResetForTests();
        Manager.DbServerQueryPortOverride = null;
        Manager.IdleLimitMillisOverride = null;
        Manager.IdleLimit = 1;
        Manager.SocketTimeout = ConnectionManager.DefaultSocketTimeout;
        Finder.PortOverride = null;
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The 19-byte query which the connection manager must send to the port-query service.
    /// </summary>
    private static readonly byte[] ExpectedQueryPacket =
    [
        0x00, 0x00, 0x00, 0x0f,
        0x52, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x44, 0x42, 0x53, 0x65, 0x72, 0x76, 0x65, 0x72,  // RemoteDBServer
        0x00
    ];

    /// <summary>
    /// Encodes a dbserver port as the big-endian 2-byte response the port-query service returns.
    /// </summary>
    private static byte[] PortBytes(int port) => [(byte)(port >> 8), (byte)port];

    /// <summary>
    /// A fake dbserver port-query service on an ephemeral loopback port. Each accepted connection
    /// reads the 19-byte query, records it, and replies with the next scripted response (repeating
    /// the last response once the script is exhausted).
    /// </summary>
    private sealed class FakeQueryServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[][] _responses;
        private int _connections;

        public int Port { get; }

        public int ResponseDelayMillis { get; init; }

        public ConcurrentQueue<byte[]> ReceivedQueries { get; } = new();

        public int ConnectionCount => Volatile.Read(ref _connections);

        public FakeQueryServer(params byte[][] responses)
        {
            _responses = responses;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;  // The listener was stopped; we are done.
                }
                var index = Interlocked.Increment(ref _connections) - 1;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var connection = client;
                        var stream = connection.GetStream();
                        var query = new byte[ExpectedQueryPacket.Length];
                        await stream.ReadExactlyAsync(query);
                        ReceivedQueries.Enqueue(query);
                        if (ResponseDelayMillis > 0)
                        {
                            await Task.Delay(ResponseDelayMillis);
                        }
                        var response = _responses[Math.Min(index, _responses.Length - 1)];
                        await stream.WriteAsync(response);
                    }
                    catch (Exception)
                    {
                        // The connection was torn down mid-script (e.g. the manager stopped).
                    }
                });
            }
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>
    /// Starts a fake dbserver on an ephemeral loopback port which accepts a single connection,
    /// serves the greeting and setup exchanges, and then records the teardown message, returning
    /// every full message it read.
    /// </summary>
    private static (int Port, Task<List<Message>> Log) StartFakeDbServer(int targetPlayer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(() =>
        {
            var log = new List<Message>();
            try
            {
                using var socket = listener.AcceptSocket();
                using var stream = new NetworkStream(socket, ownsSocket: false);

                var greeting = Assert.IsType<NumberField>(Field.Read(stream));
                Assert.Equal(1, greeting.Value);
                new NumberField(1, 4).Write(stream);

                var setup = Message.Read(stream);
                log.Add(setup);
                new Message(0xfffffffeL, Message.KnownType.MenuAvailable,
                    new NumberField(Message.KnownType.SetupReq.ProtocolValue),
                    new NumberField(targetPlayer)).Write(stream);

                try
                {
                    log.Add(Message.Read(stream));  // The teardown message sent when the client closes.
                }
                catch (IOException)
                {
                    // The client may have closed abruptly; the log will show no teardown.
                }
            }
            finally
            {
                listener.Stop();
            }
            return log;
        });
        return (port, task);
    }

    /// <summary>
    /// Blocks until the fake dbserver script finishes, returning the messages it read. Deliberate
    /// blocking, matching the synchronous style of the client API under test; the script runs on
    /// an independent thread-pool thread, so no deadlock is possible.
    /// </summary>
    private static List<Message> AwaitLog(Task<List<Message>> log)
    {
        Assert.True(log.Wait(TimeSpan.FromSeconds(10)), "fake dbserver script did not complete");
        return log.Result;
    }

    /// <summary>
    /// Polls for a condition, failing the test if it does not become true in time.
    /// </summary>
    private static void WaitUntil(Func<bool> condition, string description, int timeoutMillis = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMillis;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(25);
        }
        Assert.Fail($"Timed out waiting for {description}");
    }

    /// <summary>
    /// Starts the manager with the device finder listening on an ephemeral loopback port and the
    /// port query redirected to the supplied fake server.
    /// </summary>
    private static void StartManagerWithFakeQueryServer(FakeQueryServer server)
    {
        Finder.PortOverride = 0;
        Manager.DbServerQueryPortOverride = server.Port;
        Manager.Start();
        Assert.True(Manager.IsRunning);
        Assert.True(Finder.IsRunning);
    }

    /// <summary>
    /// Announces a synthetic device from the loopback address and waits until the device finder
    /// has registered it.
    /// </summary>
    private static void AnnounceDevice(string name, byte deviceNumber)
    {
        ServiceTestHelpers.SendToLoopback(Finder.LocalPort!.Value,
            ServiceTestHelpers.KeepAlivePacket(name, deviceNumber));
        WaitUntil(() => Finder.GetLatestAnnouncementFrom(deviceNumber) != null,
            $"device {deviceNumber} to be registered");
    }

    /// <summary>
    /// Builds a synthetic device announcement sourced from the loopback address, without involving
    /// the device finder.
    /// </summary>
    private static DeviceAnnouncement LoopbackAnnouncement(string name, byte deviceNumber) =>
        new(ServiceTestHelpers.KeepAlivePacket(name, deviceNumber), IPAddress.Loopback);

    /// <summary>
    /// Builds a synthetic CDJ status report for the given player, playing a track sourced from the
    /// specified other player, for injection into the <see cref="VirtualCdj"/>.
    /// </summary>
    private static CdjStatus MakeStatusWithSourcePlayer(int deviceNumber, int trackSourcePlayer)
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x21] = (byte)deviceNumber;
        bytes[0x28] = (byte)trackSourcePlayer;
        return new CdjStatus(bytes, VirtualCdjTestHelpers.AddressFor(deviceNumber));
    }

    [Fact]
    public void HasExpectedDefaultsAndValidatesSettings()
    {
        Assert.False(Manager.IsRunning);
        Assert.Equal(1, Manager.IdleLimit);
        Assert.Equal(10000, ConnectionManager.DefaultSocketTimeout);
        Assert.Equal(ConnectionManager.DefaultSocketTimeout, Manager.SocketTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => Manager.IdleLimit = -1);
        Manager.IdleLimit = 0;
        Assert.Equal(0, Manager.IdleLimit);
        Manager.SocketTimeout = 5000;
        Assert.Equal(5000, Manager.SocketTimeout);
        Assert.Throws<InvalidOperationException>(() => Manager.GetPlayerDBServerPort(2));
        Assert.Throws<InvalidOperationException>(() =>
            Manager.InvokeWithClientSession(2, _ => 0, "while stopped"));
        Assert.Contains("running:False", Manager.ToString());
    }

    [Fact]
    public void QueriesAndCachesDbServerPortForFoundDevices()
    {
        using var server = new FakeQueryServer(PortBytes(36677));
        StartManagerWithFakeQueryServer(server);

        AnnounceDevice("CDJ-3000", 17);
        WaitUntil(() => Manager.GetPlayerDBServerPort(17) == 36677, "the dbserver port to be cached");

        // The query packet must be the 19-byte RemoteDBServer request.
        Assert.True(server.ReceivedQueries.TryDequeue(out var query));
        Assert.Equal(ExpectedQueryPacket, query);

        // An unknown player has no port.
        Assert.Equal(-1, Manager.GetPlayerDBServerPort(9));

        // The cache is keyed by address, so another device number at the same address (as found in
        // compound devices like the XDJ-XZ) shares the port as soon as it is announced.
        AnnounceDevice("CDJ-3000", 18);
        Assert.Equal(36677, Manager.GetPlayerDBServerPort(18));
    }

    [Fact]
    public void RetriesWhenPortQueryReportsNotReady()
    {
        // The first connection reports the 65535 "not yet ready" sentinel; the retry succeeds.
        using var server = new FakeQueryServer(PortBytes(65535), PortBytes(41952));
        StartManagerWithFakeQueryServer(server);

        AnnounceDevice("CDJ-3000", 17);
        WaitUntil(() => Manager.GetPlayerDBServerPort(17) == 41952,
            "the dbserver port to be cached after the not-ready retry");
        Assert.True(server.ConnectionCount >= 2, "expected the port query to be retried");
    }

    [Fact]
    public void IgnoresKuvoGatewayAndOpusCompatibilityMode()
    {
        using var server = new FakeQueryServer(PortBytes(36677));
        StartManagerWithFakeQueryServer(server);

        // In Opus Quad compatibility mode, found devices are not queried at all.
        Vcdj.SetOpusQuadCompatibilityModeForTests(true);
        try
        {
            Manager.HandleDeviceFound(LoopbackAnnouncement("CDJ-3000", 7));
            Assert.Equal(0, Manager.ActiveQueryCountForTests);
        }
        finally
        {
            Vcdj.SetOpusQuadCompatibilityModeForTests(false);
        }

        // The Kuvo gateway device is ignored even in normal operation.
        Manager.HandleDeviceFound(LoopbackAnnouncement("NXS-GW", 25));
        Assert.Equal(0, Manager.ActiveQueryCountForTests);

        // A normal device does get queried, proving the manager was otherwise operational.
        Manager.HandleDeviceFound(LoopbackAnnouncement("CDJ-3000", 17));
        WaitUntil(() => Manager.CachedPortCountForTests == 1, "the port of the normal device to be cached");
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public void DeduplicatesConcurrentQueriesForTheSameAddress()
    {
        using var server = new FakeQueryServer(PortBytes(36677)) { ResponseDelayMillis = 300 };
        StartManagerWithFakeQueryServer(server);

        // Two players sharing one address (a compound device) are found in quick succession while
        // the first query is still in flight; only one query may be issued.
        Manager.HandleDeviceFound(LoopbackAnnouncement("XDJ-XZ", 17));
        Manager.HandleDeviceFound(LoopbackAnnouncement("XDJ-XZ", 18));
        WaitUntil(() => Manager.CachedPortCountForTests == 1, "the shared dbserver port to be cached");
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public void DeviceLostDiscardsCachedPort()
    {
        using var server = new FakeQueryServer(PortBytes(36677));
        Finder.MaximumAgeMillis = 400;
        try
        {
            StartManagerWithFakeQueryServer(server);
            AnnounceDevice("CDJ-3000", 17);
            WaitUntil(() => Manager.CachedPortCountForTests == 1, "the dbserver port to be cached");

            // Once the device expires, its cached port must be discarded.
            WaitUntil(() => Manager.CachedPortCountForTests == 0, "the cached port to be discarded");
        }
        finally
        {
            Finder.MaximumAgeMillis = DeviceFinder.MaximumAge;
        }
    }

    [Fact]
    public void InvokeWithClientSessionPoolsAndIdleCloses()
    {
        var (dbPort, dbLog) = StartFakeDbServer(targetPlayer: 17);
        using var server = new FakeQueryServer(PortBytes(dbPort));
        Manager.IdleLimitMillisOverride = 400;
        StartManagerWithFakeQueryServer(server);

        AnnounceDevice("CDJ-3000", 17);
        WaitUntil(() => Manager.GetPlayerDBServerPort(17) == dbPort, "the dbserver port to be cached");

        // The first session opens a connection (performing the handshake) and pools the client.
        var first = Manager.InvokeWithClientSession(17, client => client, "opening the first session");
        Assert.Equal(17, first.TargetPlayer);
        Assert.Equal(0, first.PosingAsPlayer);  // Target > 15, so we use our own (unassigned) number.
        Assert.True(first.IsConnected);
        Assert.Equal(1, Manager.OpenClientCountForTests);

        // A second session before the idle limit elapses reuses the pooled client.
        var second = Manager.InvokeWithClientSession(17, client => client, "opening the second session");
        Assert.Same(first, second);
        Assert.Equal(1, Manager.OpenClientCountForTests);

        // Once the idle limit elapses, the background closer disposes the client politely.
        WaitUntil(() => Manager.OpenClientCountForTests == 0, "the idle client to be closed");
        Assert.False(first.IsConnected);

        // The fake dbserver saw exactly one handshake (no second connection) and one teardown.
        var messages = AwaitLog(dbLog);
        Assert.Equal(2, messages.Count);
        Assert.Equal(Message.KnownType.SetupReq, messages[0].RecognizedType);
        Assert.Equal(0, Assert.IsType<NumberField>(Assert.Single(messages[0].Arguments)).Value);
        Assert.Equal(Message.KnownType.TeardownReq, messages[1].RecognizedType);
    }

    [Fact]
    public void ZeroIdleLimitClosesClientImmediately()
    {
        var (dbPort, dbLog) = StartFakeDbServer(targetPlayer: 17);
        using var server = new FakeQueryServer(PortBytes(dbPort));
        Manager.IdleLimitMillisOverride = 0;
        StartManagerWithFakeQueryServer(server);

        AnnounceDevice("CDJ-3000", 17);
        WaitUntil(() => Manager.GetPlayerDBServerPort(17) == dbPort, "the dbserver port to be cached");

        var client = Manager.InvokeWithClientSession(17, c =>
        {
            Assert.True(c.IsConnected);
            Assert.Equal(1, Manager.OpenClientCountForTests);
            return c;
        }, "testing immediate close");

        // With a zero idle limit, the client is closed as soon as its last use is freed.
        Assert.Equal(0, Manager.OpenClientCountForTests);
        Assert.False(client.IsConnected);
        Assert.Equal(Message.KnownType.TeardownReq, AwaitLog(dbLog)[^1].RecognizedType);
    }

    [Fact]
    public void InvokeWithClientSessionReportsMissingPlayers()
    {
        using var server = new FakeQueryServer(PortBytes(36677));
        StartManagerWithFakeQueryServer(server);

        // A player that has never been announced cannot be found at all.
        var e = Assert.Throws<InvalidOperationException>(() =>
            Manager.InvokeWithClientSession(9, _ => 0, "requesting track metadata"));
        Assert.Contains("could not be found", e.Message);
    }

    [Fact]
    public void StopClosesClientsAndClearsState()
    {
        var (dbPort, dbLog) = StartFakeDbServer(targetPlayer: 17);
        using var server = new FakeQueryServer(PortBytes(dbPort));
        Manager.IdleLimitMillisOverride = 60000;  // Keep the pooled client open until we stop.
        StartManagerWithFakeQueryServer(server);

        AnnounceDevice("CDJ-3000", 17);
        WaitUntil(() => Manager.GetPlayerDBServerPort(17) == dbPort, "the dbserver port to be cached");
        var client = Manager.InvokeWithClientSession(17, c => c, "opening a session");
        Assert.Equal(1, Manager.OpenClientCountForTests);

        Manager.Stop();

        Assert.False(Manager.IsRunning);
        Assert.Equal(0, Manager.OpenClientCountForTests);
        Assert.Equal(0, Manager.CachedPortCountForTests);
        Assert.False(client.IsConnected);
        Assert.Throws<InvalidOperationException>(() => Manager.GetPlayerDBServerPort(17));
        Assert.Throws<InvalidOperationException>(() =>
            Manager.InvokeWithClientSession(17, _ => 0, "after stopping"));

        // The pooled client was closed politely, with a teardown message.
        Assert.Equal(Message.KnownType.TeardownReq, AwaitLog(dbLog)[^1].RecognizedType);

        // The DeviceFinder is left running; the manager does not own its lifecycle.
        Assert.True(Finder.IsRunning);
    }

    [Fact]
    public void AutoStopsWhenDeviceFinderStops()
    {
        using var server = new FakeQueryServer(PortBytes(36677));
        StartManagerWithFakeQueryServer(server);

        Finder.Stop();
        WaitUntil(() => !Manager.IsRunning, "the manager to stop after the DeviceFinder did");
    }

    [Fact]
    public void StartIsIdempotent()
    {
        using var server = new FakeQueryServer(PortBytes(36677));
        StartManagerWithFakeQueryServer(server);
        Manager.Start();  // A second start has no effect.
        Assert.True(Manager.IsRunning);
        Assert.Throws<InvalidOperationException>(() => Manager.DbServerQueryPortOverride = 1234);
        Manager.Stop();
        Manager.Stop();  // Stopping twice is also idempotent.
        Assert.False(Manager.IsRunning);
    }

    [Fact]
    public void ChoosesTargetNumberWhenImpersonationIsSafe()
    {
        // With a device number above 4 and a target that can handle metadata queries from many
        // players, we impersonate the target's own number.
        Vcdj.DeviceNumber = 5;
        Assert.Equal(2, Manager.ChooseAskingPlayerNumber(LoopbackAnnouncement("CDJ-3000", 2)));
        Assert.Equal(17, Manager.ChooseAskingPlayerNumber(LoopbackAnnouncement("rekordbox", 17)));
    }

    [Fact]
    public void ChoosesOwnNumberWhenAcceptable()
    {
        // Anything works when talking to rekordbox (device numbers above 15).
        Vcdj.DeviceNumber = 2;
        Assert.Equal(2, Manager.ChooseAskingPlayerNumber(LoopbackAnnouncement("rekordbox", 17)));

        // A real player number of our own (1-4) is always safe, even for a limited target.
        Vcdj.DeviceNumber = 3;
        Assert.Equal(3, Manager.ChooseAskingPlayerNumber(LoopbackAnnouncement("CDJ-2000nexus", 2)));
    }

    [Fact]
    public void StealsIdleRealPlayerNumberWhenNecessary()
    {
        Finder.PortOverride = 0;
        Finder.Start();
        AnnounceDevice("CDJ-2000nexus", 1);
        AnnounceDevice("CDJ-2000nexus", 2);
        Vcdj.SetRunningStateForTests(true);
        var target = LoopbackAnnouncement("CDJ-2000nexus", 2);

        // With no usable number of our own, and no status proving player 1 is safe to
        // impersonate, there is no choice available.
        Assert.Throws<InvalidOperationException>(() => Manager.ChooseAskingPlayerNumber(target));

        // Player 1 playing a track served by the target still cannot be impersonated.
        Vcdj.ProcessUpdate(MakeStatusWithSourcePlayer(1, trackSourcePlayer: 2));
        Assert.Throws<InvalidOperationException>(() => Manager.ChooseAskingPlayerNumber(target));

        // Once player 1 is playing a track from somewhere else, we can steal its number.
        Vcdj.ProcessUpdate(MakeStatusWithSourcePlayer(1, trackSourcePlayer: 3));
        Assert.Equal(1, Manager.ChooseAskingPlayerNumber(target));
    }
}
