using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// The payload of the <see cref="BeatFinder.MasterYieldResponded"/> event, reporting the response
/// of a player which was asked to yield the tempo master role to us.
/// </summary>
/// <param name="Player">the device number that is responding to our request that it yield the tempo master role to us</param>
/// <param name="Yielded"><c>true</c> if we should now be the tempo master</param>
public sealed record MasterYieldResponse(int Player, bool Yielded);

/// <summary>
/// The payload of the <see cref="BeatFinder.FaderStartCommanded"/> event, reporting which players
/// the mixer has told to start and stop playing.
/// </summary>
/// <param name="PlayersToStart">the device numbers of all players that should start playing</param>
/// <param name="PlayersToStop">the device numbers of all players that should stop playing</param>
public sealed record FaderStartCommand(IReadOnlySet<int> PlayersToStart, IReadOnlySet<int> PlayersToStop);

/// <summary>
/// Allows library components (the <c>VirtualCdj</c> and, later, the <c>TimeFinder</c>) to receive
/// beats synchronously on the receive thread before the public <see cref="BeatFinder.BeatReceived"/>
/// event fires, in registration order. This replaces the hard-coded calls the Java implementation
/// made to <c>VirtualCdj.processBeat</c> and the special-cased <c>TimeFinder</c> beat listener,
/// preserving the delivery ordering semantics.
/// </summary>
internal interface IPriorityBeatListener
{
    /// <summary>
    /// Called synchronously on the receive thread for each beat, before public subscribers.
    /// </summary>
    /// <param name="beat">the message announcing the new beat</param>
    void OnBeat(Beat beat);
}

