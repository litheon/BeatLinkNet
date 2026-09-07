using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace BeatLink.DbServer;

/// <summary>
/// Manages connections to dbserver ports on the players, offering sessions that can be used to
/// perform transactions, and allowing the connections to close when there are no active sessions.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>ConnectionManager</c>, by James Elliott.
/// </remarks>
public sealed class ConnectionManager : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<ConnectionManager>();

    /// <summary>
    /// Keeps track of the clients that are currently active, indexed by player number. Only
    /// accessed while holding <see cref="_lock"/>.
    /// </summary>
    private readonly Dictionary<int, Client> _openClients = [];

    /// <summary>
    /// Keeps track of how many tasks are currently using each client. Only accessed while holding
    /// <see cref="_lock"/>.
    /// </summary>
    private readonly Dictionary<Client, int> _useCounts = [];

    /// <summary>
    /// Keeps track of the last time each client was used (in monotonic milliseconds derived from
    /// <see cref="Util.CurrentTimestampNanos"/>), so we can time out our connection. Only accessed
    /// while holding <see cref="_lock"/>.
    /// </summary>
    private readonly Dictionary<Client, long> _timestamps = [];

    /// <summary>
    /// Serializes lifecycle changes and all manipulation of the client pool, replacing the Java
    /// <c>synchronized</c> methods, which all locked the manager instance.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// How many seconds do we allow an idle connection to stay open?
    /// </summary>
    private volatile int _idleLimit = 1;

    /// <summary>
    /// Determine how long an idle connection will be kept open for reuse, in seconds. Once this
    /// time has elapsed, the connection will be closed. Setting this to zero will close connections
    /// immediately after use, which might be somewhat inefficient when multiple queries need to
    /// happen in a row, since each will require the establishment of a new connection. The default
    /// value is 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">if a negative value is supplied</exception>
    public int IdleLimit
    {
        get => _idleLimit;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _idleLimit = value;
        }
    }

    /// <summary>
    /// Test seam allowing the idle limit to be expressed with millisecond precision, so idle
    /// timeouts can be exercised without waiting whole seconds. When <c>null</c> (the default),
    /// <see cref="IdleLimit"/> (in seconds) governs.
    /// </summary>
    private long? _idleLimitMillisOverride;

    /// <inheritdoc cref="_idleLimitMillisOverride"/>
    internal long? IdleLimitMillisOverride
    {
        get { lock (_lock) { return _idleLimitMillisOverride; } }
        set { lock (_lock) { _idleLimitMillisOverride = value; } }
    }

    /// <summary>
    /// The idle limit currently in effect, in milliseconds. Only meaningful while holding
    /// <see cref="_lock"/> (or in tests that know no concurrent change is possible).
    /// </summary>
    private long EffectiveIdleLimitMillis => _idleLimitMillisOverride ?? _idleLimit * 1000L;

    /// <summary>
    /// The current monotonic time in milliseconds, used for tracking client idle times.
    /// </summary>
    private static long NowMillis => Util.CurrentTimestampNanos() / 1_000_000;

    /// <summary>
    /// Finds or opens a client to talk to the dbserver on the specified player, incrementing its
    /// use count. Called while holding <see cref="_lock"/>.
    /// </summary>
    /// <param name="targetPlayer">the player number whose database needs to be interacted with</param>
    /// <param name="description">a short description of the task being performed for error
    /// reporting if it fails, should be a verb phrase like "requesting track metadata"</param>
    /// <returns>the communication client for talking to that player</returns>
    /// <exception cref="InvalidOperationException">if we can't find the target player or there is
    /// no suitable player number for us to pretend to be</exception>
    /// <exception cref="IOException">if there is a problem communicating</exception>
    private Client AllocateClient(int targetPlayer, string description)
    {
        if (!_openClients.TryGetValue(targetPlayer, out var result))
        {
            // We need to open a new connection.
            var targetDeviceAnnouncement = DeviceFinder.Instance.GetLatestAnnouncementFrom(targetPlayer) ??
                throw new InvalidOperationException($"Player {targetPlayer} could not be found {description}");
            var dbServerPort = GetPlayerDBServerPort(targetPlayer);
            if (dbServerPort < 0)
            {
                throw new InvalidOperationException($"Player {targetPlayer} does not have a db server {description}");
            }

            var posingAsPlayerNumber = ChooseAskingPlayerNumber(targetDeviceAnnouncement);

            Socket? socket = null;
            try
            {
                var address = new IPEndPoint(targetDeviceAnnouncement.Address, dbServerPort);
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                ConnectWithTimeout(socket, address, SocketTimeout);
                socket.ReceiveTimeout = SocketTimeout;
                socket.SendTimeout = SocketTimeout;
                result = new Client(socket, targetPlayer, posingAsPlayerNumber);
            }
            catch
            {
                // (If the Client constructor itself failed, it has already disposed the socket,
                // but disposing again is harmless.)
                socket?.Dispose();
                throw;
            }
            _openClients[targetPlayer] = result;
            _useCounts[result] = 0;
        }
        _useCounts[result] = _useCounts[result] + 1;
        return result;
    }

    /// <summary>
    /// Opens a connection on the supplied socket, failing if it cannot be established within the
    /// specified number of milliseconds. (The synchronous <see cref="Socket.Connect(EndPoint)"/>
    /// has no timeout parameter, so this bridges to the asynchronous version, unwrapping the
    /// original exception if the attempt fails.)
    /// </summary>
    /// <param name="socket">the socket to connect</param>
    /// <param name="endpoint">where to connect it to</param>
    /// <param name="timeoutMillis">how long to wait before giving up</param>
    /// <exception cref="SocketException">if the connection cannot be established in time</exception>
    private static void ConnectWithTimeout(Socket socket, EndPoint endpoint, int timeoutMillis)
    {
        var connectTask = socket.ConnectAsync(endpoint);
        bool completed;
        try
        {
            completed = connectTask.Wait(timeoutMillis);
        }
        catch (AggregateException e) when (e.InnerException != null && e.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;  // Unreachable, but required for definite assignment.
        }
        if (!completed)
        {
            throw new SocketException((int)SocketError.TimedOut);
        }
    }

    /// <summary>
    /// When it is time to actually close a client, do so, and clean up the related data
    /// structures. Called while holding <see cref="_lock"/>.
    /// </summary>
    /// <param name="client">the client which has been idle for long enough to be closed</param>
    private void CloseClient(Client client)
    {
        Logger.LogDebug("Closing client {Client}", client);
        client.Dispose();
        _openClients.Remove(client.TargetPlayer);
        _useCounts.Remove(client);
        _timestamps.Remove(client);
    }

    /// <summary>
    /// Decrements the client's use count, and makes it eligible for closing if it is no longer in
    /// use.
    /// </summary>
    /// <param name="client">the dbserver connection client which is no longer being used for a task</param>
    private void FreeClient(Client client)
    {
        lock (_lock)
        {
            if (_useCounts.TryGetValue(client, out var current) && current > 0)
            {
                _timestamps[client] = NowMillis;  // Mark that it was used until now.
                _useCounts[client] = current - 1;
                if (current == 1 && EffectiveIdleLimitMillis == 0)
                {
                    CloseClient(client);  // This was the last use, and we are supposed to immediately close idle clients.
                }
            }
            else
            {
                Logger.LogError("Ignoring attempt to free a client that is not allocated: {Client}", client);
            }
        }
    }

    /// <summary>
    /// Obtain a dbserver client session that can be used to perform some task, call that task with
    /// the client, then release the client.
    /// </summary>
    /// <param name="targetPlayer">the player number whose dbserver we wish to communicate with</param>
    /// <param name="task">the activity that will be performed with exclusive access to a dbserver
    /// connection</param>
    /// <param name="description">a short description of the task being performed for error
    /// reporting if it fails, should be a verb phrase like "requesting track metadata"</param>
    /// <typeparam name="T">the type that will be returned by the task to be performed</typeparam>
    /// <returns>the value returned by the completed task</returns>
    /// <exception cref="InvalidOperationException">if we are not running, the target player cannot
    /// be found, or there is no suitable player number for us to pretend to be</exception>
    /// <exception cref="IOException">if there is a problem communicating</exception>
    public T InvokeWithClientSession<T>(int targetPlayer, Func<Client, T> task, string description)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!IsRunning)
        {
            throw new InvalidOperationException($"ConnectionManager is not running, aborting {description}");
        }

        Client client;
        lock (_lock)
        {
            client = AllocateClient(targetPlayer, description);
        }
        try
        {
            return task(client);
        }
        finally
        {
            FreeClient(client);
        }
    }

    /// <summary>
    /// Keeps track of the database server ports of all the players we have seen on the network.
    /// They are grouped by IP address, because players in compound devices like the XDJ-XZ and
    /// XDJ-AZ share a single dbserver instance.
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, int> _dbServerPorts = new();

    /// <summary>
    /// Look up the database server port reported by a given player. You should not use this port
    /// directly; instead ask this class for a session to use while you communicate with the
    /// database.
    /// </summary>
    /// <param name="player">the player number of interest</param>
    /// <returns>the port number on which its database server is running, or -1 if unknown</returns>
    /// <exception cref="InvalidOperationException">if not running</exception>
    public int GetPlayerDBServerPort(int player)
    {
        EnsureRunning();
        var announcement = DeviceFinder.Instance.GetLatestAnnouncementFrom(player);
        if (announcement == null)
        {
            return -1;
        }
        return _dbServerPorts.TryGetValue(announcement.Address, out var result) ? result : -1;
    }

    /// <summary>
    /// Watches for devices to appear on the network so we can ask them for their database server
    /// port.
    /// </summary>
    private void OnDeviceFound(object? sender, DeviceAnnouncement announcement) => HandleDeviceFound(announcement);

    /// <summary>
    /// Reacts to a new device on the network (whether from a live announcement or the replay of
    /// already-known devices during startup) by spawning a task to query its database server port,
    /// unless one is already running for the same address. (Internal, rather than private as in
    /// the Java implementation, so the filtering logic can be tested deterministically.)
    /// </summary>
    /// <param name="announcement">the device announcement that has been received</param>
    internal void HandleDeviceFound(DeviceAnnouncement announcement)
    {
        if (VirtualCdj.Instance.InOpusQuadCompatibilityMode)
        {
            Logger.LogDebug("Nothing to do when new devices found in Opus Quad compatibility mode.");
            return;
        }
        if (announcement.DeviceNumber == 25 && announcement.DeviceName == "NXS-GW")
        {
            Logger.LogDebug("Ignoring arrival of Kuvo gateway, which fight each other and come and go constantly, especially in CDJ-3000s.");
            return;
        }
        var cancellation = _stopCancellation;
        if (cancellation == null || cancellation.IsCancellationRequested)
        {
            return;  // We are shutting down (or a stale event arrived after we stopped).
        }
        Logger.LogDebug("Processing device found, number: {Number}, name: {Name}",
            announcement.DeviceNumber, announcement.DeviceName);
        var token = cancellation.Token;
        var queryTask = new Task(() => RequestPlayerDbServerPort(announcement, token));
        if (_activeQueryTasks.TryAdd(announcement.Address, queryTask))
        {
            // We were not already querying a device at this address, so we can start our task.
            // Otherwise, it will just get garbage collected without ever running.
            queryTask.Start(TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Watches for devices to disappear from the network, discarding all information about them.
    /// </summary>
    private void OnDeviceLost(object? sender, DeviceAnnouncement announcement)
    {
        if (announcement.DeviceNumber == 25 && announcement.DeviceName == "NXS-GW")
        {
            Logger.LogDebug("Ignoring departure of Kuvo gateway, which fight each other and come and go constantly, especially in CDJ-3000s.");
            return;
        }
        _dbServerPorts.TryRemove(announcement.Address, out _);
    }

    /// <summary>
    /// The packet written to the query port to ask a player for the port on which its database
    /// server is running: a 4-byte length, the ASCII string "RemoteDBServer", and a trailing zero.
    /// </summary>
    private static readonly byte[] DbServerQueryPacket =
    [
        0x00, 0x00, 0x00, 0x0f,
        0x52, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x44, 0x42, 0x53, 0x65, 0x72, 0x76, 0x65, 0x72,  // RemoteDBServer
        0x00
    ];

    /// <summary>
    /// The value reported on the query port by a player whose database server is not yet ready to
    /// report its actual port.
    /// </summary>
    private const int DbServerNotReady = 65535;

    /// <summary>
    /// Test hook allowing the port on which we send database server port queries to be redirected
    /// to a fake server, so tests never touch the real port 12523 (on which an actual rekordbox
    /// instance might be listening). May only be changed while the service is stopped; <c>null</c>
    /// (the default) means the real <see cref="Ports.DbServerQueryPort"/> is used.
    /// </summary>
    private int? _dbServerQueryPortOverride;

    /// <inheritdoc cref="_dbServerQueryPortOverride"/>
    internal int? DbServerQueryPortOverride
    {
        get => _dbServerQueryPortOverride;
        set
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("DbServerQueryPortOverride can only be changed while the ConnectionManager is stopped.");
            }
            _dbServerQueryPortOverride = value;
        }
    }

    /// <summary>
    /// Keeps track of tasks currently querying for database server ports, indexed by the address
    /// of the device being queried, so we can make sure we only ask once, even when multiple
    /// devices share the same address.
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, Task> _activeQueryTasks = new();

    /// <summary>
    /// Query a player to determine the port on which its database server is running.
    /// </summary>
    /// <param name="announcement">the device announcement with which we detected a new player on
    /// the network</param>
    /// <param name="token">signaled when we are shutting down, so the query should be abandoned</param>
    private void RequestPlayerDbServerPort(DeviceAnnouncement announcement, CancellationToken token)
    {
        try
        {
            Logger.LogDebug("Trying to determine database server port for device {Number} at IP address {Address}",
                announcement.DeviceNumber, announcement.Address);
            var queryPort = _dbServerQueryPortOverride ?? Ports.DbServerQueryPort;
            for (var tries = 0; tries < 4; ++tries)
            {
                if (tries > 0)
                {
                    // Give the player more time to be ready, it may be booting.
                    if (token.WaitHandle.WaitOne(1000 * tries))
                    {
                        Logger.LogInformation("Interrupted while trying to retry dbserver port query, must be shutting down.");
                        return;
                    }
                }

                Socket? socket = null;
                try
                {
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    // If we are stopped mid-query, closing the socket unblocks any pending
                    // connect or read, so the task can wind down promptly.
                    using var registration = token.Register(static state => ((Socket)state!).Close(), socket);
                    ConnectWithTimeout(socket, new IPEndPoint(announcement.Address, queryPort), SocketTimeout);
                    socket.ReceiveTimeout = SocketTimeout;
                    socket.SendTimeout = SocketTimeout;
                    socket.Send(DbServerQueryPacket);
                    var response = ReadResponseWithExpectedSize(socket);
                    if (response.Length == 2)
                    {
                        var portReturned = (int)Util.BytesToNumber(response, 0, 2);
                        if (portReturned == DbServerNotReady)
                        {
                            // (The Java implementation records and returns this sentinel value;
                            // we treat it as a signal to keep retrying, since it means the
                            // dbserver is not yet ready to accept connections.)
                            Logger.LogInformation("Device {Number} at address {Address} reported dbserver port of {Port}, not yet ready?",
                                announcement.DeviceNumber, announcement.Address, portReturned);
                        }
                        else
                        {
                            Logger.LogInformation("Device {Number} at address {Address} reported dbserver port of {Port}.",
                                announcement.DeviceNumber, announcement.Address, portReturned);
                            if (IsRunning)  // Bail if we were shut down before we received a response.
                            {
                                _dbServerPorts[announcement.Address] = portReturned;
                            }
                            return;  // Success!
                        }
                    }
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    Logger.LogInformation("Device {Number} at address {Address} doesn't answer rekordbox port queries, connection refused, not yet ready?",
                        announcement.DeviceNumber, announcement.Address);
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested)
                    {
                        Logger.LogDebug("Abandoning dbserver port query for device {Number} because we are stopping.",
                            announcement.DeviceNumber);
                        return;
                    }
                    Logger.LogWarning(e, "Problem requesting database server port number");
                }
                finally
                {
                    socket?.Dispose();
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }
            }

            Logger.LogInformation("Device {Number} at address {Address} never responded with a valid rekordbox dbserver port. Won't attempt to request metadata.",
                announcement.DeviceNumber, announcement.Address);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem querying for database server port on device {Number} at address {Address}:",
                announcement.DeviceNumber, announcement.Address);
        }
        finally
        {
            // No matter how we exit, record the fact that there is no longer a query active for this address.
            _activeQueryTasks.TryRemove(announcement.Address, out _);
        }
    }

    /// <summary>
    /// The default value we will use for timeouts on opening and reading from sockets, in
    /// milliseconds.
    /// </summary>
    public const int DefaultSocketTimeout = 10000;

    /// <summary>
    /// The number of milliseconds after which an attempt to open or read from a socket will fail.
    /// </summary>
    private volatile int _socketTimeout = DefaultSocketTimeout;

    /// <summary>
    /// How long we will wait, in milliseconds, for a socket to connect or for a read operation to
    /// complete. Adjust this if your players or network require it.
    /// </summary>
    public int SocketTimeout
    {
        get => _socketTimeout;
        set => _socketTimeout = value;
    }

    /// <summary>
    /// Receive an expected number of bytes from the player, logging a warning if we get a
    /// different number of them.
    /// </summary>
    /// <param name="socket">the socket connected to the player's port-query service</param>
    /// <returns>the bytes read</returns>
    /// <exception cref="IOException">if there is a problem reading the response</exception>
    private static byte[] ReadResponseWithExpectedSize(Socket socket)
    {
        var buffer = new byte[8192];
        var len = socket.Receive(buffer);
        if (len < 1)
        {
            throw new IOException($"ReadResponseWithExpectedSize read {len} bytes.");
        }
        if (len != 2)
        {
            Logger.LogWarning("Expected 2 bytes while reading database server port query packet response, received {Length}", len);
        }
        return buffer[..len];
    }

    /// <summary>
    /// Finds a valid player number that is currently visible but which is different from the one
    /// specified, so it can be used as the source player for a query being sent to the specified
    /// one. If the virtual CDJ is running on an acceptable player number (which must be 1-4 to
    /// request metadata from an actual CDJ, but can be anything if we are talking to rekordbox),
    /// uses that, since it will always be safe. Otherwise, tries to borrow the player number of
    /// another actual CDJ on the network, but we can't do that if the player we want to
    /// impersonate has mounted a track from the player that we want to talk to. (Internal, rather
    /// than private as in the Java implementation, so the logic can be unit tested directly.)
    /// </summary>
    /// <param name="targetPlayer">the player to which a metadata query is being sent</param>
    /// <returns>some other currently active player number, ideally not a real player, but
    /// sometimes we have to</returns>
    /// <exception cref="InvalidOperationException">if there is no other player number available to use</exception>
    internal int ChooseAskingPlayerNumber(DeviceAnnouncement targetPlayer)
    {
        var fakeDevice = VirtualCdj.Instance.DeviceNumber;

        if (fakeDevice > 4 && !DeviceFinder.Instance.IsDeviceMetadataLimited(targetPlayer))
        {
            return targetPlayer.DeviceNumber;
        }

        if (targetPlayer.DeviceNumber > 15 || (fakeDevice >= 1 && fakeDevice <= 4))
        {
            return fakeDevice;
        }

        foreach (var candidate in DeviceFinder.Instance.CurrentDevices)
        {
            var realDevice = candidate.DeviceNumber;
            if (realDevice != targetPlayer.DeviceNumber && realDevice >= 1 && realDevice <= 4)
            {
                var lastUpdate = VirtualCdj.Instance.GetLatestStatusFor(realDevice);
                if (lastUpdate is CdjStatus status && status.TrackSourcePlayer != targetPlayer.DeviceNumber)
                {
                    return candidate.DeviceNumber;
                }
            }
        }
        throw new InvalidOperationException($"No player number available to query player {targetPlayer}. " +
            "If such a player is present on the network, it must be using Link to play a track from " +
            "our target player, so we can't steal its channel number.");
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently offering shared dbserver sessions.
    /// </summary>
    public override bool IsRunning => _running;

    /// <summary>
    /// Responds to the <see cref="DeviceFinder"/> stopping by stopping ourselves, since we can no
    /// longer find any players. (We do not auto-start when it starts.)
    /// </summary>
    private void OnDeviceFinderStopped(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            Logger.LogInformation("ConnectionManager stopping because DeviceFinder did.");
            Stop();
        }
    }

    /// <summary>
    /// Signaled when we are stopping, so the port-query tasks and the idle-client closer can wind
    /// down promptly. Created each time we start; cancelled (but deliberately never disposed, so
    /// that late-arriving device-found events can safely observe its token) when we stop.
    /// </summary>
    private volatile CancellationTokenSource? _stopCancellation;

    /// <summary>
    /// Finds any clients which are not currently in use, and which have been idle for longer than
    /// the idle timeout, and closes them.
    /// </summary>
    private void CloseIdleClients()
    {
        lock (_lock)
        {
            var candidates = new List<Client>(_openClients.Values);
            // Trace, not Debug: this fires every 500 ms for the life of the service, and at
            // Debug level it floods UI-visible log sinks (observed locking up the ShowControl
            // log pane, which scrolls on every new row).
            Logger.LogTrace("Scanning for idle clients; {Count} candidates.", candidates.Count);
            foreach (var client in candidates)
            {
                if (_useCounts.GetValueOrDefault(client) < 1 &&
                    _timestamps.GetValueOrDefault(client) + EffectiveIdleLimitMillis <= NowMillis)
                {
                    Logger.LogDebug("Idle time reached for unused client {Client}", client);
                    CloseClient(client);
                }
            }
        }
    }

    /// <summary>
    /// The body of the idle-client closer: wakes every half second and closes any clients that
    /// have been idle for longer than the idle limit, until we are stopped.
    /// </summary>
    /// <param name="token">signaled when we are stopping</param>
    private async Task RunIdleCloserAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                CloseIdleClients();
            }
        }
        catch (OperationCanceledException)
        {
            // We are stopping.
        }
        Logger.LogInformation("Idle dbserver client closer shutting down.");
    }

    /// <summary>
    /// Start offering shared dbserver sessions. This starts the <see cref="DeviceFinder"/> (if it
    /// was not already running) so that players can be located, and immediately queries any
    /// players it already knows about for their database server ports.
    /// </summary>
    /// <exception cref="SocketException">if there is a problem starting the
    /// <see cref="DeviceFinder"/></exception>
    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                return;
            }
            DeviceFinder.Instance.Stopped += OnDeviceFinderStopped;
            DeviceFinder.Instance.DeviceFound += OnDeviceFound;
            DeviceFinder.Instance.DeviceLost += OnDeviceLost;
            try
            {
                DeviceFinder.Instance.Start();
            }
            catch
            {
                DeviceFinder.Instance.DeviceLost -= OnDeviceLost;
                DeviceFinder.Instance.DeviceFound -= OnDeviceFound;
                DeviceFinder.Instance.Stopped -= OnDeviceFinderStopped;
                throw;
            }

            var cancellation = new CancellationTokenSource();
            _stopCancellation = cancellation;
            _running = true;

            foreach (var device in DeviceFinder.Instance.CurrentDevices)
            {
                HandleDeviceFound(device);
            }

            _ = Task.Run(() => RunIdleCloserAsync(cancellation.Token), CancellationToken.None);

            DeliverLifecycleAnnouncement(Logger, true);
        }
    }

    /// <summary>
    /// Stop offering shared dbserver sessions. Any ongoing port queries are cancelled, cached
    /// database server ports are discarded, and all open clients are closed.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning)
            {
                return;
            }
            _running = false;
            DeviceFinder.Instance.DeviceLost -= OnDeviceLost;
            DeviceFinder.Instance.DeviceFound -= OnDeviceFound;
            DeviceFinder.Instance.Stopped -= OnDeviceFinderStopped;
            _stopCancellation?.Cancel();  // Cancel any ongoing attempts to find server ports, and the idle closer.
            _dbServerPorts.Clear();
            foreach (var client in _openClients.Values)
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "Problem closing {Client} when stopping", client);
                }
            }
            _openClients.Clear();
            _useCounts.Clear();
            _timestamps.Clear();
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    // -- Test seams ------------------------------------------------------------------------

    /// <summary>Test seam: the number of clients currently held open in the pool.</summary>
    internal int OpenClientCountForTests
    {
        get { lock (_lock) { return _openClients.Count; } }
    }

    /// <summary>Test seam: the pooled client for the given player, if one is currently open.</summary>
    internal Client? GetOpenClientForTests(int player)
    {
        lock (_lock)
        {
            return _openClients.GetValueOrDefault(player);
        }
    }

    /// <summary>Test seam: the number of database server ports currently cached.</summary>
    internal int CachedPortCountForTests => _dbServerPorts.Count;

    /// <summary>Test seam: the number of port-query tasks currently in flight.</summary>
    internal int ActiveQueryCountForTests => _activeQueryTasks.Count;

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static ConnectionManager Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private ConnectionManager()
    {
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder("ConnectionManager[running:").Append(IsRunning);
            sb.Append(", dbServerPorts:{").Append(string.Join(", ", _dbServerPorts.Select(e => $"{e.Key}={e.Value}"))).Append('}');
            sb.Append(", openClients:{").Append(string.Join(", ", _openClients.Select(e => $"{e.Key}={e.Value}"))).Append('}');
            sb.Append(", useCounts:{").Append(string.Join(", ", _useCounts.Select(e => $"{e.Key}={e.Value}"))).Append('}');
            return sb.Append(", idleLimit:").Append(IdleLimit).Append(']').ToString();
        }
    }
}
