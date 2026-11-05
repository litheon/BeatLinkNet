using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Watches for devices to report their presence by broadcasting announcement packets on port 50000,
/// and keeps a list of the devices that have been seen, and the network address on which they were
/// seen.
/// </summary>
public sealed class DeviceFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<DeviceFinder>();

    /// <summary>
    /// The port to which devices broadcast announcement messages to report their presence on the
    /// network.
    /// </summary>
    public const int AnnouncementPort = Ports.AnnouncementPort;

    /// <summary>
    /// The number of milliseconds after which we will consider a device to have disappeared if we
    /// have not received an announcement from it.
    /// </summary>
    public const int MaximumAge = 10000;

    /// <summary>
    /// Test hook allowing the device expiration age to be shortened so expiry can be exercised
    /// without waiting ten seconds. Defaults to <see cref="MaximumAge"/>.
    /// </summary>
    internal volatile int MaximumAgeMillis = MaximumAge;

    /// <summary>
    /// The socket used to listen for announcement packets while we are active, or <c>null</c> when
    /// we are not.
    /// </summary>
    private volatile Socket? _socket;

    /// <summary>
    /// Used to serialize starting and stopping the service.
    /// </summary>
    private readonly Lock _lifecycleLock = new();

    /// <summary>
    /// Track when we started listening for announcement packets, as monotonic milliseconds derived
    /// from <see cref="Util.CurrentTimestampNanos"/>.
    /// </summary>
    private long _startTime;

    /// <summary>
    /// Track when we saw the first announcement packet, to help the <see cref="VirtualCdj"/>
    /// determine how long it needs to watch for devices in order to avoid conflicts when
    /// self-assigning a device number. Will be zero when none have yet been seen.
    /// </summary>
    private long _firstDeviceTime;

    /// <summary>
    /// Indicates whether we have seen any players older than a CDJ-3000, which are unable to
    /// respond to metadata queries from more than three other players.
    /// </summary>
    private volatile bool _limit3PlayersSeen;

    /// <summary>
    /// Test hook allowing the service to be bound to an alternate port (or an ephemeral port, by
    /// supplying zero) so tests can run without the real announcement port, and in parallel with
    /// any actual DJ Link traffic. May only be changed while the service is stopped; <c>null</c>
    /// (the default) means the real <see cref="AnnouncementPort"/> is used.
    /// </summary>
    private int? _portOverride;

    /// <inheritdoc cref="_portOverride"/>
    internal int? PortOverride
    {
        get => _portOverride;
        set
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("PortOverride can only be changed while the DeviceFinder is stopped.");
            }
            _portOverride = value;
        }
    }

    /// <summary>
    /// Reports the local port to which our announcement socket is actually bound, or <c>null</c>
    /// if we are not running. Used by tests which bind to an ephemeral port.
    /// </summary>
    internal int? LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port;

    /// <summary>
    /// Check whether we are presently listening for device announcements.
    /// </summary>
    public override bool IsRunning => _socket != null;

    /// <summary>
    /// Get the timestamp of when we started listening for device announcements, in monotonic
    /// milliseconds (derived from <see cref="Util.CurrentTimestampNanos"/>, so values are only
    /// meaningful relative to other monotonic timestamps in the current process).
    /// </summary>
    /// <exception cref="InvalidOperationException">if we are not listening for announcements</exception>
    public long StartTime
    {
        get
        {
            EnsureRunning();
            return Interlocked.Read(ref _startTime);
        }
    }

    /// <summary>
    /// Get the timestamp of when we saw the first announcement packet, to help the
    /// <see cref="VirtualCdj"/> determine how long it needs to watch for devices in order to avoid
    /// conflicts when self-assigning a device number. Reported in monotonic milliseconds (derived
    /// from <see cref="Util.CurrentTimestampNanos"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">if we are not listening for announcements, or if none have been seen</exception>
    public long FirstDeviceTime
    {
        get
        {
            EnsureRunning();
            var result = Interlocked.Read(ref _firstDeviceTime);
            if (result == 0)
            {
                throw new InvalidOperationException("No device announcements have yet been seen");
            }
            return result;
        }
    }

    /// <summary>
    /// Keep track of the announcements we have seen.
    /// </summary>
    private readonly ConcurrentDictionary<DeviceReference, DeviceAnnouncement> _devices = new();

    /// <summary>
    /// Remove any device announcements that are so old that the device seems to have gone away.
    /// </summary>
    private void ExpireDevices()
    {
        var now = Util.CurrentTimestampNanos();
        // Take a snapshot, so we don't have to worry about concurrent modification.
        foreach (var entry in _devices.ToArray())
        {
            if ((now - entry.Value.Timestamp) / 1_000_000 > MaximumAgeMillis)
            {
                Logger.LogDebug("Expiring: {Announcement}", entry.Value);
                _devices.TryRemove(entry.Key, out _);
                DeliverLostAnnouncement(entry.Value);
            }
        }
        if (_devices.IsEmpty)
        {
            // We have lost contact with the Pro DJ Link network, so start over with the next device.
            Interlocked.Exchange(ref _firstDeviceTime, 0);
        }
    }

    /// <summary>
    /// Record a device announcement in the devices map, so we know we saw it.
    /// </summary>
    /// <param name="announcement">the announcement to be recorded</param>
    private void UpdateDevices(DeviceAnnouncement announcement)
    {
        Interlocked.CompareExchange(ref _firstDeviceTime, Util.CurrentTimestampNanos() / 1_000_000, 0);
        _devices[DeviceReference.GetDeviceReference(announcement.DeviceNumber, announcement.Address)] = announcement;
    }

    /// <summary>
    /// Check whether a device is already known, or if it is newly found.
    /// </summary>
    /// <param name="announcement">the message from the device to be considered</param>
    /// <returns><c>true</c> if this is the first message from this device</returns>
    private bool IsDeviceNew(DeviceAnnouncement announcement) =>
        !_devices.ContainsKey(DeviceReference.GetDeviceReference(announcement.DeviceNumber, announcement.Address));

    /// <summary>
    /// Maintain a set of addresses from which device announcements should be ignored. The
    /// <see cref="VirtualCdj"/> will add its socket's address to this set when it is active so that
    /// it does not show up in the set of devices found on the network.
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, byte> _ignoredAddresses = new();

    /// <summary>
    /// Start ignoring any device updates which are received from the specified address. Intended
    /// for use by the <see cref="VirtualCdj"/>, so that its updates do not cause it to appear as a
    /// device.
    /// </summary>
    /// <param name="address">the address from which any device updates should be ignored</param>
    public void AddIgnoredAddress(IPAddress address) => _ignoredAddresses.TryAdd(address, 0);

    /// <summary>
    /// Stop ignoring device updates which are received from the specified address. Intended for
    /// use by the <see cref="VirtualCdj"/>, so that when it shuts down, its socket stops being
    /// treated specially.
    /// </summary>
    /// <param name="address">the address from which any device updates should no longer be ignored</param>
    public void RemoveIgnoredAddress(IPAddress address) => _ignoredAddresses.TryRemove(address, out _);

    /// <summary>
    /// Checks whether the supplied address is an IPv6 address that represents a wrapped IPv4
    /// address (either an IPv4-mapped address like <c>::ffff:a.b.c.d</c> or an IPv4-compatible
    /// address like <c>::a.b.c.d</c>). If so, returns the IPv4 version of the address.
    /// </summary>
    /// <param name="address">an address to potentially unwrap</param>
    /// <returns>either the original address, or the IPv4 equivalent, if one exists</returns>
    public IPAddress UnwrapV6MappedV4Address(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return address.MapToIPv4();
            }
            var addressBytes = address.GetAddressBytes();
            var isCompatible = true;
            for (var i = 0; i < 12; i++)
            {
                if (addressBytes[i] != 0)
                {
                    isCompatible = false;
                    break;
                }
            }
            if (isCompatible)
            {
                // Extract the last 4 bytes, which hold the wrapped IPv4 address.
                return new IPAddress(addressBytes.AsSpan(12, 4));
            }
        }
        return address;
    }

    /// <summary>
    /// Check whether an address is being ignored. (The <see cref="BeatFinder"/> will call this, so
    /// it can filter out the <see cref="VirtualCdj"/>'s beat messages when it is broadcasting them,
    /// for example.)
    /// </summary>
    /// <param name="address">the address to be checked as a candidate to be ignored</param>
    /// <returns><c>true</c> if packets from the address should be ignored</returns>
    public bool IsAddressIgnored(IPAddress address) =>
        _ignoredAddresses.ContainsKey(UnwrapV6MappedV4Address(address));

    /// <summary>
    /// Maintain a set of device names from which device announcements should be ignored. For
    /// example if you want to try to coexist with ShowKontrol, you could add "TCS-SHOWKONTROL" to
    /// this list.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _ignoredDeviceNames = new();

    /// <summary>
    /// Start ignoring any packets whose device names match the supplied string. For example if you
    /// want to try to coexist with ShowKontrol, you could add "TCS-SHOWKONTROL" to this list.
    /// Experimental.
    /// </summary>
    /// <param name="name">the device name for which any device updates should be ignored</param>
    public void AddIgnoredDeviceName(string name) => _ignoredDeviceNames.TryAdd(name, 0);

    /// <summary>
    /// Stop ignoring device updates whose device names match the specified string. Experimental.
    /// </summary>
    /// <param name="name">the device name for which any device updates should no longer be ignored</param>
    public void RemoveIgnoredDeviceName(string name) => _ignoredDeviceNames.TryRemove(name, out _);

    /// <summary>
    /// Check whether a device name is being ignored. Experimental.
    /// </summary>
    /// <param name="name">the device name to be checked as a candidate to be ignored</param>
    /// <returns><c>true</c> if packets specifying that device name should be ignored</returns>
    public bool IsDeviceNameIgnored(string name) => _ignoredDeviceNames.ContainsKey(name);

    /// <summary>
    /// The names of devices that are able to respond to metadata requests from more than three
    /// other devices.
    /// </summary>
    public static readonly FrozenSet<string> MetadataFlexibleDevices =
        new[] { "CDJ-3000", Data.OpusProvider.XdjAzName }.ToFrozenSet();

    /// <summary>
    /// Handle a device announcement packet we have received.
    /// </summary>
    /// <param name="announcement">the device announcement that has been received</param>
    private void ProcessAnnouncement(DeviceAnnouncement announcement)
    {
        var foundNewDevice = IsDeviceNew(announcement);
        Logger.LogDebug("Announcement (new? {IsNew}): {Announcement}", foundNewDevice, announcement);
        UpdateDevices(announcement);
        if (foundNewDevice)
        {
            DeliverFoundAnnouncement(announcement);

            if (announcement.IsXdjAz)
            {
                // Start the BeatFinder so the VirtualCdj can check whether an XDJ-AZ seems to be in Pro DJ Link mode.
                try
                {
                    BeatFinder.Instance.Start();
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Unable to start BeatFinder in presence of XDJ-AZ");
                }
            }
        }
    }

    /// <summary>
    /// Handle a device announcement packet we have received from the Opus Quad, fanning it out
    /// into announcements for each of its four decks. Experimental.
    /// </summary>
    /// <param name="packet">the packet from the Opus Quad to infer the 4 players from</param>
    /// <param name="address">the address from which the packet was received</param>
    private void CreateAndProcessOpusAnnouncements(ReadOnlySpan<byte> packet, IPAddress address)
    {
        for (var i = 1; i <= 4; i++)
        {
            var opusAnnouncement = new DeviceAnnouncement(packet, address, i);
            // (The Java implementation records the announcement before checking whether the device
            // is new, so its found announcements can never fire; we check first, as was intended.)
            var foundNewDevice = IsDeviceNew(opusAnnouncement);
            UpdateDevices(opusAnnouncement);
            if (foundNewDevice)
            {
                DeliverFoundAnnouncement(opusAnnouncement);
            }
        }
    }

    /// <summary>
    /// <para>Start listening for device announcements and keeping track of the DJ Link devices
    /// visible on the network.</para>
    ///
    /// <para>If already active, has no effect.</para>
    /// </summary>
    /// <exception cref="SocketException">if the socket to listen on port 50000 cannot be created,
    /// for example because another application already has exclusive use of the port</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return;
            }
            Interlocked.Exchange(ref _startTime, Util.CurrentTimestampNanos() / 1_000_000);
            DeliverLifecycleAnnouncement(Logger, true);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ExclusiveAddressUse = true
            };
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, _portOverride ?? AnnouncementPort));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            _socket = socket;

            var receiver = new Thread(() => ReceiveLoop(socket))
            {
                Name = "beat-link DeviceFinder receiver",
                IsBackground = true
            };
            receiver.Start();
        }
    }

    /// <summary>
    /// The body of the receiver thread: watches for device announcement packets and processes
    /// them, expiring devices that have not been heard from recently on every pass.
    /// </summary>
    /// <param name="socket">the socket on which announcements are received</param>
    private void ReceiveLoop(Socket socket)
    {
        var buffer = new byte[512];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        // Run for as long as our socket remains the active one (a plain IsRunning check would race
        // with a rapid stop-then-restart, letting a stale thread shut down its replacement).
        while (ReferenceEquals(_socket, socket))
        {
            var received = 0;
            IPAddress? sourceAddress = null;
            try
            {
                // If we have no devices to check for timeout, block for a whole minute between
                // shutdown checks; otherwise check every second to see if a device has vanished.
                socket.ReceiveTimeout = _devices.IsEmpty ? 60000 : 1000;
                received = socket.ReceiveFrom(buffer, ref sender);
                sourceAddress = ((IPEndPoint)sender).Address;
                if (_ignoredAddresses.ContainsKey(sourceAddress))
                {
                    received = 0;
                }
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                received = 0;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Don't log a warning if the exception was due to the socket closing at shutdown.
                if (ReferenceEquals(_socket, socket))
                {
                    // We did not expect to have a problem; log a warning and shut down.
                    Logger.LogWarning(e, "Problem reading from DeviceAnnouncement socket, stopping");
                    Stop();
                }
                received = 0;
            }
            try
            {
                if (received > 0 && sourceAddress != null)
                {
                    ProcessPacket(buffer.AsSpan(0, received), sourceAddress);
                }
                ExpireDevices();
            }
            catch (Exception t)
            {
                Logger.LogWarning(t, "Problem processing DeviceAnnouncement packet");
            }
        }
    }

    /// <summary>
    /// Process a single packet received on the announcement port, dispatching it based on its type.
    /// </summary>
    /// <param name="packet">the content of the packet</param>
    /// <param name="sourceAddress">the address from which it was received</param>
    private void ProcessPacket(ReadOnlySpan<byte> packet, IPAddress sourceAddress)
    {
        var kind = Util.ValidateHeader(packet, AnnouncementPort);
        if (kind == PacketType.DeviceKeepAlive)
        {
            // Looks like the kind of packet we need.
            if (packet.Length < 0x36)
            {
                Logger.LogWarning("Ignoring too-short {Kind} packet; expected 54 bytes, but only got {Length}.",
                    kind.Name, packet.Length);
                return;
            }
            if (packet.Length > 0x36)
            {
                Logger.LogWarning("Processing too-long {Kind} packet; expected 54 bytes, but got {Length}.",
                    kind.Name, packet.Length);
            }

            var announcement = new DeviceAnnouncement(packet[..0x36], sourceAddress);
            if (IsDeviceNameIgnored(announcement.DeviceName))
            {
                Logger.LogDebug("Ignoring device announcement because of device name {Name}", announcement.DeviceName);
            }
            else if (announcement.IsOpusQuad)
            {
                CreateAndProcessOpusAnnouncements(packet[..0x36], sourceAddress);
            }
            else
            {
                ProcessAnnouncement(announcement);

                if (VirtualCdj.Instance.IsRunning &&
                    announcement.DeviceNumber == VirtualCdj.Instance.DeviceNumber)
                {
                    // Someone is using the same device number as we are! Try to defend it.
                    VirtualCdj.Instance.DefendDeviceNumber(announcement.Address);
                }
            }
        }
        else if (kind == PacketType.DeviceHello)
        {
            Logger.LogDebug("Received device hello packet.");
        }
        else if (kind != null)
        {
            var deviceName = Util.DecodeAndTrim(packet, 0x0c, 20);
            if (IsDeviceNameIgnored(deviceName))
            {
                Logger.LogDebug("Ignoring special announcement packet because of device name {Name}", deviceName);
            }
            else
            {
                VirtualCdj.Instance.HandleSpecialAnnouncementPacket(kind, packet, sourceAddress);
            }
        }
    }

    /// <summary>
    /// Discard any knowledge we have about current devices. Called when shutting down, and also by
    /// the <see cref="VirtualCdj"/> when it believes the network has changed in a way that makes
    /// them no longer reachable.
    /// </summary>
    internal void Flush()
    {
        var lastDevices = _devices.Values.ToArray();
        _devices.Clear();
        Interlocked.Exchange(ref _firstDeviceTime, 0);

        // Report the loss of all our devices, in the configured event delivery context, outside our
        // lock. The subscriber list is snapshotted now, rather than when the posted work runs, so
        // that handlers subscribed after the flush cannot receive these stale announcements.
        var handlers = DeviceLost;
        EventDispatcher.Post(() =>
        {
            foreach (var announcement in lastDevices)
            {
                UpdateMetadataLimitedTracking(announcement);
                EventDispatcher.DeliverNow(handlers, this, announcement, Logger);
            }
        });
    }

    /// <summary>
    /// Stop listening for device announcements. Also discard any announcements which had been
    /// received, and notify any registered listeners that those devices have been lost.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                return;
            }
            var socket = _socket;
            _socket = null;
            if (socket != null)
            {
                Util.WakeUdpReceiver(socket);
                socket.Close();
            }
            Flush();
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// Get the set of DJ Link devices which currently can be seen on the network. These can be
    /// passed to <see cref="VirtualCdj.GetLatestStatusFor(DeviceUpdate)"/> to find the current
    /// detailed status for that device, as long as the Virtual CDJ is active.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="DeviceFinder"/> is not active</exception>
    public IReadOnlySet<DeviceAnnouncement> CurrentDevices
    {
        get
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("DeviceFinder is not active");
            }
            ExpireDevices();  // Get rid of anything past its sell-by date.
            // Make a copy so callers get an immutable snapshot of the current state.
            return _devices.Values.ToHashSet();
        }
    }

    /// <summary>
    /// Check whether we have seen any players that are old enough to only be able to answer
    /// metadata queries from at most three other players. Experimental.
    /// </summary>
    public bool IsAnyDeviceLimitedToThreeDatabaseClients => _limit3PlayersSeen;

    /// <summary>
    /// Find and return the device announcement that was most recently received from a device
    /// identifying itself with the specified device number, if any.
    /// </summary>
    /// <param name="deviceNumber">the device number of interest</param>
    /// <returns>the matching announcement or <c>null</c> if no such device has been heard from</returns>
    /// <exception cref="InvalidOperationException">if the <see cref="DeviceFinder"/> is not active</exception>
    public DeviceAnnouncement? GetLatestAnnouncementFrom(int deviceNumber)
    {
        EnsureRunning();
        foreach (var announcement in CurrentDevices)
        {
            if (announcement.DeviceNumber == deviceNumber)
            {
                return announcement;
            }
        }
        return null;
    }

    /// <summary>
    /// <para>Raised when a new DJ Link device is found on the network. Delivered in the configured
    /// <see cref="EventDispatcher"/> context (thread-pool threads by default, or the UI thread if
    /// a <see cref="SynchronizationContext"/> has been assigned), so it is fine to interact with
    /// user interface objects in handlers.</para>
    ///
    /// <para>Any code in a handler must finish quickly, or unhandled events will back up and the
    /// user interface will become unresponsive.</para>
    /// </summary>
    public event EventHandler<DeviceAnnouncement>? DeviceFound;

    /// <summary>
    /// <para>Raised when a DJ Link device has disappeared from the network (no announcement has
    /// been received from it within <see cref="MaximumAge"/> milliseconds). Delivered in the
    /// configured <see cref="EventDispatcher"/> context (thread-pool threads by default, or the UI
    /// thread if a <see cref="SynchronizationContext"/> has been assigned).</para>
    /// </summary>
    public event EventHandler<DeviceAnnouncement>? DeviceLost;

    /// <summary>
    /// Checks whether the device is old enough that it is unable to respond to metadata queries
    /// from more than three other players. Experimental.
    /// </summary>
    /// <param name="announcement">the device to be examined</param>
    /// <returns><c>true</c> if the device appears to be a CDJ that is not known to support metadata
    /// queries from more than three other devices</returns>
    public bool IsDeviceMetadataLimited(DeviceAnnouncement announcement) =>
        announcement.DeviceNumber < 7 && !MetadataFlexibleDevices.Contains(announcement.DeviceName);

    /// <summary>
    /// Send a device found announcement to all registered listeners.
    /// </summary>
    /// <param name="announcement">the message announcing the new device</param>
    private void DeliverFoundAnnouncement(DeviceAnnouncement announcement)
    {
        // If this is a metadata-query-limited device, note that there is now at least one on the network.
        if (IsDeviceMetadataLimited(announcement))
        {
            _limit3PlayersSeen = true;
        }
        // Send out the device announcement.
        EventDispatcher.Deliver(DeviceFound, this, announcement, Logger);
    }

    /// <summary>
    /// Send a device lost announcement to all registered listeners.
    /// </summary>
    /// <param name="announcement">the last message received from the vanished device</param>
    private void DeliverLostAnnouncement(DeviceAnnouncement announcement)
    {
        UpdateMetadataLimitedTracking(announcement);

        // Send out the announcement.
        EventDispatcher.Deliver(DeviceLost, this, announcement, Logger);
    }

    /// <summary>
    /// If the device we lost was metadata-limited, see if it was the last one, and if so, update
    /// our tracker. (We check our internal map directly, rather than <see cref="CurrentDevices"/>,
    /// so this cannot throw when it runs after the <see cref="DeviceFinder"/> has been stopped, as
    /// happens when reporting devices lost during shutdown.)
    /// </summary>
    /// <param name="announcement">the last message received from the vanished device</param>
    private void UpdateMetadataLimitedTracking(DeviceAnnouncement announcement)
    {
        if (IsDeviceMetadataLimited(announcement))
        {
            var anotherSeen = false;
            foreach (var current in _devices.Values)
            {
                if (IsDeviceMetadataLimited(current))
                {
                    anotherSeen = true;
                    break;
                }
            }
            if (!anotherSeen)
            {
                _limit3PlayersSeen = false;
            }
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static DeviceFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private DeviceFinder()
    {
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("DeviceFinder[active:").Append(IsRunning);
        if (IsRunning)
        {
            sb.Append(", startTime:").Append(StartTime);
            sb.Append(", firstDeviceTime:").Append(Interlocked.Read(ref _firstDeviceTime));
            sb.Append(", currentDevices:[").Append(string.Join(", ", CurrentDevices)).Append(']');
        }
        return sb.Append(']').ToString();
    }
}