/// <summary>
/// <para>Watches for devices to report new beats by broadcasting beat packets on port 50001, and
/// passes them on to registered listeners. When players are actively playing music, they send beat
/// packets at the start of each beat which, in addition to announcing the start of the beat,
/// provide information about where the beat falls within a measure of music (assuming that the
/// track was properly configured in rekordbox, and is in 4/4 time), the current BPM of the track
/// being played, and the current player pitch adjustment, from which the actual effective BPM can
/// be calculated.</para>
///
/// <para>When players are stopped, they do not send beat packets, but the mixer continues sending
/// them at the last BPM reported by the master player, so it acts as the most reliable
/// synchronization source. The mixer does not make any effort to keep its notion of measures
/// (downbeats) consistent with any player, however. So systems which want to stay in sync with
/// measures as well as beats will want to use the <see cref="VirtualCdj"/> to maintain awareness
/// of which player is the master player.</para>
///
/// <para>This class also receives special sync and on-air control messages which are sent to the
/// same port as beat packets. If the <see cref="VirtualCdj"/> is sending status packets, it needs
/// to be notified about these so it can properly update its sync and on-air state if the mixer
/// tells it to, so it will ensure the <see cref="BeatFinder"/> is running whenever it is sending
/// status updates.</para>
///
/// <para>All events raised by this class are delivered synchronously on the packet receive thread
/// to minimize latency, so handlers must finish very quickly; do any lengthy processing (or any
/// user-interface work) on another thread.</para>
/// </summary>
public sealed class BeatFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<BeatFinder>();

    /// <summary>
    /// The port to which devices broadcast beat messages.
    /// </summary>
    public const int BeatPort = Ports.BeatPort;

    /// <summary>
    /// The socket used to listen for beat packets while we are active, or <c>null</c> when we are
    /// not.
    /// </summary>
    private volatile Socket? _socket;

    /// <summary>
    /// Used to serialize starting and stopping the service.
    /// </summary>
    private readonly Lock _lifecycleLock = new();

    /// <summary>
    /// Test hook allowing the service to be bound to an alternate port (or an ephemeral port, by
    /// supplying zero) so tests can run without the real beat port, and in parallel with any actual
    /// DJ Link traffic. May only be changed while the service is stopped; <c>null</c> (the
    /// default) means the real <see cref="BeatPort"/> is used.
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
                throw new InvalidOperationException("PortOverride can only be changed while the BeatFinder is stopped.");
            }
            _portOverride = value;
        }
    }

    /// <summary>
    /// Reports the local port to which our beat socket is actually bound, or <c>null</c> if we are
    /// not running. Used by tests which bind to an ephemeral port.
    /// </summary>
    internal int? LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port;

    /// <summary>
    /// Check whether we are presently listening for beat packets.
    /// </summary>
    public override bool IsRunning => _socket != null;

    /// <summary>
    /// Keeps track of the warnings we have already issued for packets of a given type with a given
    /// unexpected size, so we only issue them once.
    /// </summary>
    private static readonly Dictionary<string, HashSet<int>> SizeWarningsIssued = new();

    /// <summary>
    /// Checks whether this is the first time we have seen a particular bad size for a given packet
    /// type, and returns <c>true</c> in that circumstance, making note that we have now seen this
    /// combination, so we will return <c>false</c> for it in the future.
    /// </summary>
    /// <param name="packetName">the type of packet to be warned about</param>
    /// <param name="unexpectedLength">the number of bytes in this packet, which we did not expect to see</param>
    /// <returns><c>true</c> if this is the first time we have seen a packet like this</returns>
    private static bool ShouldWarn(string packetName, int unexpectedLength)
    {
        lock (SizeWarningsIssued)
        {
            if (!SizeWarningsIssued.TryGetValue(packetName, out var sizeSet))
            {
                sizeSet = [];
                SizeWarningsIssued[packetName] = sizeSet;
            }
            return sizeSet.Add(unexpectedLength);
        }
    }

    /// <summary>
    /// Helper method to check that we got the right size packet.
    /// </summary>
    /// <param name="length">the number of bytes the received packet contains</param>
    /// <param name="expectedLength">the number of bytes we expect it to contain</param>
    /// <param name="name">the description of the packet in case we need to report issues with the length</param>
    /// <returns><c>true</c> if enough bytes were received to process the packet</returns>
    private static bool IsPacketLongEnough(int length, int expectedLength, string name)
    {
        if (length < expectedLength && ShouldWarn(name, length))
        {
            Logger.LogWarning("Ignoring too-short {Name} packet; expecting {Expected} bytes and got {Length}.",
                name, expectedLength, length);
            return false;
        }

        if (length > expectedLength && ShouldWarn(name, length))
        {
            Logger.LogWarning("Processing too-long {Name} packet; expecting {Expected} bytes and got {Length}.",
                name, expectedLength, length);
        }

        return length >= expectedLength;
    }

    /// <summary>
    /// Used to keep track of the last time we saw a channels-on-air packet from an XDJ-AZ (as a
    /// monotonic nanosecond timestamp, zero meaning never), which would tell us that it is
    /// operating in Pro DJ Link mode.
    /// </summary>
    private long _lastSeenXdjAzChannelsOnAir;

    /// <summary>
    /// Check if there seems to be an XDJ-AZ on the network that is operating in Pro DJ Link mode,
    /// by seeing if we have received a Channels On-Air packet from one within the past second.
    /// Experimental.
    /// </summary>
    /// <exception cref="InvalidOperationException">if we are not running</exception>
    public bool CanSeeXdjAzInProDjLinkMode
    {
        get
        {
            EnsureRunning();
            var saw = Interlocked.Read(ref _lastSeenXdjAzChannelsOnAir);
            return saw != 0 && Util.CurrentTimestampNanos() - saw < 1_000_000_000L;
        }
    }

    /// <summary>
    /// The bytes of the XDJ-AZ device name, used to recognize packets sent by one.
    /// </summary>
    private static readonly byte[] XdjAzNameBytes = Encoding.ASCII.GetBytes(Data.OpusProvider.XdjAzName);

    /// <summary>
    /// Checks if a device update packet was sent by an XDJ-AZ by looking at the device name field.
    /// </summary>
    /// <param name="packet">the packet that was received</param>
    /// <returns><c>true</c> if the device name matches XDJ-AZ</returns>
    private static bool IsFromXdjAz(ReadOnlySpan<byte> packet)
    {
        var length = XdjAzNameBytes.Length;
        return packet.Length > 0x0b + length + 1 &&
               packet.Slice(0x0b, length).SequenceEqual(XdjAzNameBytes) &&
               packet[0x0b + length + 1] == 0;
    }

    /// <summary>
    /// Start listening for beat announcements and sync commands. If already listening, has no
    /// effect.
    /// </summary>
    /// <exception cref="SocketException">if the socket to listen on port 50001 cannot be created,
    /// for example because another application already has exclusive use of the port</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return;
            }
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ExclusiveAddressUse = true
            };
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, _portOverride ?? BeatPort));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            _socket = socket;
            DeliverLifecycleAnnouncement(Logger, true);

            var receiver = new Thread(() => ReceiveLoop(socket))
            {
                Name = "beat-link BeatFinder receiver",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            receiver.Start();
        }
    }

    /// <summary>
    /// The body of the receiver thread: blocks waiting for packets on the beat port, and processes
    /// each according to its type.
    /// </summary>
    /// <param name="socket">the socket on which packets are received</param>
    private void ReceiveLoop(Socket socket)
    {
        var buffer = new byte[512];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        // Run for as long as our socket remains the active one (a plain IsRunning check would race
        // with a rapid stop-then-restart, letting a stale thread shut down its replacement).
        while (ReferenceEquals(_socket, socket))
        {
            var received = 0;
            try
            {
                received = socket.ReceiveFrom(buffer, ref sender);
                if (DeviceFinder.Instance.IsAddressIgnored(((IPEndPoint)sender).Address))
                {
                    received = 0;
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Don't log a warning if the exception was due to the socket closing at shutdown.
                if (ReferenceEquals(_socket, socket))
                {
                    // We did not expect to have a problem; log a warning and shut down.
                    Logger.LogWarning(e, "Problem reading from beat/sync socket, stopping");
                    Stop();
                }
                received = 0;
            }
            try
            {
                if (received > 0)
                {
                    ProcessPacket(buffer.AsSpan(0, received), ((IPEndPoint)sender).Address);
                }
            }
            catch (Exception t)
            {
                Logger.LogWarning(t, "Problem processing beat packet");
            }
        }
    }

    /// <summary>
    /// Process a single packet received on the beat port, dispatching it based on its type.
    /// </summary>
    /// <param name="packet">the content of the packet</param>
    /// <param name="sourceAddress">the address from which it was received</param>
    private void ProcessPacket(ReadOnlySpan<byte> packet, IPAddress sourceAddress)
    {
        var kind = Util.ValidateHeader(packet, BeatPort);
        if (kind == null)
        {
            return;
        }

        if (kind == PacketType.Beat)
        {
            if (IsPacketLongEnough(packet.Length, 96, "beat"))
            {
                var result = new Beat(packet[..96], sourceAddress);
                if (!DeviceFinder.Instance.IsDeviceNameIgnored(result.DeviceName))
                {
                    DeliverBeat(result);
                }
            }
        }
        else if (kind == PacketType.PrecisePosition)
        {
            if (IsPacketLongEnough(packet.Length, 60, "precise position"))
            {
                var result = new PrecisePosition(packet[..60], sourceAddress);
                if (!DeviceFinder.Instance.IsDeviceNameIgnored(result.DeviceName))
                {
                    EventDispatcher.DeliverNow(PrecisePositionReceived, this, result, Logger);
                }
            }
        }
        else if (kind == PacketType.ChannelsOnAir)
        {
            if (packet.Length == 0x35 ||  // New DJM-V10 packet with six channels
                IsPacketLongEnough(packet.Length, 0x2d, "channels on-air"))
            {
                var deviceName = Util.DecodeAndTrim(packet, 0x0b, 20);
                if (!DeviceFinder.Instance.IsDeviceNameIgnored(deviceName))
                {
                    var audibleChannels = GetAudibleChannels(packet);
                    EventDispatcher.DeliverNow(ChannelsOnAir, this, audibleChannels, Logger);
                    if (IsFromXdjAz(packet))
                    {
                        // Record that we saw an XDJ-AZ channels-on-air packet.
                        Interlocked.Exchange(ref _lastSeenXdjAzChannelsOnAir, Util.CurrentTimestampNanos());
                    }
                }
            }
        }
        else if (kind == PacketType.SyncControl)
        {
            if (IsPacketLongEnough(packet.Length, 0x2c, "sync control command"))
            {
                var deviceName = Util.DecodeAndTrim(packet, 0x0b, 20);
                if (!DeviceFinder.Instance.IsDeviceNameIgnored(deviceName))
                {
                    DeliverSyncCommand(packet[0x2b]);
                }
            }
        }
        else if (kind == PacketType.MasterHandoffRequest)
        {
            if (IsPacketLongEnough(packet.Length, 0x28, "tempo master handoff request"))
            {
                var deviceName = Util.DecodeAndTrim(packet, 0x0b, 20);
                if (!DeviceFinder.Instance.IsDeviceNameIgnored(deviceName))
                {
                    EventDispatcher.DeliverNow(MasterYieldRequested, this, (int)packet[0x21], Logger);
                }
            }
        }
        else if (kind == PacketType.MasterHandoffResponse)
        {
            if (IsPacketLongEnough(packet.Length, 0x2c, "tempo master handoff response"))
            {
                var deviceName = Util.DecodeAndTrim(packet, 0x0b, 20);
                if (!DeviceFinder.Instance.IsDeviceNameIgnored(deviceName))
                {
                    EventDispatcher.DeliverNow(MasterYieldResponded, this,
                        new MasterYieldResponse(packet[0x21], packet[0x2b] == 1), Logger);
                }
            }
        }
        else if (kind == PacketType.FaderStartCommand)
        {
            if (IsPacketLongEnough(packet.Length, 0x28, "fader start command"))
            {
                var deviceName = Util.DecodeAndTrim(packet, 0x0b, 20);
                if (!DeviceFinder.Instance.IsDeviceNameIgnored(deviceName))
                {
                    var playersToStart = new SortedSet<int>();
                    var playersToStop = new SortedSet<int>();
                    for (var channel = 1; channel <= 4; channel++)
                    {
                        switch (packet[0x23 + channel])
                        {
                            case 0:
                                playersToStart.Add(channel);
                                break;

                            case 1:
                                playersToStop.Add(channel);
                                break;

                            case 2:
                                // Leave this player alone.
                                break;

                            default:
                                Logger.LogWarning("Ignoring unrecognized fader start command, {Command}, for channel {Channel}",
                                    packet[0x23 + channel], channel);
                                break;
                        }
                    }
                    // (The Java implementation delivers this once per channel, inside its loop, with the sets only
                    // partially built; that is clearly unintended, so we deliver once, after examining all channels.)
                    EventDispatcher.DeliverNow(FaderStartCommanded, this,
                        new FaderStartCommand(playersToStart, playersToStop), Logger);
                }
            }
        }
        else
        {
            Logger.LogWarning("Ignoring packet received on beat port with unexpected type: {Kind}", kind);
        }
    }

    /// <summary>
    /// Finds the channel numbers that are marked as being on-air in a Channels On-Air packet.
    /// </summary>
    /// <param name="packet">the packet we received</param>
    /// <returns>the channels it reports as being on-air</returns>
    private static IReadOnlySet<int> GetAudibleChannels(ReadOnlySpan<byte> packet)
    {
        var audibleChannels = new SortedSet<int>();
        for (var channel = 1; channel <= 4; channel++)
        {
            if (packet[0x23 + channel] != 0)
            {
                audibleChannels.Add(channel);
            }
        }
        if (packet.Length >= 0x35)
        {
            for (var channel = 5; channel <= 6; channel++)
            {
                if (packet[0x28 + channel] != 0)
                {
                    audibleChannels.Add(channel);
                }
            }
        }
        return audibleChannels;
    }

    /// <summary>
    /// Stop listening for beats.
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
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// Keeps track of the registered priority beat listeners, which are invoked in registration
    /// order, synchronously, before the public <see cref="BeatReceived"/> event fires. Guarded by
    /// locking the list itself.
    /// </summary>
    private readonly List<IPriorityBeatListener> _priorityBeatListeners = [];

    /// <summary>
    /// Register a library component to receive beats synchronously before the public
    /// <see cref="BeatReceived"/> event fires. Listeners are invoked in registration order; the
    /// <c>VirtualCdj</c> registers first and the <c>TimeFinder</c> second, preserving the delivery
    /// order the Java implementation hard-coded. Registering a listener that is already present
    /// has no effect.
    /// </summary>
    /// <param name="listener">the priority beat listener to add</param>
    internal void AddPriorityBeatListener(IPriorityBeatListener listener)
    {
        lock (_priorityBeatListeners)
        {
            if (!_priorityBeatListeners.Contains(listener))
            {
                _priorityBeatListeners.Add(listener);
            }
        }
    }

    /// <summary>
    /// Remove a previously registered priority beat listener. Removing a listener that is not
    /// present has no effect.
    /// </summary>
    /// <param name="listener">the priority beat listener to remove</param>
    internal void RemovePriorityBeatListener(IPriorityBeatListener listener)
    {
        lock (_priorityBeatListeners)
        {
            _priorityBeatListeners.Remove(listener);
        }
    }

    /// <summary>
    /// <para>Raised when a DJ Link device reports a new beat.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving beats from the network, so if you want to interact with user interface objects in
    /// handlers, you need to arrange to do so on your UI thread. Even if you are not interacting
    /// with user interface objects, any code in a handler must finish quickly, or it will add
    /// latency for other handlers, and beat announcements will back up.</para>
    /// </summary>
    public event EventHandler<Beat>? BeatReceived;

    /// <summary>
    /// Send a beat announcement to all priority listeners (the <c>VirtualCdj</c> and the
    /// <c>TimeFinder</c>, once they have registered) and then to the public
    /// <see cref="BeatReceived"/> event subscribers.
    /// </summary>
    /// <param name="beat">the message announcing the new beat</param>
    private void DeliverBeat(Beat beat)
    {
        IPriorityBeatListener[] priorityListeners;
        lock (_priorityBeatListeners)
        {
            priorityListeners = [.. _priorityBeatListeners];
        }
        foreach (var listener in priorityListeners)
        {
            try
            {
                listener.OnBeat(beat);
            }
            catch (Exception t)
            {
                Logger.LogWarning(t, "Problem delivering beat announcement to priority listener");
            }
        }
        EventDispatcher.DeliverNow(BeatReceived, this, beat, Logger);
    }

    /// <summary>
    /// <para>Raised when a CDJ-3000 (or newer) player reports its exact playback position, which
    /// happens roughly every 30 milliseconds as long as it has a track loaded.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<PrecisePosition>? PrecisePositionReceived;

    /// <summary>
    /// <para>Raised when the mixer broadcasts a channels on-air update; the payload holds the
    /// device numbers of all channels that can currently be heard in the mixer output.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<IReadOnlySet<int>>? ChannelsOnAir;

    /// <summary>
    /// <para>Raised when the mixer commands us to turn sync mode on (<c>true</c>) or off
    /// (<c>false</c>).</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<bool>? SyncModeCommanded;

    /// <summary>
    /// <para>Raised when the mixer commands us to become the tempo master. (As in the Java
    /// implementation, this is immediately followed by a <see cref="SyncModeCommanded"/> event
    /// with the value <c>true</c>, because becoming master implies being synced.)</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<EventArgs>? MasterCommanded;

    /// <summary>
    /// Send a sync command to all registered listeners.
    /// </summary>
    /// <param name="command">the byte which identifies the type of sync command we received</param>
    private void DeliverSyncCommand(byte command)
    {
        switch (command)
        {
            case 0x01:
                EventDispatcher.DeliverNow(MasterCommanded, this, EventArgs.Empty, Logger);
                // The Java implementation falls through here, so a become-master command also
                // reports that sync mode has been turned on.
                EventDispatcher.DeliverNow(SyncModeCommanded, this, true, Logger);
                break;

            case 0x10:
                EventDispatcher.DeliverNow(SyncModeCommanded, this, true, Logger);
                break;

            case 0x20:
                EventDispatcher.DeliverNow(SyncModeCommanded, this, false, Logger);
                break;
        }
    }

    /// <summary>
    /// <para>Raised when an incoming tempo master asks us to relinquish that role; the payload is
    /// the device number to which we are being instructed to yield the tempo master role.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<int>? MasterYieldRequested;

    /// <summary>
    /// <para>Raised when a player responds to our request that it yield the tempo master role to
    /// us.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<MasterYieldResponse>? MasterYieldResponded;

    /// <summary>
    /// <para>Raised when the mixer broadcasts a fader start command, telling a set of players to
    /// start and/or stop playing.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that is
    /// receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<FaderStartCommand>? FaderStartCommanded;

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static BeatFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private BeatFinder()
    {
    }

    /// <inheritdoc/>
    public override string ToString() => $"BeatFinder[active:{IsRunning}]";
}
