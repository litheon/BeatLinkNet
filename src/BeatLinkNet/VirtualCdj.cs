using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using BeatLink.Data;
using BeatLink.Electro;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Describes the interface address the <see cref="VirtualCdj"/> chose to communicate with the DJ
/// Link network, playing the role of Java's <c>InterfaceAddress</c> (which .NET does not expose in
/// a directly equivalent form), augmented with the interface's hardware (MAC) address. Also used
/// as an injection seam so tests can supply a fabricated match instead of scanning real network
/// interfaces.
/// </summary>
/// <param name="Address">the local IPv4 address which shares a subnet with the DJ Link devices</param>
/// <param name="PrefixLength">the number of bits of the address which identify the network</param>
/// <param name="BroadcastAddress">the broadcast address of that subnet</param>
/// <param name="HardwareAddress">the MAC address of the network interface (six bytes, or empty if unavailable)</param>
internal sealed record MatchedAddressInfo(IPAddress Address, int PrefixLength, IPAddress? BroadcastAddress, byte[] HardwareAddress);

/// <summary>
/// <para>Provides the ability to create a virtual CDJ device that can lurk on a DJ Link network and
/// receive packets sent to players, monitoring the detailed state of the other devices. This
/// detailed information is helpful for augmenting what <see cref="BeatFinder"/> reports, allowing
/// you to keep track of which player is the tempo master, how many beats of a track have been
/// played, how close a player is getting to its next cue point, and more. It is also the
/// foundation for finding out the rekordbox ID of the loaded track, which supports all the
/// features associated with the <c>MetadataFinder</c>.</para>
///
/// <para>Ported from the beat-link <c>VirtualCdj</c> class by James Elliott.</para>
/// </summary>
public sealed partial class VirtualCdj : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<VirtualCdj>();

    /// <summary>
    /// The port to which other devices will send status update messages.
    /// </summary>
    public const int UpdatePort = Ports.UpdatePort;

    /// <summary>
    /// The position within a keep-alive packet at which the MAC address is stored.
    /// </summary>
    public const int MacAddressOffset = 38;

    /// <summary>
    /// The location of the device name in the announcement packet.
    /// </summary>
    public const int DeviceNameOffset = 0x0c;

    /// <summary>
    /// The length of the device name in the announcement packet.
    /// </summary>
    public const int DeviceNameLength = 0x14;

    /// <summary>
    /// The location of the device number in the announcement packet.
    /// </summary>
    public const int DeviceNumberOffset = 0x24;

    /// <summary>
    /// The longest beat we will report playing; if we are still playing and reach this beat, we
    /// will loop back to beat one. If we are told to jump to a larger beat than this, we map it
    /// back into the range we will play. This would be a little over nine hours at 120 bpm, which
    /// seems long enough for any track.
    /// </summary>
    public const int MaxBeat = 65536;

    /// <summary>
    /// The number of milliseconds for which the <see cref="DeviceFinder"/> needs to have been
    /// watching the network in order for us to be confident we can choose a device number that
    /// will not conflict.
    /// </summary>
    private const long SelfAssignmentWatchPeriod = 4000;

    /// <summary>
    /// Used to serialize starting and stopping the service, as well as the other operations the
    /// Java implementation performed in <c>synchronized</c> methods (this lock supports reentrant
    /// acquisition, as Java monitors do).
    /// </summary>
    private readonly Lock _lifecycleLock = new();

    /// <summary>
    /// The socket used to send and receive packets while we are active, or <c>null</c> when we
    /// are not.
    /// </summary>
    private volatile Socket? _socket;

    /// <summary>
    /// The local address to which our socket is bound while we are active, so we can tell the
    /// <see cref="DeviceFinder"/> to stop ignoring it during shutdown even after the socket has
    /// closed.
    /// </summary>
    private volatile IPAddress? _localAddress;

    /// <summary>
    /// Indicates we started <see cref="VirtualRekordbox"/> running, so we are just acting as a
    /// proxy for it, to work with the Opus Quad.
    /// </summary>
    private volatile bool _proxyingForVirtualRekordbox;

    /// <summary>
    /// Indicates that even though we are actually running, we are talking to an Opus Quad, so
    /// metadata requests need to go through the <c>OpusProvider</c> to an attached metadata
    /// archive.
    /// </summary>
    private volatile bool _inOpusQuadSQLiteMode;

    /// <summary>
    /// Keeps track of the fact that we are starting, so that <see cref="IsRunning"/> can return
    /// <c>false</c> even though we have a socket open while we are trying to establish a device
    /// number.
    /// </summary>
    private volatile bool _starting;

    /// <summary>
    /// Test hook that forces <see cref="IsRunning"/> to report <c>true</c> without any socket
    /// being open, so the tempo master state machine can be exercised by injecting synthetic
    /// updates rather than real network traffic. Never set outside tests.
    /// </summary>
    private volatile bool _testForcedRunning;

    /// <summary>
    /// Check whether we are simply proxying information from <see cref="VirtualRekordbox"/> so
    /// that we can work with the Opus Quad rather than real Pro DJ Link hardware, or otherwise
    /// running in a limited Opus Quad compatibility mode. Experimental, like all Opus Quad
    /// support.
    /// </summary>
    public bool InOpusQuadCompatibilityMode => _proxyingForVirtualRekordbox || _inOpusQuadSQLiteMode;

    /// <summary>
    /// Check whether we are presently posing as a virtual CDJ and receiving device status
    /// updates, or as rekordbox to receive lighting packets from the Opus Quad: <c>true</c> if
    /// our socket is open, sending presence announcements, and receiving status packets, or if we
    /// were started in a mode where we delegate most of our responsibility to
    /// <see cref="VirtualRekordbox"/>.
    /// </summary>
    public override bool IsRunning =>
        _testForcedRunning || _proxyingForVirtualRekordbox || (_socket != null && !_starting);

    /// <summary>
    /// Return the address being used by the virtual CDJ to send its own presence announcement
    /// broadcasts, or the one <see cref="VirtualRekordbox"/> is using when we are proxying for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public IPAddress? LocalAddress
    {
        get
        {
            EnsureRunning();
            return _localAddress ?? VirtualRekordbox.Instance.LocalAddress;
        }
    }

    /// <summary>
    /// The broadcast address on which we can reach the DJ Link devices. Determined when we start
    /// up by finding the network interface address on which we are receiving the other devices'
    /// announcement broadcasts.
    /// </summary>
    private volatile IPAddress? _broadcastAddress;

    /// <summary>
    /// Return the broadcast address used to reach the DJ Link network, or the one
    /// <see cref="VirtualRekordbox"/> is using when we are proxying for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public IPAddress? BroadcastAddress
    {
        get
        {
            EnsureRunning();
            return _broadcastAddress ?? VirtualRekordbox.Instance.BroadcastAddress;
        }
    }

    /// <summary>
    /// Keep track of the most recent updates we have seen, indexed by the device they came from.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DeviceReference, DeviceUpdate> _updates = new();

    /// <summary>
    /// When self-assigning a player number, should we try to use a value that is legal for a
    /// standard CDJ, in the range 1 to 4? By default, we do not, to avoid any potential conflict
    /// with real players. However, if the user is intending to use features (like becoming tempo
    /// master) which require Beat Link to operate with a device number in the standard range, and
    /// will always have fewer than four real players on the network, this can be set to
    /// <c>true</c>, and a device number in this range will be chosen if it is not in use on the
    /// network during startup.
    /// </summary>
    public bool UseStandardPlayerNumber { get; set; }

    /// <summary>
    /// <para>Get or set the device number used when sending presence announcements on the network
    /// to pose as a virtual CDJ. This starts out being zero unless you explicitly assign another
    /// value, which means the <see cref="VirtualCdj"/> should assign itself an unused device
    /// number by watching the network when you call <see cref="Start()"/>. If
    /// <see cref="UseStandardPlayerNumber"/> is <c>true</c>, self-assignment will try to find a
    /// value in the range 1 to 4. Otherwise, it will try to find a value in the range 7 to 15 (so
    /// that it does not even conflict with newer CDJ-3000 networks, which can use channels 5 and
    /// 6). Even when told to use a standard player number, if all device numbers in that range
    /// are already in use, we will be forced to use a larger number.</para>
    ///
    /// <para>Cannot be set while we are running; it will be reset to zero (enabling
    /// self-assignment) each time the <see cref="VirtualCdj"/> is stopped. If the mixer tells us
    /// that we are plugged into a channel-specific Ethernet port, we will honor that and use the
    /// device number specified by the mixer.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">if set while we are currently running</exception>
    public int DeviceNumber
    {
        get
        {
            lock (_keepAliveBytes)
            {
                return _keepAliveBytes[DeviceNumberOffset];
            }
        }
        set
        {
            lock (_lifecycleLock)
            {
                if (IsRunning)
                {
                    throw new InvalidOperationException("Can't change device number once started.");
                }
                lock (_keepAliveBytes)
                {
                    _keepAliveBytes[DeviceNumberOffset] = (byte)value;
                }
            }
        }
    }

    /// <summary>
    /// The interval, in milliseconds, at which we post presence announcements on the network.
    /// </summary>
    private volatile int _announceInterval = 1500;

    /// <summary>
    /// The interval, in milliseconds, at which we broadcast presence announcements on the network
    /// to pose as a virtual CDJ.
    /// </summary>
    /// <exception cref="ArgumentException">if set to a value not between 200 and 2000</exception>
    public int AnnounceInterval
    {
        get => _announceInterval;
        set
        {
            if (value < 200 || value > 2000)
            {
                throw new ArgumentException("Interval must be between 200 and 2000", nameof(value));
            }
            _announceInterval = value;
        }
    }

    /// <summary>
    /// Used to construct the keep-alive packet we broadcast in order to participate in the DJ
    /// Link network. Some of these bytes are fixed, some get replaced by things like our device
    /// name and number, MAC address, and IP address, as described in the
    /// <see href="https://djl-analysis.deepsymmetry.org/djl-analysis/startup.html#cdj-keep-alive">Packet Analysis document</see>.
    /// Also acts as the lock guarding all packet-template mutation (device name and number).
    /// </summary>
    private readonly byte[] _keepAliveBytes =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,   0x4f, 0x4c, 0x06, 0x00,  0x62, 0x65, 0x61, 0x74,
        0x2d, 0x6c, 0x69, 0x6e,  0x6b, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x02, 0x00, 0x36,  0x00, 0x01, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00,  0x01, 0x64
    ];

    /// <summary>
    /// The initial packet sent three times when coming online.
    /// </summary>
    private static readonly byte[] HelloTemplate =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,   0x4f, 0x4c, 0x0a, 0x00,  0x62, 0x65, 0x61, 0x74,
        0x2d, 0x6c, 0x69, 0x6e,  0x6b, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x04, 0x00, 0x26,  0x01, 0x40
    ];

    /// <summary>
    /// The first-stage device number claim packet series.
    /// </summary>
    private static readonly byte[] ClaimStage1Template =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,   0x4f, 0x4c, 0x00, 0x00,  0x62, 0x65, 0x61, 0x74,
        0x2d, 0x6c, 0x69, 0x6e,  0x6b, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x03, 0x00, 0x2c,  0x0d, 0x01, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// The second-stage device number claim packet series.
    /// </summary>
    private static readonly byte[] ClaimStage2Template =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,   0x4f, 0x4c, 0x02, 0x00,  0x62, 0x65, 0x61, 0x74,
        0x2d, 0x6c, 0x69, 0x6e,  0x6b, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x03, 0x00, 0x32,  0x00, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x0d, 0x00,
        0x01, 0x00
    ];

    /// <summary>
    /// The third-stage (final) device number claim packet series.
    /// </summary>
    private static readonly byte[] ClaimStage3Template =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,   0x4f, 0x4c, 0x04, 0x00,  0x62, 0x65, 0x61, 0x74,
        0x2d, 0x6c, 0x69, 0x6e,  0x6b, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x03, 0x00, 0x26,  0x0d, 0x00
    ];

    /// <summary>
    /// Packet used to acknowledge a mixer's intention to assign us a device number.
    /// </summary>
    private static readonly byte[] AssignmentRequestTemplate =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,   0x4f, 0x4c, 0x02, 0x01,  0x62, 0x65, 0x61, 0x74,
        0x2d, 0x6c, 0x69, 0x6e,  0x6b, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x02, 0x00, 0x32,  0x00, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x00
    ];

    /// <summary>
    /// Packet used to tell another device we are already using a device number.
    /// </summary>
    private static readonly byte[] DeviceNumberDefenseTemplate =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,   0x4f, 0x4c, 0x08, 0x00,  0x62, 0x65, 0x61, 0x74,
        0x2d, 0x6c, 0x69, 0x6e,  0x6b, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x01, 0x02, 0x00, 0x29,  0x00, 0x00, 0x00, 0x00,   0x00
    ];

    /// <summary>
    /// Get or set the name to be used in announcing our presence on the network. The name can be
    /// no longer than twenty bytes, and should be normal ASCII, no Unicode.
    /// </summary>
    /// <exception cref="ArgumentException">if set to a value whose encoding is longer than twenty bytes</exception>
    public string DeviceName
    {
        get
        {
            lock (_keepAliveBytes)
            {
                return Util.DecodeAndTrim(_keepAliveBytes, DeviceNameOffset, DeviceNameLength);
            }
        }
        set
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            if (encoded.Length > DeviceNameLength)
            {
                throw new ArgumentException($"name cannot be more than {DeviceNameLength} bytes long", nameof(value));
            }
            lock (_keepAliveBytes)
            {
                Array.Clear(_keepAliveBytes, DeviceNameOffset, DeviceNameLength);
                encoded.CopyTo(_keepAliveBytes, DeviceNameOffset);
            }
        }
    }

    /// <summary>
    /// Copies the current device name into the name field of a raw announcement-family packet
    /// template copy, clearing the field first.
    /// </summary>
    /// <param name="packet">the packet copy being built</param>
    private void WriteDeviceName(byte[] packet)
    {
        lock (_keepAliveBytes)
        {
            Array.Clear(packet, DeviceNameOffset, DeviceNameLength);
            Array.Copy(_keepAliveBytes, DeviceNameOffset, packet, DeviceNameOffset, DeviceNameLength);
        }
    }

    /// <summary>
    /// Returns a copy of the twenty bytes of the current device name, for use with
    /// <see cref="Util.BuildPacket"/>.
    /// </summary>
    private byte[] DeviceNameBytes()
    {
        lock (_keepAliveBytes)
        {
            return _keepAliveBytes.AsSpan(DeviceNameOffset, DeviceNameLength).ToArray();
        }
    }

    /// <summary>
    /// Keep track of which device has reported itself as the current tempo master.
    /// </summary>
    private DeviceUpdate? _tempoMaster;

    /// <summary>
    /// Check which device is the current tempo master, returning the <see cref="DeviceUpdate"/>
    /// packet in which it reported itself to be master. If there is no current tempo master
    /// returns <c>null</c>. Note that when we are acting as tempo master ourselves in order to
    /// control player tempo and beat alignment, this will also have a <c>null</c> value, as there
    /// is no real player that is acting as master; we will instead send tempo and beat updates
    /// ourselves.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public DeviceUpdate? TempoMaster
    {
        get
        {
            EnsureRunning();
            return Volatile.Read(ref _tempoMaster);
        }
    }

    /// <summary>
    /// Establish a new tempo master, and if it is a change from the existing one, report it to
    /// the listeners.
    /// </summary>
    /// <param name="newMaster">the packet which caused the change of masters, or <c>null</c> if there is now no master</param>
    private void SetTempoMaster(DeviceUpdate? newMaster)
    {
        var oldMaster = Interlocked.Exchange(ref _tempoMaster, newMaster);
        if ((newMaster == null && oldMaster != null) ||
            (newMaster != null && (oldMaster == null || !newMaster.Address.Equals(oldMaster.Address) ||
                newMaster.DeviceNumber != oldMaster.DeviceNumber)))
        {
            // This is a change in master, so report it to any registered listeners.
            EventDispatcher.DeliverNow(MasterChanged, this, newMaster, Logger);
        }
    }

    /// <summary>
    /// How large a tempo change is required before we consider it to be a real difference, stored
    /// as raw <see cref="double"/> bits so it can be read and written atomically.
    /// </summary>
    private long _tempoEpsilonBits = BitConverter.DoubleToInt64Bits(0.0001);

    /// <summary>
    /// How large a tempo change is required (as a BPM fraction) before we consider it to be a
    /// real difference that will trigger a tempo change update.
    /// </summary>
    public double TempoEpsilon
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _tempoEpsilonBits));
        set => Interlocked.Exchange(ref _tempoEpsilonBits, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>
    /// Track the most recently reported master tempo, stored as raw <see cref="double"/> bits so
    /// it can be read and written atomically.
    /// </summary>
    private long _masterTempoBits = BitConverter.DoubleToInt64Bits(0.0);

    /// <summary>
    /// Get the current master tempo (the most recently reported master tempo).
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public double MasterTempo
    {
        get
        {
            EnsureRunning();
            return BitConverter.Int64BitsToDouble(Interlocked.Read(ref _masterTempoBits));
        }
    }

    /// <summary>
    /// Establish a new master tempo, and if it is a change from the existing one, report it to
    /// the listeners (and slave our own metronome to it if we are synced).
    /// </summary>
    /// <param name="newTempo">the newly reported master tempo</param>
    private void SetMasterTempo(double newTempo)
    {
        var oldTempo = BitConverter.Int64BitsToDouble(
            Interlocked.Exchange(ref _masterTempoBits, BitConverter.DoubleToInt64Bits(newTempo)));
        // (The Java implementation calls getTempoMaster() here, whose state check could throw
        // during shutdown races; we read the field directly, which is what it really wants.)
        if (Volatile.Read(ref _tempoMaster) != null && Math.Abs(newTempo - oldTempo) > TempoEpsilon)
        {
            // This is a change in tempo, so report it to any registered listeners, and update our
            // metronome if we are synced.
            if (IsSynced)
            {
                _metronome.Tempo = newTempo;
                NotifyBeatSenderOfChange();
            }
            EventDispatcher.DeliverNow(TempoChanged, this, newTempo, Logger);
        }
    }

    /// <summary>
    /// Given an update packet sent to us, create the appropriate object to describe it. Internal
    /// (rather than private, as in Java) so packet-routing behavior can be exercised by tests.
    /// </summary>
    /// <param name="packet">the packet received on our update port</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <returns>the corresponding <see cref="DeviceUpdate"/> subclass, or <c>null</c> if the packet was not recognizable</returns>
    internal DeviceUpdate? BuildUpdate(ReadOnlySpan<byte> packet, IPAddress address)
    {
        var length = packet.Length;
        var kind = Util.ValidateHeader(packet, UpdatePort);

        if (kind == null)
        {
            Logger.LogDebug("Ignoring unrecognized packet sent to update port.");
            return null;
        }

        if (kind == PacketType.MixerStatus)
        {
            if (length != 56)
            {
                Logger.LogWarning("Processing a Mixer Status packet with unexpected length {Length}, expected 56 bytes.", length);
            }
            if (length >= 56)
            {
                return new MixerStatus(packet, address);
            }
            Logger.LogWarning("Ignoring too-short Mixer Status packet.");
            return null;
        }

        if (kind == PacketType.CdjStatus)
        {
            if (length >= CdjStatus.MinimumPacketSize)
            {
                return new CdjStatus(packet, address);
            }
            Logger.LogWarning("Ignoring too-short CDJ Status packet with length {Length} (we need {Minimum} bytes).",
                length, CdjStatus.MinimumPacketSize);
            return null;
        }

        if (kind == PacketType.LoadTrackAck)
        {
            Logger.LogInformation("Received track load acknowledgment from player {Player}", packet[0x21]);
            return null;
        }

        if (kind == PacketType.MediaQuery)
        {
            Logger.LogWarning("Received a media query packet, we don't yet support responding to this.");
            return null;
        }

        if (kind == PacketType.MediaResponse)
        {
            DeliverMediaDetailsUpdate(new MediaDetails(packet));
            return null;
        }

        Logger.LogWarning("Ignoring {Kind} packet sent to update port.", kind.Name);
        return null;
    }

    /// <summary>
    /// <para>Process a device update once it has been received. Track it as the most recent
    /// update from its device, and notify any registered listeners, including master listeners if
    /// it results in changes to tracked state, such as the current master player and tempo. Also
    /// handles the Baroque dance of handing off the tempo master role from or to another
    /// device.</para>
    ///
    /// <para>This is internal (package accessible in Java) so that <see cref="VirtualRekordbox"/>
    /// can proxy the status packets it receives through us, and so tests can inject synthetic
    /// updates.</para>
    /// </summary>
    /// <param name="update">the update packet that was received</param>
    internal void ProcessUpdate(DeviceUpdate update)
    {
        _updates[DeviceReference.GetDeviceReference(update.DeviceNumber, update.Address)] = update;

        // Keep track of the largest sync number we see.
        if (update is CdjStatus cdjStatus)
        {
            var syncNumber = cdjStatus.SyncNumber;
            if (syncNumber > _largestSyncCounter)
            {
                _largestSyncCounter = syncNumber;
            }
        }

        // Deal with the tempo master complexities, including handoff to/from us.
        if (update.IsTempoMaster)
        {
            var packetYieldingTo = update.DeviceMasterIsBeingYieldedTo;
            if (packetYieldingTo == null)
            {
                // This is a normal, non-yielding master packet. Update our notion of the current
                // master, and, if we were yielding, finish that process, updating our sync number
                // appropriately.
                if (_master)
                {
                    if (_nextMaster == update.DeviceNumber)
                    {
                        _syncCounter = _largestSyncCounter + 1;
                    }
                    else if (_nextMaster == 0xff)
                    {
                        Logger.LogWarning("Saw master asserted by player {Player} when we were not yielding it.", update.DeviceNumber);
                    }
                    else
                    {
                        Logger.LogWarning("Expected to yield master role to player {Expected} but saw master asserted by player {Actual}",
                            _nextMaster, update.DeviceNumber);
                    }
                }
                _master = false;
                _nextMaster = 0xff;
                SetTempoMaster(update);
                if (update.Bpm != 0xffff)  // Ignore invalid tempo, i.e. when master has no track loaded.
                {
                    SetMasterTempo(update.EffectiveTempo);
                }
            }
            else
            {
                // This is a yielding master packet. If it is us that is being yielded to, take
                // over master. Log a message if it was unsolicited, and a warning if it's coming
                // from a different player than we asked.
                if (packetYieldingTo == DeviceNumber)
                {
                    if (update.DeviceNumber != _masterYieldedFrom)
                    {
                        if (_masterYieldedFrom == 0)
                        {
                            Logger.LogInformation("Accepting unsolicited Master yield; we must be the only synced device playing.");
                        }
                        else
                        {
                            Logger.LogWarning("Expected player {Expected} to yield master to us, but player {Actual} did.",
                                _masterYieldedFrom, update.DeviceNumber);
                        }
                    }
                    _master = true;
                    _masterYieldedFrom = 0;
                    SetTempoMaster(null);
                    SetMasterTempo(Tempo);
                }
            }
        }
        else
        {
            // This update was not acting as a tempo master; if we thought it should be, update
            // our records.
            var oldMaster = Volatile.Read(ref _tempoMaster);
            if (oldMaster != null && oldMaster.Address.Equals(update.Address) &&
                oldMaster.DeviceNumber == update.DeviceNumber)
            {
                // This device has resigned master status, and nobody else has claimed it so far.
                SetTempoMaster(null);
            }
        }
        EventDispatcher.DeliverNow(DeviceUpdateReceived, this, update, Logger);
    }

    /// <summary>
    /// Process a beat packet, potentially updating the master tempo and sending our listeners a
    /// master beat notification. Does nothing if we are not active. Called by the
    /// <see cref="BeatFinder"/> through the internal priority beat listener seam, before any
    /// public subscribers see the beat.
    /// </summary>
    /// <param name="beat">the beat packet that was received</param>
    internal void ProcessBeat(Beat beat)
    {
        if (IsRunning && beat.IsTempoMaster)
        {
            SetMasterTempo(beat.EffectiveTempo);
            EventDispatcher.DeliverNow(MasterBeat, this, beat, Logger);
        }
    }

    /// <summary>
    /// Relays beats from the <see cref="BeatFinder"/> to <see cref="ProcessBeat"/> ahead of all
    /// other listeners.
    /// </summary>
    /// <param name="owner">the VirtualCdj which should process the beats</param>
    private sealed class PriorityBeatListener(VirtualCdj owner) : IPriorityBeatListener
    {
        /// <inheritdoc/>
        public void OnBeat(Beat beat) => owner.ProcessBeat(beat);
    }

    /// <summary>
    /// Test hook allowing the period the <see cref="DeviceFinder"/> must have been watching the
    /// network before we are willing to self-assign a device number to be shortened. Defaults to
    /// <see cref="SelfAssignmentWatchPeriod"/> (four seconds).
    /// </summary>
    internal long SelfAssignmentWatchMillis = SelfAssignmentWatchPeriod;

    /// <summary>
    /// Test hook allowing the 300 millisecond pause between the packets of the device number
    /// claim conversation to be shortened, so the claim state machine can run quickly over
    /// loopback in tests.
    /// </summary>
    internal int ClaimSleepMillis = 300;

    /// <summary>
    /// Test hook allowing the port to which announcement-family packets (keep-alives, hellos,
    /// claims, defenses, and mixer assignment requests) are sent to be redirected, so tests can
    /// observe them on an ephemeral loopback port instead of the real announcement port.
    /// </summary>
    internal int AnnouncementSendPort = Ports.AnnouncementPort;

    /// <summary>
    /// Test hook allowing the port to which beat-family packets (beats, sync commands, fader
    /// starts, on-air reports, and master handoff requests) are sent to be redirected.
    /// </summary>
    internal int BeatSendPort = Ports.BeatPort;

    /// <summary>
    /// Test hook allowing the port to which update-family packets (status packets, media
    /// queries, load commands, and master handoff responses) are sent to be redirected.
    /// </summary>
    internal int UpdateSendPort = Ports.UpdatePort;

    /// <summary>
    /// Test hook allowing the service's own socket to be bound to an alternate port (or an
    /// ephemeral port, by supplying zero) so tests can run without the real update port, and in
    /// parallel with any actual DJ Link traffic. May only be changed while the service is
    /// stopped; <c>null</c> (the default) means the real <see cref="UpdatePort"/> is used.
    /// </summary>
    private int? _portOverride;

    /// <summary>
    /// Reports the local port to which our status socket is actually bound, or <c>null</c> if we
    /// are not running. Used by tests which bind to an ephemeral port.
    /// </summary>
    internal int? LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port;

    /// <inheritdoc cref="_portOverride"/>
    internal int? PortOverride
    {
        get => _portOverride;
        set
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("PortOverride can only be changed while the VirtualCdj is stopped.");
            }
            _portOverride = value;
        }
    }

    /// <summary>
    /// Test hook which, when assigned, replaces the scan of real network interfaces during
    /// startup: given the address of the first DJ Link device we found, it must return the
    /// interface address details to use (or <c>null</c> to simulate no match). When it is used,
    /// <see cref="MatchingInterfaces"/> will report an empty list, since no real
    /// <see cref="NetworkInterface"/> objects are involved.
    /// </summary>
    internal Func<IPAddress, MatchedAddressInfo?>? InterfaceScanOverride;

    /// <summary>
    /// <para>Try to choose a device number, which we have not seen on the network. If we have
    /// already tried one, it must have been defended, so increment to the next one we can try (we
    /// stop at 15). If we have not yet tried one, pick the first appropriate one to try, honoring
    /// the value of <see cref="UseStandardPlayerNumber"/> to determine if we start at 1 or 7. Set
    /// the number we are going to try next in <see cref="_claimingNumber"/>.</para>
    ///
    /// <para>Even though in theory we should be able to rely on the protocol to tell us if we are
    /// claiming a number that belongs to another player, it turns out the XDJ-XZ is buggy and
    /// tells us to go ahead and use whatever we were claiming, and further fails to defend the
    /// device numbers that it is using. So we still need to make sure the
    /// <see cref="DeviceFinder"/> has been running long enough to see all devices so we can avoid
    /// trying to claim a number that some other device is already using.</para>
    /// </summary>
    /// <returns><c>true</c> if there was a number available for us to try claiming</returns>
    private bool SelfAssignDeviceNumber()
    {
        var now = Util.CurrentTimestampNanos() / 1_000_000;
        var started = DeviceFinder.Instance.FirstDeviceTime;
        while (now - started < SelfAssignmentWatchMillis)
        {
            var sleepMillis = SelfAssignmentWatchMillis - (now - started);
            Logger.LogDebug("Not waited long enough for devices; sleeping for {Millis}ms", sleepMillis);
            try
            {
                Thread.Sleep((int)sleepMillis);  // Sleep until we hit the right time.
            }
            catch (ThreadInterruptedException)
            {
                Logger.LogWarning("Interrupted waiting to self-assign device number, giving up.");
                return false;
            }
            now = Util.CurrentTimestampNanos() / 1_000_000;
        }
        Logger.LogDebug("Self-assigning number {Millis}ms after first device seen.",
            Util.CurrentTimestampNanos() / 1_000_000 - started);

        if (_claimingNumber == 0)
        {
            // We have not yet tried a number. If we are not supposed to use standard player
            // numbers, make sure the first one we try is 7 (to accommodate the CDJ-3000, which
            // can use channels 5 and 6).
            if (!UseStandardPlayerNumber)
            {
                _claimingNumber = 6;
            }
        }

        // Record what numbers we have already seen, since there is no point trying one of them.
        var numbersUsed = new HashSet<int>();
        foreach (var device in DeviceFinder.Instance.CurrentDevices)
        {
            numbersUsed.Add(device.DeviceNumber);
        }
        Logger.LogDebug("Player numbers seen on network: {Numbers}", string.Join(", ", numbersUsed));

        // Try the next available player number less than mixers use.
        var startingNumber = _claimingNumber + 1;
        for (var result = startingNumber; result < 16; result++)
        {
            if (!numbersUsed.Contains(result))  // We found one that is not used, so we can use it.
            {
                _claimingNumber = result;
                if (UseStandardPlayerNumber && result > 4)
                {
                    Logger.LogWarning("Unable to self-assign a standard player number, all are in use. Trying number {Number}.", result);
                }
                return true;
            }
        }
        Logger.LogWarning("Found no unused device numbers between {Start} and 15, giving up.", startingNumber);
        return false;
    }

    /// <summary>
    /// Hold the network interfaces which match the address on which we found player traffic.
    /// Should only be one, or we will likely receive duplicate packets, which will cause
    /// problematic behavior.
    /// </summary>
    private volatile List<NetworkInterface>? _matchingInterfaces;

    /// <summary>
    /// Check the interfaces that match the address from which we are receiving DJ Link traffic.
    /// If there is more than one value in this list, that is a problem because we will likely
    /// receive duplicate packets that will play havoc with our understanding of player states.
    /// (Reports an empty list when the internal interface-injection test seam supplied the
    /// matched address, since no real interface objects are involved then.)
    /// </summary>
    /// <exception cref="InvalidOperationException">if we are not running</exception>
    public IReadOnlyList<NetworkInterface> MatchingInterfaces
    {
        get
        {
            if (VirtualRekordbox.Instance.IsRunning)
            {
                return VirtualRekordbox.Instance.MatchingInterfaces;
            }
            EnsureRunning();
            return _matchingInterfaces?.AsReadOnly() ?? (IReadOnlyList<NetworkInterface>)[];
        }
    }

    /// <summary>
    /// Holds the interface address we chose to communicate with the DJ Link device we found
    /// during startup, so we can check if there are any unreachable ones.
    /// </summary>
    private volatile MatchedAddressInfo? _matchedAddress;

    /// <summary>
    /// If we are in the process of trying to establish a device number, this will hold the
    /// number we are currently trying to claim. Otherwise, it will hold the value 0.
    /// </summary>
    private volatile int _claimingNumber;

    /// <summary>
    /// If another player defends the number we tried to claim, this value will get set to
    /// <c>true</c>, and we will either have to try another number, or fail to start up, as
    /// appropriate.
    /// </summary>
    private volatile bool _claimRejected;

    /// <summary>
    /// If a mixer has assigned us a device number, this will be that non-zero value.
    /// </summary>
    private volatile int _mixerAssigned;

    /// <summary>
    /// Implement the process of requesting a device number from a mixer that has told us it is
    /// responsible for assigning it to us as described in the
    /// <see href="https://djl-analysis.deepsymmetry.org/djl-analysis/startup.html#cdj-startup">protocol analysis</see>.
    /// </summary>
    /// <param name="mixerAddress">the address from which we received a mixer device number assignment offer</param>
    private void RequestNumberFromMixer(IPAddress mixerAddress)
    {
        var currentSocket = _socket;
        if (currentSocket == null)
        {
            Logger.LogWarning("Gave up before sending device number request to mixer.");
            return;  // We've already given up.
        }

        // Send a packet directly to the mixer telling it we are ready for its device assignment.
        var request = BuildAssignmentRequestPacket();
        try
        {
            Logger.LogDebug("Sending device number request to mixer at address {Address}, port {Port}",
                mixerAddress, AnnouncementSendPort);
            currentSocket.SendTo(request, new IPEndPoint(mixerAddress, AnnouncementSendPort));
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Unable to send device number request to mixer.");
        }
    }

    /// <summary>
    /// Builds the packet used to acknowledge a mixer's intention to assign us a device number.
    /// Separated out (and made internal) so the packet layout can be verified by tests. The
    /// device name field is fully cleared before the current name is written, where the Java
    /// implementation's in-place template mutation only partially cleared it.
    /// </summary>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildAssignmentRequestPacket()
    {
        var bytes = (byte[])AssignmentRequestTemplate.Clone();
        WriteDeviceName(bytes);
        var matched = _matchedAddress ?? throw new InvalidOperationException("No matched interface address is available.");
        matched.Address.GetAddressBytes().CopyTo(bytes, 0x24);
        lock (_keepAliveBytes)
        {
            Array.Copy(_keepAliveBytes, MacAddressOffset, bytes, 0x28, 6);
            bytes[0x31] = (byte)(_keepAliveBytes[DeviceNumberOffset] == 0 ? 1 : 2);  // The auto-assign flag.
        }
        bytes[0x2f] = 1;  // The packet counter.
        return bytes;
    }

    /// <summary>
    /// Tell a device that is trying to use the same number that we have ownership of it. Called
    /// by the <see cref="DeviceFinder"/> when it sees an announcement claiming our device number
    /// while we are running.
    /// </summary>
    /// <param name="invaderAddress">the address from which a rogue device claim or announcement
    /// was received for the same device number we are using</param>
    internal void DefendDeviceNumber(IPAddress invaderAddress)
    {
        var currentSocket = _socket;
        if (currentSocket == null)
        {
            Logger.LogWarning("Went offline before we could defend our device number.");
            return;
        }

        // Send a packet to the interloper telling it that we are using that device number.
        var defense = BuildDeviceNumberDefensePacket();
        try
        {
            Logger.LogInformation("Sending device number defense packet to invader at address {Address}, port {Port}",
                invaderAddress, AnnouncementSendPort);
            currentSocket.SendTo(defense, new IPEndPoint(invaderAddress, AnnouncementSendPort));
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Unable to send device defense packet.");
        }
    }

    /// <summary>
    /// Builds the packet used to tell another device we are already using a device number.
    /// Separated out (and made internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildDeviceNumberDefensePacket()
    {
        var bytes = (byte[])DeviceNumberDefenseTemplate.Clone();
        WriteDeviceName(bytes);
        lock (_keepAliveBytes)
        {
            bytes[0x24] = _keepAliveBytes[DeviceNumberOffset];
        }
        var matched = _matchedAddress ?? throw new InvalidOperationException("No matched interface address is available.");
        matched.Address.GetAddressBytes().CopyTo(bytes, 0x25);
        return bytes;
    }

    /// <summary>
    /// Builds one of the initial packets sent three times when coming online. Separated out (and
    /// made internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildHelloPacket()
    {
        var bytes = (byte[])HelloTemplate.Clone();
        WriteDeviceName(bytes);
        return bytes;
    }

    /// <summary>
    /// Builds one of the first-stage device number claim packets. Separated out (and made
    /// internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <param name="counter">the packet counter, in the range 1 to 3</param>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildClaimStage1Packet(int counter)
    {
        var bytes = (byte[])ClaimStage1Template.Clone();
        WriteDeviceName(bytes);
        lock (_keepAliveBytes)
        {
            Array.Copy(_keepAliveBytes, MacAddressOffset, bytes, 0x26, 6);
        }
        bytes[0x24] = (byte)counter;  // The packet counter.
        return bytes;
    }

    /// <summary>
    /// Builds one of the second-stage device number claim packets. Separated out (and made
    /// internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <param name="counter">the packet counter, in the range 1 to 3</param>
    /// <param name="claimingNumber">the device number we are trying to claim</param>
    /// <param name="autoAssign">whether we are self-assigning a device number (rather than claiming a specific configured one)</param>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildClaimStage2Packet(int counter, int claimingNumber, bool autoAssign)
    {
        var bytes = (byte[])ClaimStage2Template.Clone();
        WriteDeviceName(bytes);
        var matched = _matchedAddress ?? throw new InvalidOperationException("No matched interface address is available.");
        matched.Address.GetAddressBytes().CopyTo(bytes, 0x24);
        lock (_keepAliveBytes)
        {
            Array.Copy(_keepAliveBytes, MacAddressOffset, bytes, 0x28, 6);
        }
        bytes[0x2e] = (byte)claimingNumber;             // The number we are claiming.
        bytes[0x2f] = (byte)counter;                    // The packet counter.
        bytes[0x31] = (byte)(autoAssign ? 1 : 2);       // The auto-assign flag.
        return bytes;
    }

    /// <summary>
    /// Builds one of the third-stage (final) device number claim packets. Separated out (and made
    /// internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <param name="counter">the packet counter, in the range 1 to 3</param>
    /// <param name="claimingNumber">the device number we are trying to claim</param>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildClaimStage3Packet(int counter, int claimingNumber)
    {
        var bytes = (byte[])ClaimStage3Template.Clone();
        WriteDeviceName(bytes);
        bytes[0x24] = (byte)claimingNumber;  // The number we are claiming.
        bytes[0x25] = (byte)counter;         // The packet counter.
        return bytes;
    }

    /// <summary>
    /// Builds a copy of the current keep-alive (presence announcement) packet, including the
    /// device name, device number, MAC address, and IP address fields. Separated out (and made
    /// internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildKeepAlivePacket()
    {
        lock (_keepAliveBytes)
        {
            return (byte[])_keepAliveBytes.Clone();
        }
    }

    /// <summary>
    /// The possible outcomes of one pass through the three device number claim stages, used to
    /// restructure the labelled <c>continue selfAssignLoop</c> of the Java implementation.
    /// </summary>
    private enum ClaimOutcome
    {
        /// <summary>All three stages completed, the number is ours.</summary>
        Claimed,

        /// <summary>Another device defended the number while we were self-assigning; try the next candidate.</summary>
        RetrySelfAssign,

        /// <summary>We cannot go online (send failure, or a defended explicitly-requested number).</summary>
        Failed
    }

    /// <summary>
    /// Implement the device-number claim protocol described in the
    /// <see href="https://djl-analysis.deepsymmetry.org/djl-analysis/startup.html#cdj-startup">protocol analysis</see>.
    /// </summary>
    /// <returns><c>true</c> iff a device number was successfully established and startup can proceed</returns>
    private bool ClaimDeviceNumber()
    {
        // Set up our state trackers for device assignment negotiation.
        _claimRejected = false;
        _mixerAssigned = 0;

        // Send the initial series of three "coming online" packets.
        for (var i = 1; i <= 3; i++)
        {
            try
            {
                Logger.LogDebug("Sending hello packet {Number}", i);
                SendAnnouncementFamilyPacket(BuildHelloPacket());
                Thread.Sleep(ClaimSleepMillis);
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Unable to send hello packet to network, failing to go online.");
                return false;
            }
        }

        // Establish the device number we want to claim; if zero that means we will try to
        // self-assign.
        _claimingNumber = DeviceNumber;

        var claimed = false;  // Indicates we have successfully claimed a number and can be done.
        while (!claimed)
        {
            // If we are supposed to self-assign a number, find the next one we can try.
            if (DeviceNumber == 0 && !SelfAssignDeviceNumber())
            {
                // There are no numbers left for us to try, give up and report failure.
                _claimingNumber = 0;
                return false;
            }

            switch (RunClaimStages())
            {
                case ClaimOutcome.Claimed:
                    claimed = true;  // The number we wanted is ours.
                    break;

                case ClaimOutcome.RetrySelfAssign:
                    break;  // Loop back and try the next available number, if any.

                default:
                    _claimingNumber = 0;
                    return false;
            }
        }

        // Set the device number we claimed.
        lock (_keepAliveBytes)
        {
            _keepAliveBytes[DeviceNumberOffset] = (byte)_claimingNumber;
        }
        _claimingNumber = 0;
        _mixerAssigned = 0;
        return true;  // Huzzah, we found the right device number to use!
    }

    /// <summary>
    /// Runs the three stages of the device number claim conversation for the number currently
    /// held in <see cref="_claimingNumber"/>, each of which can be interrupted by a defense from
    /// another player (<see cref="_claimRejected"/>) or a mixer assigning us a specific number
    /// (<see cref="_mixerAssigned"/>). Replaces the labelled <c>selfAssignLoop</c> retries of the
    /// Java implementation with an explicit outcome value.
    /// </summary>
    /// <returns>the outcome of this pass through the claim stages</returns>
    private ClaimOutcome RunClaimStages()
    {
        // Send the series of three initial device number claim packets, unless we are interrupted
        // by a defense or a mixer assigning us a specific number.
        for (var i = 1; i <= 3 && _mixerAssigned == 0; i++)
        {
            try
            {
                Logger.LogDebug("Sending claim stage 1 packet {Number}", i);
                SendAnnouncementFamilyPacket(BuildClaimStage1Packet(i));
                Thread.Sleep(ClaimSleepMillis);
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Unable to send device number claim stage 1 packet to network, failing to go online.");
                return ClaimOutcome.Failed;
            }
            var rejection = CheckClaimRejected();
            if (rejection != null)
            {
                return rejection.Value;
            }
        }

        // Send the middle series of device claim packets, unless we are interrupted by a defense
        // or a mixer assigning us a specific number.
        for (var i = 1; i <= 3 && _mixerAssigned == 0; i++)
        {
            try
            {
                Logger.LogDebug("Sending claim stage 2 packet {Number} for device {Device}", i, _claimingNumber);
                SendAnnouncementFamilyPacket(BuildClaimStage2Packet(i, _claimingNumber, DeviceNumber == 0));
                Thread.Sleep(ClaimSleepMillis);
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Unable to send device number claim stage 2 packet to network, failing to go online.");
                return ClaimOutcome.Failed;
            }
            var rejection = CheckClaimRejected();
            if (rejection != null)
            {
                return rejection.Value;
            }
        }

        // If the mixer assigned us a number, use it.
        var assigned = _mixerAssigned;
        _mixerAssigned = 0;
        if (assigned > 0)
        {
            _claimingNumber = assigned;
        }

        // Send the final series of device claim packets, unless we are interrupted by a defense,
        // or the mixer acknowledges our acceptance of its assignment.
        for (var i = 1; i <= 3 && _mixerAssigned == 0; i++)
        {
            try
            {
                Logger.LogDebug("Sending claim stage 3 packet {Number} for device {Device}", i, _claimingNumber);
                SendAnnouncementFamilyPacket(BuildClaimStage3Packet(i, _claimingNumber));
                Thread.Sleep(ClaimSleepMillis);
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Unable to send device number claim stage 3 packet to network, failing to go online.");
                return ClaimOutcome.Failed;
            }
            var rejection = CheckClaimRejected();
            if (rejection != null)
            {
                return rejection.Value;
            }
        }

        return ClaimOutcome.Claimed;
    }

    /// <summary>
    /// Checks whether some other player is defending the number we tried to claim, and if so,
    /// determines the resulting outcome (retrying the next candidate when we are self-assigning,
    /// or failing to go online when a specific number was requested).
    /// </summary>
    /// <returns>the outcome forced by a rejection, or <c>null</c> if the claim has not been rejected</returns>
    private ClaimOutcome? CheckClaimRejected()
    {
        if (!_claimRejected)
        {
            return null;
        }
        if (DeviceNumber == 0)  // We are trying to pick a number.
        {
            _claimRejected = false;
            return ClaimOutcome.RetrySelfAssign;  // Try the next available number, if any.
        }
        Logger.LogWarning("Unable to use device number {Number}, another device has it. Failing to go online.", DeviceNumber);
        return ClaimOutcome.Failed;
    }

    /// <summary>
    /// Sends a raw announcement-family packet (whose bytes already include the protocol header)
    /// to the broadcast address on the announcement port.
    /// </summary>
    /// <param name="packet">the complete packet to broadcast</param>
    private void SendAnnouncementFamilyPacket(byte[] packet)
    {
        var socket = _socket ?? throw new InvalidOperationException("VirtualCdj has no open socket");
        var broadcast = _broadcastAddress ?? throw new InvalidOperationException("VirtualCdj has no broadcast address");
        socket.SendTo(packet, new IPEndPoint(broadcast, AnnouncementSendPort));
    }

    /// <summary>
    /// Once we have seen some DJ Link devices on the network, we can proceed to create a virtual
    /// player on that same network.
    /// </summary>
    /// <returns><c>true</c> if we found DJ Link devices and were able to create the virtual CDJ</returns>
    /// <exception cref="SocketException">if there is a problem opening a socket on the right network</exception>
    private bool CreateVirtualCdj()
    {
        // Find the network interface and address to use to communicate with the first device we
        // found.
        var matchingInterfaces = new List<NetworkInterface>();
        _matchingInterfaces = matchingInterfaces;
        _matchedAddress = null;
        var aDevice = DeviceFinder.Instance.CurrentDevices.First();
        var scanOverride = InterfaceScanOverride;
        if (scanOverride != null)
        {
            _matchedAddress = scanOverride(aDevice.Address);
        }
        else
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                var candidate = Util.FindMatchingAddress(aDevice.Address, networkInterface);
                if (candidate != null)
                {
                    if (_matchedAddress == null)
                    {
                        _matchedAddress = new MatchedAddressInfo(candidate.Address, candidate.PrefixLength,
                            Util.ComputeBroadcastAddress(candidate),
                            networkInterface.GetPhysicalAddress().GetAddressBytes());
                    }
                    matchingInterfaces.Add(networkInterface);
                }
            }
        }

        var matched = _matchedAddress;
        if (matched == null)
        {
            Logger.LogWarning("Unable to find network interface to communicate with {Device}, giving up.", aDevice);
            return false;
        }

        if (matchingInterfaces.Count > 0)
        {
            Logger.LogInformation("Found matching network interface {DisplayName} ({Name}), will use address {Address}",
                matchingInterfaces[0].Description, matchingInterfaces[0].Name, matched.Address);
        }
        else
        {
            Logger.LogInformation("Using injected network interface address {Address}", matched.Address);
        }
        for (var i = 1; i < matchingInterfaces.Count; i++)
        {
            Logger.LogWarning("Network interface {DisplayName} ({Name}) sees same network: we will likely get duplicate DJ Link packets, causing severe problems.",
                matchingInterfaces[i].Description, matchingInterfaces[i].Name);
        }

        // Copy the chosen interface's hardware and IP addresses into the announcement packet
        // template.
        lock (_keepAliveBytes)
        {
            if (matched.HardwareAddress.Length >= 6)
            {
                Array.Copy(matched.HardwareAddress, 0, _keepAliveBytes, MacAddressOffset, 6);
            }
            matched.Address.GetAddressBytes().CopyTo(_keepAliveBytes, 44);
        }
        _broadcastAddress = matched.BroadcastAddress;

        // Open our communication socket.
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
            EnableBroadcast = true
        };
        try
        {
            socket.Bind(new IPEndPoint(matched.Address, _portOverride ?? UpdatePort));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _localAddress = matched.Address;
        _socket = socket;

        // Inform the DeviceFinder to ignore our own device announcement packets.
        DeviceFinder.Instance.AddIgnoredAddress(matched.Address);

        // Determine the device number we are supposed to use, and make sure it can be claimed by
        // us.
        if (!ClaimDeviceNumber())
        {
            // We couldn't get a device number, so clean up and report failure.
            Logger.LogWarning("Unable to allocate a device number for the Virtual CDJ, giving up.");
            DeviceFinder.Instance.RemoveIgnoredAddress(matched.Address);
            _socket = null;
            _localAddress = null;
            socket.Close();
            return false;
        }

        // Start the thread that will wait for and process status update packets sent to our
        // socket.
        var receiver = new Thread(() => ReceiveLoop(socket))
        {
            Name = "beat-link VirtualCdj status receiver",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _starting = false;
        receiver.Start();

        // Create the thread which announces our participation in the DJ Link network, to request
        // update packets.
        var announcer = new Thread(() => AnnounceLoop(socket))
        {
            Name = "beat-link VirtualCdj announcement sender",
            IsBackground = true
        };
        announcer.Start();
        DeliverLifecycleAnnouncement(Logger, true);
        return true;
    }

    /// <summary>
    /// The body of the status receiver thread: waits for and processes status update packets sent
    /// to our socket, until the socket is replaced or closed.
    /// </summary>
    /// <param name="socket">the socket on which status updates are received</param>
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
                // Zero-length reads are the loopback wake datagrams sent at shutdown, and are
                // ignored.
                received = socket.ReceiveFrom(buffer, ref sender);
                sourceAddress = ((IPEndPoint)sender).Address;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Don't log a warning if the exception was due to the socket closing at shutdown.
                if (ReferenceEquals(_socket, socket))
                {
                    // We did not expect to have a problem; log a warning and shut down.
                    Logger.LogWarning(e, "Problem reading from DeviceStatus socket, flushing DeviceFinder due to likely network change and shutting down.");
                    DeviceFinder.Instance.Flush();
                    Stop();
                }
                received = 0;
            }
            try
            {
                if (received > 0 && sourceAddress != null)
                {
                    var update = BuildUpdate(buffer.AsSpan(0, received), sourceAddress);
                    if (update != null && IsRunning)
                    {
                        ProcessUpdate(update);
                    }
                }
            }
            catch (Exception t)
            {
                Logger.LogWarning(t, "Problem processing device update packet");
            }
        }
    }

    /// <summary>
    /// The body of the announcement sender thread: periodically broadcasts our keep-alive packet
    /// so the other devices see us as being part of the DJ Link network and send us updates.
    /// </summary>
    /// <param name="socket">the socket used to send the announcements</param>
    private void AnnounceLoop(Socket socket)
    {
        while (ReferenceEquals(_socket, socket))
        {
            try
            {
                SendAnnouncementFamilyPacket(BuildKeepAlivePacket());
                Thread.Sleep(AnnounceInterval);
            }
            catch (Exception t)
            {
                // Don't tear things down if the failure was simply our socket closing at shutdown.
                if (ReferenceEquals(_socket, socket))
                {
                    Logger.LogWarning(t, "Unable to send announcement packet, flushing DeviceFinder due to likely network change and shutting down.");
                    DeviceFinder.Instance.Flush();
                    Stop();
                }
            }
        }
    }

    /// <summary>
    /// Checks if we can see any players that are on a different network than the one we chose for
    /// the Virtual CDJ. If so, we are not going to be able to communicate with them, and they
    /// should all be moved onto a single network.
    /// </summary>
    /// <returns>the device announcements of any players which are on unreachable networks, or hopefully an empty set</returns>
    /// <exception cref="InvalidOperationException">if we are not running</exception>
    public IReadOnlySet<DeviceAnnouncement> FindUnreachablePlayers()
    {
        EnsureRunning();
        var matched = _matchedAddress ?? throw new InvalidOperationException("No matched interface address is available.");
        var result = new HashSet<DeviceAnnouncement>();
        foreach (var candidate in DeviceFinder.Instance.CurrentDevices)
        {
            if (!Util.SameNetwork(matched.PrefixLength, matched.Address, candidate.Address))
            {
                result.Add(candidate);
            }
        }
        return result;
    }

    /// <summary>
    /// Tracks whether we have hooked up the handler that makes sure we get shut down if the
    /// <see cref="DeviceFinder"/> does, because we rely on it. (Guarded by
    /// <see cref="_lifecycleLock"/>; the Java implementation could re-register its listener
    /// because its listener collections were sets.)
    /// </summary>
    private bool _deviceFinderListenerRegistered;

    /// <summary>
    /// Tracks whether we have hooked up the handler that makes sure we get shut down if
    /// <see cref="VirtualRekordbox"/> does when in proxy mode.
    /// </summary>
    private bool _virtualRekordboxListenerRegistered;

    /// <summary>
    /// Makes sure we get shut down if the <see cref="DeviceFinder"/> does, because we rely on it.
    /// </summary>
    private void OnDeviceFinderStopped(object? sender, EventArgs e)
    {
        // Only a genuinely started instance owns resources tied to the DeviceFinder. Reacting to
        // a merely test-forced running state would let a stale asynchronous Stopped announcement
        // from a previous test's teardown clear the seam mid-test (and trigger a full, slow real
        // startup inside the next service that starts us).
        if (_socket != null && IsRunning)
        {
            Logger.LogInformation("VirtualCDJ stopping because DeviceFinder has stopped.");
            Stop();
        }
    }

    /// <summary>
    /// Makes sure we get shut down if <see cref="VirtualRekordbox"/> does when in proxy mode.
    /// </summary>
    private void OnVirtualRekordboxStopped(object? sender, EventArgs e)
    {
        if (_proxyingForVirtualRekordbox)
        {
            Logger.LogInformation("Shutting down because VirtualRekordbox is and we were proxying for it.");
            Stop();
        }
    }

    /// <summary>
    /// <para>In normal operation (with Pro DJ Link devices), start announcing ourselves and
    /// listening for status packets. If, however, we find an Opus Quad on the network, there are
    /// two possible paths:</para>
    ///
    /// <para>The most common is to start <see cref="VirtualRekordbox"/> and operate in a more
    /// limited Opus Quad compatibility mode (relying on the user to maintain and attach metadata
    /// archives of track information for the USBs they are using), acting as a proxy for packets
    /// that <see cref="VirtualRekordbox"/> is responsible for receiving. A variant mode
    /// (Opus Quad SQLite mode) is used when a database key has been supplied for opening the
    /// SQLite databases that it uses; then we start normally but still report
    /// <see cref="InOpusQuadCompatibilityMode"/>.</para>
    ///
    /// <para>Any mode requires the <see cref="DeviceFinder"/> to be active in order to find out
    /// how to communicate with other devices, so we start that if it is not already. If already
    /// active, has no effect.</para>
    /// </summary>
    /// <returns><c>true</c> if we found DJ Link devices and were able to create the virtual CDJ,
    /// or it was already running</returns>
    /// <exception cref="SocketException">if the socket to listen on port 50002 cannot be created</exception>
    public bool Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return true;  // We were already active.
            }
            try
            {
                _starting = true;

                // Set up so we know we have to shut down if the DeviceFinder shuts down.
                if (!_deviceFinderListenerRegistered)
                {
                    DeviceFinder.Instance.Stopped += OnDeviceFinderStopped;
                    _deviceFinderListenerRegistered = true;
                }

                // Find some DJ Link devices, so we can figure out the interface and address to
                // use to talk to them.
                DeviceFinder.Instance.Start();
                for (var i = 0; DeviceFinder.Instance.CurrentDevices.Count == 0 && i < 200; i++)
                {
                    try
                    {
                        Thread.Sleep(500);
                    }
                    catch (ThreadInterruptedException e)
                    {
                        Logger.LogWarning(e, "Interrupted waiting for devices, giving up");
                        return false;
                    }
                }

                if (DeviceFinder.Instance.CurrentDevices.Count == 0)
                {
                    Logger.LogWarning("No DJ Link devices found, giving up");
                    return false;
                }

                // See if there is an Opus Quad on the network, which means we need to be in one
                // of the limited compatibility modes.
                foreach (var device in DeviceFinder.Instance.CurrentDevices)
                {
                    if (device.IsOpusQuad)
                    {
                        if (OpusProvider.Instance.UsingDeviceLibraryPlus)
                        {
                            Logger.LogInformation("In Opus Quad SQLite mode. Not starting VirtualRekordbox. Starting OpusProvider.");
                            _proxyingForVirtualRekordbox = false;
                            _inOpusQuadSQLiteMode = true;
                            OpusProvider.Instance.Start();
                            return CreateVirtualCdj();
                        }

                        Logger.LogInformation("In Opus Quad rekordbox lighting mode. Not starting VirtualCdj. Starting VirtualRekordbox.");
                        _proxyingForVirtualRekordbox = true;
                        _inOpusQuadSQLiteMode = false;
                        if (!_virtualRekordboxListenerRegistered)
                        {
                            VirtualRekordbox.Instance.Stopped += OnVirtualRekordboxStopped;
                            _virtualRekordboxListenerRegistered = true;
                        }
                        var success = VirtualRekordbox.Instance.Start();
                        if (success)
                        {
                            // Copy values from VirtualRekordbox that we depend on for various
                            // internal calculations.
                            _matchedAddress = VirtualRekordbox.Instance.MatchedAddress;
                            _matchingInterfaces = [.. VirtualRekordbox.Instance.MatchingInterfaces];
                        }
                        return success;
                    }
                }

                _proxyingForVirtualRekordbox = false;
                _inOpusQuadSQLiteMode = false;
                return CreateVirtualCdj();
            }
            finally
            {
                _starting = false;
            }
        }
    }

    /// <summary>
    /// <para>Start announcing ourselves as a specific device number (if we can claim it) and
    /// listening for status packets. If already active, has no effect. Requires the
    /// <see cref="DeviceFinder"/> to be active in order to find out how to communicate with other
    /// devices, so will start that if it is not already.</para>
    ///
    /// <para>This version is shorthand for setting <see cref="DeviceNumber"/> and then
    /// immediately calling <see cref="Start()"/>, but avoids the race condition which can occur
    /// if startup is already in progress, which would lead to an
    /// <see cref="InvalidOperationException"/>. This is not uncommon when startup is being driven
    /// by receipt of device announcement packets.</para>
    /// </summary>
    /// <param name="deviceNumber">the device number to try to claim</param>
    /// <returns><c>true</c> if we found DJ Link devices and were able to create the virtual CDJ,
    /// or it was already running</returns>
    /// <exception cref="SocketException">if the socket to listen on port 50002 cannot be created</exception>
    public bool Start(byte deviceNumber)
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                DeviceNumber = deviceNumber;
                return Start();
            }
            return true;  // We are already running.
        }
    }

    /// <summary>
    /// Stop announcing ourselves and listening for status updates.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                return;
            }
            _broadcastAddress = null;
            _matchingInterfaces = null;
            _updates.Clear();
            SetTempoMaster(null);

            var wasInAnOpusMode = InOpusQuadCompatibilityMode;
            var wasInRekordboxLightingMode = _proxyingForVirtualRekordbox;

            if (wasInAnOpusMode)
            {
                // We were running in limited compatibility mode, so we need to shut down
                // VirtualRekordbox (rekordbox lighting mode) or ourselves (SQLite mode); either
                // way we need to shut down OpusProvider.
                if (wasInRekordboxLightingMode)
                {
                    try
                    {
                        VirtualRekordbox.Instance.Stop();
                    }
                    catch (Exception t)
                    {
                        Logger.LogError(t, "Problem stopping VirtualRekordbox");
                    }
                }
                if (OpusProvider.Instance.IsRunning)
                {
                    try
                    {
                        OpusProvider.Instance.Stop();
                    }
                    catch (Exception t)
                    {
                        Logger.LogError(t, "Problem stopping OpusProvider");
                    }
                }
                _proxyingForVirtualRekordbox = false;
                _inOpusQuadSQLiteMode = false;
            }

            if (!wasInRekordboxLightingMode)
            {
                // We were ourselves running, so shut down the rest of our state.
                try
                {
                    SetSendingStatus(false);
                }
                catch (Exception t)
                {
                    Logger.LogError(t, "Problem stopping sending status during shutdown");
                }
                var socket = _socket;
                _socket = null;
                if (socket != null)
                {
                    var localAddress = _localAddress;
                    if (localAddress != null)
                    {
                        DeviceFinder.Instance.RemoveIgnoredAddress(localAddress);
                    }
                    Util.WakeUdpReceiver(socket);
                    socket.Close();
                }
                _localAddress = null;
            }

            _testForcedRunning = false;

            // Either way, let people know we are done.
            DeliverLifecycleAnnouncement(Logger, false);
            DeviceNumber = 0;  // Set up for self-assignment if restarted.
        }
    }

    /// <summary>
    /// Get the most recent status we have seen from all devices that are recent enough (within
    /// <see cref="DeviceFinder.MaximumAge"/> milliseconds) to be considered still active on the
    /// network. (The Java implementation compared a monotonic timestamp against wall-clock time
    /// here; this port compares monotonic timestamps consistently.)
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public IReadOnlySet<DeviceUpdate> LatestStatus
    {
        get
        {
            EnsureRunning();
            var result = new HashSet<DeviceUpdate>();
            var now = Util.CurrentTimestampNanos();
            foreach (var update in _updates.Values)
            {
                if ((now - update.Timestamp) / 1_000_000 <= DeviceFinder.MaximumAge)
                {
                    result.Add(update);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Look up the most recent status we have seen for a device, given another update from it,
    /// which might be a beat packet containing far less information.
    ///
    /// <para><em>Note:</em> If you are trying to determine the current tempo or beat being played
    /// by the device, you should either use the status you just received, or the
    /// <c>TimeFinder</c>, because that combines both status updates and beat messages, and so is
    /// more likely to be current and definitive.</para>
    /// </summary>
    /// <param name="device">the update identifying the device for which current status information is desired</param>
    /// <returns>the most recent detailed status update received for that device, or <c>null</c> if none has been received</returns>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public DeviceUpdate? GetLatestStatusFor(DeviceUpdate device)
    {
        EnsureRunning();
        return _updates.GetValueOrDefault(DeviceReference.GetDeviceReference(device.DeviceNumber, device.Address));
    }

    /// <summary>
    /// Look up the most recent status we have seen for a device, given its device announcement
    /// packet as returned by <see cref="DeviceFinder.CurrentDevices"/>.
    /// </summary>
    /// <param name="device">the announcement identifying the device for which current status information is desired</param>
    /// <returns>the most recent detailed status update received for that device, or <c>null</c> if none has been received</returns>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public DeviceUpdate? GetLatestStatusFor(DeviceAnnouncement device)
    {
        EnsureRunning();
        return _updates.GetValueOrDefault(DeviceReference.GetDeviceReference(device.DeviceNumber, device.Address));
    }

    /// <summary>
    /// Look up the most recent status we have seen for a device from a device identifying itself
    /// with the specified device number, if any.
    /// </summary>
    /// <param name="deviceNumber">the device number of interest</param>
    /// <returns>the matching detailed status update or <c>null</c> if none have been received</returns>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    public DeviceUpdate? GetLatestStatusFor(int deviceNumber)
    {
        EnsureRunning();
        foreach (var update in _updates.Values)
        {
            if (update.DeviceNumber == deviceNumber)
            {
                return update;
            }
        }
        return null;
    }

    /// <summary>
    /// <para>Raised when the tempo master changes; the payload is the update from the new master,
    /// or <c>null</c> if there is now no master (including when we have taken over that role
    /// ourselves).</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that
    /// is receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<DeviceUpdate?>? MasterChanged;

    /// <summary>
    /// <para>Raised when the master tempo changes; the payload is the new master tempo in beats
    /// per minute.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that
    /// is receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<double>? TempoChanged;

    /// <summary>
    /// <para>Raised when the tempo master announces the start of a new beat.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that
    /// is receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<Beat>? MasterBeat;

    /// <summary>
    /// <para>Raised whenever a device status update is received while we are active.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that
    /// is receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<DeviceUpdate>? DeviceUpdateReceived;

    /// <summary>
    /// <para>Raised whenever a media details response is received (either in response to a
    /// <see cref="SendMediaQuery"/> call, or spontaneously).</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that
    /// is receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<MediaDetails>? MediaDetailsReceived;

    /// <summary>
    /// Send a media details response to all registered listeners. Only public so that it can be
    /// called from the <c>OpusProvider</c> when archives are attached.
    /// </summary>
    /// <param name="details">the response that has just arrived (or been synthesized)</param>
    public void DeliverMediaDetailsUpdate(MediaDetails details) =>
        EventDispatcher.DeliverNow(MediaDetailsReceived, this, details, Logger);

    /// <summary>
    /// Troubleshooting method to produce a compact summary of the IPv4 addresses (if any)
    /// attached to a network interface.
    /// </summary>
    /// <param name="networkInterface">the interface whose addresses should be described</param>
    /// <returns>the description</returns>
    private static string DescribeIPv4Addresses(NetworkInterface networkInterface)
    {
        var builder = new StringBuilder();
        foreach (var candidate in networkInterface.GetIPProperties().UnicastAddresses)
        {
            if (candidate.Address.AddressFamily == AddressFamily.InterNetwork)  // It is an IPv4 network.
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(candidate.Address).Append('/').Append(candidate.PrefixLength);
            }
        }
        return builder.Length > 0 ? builder.ToString() : "No IPv4 addresses";
    }

    /// <summary>
    /// Troubleshooting method to describe a network interface.
    /// </summary>
    /// <param name="networkInterface">the interface whose description is desired</param>
    /// <returns>a description of the interface that may help debug network problems</returns>
    private static string DescribeInterface(NetworkInterface networkInterface)
    {
        var displayName = networkInterface.Description;
        var rawName = networkInterface.Name;
        var result = new StringBuilder(displayName);
        if (displayName != rawName)
        {
            result.Append(" (").Append(rawName).Append(')');
        }
        result.Append(": ").Append(DescribeIPv4Addresses(networkInterface));
        return result.ToString();
    }

    /// <summary>
    /// Troubleshooting method to log all the active network interfaces present in the system with
    /// IPv4 addresses.
    /// </summary>
    private static void ListNetworkInterfaces()
    {
        var interfaces = new List<string>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                interfaces.Add(DescribeInterface(networkInterface));
            }
            interfaces.Sort(StringComparer.Ordinal);
            if (interfaces.Count == 0)
            {
                Logger.LogWarning("No active network interfaces with IPv4 addresses found!");
            }
            else
            {
                Logger.LogWarning("Active network interfaces with IPv4 addresses:");
                foreach (var description in interfaces)
                {
                    Logger.LogWarning("{Description}", description);
                }
            }
        }
        catch (NetworkInformationException e)
        {
            Logger.LogError(e, "Unable to enumerate network interfaces");
        }
    }

    /// <summary>
    /// Finish the work of building and sending a protocol packet.
    /// </summary>
    /// <param name="kind">the type of packet to create and send</param>
    /// <param name="payload">the content which will follow our device name in the packet</param>
    /// <param name="destination">where the packet should be sent</param>
    /// <param name="port">the port to which the packet should be sent</param>
    /// <exception cref="InvalidOperationException">if we have no open socket</exception>
    /// <exception cref="SocketException">if there is a problem sending the packet</exception>
    private void AssembleAndSendPacket(PacketType kind, byte[] payload, IPAddress destination, int port)
    {
        var socket = _socket ?? throw new InvalidOperationException("VirtualCdj has no open socket");
        var packet = Util.BuildPacket(kind, DeviceNameBytes(), payload);
        try
        {
            socket.SendTo(packet, new IPEndPoint(destination, port));
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable)
        {
            Logger.LogWarning("Unable to route packet to player at address {Address}", destination);
            ListNetworkInterfaces();
            throw;
        }
    }

    /// <summary>
    /// The bytes after the device name in a media query packet.
    /// </summary>
    private static readonly byte[] MediaQueryPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x0c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// Ask a device for information about the media mounted in a particular slot. Will raise
    /// <see cref="MediaDetailsReceived"/> when a response is received.
    /// </summary>
    /// <param name="slot">the slot holding media we want to know about</param>
    /// <exception cref="ArgumentException">if the device for the slot cannot be found on the network</exception>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem sending the request</exception>
    public void SendMediaQuery(SlotReference slot)
    {
        var announcement = DeviceFinder.Instance.GetLatestAnnouncementFrom(slot.Player) ??
            throw new ArgumentException($"Device for {slot} not found on network.", nameof(slot));
        EnsureRunning();
        var payload = (byte[])MediaQueryPayload.Clone();
        payload[2] = (byte)DeviceNumber;
        lock (_keepAliveBytes)
        {
            Array.Copy(_keepAliveBytes, 44, payload, 5, 4);  // Copy in our IP address.
        }
        payload[12] = (byte)slot.Player;
        payload[16] = slot.Slot.ProtocolValue;
        AssembleAndSendPacket(PacketType.MediaQuery, payload, announcement.Address, UpdateSendPort);
    }

    /// <summary>
    /// The bytes after the device name in a sync control command packet.
    /// </summary>
    private static readonly byte[] SyncControlPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x08, 0x00, 0x00, 0x00, 0x0d, 0x00, 0x00, 0x00, 0x0f
    ];

    /// <summary>
    /// Assemble and send a packet that performs sync control, turning a device's sync mode on or
    /// off, or telling it to become the tempo master.
    /// </summary>
    /// <param name="target">an update from the device whose sync state is to be set</param>
    /// <param name="command">the byte identifying the specific sync command to be sent</param>
    private void SendSyncControlCommand(DeviceUpdate target, byte command)
    {
        EnsureRunning();
        var payload = (byte[])SyncControlPayload.Clone();
        payload[2] = (byte)DeviceNumber;
        payload[8] = (byte)DeviceNumber;
        payload[12] = command;
        AssembleAndSendPacket(PacketType.SyncControl, payload, target.Address, BeatSendPort);
    }

    /// <summary>
    /// Tell a device to turn sync on or off.
    /// </summary>
    /// <param name="deviceNumber">the device whose sync state is to be set</param>
    /// <param name="synced"><c>true</c> if sync should be turned on, else it will be turned off</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="ArgumentException">if the device number is not found on the network</exception>
    /// <exception cref="SocketException">if there is a problem sending the command to the device</exception>
    public void SendSyncModeCommand(int deviceNumber, bool synced)
    {
        var update = GetLatestStatusFor(deviceNumber) ??
            throw new ArgumentException($"Device {deviceNumber} not found on network.", nameof(deviceNumber));
        SendSyncModeCommand(update, synced);
    }

    /// <summary>
    /// Tell a device to turn sync on or off.
    /// </summary>
    /// <param name="target">an update from the device whose sync state is to be set</param>
    /// <param name="synced"><c>true</c> if sync should be turned on, else it will be turned off</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem sending the command to the device</exception>
    public void SendSyncModeCommand(DeviceUpdate target, bool synced) =>
        SendSyncControlCommand(target, synced ? (byte)0x10 : (byte)0x20);

    /// <summary>
    /// Tell a device to become tempo master.
    /// </summary>
    /// <param name="deviceNumber">the device we want to take over the role of tempo master</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="ArgumentException">if the device number is not found on the network</exception>
    /// <exception cref="SocketException">if there is a problem sending the command to the device</exception>
    public void AppointTempoMaster(int deviceNumber)
    {
        var update = GetLatestStatusFor(deviceNumber) ??
            throw new ArgumentException($"Device {deviceNumber} not found on network.", nameof(deviceNumber));
        AppointTempoMaster(update);
    }

    /// <summary>
    /// Tell a device to become tempo master.
    /// </summary>
    /// <param name="target">an update from the device that we want to take over the role of tempo master</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem sending the command to the device</exception>
    public void AppointTempoMaster(DeviceUpdate target) => SendSyncControlCommand(target, 0x01);

    /// <summary>
    /// The bytes after the device name in a fader start command packet.
    /// </summary>
    private static readonly byte[] FaderStartPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x04, 0x02, 0x02, 0x02, 0x02
    ];

    /// <summary>
    /// Broadcast a packet that tells some players to start playing and others to stop. If a
    /// player number is in both sets, it will be told to stop. Numbers outside the range 1 to 4
    /// are ignored.
    /// </summary>
    /// <param name="deviceNumbersToStart">the players that should start playing if they aren't already</param>
    /// <param name="deviceNumbersToStop">the players that should stop playing</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem broadcasting the command to the players</exception>
    public void SendFaderStartCommand(IReadOnlySet<int> deviceNumbersToStart, IReadOnlySet<int> deviceNumbersToStop)
    {
        EnsureRunning();
        var payload = (byte[])FaderStartPayload.Clone();
        payload[2] = (byte)DeviceNumber;

        for (var i = 1; i <= 4; i++)
        {
            if (deviceNumbersToStart.Contains(i))
            {
                payload[i + 4] = 0;
            }
            if (deviceNumbersToStop.Contains(i))
            {
                payload[i + 4] = 1;
            }
        }

        AssembleAndSendPacket(PacketType.FaderStartCommand, payload,
            _broadcastAddress ?? throw new InvalidOperationException("VirtualCdj has no broadcast address"), BeatSendPort);
    }

    /// <summary>
    /// The bytes after the device name in a channels on-air report packet.
    /// </summary>
    private static readonly byte[] ChannelsOnAirPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// Broadcast a packet that tells the players which channels are on the air (audible in the
    /// mixer output). This version sends the packet which was used by mixers older than the
    /// DJM-V10, and supports devices 1-4. Numbers outside the range 1 to 4 are ignored. If there
    /// is an actual DJM mixer on the network, it will be sending these packets several times per
    /// second, so the results of calling this method will be quickly overridden.
    /// </summary>
    /// <param name="deviceNumbersOnAir">the players whose channels are currently on the air</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem broadcasting the command to the players</exception>
    public void SendOnAirCommand(IReadOnlySet<int> deviceNumbersOnAir)
    {
        EnsureRunning();
        var payload = (byte[])ChannelsOnAirPayload.Clone();
        payload[2] = (byte)DeviceNumber;

        for (var i = 1; i <= 4; i++)
        {
            if (deviceNumbersOnAir.Contains(i))
            {
                payload[i + 4] = 1;
            }
        }

        AssembleAndSendPacket(PacketType.ChannelsOnAir, payload,
            _broadcastAddress ?? throw new InvalidOperationException("VirtualCdj has no broadcast address"), BeatSendPort);
    }

    /// <summary>
    /// The bytes after the device name in an extended channels on-air report packet.
    /// </summary>
    private static readonly byte[] ChannelsOnAirExtendedPayload =
    [
        0x01,
        0x03, 0x0d, 0x00, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// Broadcast a packet that tells the players which channels are on the air (audible in the
    /// mixer output). This version sends the packet used by the DJM-V10 mixer, and supports
    /// devices 1-6. Numbers outside the range 1 to 6 are ignored. If there is an actual DJM mixer
    /// on the network, it will be sending these packets several times per second, so the results
    /// of calling this method will be quickly overridden.
    /// </summary>
    /// <param name="deviceNumbersOnAir">the players whose channels are currently on the air</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem broadcasting the command to the players</exception>
    public void SendOnAirExtendedCommand(IReadOnlySet<int> deviceNumbersOnAir)
    {
        EnsureRunning();
        var payload = (byte[])ChannelsOnAirExtendedPayload.Clone();
        payload[2] = (byte)DeviceNumber;

        for (var i = 1; i <= 4; i++)
        {
            if (deviceNumbersOnAir.Contains(i))
            {
                payload[i + 4] = 1;
            }
        }

        for (var i = 5; i <= 6; i++)
        {
            if (deviceNumbersOnAir.Contains(i))
            {
                payload[i + 9] = 1;
            }
        }

        AssembleAndSendPacket(PacketType.ChannelsOnAir, payload,
            _broadcastAddress ?? throw new InvalidOperationException("VirtualCdj has no broadcast address"), BeatSendPort);
    }

    /// <summary>
    /// The bytes after the device name in a load-track command packet.
    /// </summary>
    private static readonly byte[] LoadTrackPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x34,  0x0d, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x32,  0x00, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// Send a packet to the target player telling it to load the specified track from the
    /// specified source player.
    /// </summary>
    /// <param name="targetPlayer">the device number of the player that you want to have load a track</param>
    /// <param name="rekordboxId">the identifier of a track within the source player's rekordbox database</param>
    /// <param name="sourcePlayer">the device number of the player from which the track should be loaded</param>
    /// <param name="sourceSlot">the media slot from which the track should be loaded</param>
    /// <param name="sourceType">the type of track to be loaded</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="ArgumentException">if the target device cannot be found</exception>
    /// <exception cref="SocketException">if there is a problem sending the command</exception>
    public void SendLoadTrackCommand(int targetPlayer, int rekordboxId,
        int sourcePlayer, CdjStatus.TrackSourceSlot sourceSlot, CdjStatus.TrackType sourceType)
    {
        var update = GetLatestStatusFor(targetPlayer) ??
            throw new ArgumentException($"Device {targetPlayer} not found on network.", nameof(targetPlayer));
        SendLoadTrackCommand(update, rekordboxId, sourcePlayer, sourceSlot, sourceType);
    }

    /// <summary>
    /// Send a packet to the target device telling it to load the specified track from the
    /// specified source player.
    /// </summary>
    /// <param name="target">an update from the player that you want to have load a track</param>
    /// <param name="rekordboxId">the identifier of a track within the source player's rekordbox database</param>
    /// <param name="sourcePlayer">the device number of the player from which the track should be loaded</param>
    /// <param name="sourceSlot">the media slot from which the track should be loaded</param>
    /// <param name="sourceType">the type of track to be loaded</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem sending the command</exception>
    public void SendLoadTrackCommand(DeviceUpdate target, int rekordboxId,
        int sourcePlayer, CdjStatus.TrackSourceSlot sourceSlot, CdjStatus.TrackType sourceType)
    {
        EnsureRunning();
        Logger.LogInformation("Sending track load command, target player: {Target}, id: {Id}, sourcePlayer: {Source}, sourceSlot: {Slot}, sourceType: {Type}",
            target.DeviceNumber, rekordboxId, sourcePlayer, sourceSlot, sourceType);
        var payload = (byte[])LoadTrackPayload.Clone();
        payload[0x02] = (byte)DeviceNumber;
        payload[0x05] = (byte)DeviceNumber;
        payload[0x09] = (byte)sourcePlayer;
        payload[0x0a] = sourceSlot.ProtocolValue;
        payload[0x0b] = sourceType.ProtocolValue;
        payload[0x21] = (byte)(target.DeviceNumber - 1);
        if (target.DeviceName.StartsWith("XDJ-XZ", StringComparison.Ordinal))
        {
            // Use the packet variant that seems to be necessary for the XDJ-XZ to accept it.
            payload[0x01] = 1;
            payload[0x2c] = 0x32;
            // See if we can find a rekordbox instance to pose as, which is also necessary to make
            // this work.
            foreach (var device in DeviceFinder.Instance.CurrentDevices)
            {
                var number = (byte)device.DeviceNumber;
                if (number >= 0x11 && number < 0x20)  // Aha! We can see a regular rekordbox instance, this will work.
                {
                    payload[0x02] = number;
                    payload[0x05] = number;
                    break;
                }
            }
            if (payload[0x02] == (byte)DeviceNumber)  // We did not find a laptop rekordbox, see if there's a mobile one.
            {
                foreach (var device in DeviceFinder.Instance.CurrentDevices)
                {
                    var number = (byte)device.DeviceNumber;
                    if (number >= 0x29 && number < 0x30)  // We can see rekordbox mobile, this will work too.
                    {
                        payload[0x02] = number;
                        payload[0x05] = number;
                        break;
                    }
                }
            }
        }
        Util.NumberToBytes(rekordboxId, payload, 0x0d, 4);
        AssembleAndSendPacket(PacketType.LoadTrackCommand, payload, target.Address, UpdateSendPort);
    }

    /// <summary>
    /// The bytes after the device name in a master handoff (yield) acknowledgment packet.
    /// </summary>
    private static readonly byte[] YieldAckPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x08,  0x00, 0x00, 0x00, 0x0d,   0x00, 0x00, 0x00, 0x01
    ];

    /// <summary>
    /// The bytes after the device name in a load-settings command packet.
    /// </summary>
    private static readonly byte[] LoadSettingsPayload =
    [
        0x02,
        0x0d, 0x0d, 0x00, 0x50,  0x12, 0x34, 0x56, 0x78,   0x00, 0x00, 0x00, 0x03,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x01, 0x00, 0x00,  0x00, 0x01, 0x01, 0x01,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// Send a packet to the target player telling it to apply the supplied settings.
    /// </summary>
    /// <param name="targetPlayer">the device number of the player that you want to configure</param>
    /// <param name="settings">the device settings that should be loaded</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="ArgumentException">if the target device cannot be found</exception>
    /// <exception cref="SocketException">if there is a problem sending the command</exception>
    public void SendLoadSettingsCommand(int targetPlayer, PlayerSettings settings)
    {
        var update = GetLatestStatusFor(targetPlayer) ??
            throw new ArgumentException($"Device {targetPlayer} not found on network.", nameof(targetPlayer));
        SendLoadSettingsCommand(update, settings);
    }

    /// <summary>
    /// Send a packet to the target device telling it to apply the supplied settings.
    /// </summary>
    /// <param name="target">an update from the player that you want to configure</param>
    /// <param name="settings">the device settings that should be loaded</param>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not active</exception>
    /// <exception cref="SocketException">if there is a problem sending the command</exception>
    public void SendLoadSettingsCommand(DeviceUpdate target, PlayerSettings settings)
    {
        EnsureRunning();
        var payload = BuildLoadSettingsPayload(target, settings);
        AssembleAndSendPacket(PacketType.LoadSettingsCommand, payload, target.Address, UpdateSendPort);
    }

    /// <summary>
    /// Builds the payload of a load-settings command packet. Separated out (and made internal) so
    /// the packet layout can be verified by tests.
    /// </summary>
    /// <param name="target">an update from the player being configured</param>
    /// <param name="settings">the device settings that should be loaded</param>
    /// <returns>the payload bytes which follow the device name in the packet</returns>
    internal byte[] BuildLoadSettingsPayload(DeviceUpdate target, PlayerSettings settings)
    {
        var payload = (byte[])LoadSettingsPayload.Clone();
        Util.SetPayloadByte(payload, 0x20, (byte)DeviceNumber);
        Util.SetPayloadByte(payload, 0x21, (byte)target.DeviceNumber);
        Util.SetPayloadByte(payload, 0x2c, settings.OnAirDisplay.ProtocolValue);
        Util.SetPayloadByte(payload, 0x2d, settings.MainLcdBrightness.ProtocolValue);
        Util.SetPayloadByte(payload, 0x2e, settings.Quantize.ProtocolValue);
        Util.SetPayloadByte(payload, 0x2f, settings.InitialAutoCueLevel.ProtocolValue);
        Util.SetPayloadByte(payload, 0x30, settings.InterfaceLanguage.ProtocolValue);
        Util.SetPayloadByte(payload, 0x32, settings.JogRingIllumination.ProtocolValue);
        Util.SetPayloadByte(payload, 0x33, settings.JogRingIndicator.ProtocolValue);
        Util.SetPayloadByte(payload, 0x34, settings.SlipFlashing.ProtocolValue);
        Util.SetPayloadByte(payload, 0x38, settings.DiscSlotIllumination.ProtocolValue);
        Util.SetPayloadByte(payload, 0x39, settings.EjectLoadLock.ProtocolValue);
        Util.SetPayloadByte(payload, 0x3a, settings.Sync.ProtocolValue);
        Util.SetPayloadByte(payload, 0x3b, settings.AutoPlayMode.ProtocolValue);
        Util.SetPayloadByte(payload, 0x3c, settings.QuantizeBeatValue.ProtocolValue);
        Util.SetPayloadByte(payload, 0x3d, settings.HotCueAutoLoadMode.ProtocolValue);
        Util.SetPayloadByte(payload, 0x3e, settings.HotCueColor.ProtocolValue);
        Util.SetPayloadByte(payload, 0x41, settings.NeedleLock.ProtocolValue);
        Util.SetPayloadByte(payload, 0x44, settings.TimeDisplay.ProtocolValue);
        Util.SetPayloadByte(payload, 0x45, settings.JogWheelMode.ProtocolValue);
        Util.SetPayloadByte(payload, 0x46, settings.AutoCue.ProtocolValue);
        Util.SetPayloadByte(payload, 0x47, settings.MasterTempo.ProtocolValue);
        Util.SetPayloadByte(payload, 0x48, settings.TempoFaderRange.ProtocolValue);
        Util.SetPayloadByte(payload, 0x49, settings.PhaseMeter.ProtocolValue);
        Util.SetPayloadByte(payload, 0x4c, settings.VinylSpeedAdjustMode.ProtocolValue);
        Util.SetPayloadByte(payload, 0x4d, settings.JogWheelDisplayMode.ProtocolValue);
        Util.SetPayloadByte(payload, 0x4e, settings.PadAndButtonBrightness.ProtocolValue);
        Util.SetPayloadByte(payload, 0x4f, settings.JogWheelLcdBrightness.ProtocolValue);
        return payload;
    }

    /// <summary>
    /// The number of milliseconds that we will wait between sending status packets, when we are
    /// sending them.
    /// </summary>
    private volatile int _statusInterval = 200;

    /// <summary>
    /// How often, in milliseconds, we will send status packets, if we are configured to send
    /// them. You probably won't need to change this unless you are experimenting.
    /// </summary>
    /// <exception cref="ArgumentException">if set to a value less than 20 or more than 2000</exception>
    public int StatusInterval
    {
        get => _statusInterval;
        set
        {
            if (value < 20 || value > 2000)
            {
                throw new ArgumentException("interval must be between 20 and 2000", nameof(value));
            }
            _statusInterval = value;
        }
    }

    /// <summary>
    /// Makes sure we stop sending status if the <see cref="BeatFinder"/> shuts down, because we
    /// rely on it.
    /// </summary>
    private void OnBeatFinderStopped(object? sender, EventArgs e)
    {
        if (IsSendingStatus)
        {
            Logger.LogInformation("VirtualCDJ no longer sending status updates because BeatFinder has stopped.");
            try
            {
                SetSendingStatus(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Problem stopping sending status packets when the BeatFinder stopped");
            }
        }
    }

    /// <summary>
    /// Will hold an instance when we are actively sending beats, so we can let it know when the
    /// metronome changes, and when it is time to shut down.
    /// </summary>
    private volatile BeatSender? _beatSender;

    /// <summary>
    /// Check whether we are currently running a <see cref="BeatSender"/>; if we are, notify it
    /// that there has been a change to the metronome timeline, so it needs to wake up and
    /// reassess its situation.
    /// </summary>
    private void NotifyBeatSenderOfChange() => _beatSender?.TimelineChanged();

    /// <summary>
    /// The bytes following the device name in a beat packet.
    /// </summary>
    private static readonly byte[] BeatPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x3c,  0x01, 0x01, 0x01, 0x01,   0x02, 0x02, 0x02, 0x02,  0x10, 0x10, 0x10, 0x10,
        0x04, 0x04, 0x04, 0x04,  0x20, 0x20, 0x20, 0x20,   0x08, 0x08, 0x08, 0x08,  0xff, 0xff, 0xff, 0xff,
        0xff, 0xff, 0xff, 0xff,  0xff, 0xff, 0xff, 0xff,   0xff, 0xff, 0xff, 0xff,  0xff, 0xff, 0xff, 0xff,
        0xff, 0xff, 0xff, 0xff,  0x00, 0x10, 0x00, 0x00,   0x00, 0x00, 0x00, 0x00,  0x0b, 0x00, 0x00, 0x0d
    ];

    /// <summary>
    /// Sends a beat packet. Generally this should only be invoked when our
    /// <see cref="BeatSender"/> has determined that it is time to do so, but it is public to
    /// allow experimentation.
    /// </summary>
    /// <returns>the beat number that was sent, computed from the current (or stopped) playback position</returns>
    public long SendBeat() => SendBeat(GetPlaybackPosition());

    /// <summary>
    /// Sends a beat packet. Generally this should only be invoked when our
    /// <see cref="BeatSender"/> has determined that it is time to do so, but it is public to
    /// allow experimentation.
    /// </summary>
    /// <param name="snapshot">the time at which the beat to be sent occurred, for computation of its beat number</param>
    /// <returns>the beat number that was sent</returns>
    public long SendBeat(Snapshot snapshot)
    {
        try
        {
            AssembleAndSendPacket(PacketType.Beat, BuildBeatPayload(snapshot),
                _broadcastAddress ?? throw new InvalidOperationException("VirtualCdj has no broadcast address"),
                BeatSendPort);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "VirtualCdj Failed to send beat packet.");
        }

        return snapshot.Beat;
    }

    /// <summary>
    /// Builds the payload of a beat packet from a metronome snapshot. Separated out (and made
    /// internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <param name="snapshot">the moment at which the beat occurred</param>
    /// <returns>the payload bytes which follow the device name in the packet</returns>
    internal byte[] BuildBeatPayload(Snapshot snapshot)
    {
        var payload = (byte[])BeatPayload.Clone();
        payload[0x02] = (byte)DeviceNumber;

        Util.NumberToBytes((int)snapshot.BeatInterval, payload, 0x05, 4);
        Util.NumberToBytes((int)(snapshot.BeatInterval * 2), payload, 0x09, 4);
        Util.NumberToBytes((int)(snapshot.BeatInterval * 4), payload, 0x11, 4);
        Util.NumberToBytes((int)(snapshot.BeatInterval * 8), payload, 0x19, 4);

        var beatsLeft = 5 - snapshot.BeatWithinBar;
        var nextBar = (int)(snapshot.BeatInterval * beatsLeft);
        Util.NumberToBytes(nextBar, payload, 0x0d, 4);
        Util.NumberToBytes(nextBar + (int)snapshot.BarInterval, payload, 0x15, 4);

        Util.NumberToBytes((long)Math.Round(snapshot.Tempo * 100, MidpointRounding.AwayFromZero), payload, 0x3b, 2);
        payload[0x3d] = (byte)snapshot.BeatWithinBar;
        payload[0x40] = (byte)DeviceNumber;
        return payload;
    }

    /// <summary>
    /// Builds a complete beat packet (header, device name, and payload) from a metronome
    /// snapshot, for verification by tests.
    /// </summary>
    /// <param name="snapshot">the moment at which the beat occurred</param>
    /// <returns>the bytes of the packet that would be broadcast</returns>
    internal byte[] BuildBeatPacket(Snapshot snapshot) =>
        Util.BuildPacket(PacketType.Beat, DeviceNameBytes(), BuildBeatPayload(snapshot));

    /// <summary>
    /// Tracks the token identifying the currently active status sender thread, if any, so we can
    /// tell it to stop, and so <see cref="IsSendingStatus"/> can report whether we are sending.
    /// </summary>
    private sealed class StatusSenderHandle
    {
        /// <summary>
        /// Becomes <c>false</c> when the sender thread should shut down.
        /// </summary>
        public volatile bool Running = true;
    }

    /// <summary>
    /// Will hold a non-null value when we are sending our own status packets, which can be used
    /// to stop the thread doing so.
    /// </summary>
    private volatile StatusSenderHandle? _statusSender;

    /// <summary>
    /// Check whether we are currently sending status packets, and so can participate in (and
    /// control) tempo and beat sync.
    /// </summary>
    public bool IsSendingStatus => _statusSender != null;

    /// <summary>
    /// Control whether the Virtual CDJ sends status packets to the other players. Most uses of
    /// Beat Link will not require this level of activity. However, if you want to be able to take
    /// over the tempo master role, and control the tempo and beat alignment of other players, you
    /// will need to turn on this feature, which also requires that you are using one of the
    /// standard player numbers, 1-4.
    /// </summary>
    /// <param name="send">if <c>true</c> we will send status packets, and can participate in (and control) tempo and beat sync</param>
    /// <exception cref="InvalidOperationException">if the virtual CDJ is not running, or if it is
    /// not using a device number in the range 1 through 4</exception>
    /// <exception cref="SocketException">if there is a problem starting the <see cref="BeatFinder"/></exception>
    public void SetSendingStatus(bool send)
    {
        lock (_lifecycleLock)
        {
            if (IsSendingStatus == send)
            {
                return;
            }

            if (send)  // Start sending status packets.
            {
                EnsureRunning();
                if (_proxyingForVirtualRekordbox)
                {
                    throw new InvalidOperationException("Cannot send status when in Opus Quad rekordbox lighting mode.");
                }
                if (DeviceNumber is < 1 or > 4)
                {
                    throw new InvalidOperationException("Can only send status when using a standard player number, 1 through 4.");
                }

                BeatFinder.Instance.Start();
                BeatFinder.Instance.Stopped += OnBeatFinderStopped;

                var handle = new StatusSenderHandle();
                _statusSender = handle;  // Allow other threads to stop us when necessary.
                StartSenderThread(handle);

                if (IsSynced)  // If we are supposed to be synced, we need to respond to master beats and tempo changes.
                {
                    AddOurSyncListener();
                }

                if (IsPlaying)  // Start the beat sender too, if we are supposed to be playing.
                {
                    _beatSender = new BeatSender(_metronome);
                }
            }
            else  // Stop sending status packets, and responding to master beats and tempo changes if we were synced.
            {
                BeatFinder.Instance.Stopped -= OnBeatFinderStopped;
                RemoveOurSyncListener();

                var handle = _statusSender;
                if (handle != null)
                {
                    handle.Running = false;  // Stop the status sending thread.
                }
                _statusSender = null;  // Indicate that we are no longer sending status.
                var activeSender = _beatSender;  // And stop the beat sender if we have one.
                if (activeSender != null)
                {
                    activeSender.ShutDown();
                    _beatSender = null;
                }
            }
        }
    }

    /// <summary>
    /// Creates and starts the thread which sends our status packets as long as the handle remains
    /// marked as running.
    /// </summary>
    /// <param name="handle">used to determine when to shut down</param>
    private void StartSenderThread(StatusSenderHandle handle)
    {
        var sender = new Thread(() =>
        {
            while (handle.Running)
            {
                try
                {
                    SendStatus();
                }
                catch (Exception e)
                {
                    // (The Java implementation let exceptions here silently kill the sender
                    // thread; we log and keep trying, since transient conditions like the
                    // DeviceFinder stopping mid-send should not disable status sending.)
                    Logger.LogWarning(e, "Problem sending status packets; continuing");
                }
                try
                {
                    Thread.Sleep(StatusInterval);
                }
                catch (ThreadInterruptedException)
                {
                    Logger.LogWarning("beat-link VirtualCDJ status sender thread was interrupted; continuing");
                }
            }
        })
        {
            Name = "beat-link VirtualCdj status sender",
            IsBackground = true
        };
        sender.Start();
    }

    /// <summary>
    /// Used to keep time when we are pretending to play a track, and to allow us to sync with
    /// other players when we are told to do so.
    /// </summary>
    private readonly Metronome _metronome = new();

    /// <summary>
    /// Keeps track of our position when we are not playing; this beat gets loaded into the
    /// metronome when we start playing, and it will keep time from there. When we stop again, we
    /// save the metronome's current beat here.
    /// </summary>
    private volatile Snapshot _whereStopped;

    /// <summary>
    /// Indicates whether we should currently pretend to be playing. This will only have an impact
    /// when we are sending status and beat packets.
    /// </summary>
    private volatile bool _playing;

    /// <summary>
    /// Controls whether we report that we are playing. This will only have an impact when we are
    /// sending status and beat packets.
    /// </summary>
    /// <param name="playing"><c>true</c> if we should seem to be playing</param>
    public void SetPlaying(bool playing)
    {
        if (_playing == playing)
        {
            return;
        }

        _playing = playing;

        if (playing)
        {
            _metronome.JumpToBeat(_whereStopped.Beat);
            if (IsSendingStatus)  // Need to also start the beat sender.
            {
                _beatSender = new BeatSender(_metronome);
            }
        }
        else
        {
            var activeSender = _beatSender;
            if (activeSender != null)  // We have a beat sender we need to stop.
            {
                activeSender.ShutDown();
                _beatSender = null;
            }
            _whereStopped = _metronome.GetSnapshot();
        }
    }

    /// <summary>
    /// Check whether we are pretending to be playing. This will only have an impact when we are
    /// sending status and beat packets.
    /// </summary>
    public bool IsPlaying => _playing;

    /// <summary>
    /// Find details about the current simulated playback position.
    /// </summary>
    /// <returns>the current (or last, if we are stopped) playback state</returns>
    public Snapshot GetPlaybackPosition() => _playing ? _metronome.GetSnapshot() : _whereStopped;

    /// <summary>
    /// <para>Nudge the playback position by the specified number of milliseconds, to support
    /// synchronization with an external clock. Positive values move playback forward in time,
    /// while negative values jump back. If we are sending beat packets, notify the beat sender
    /// that the timeline has changed.</para>
    ///
    /// <para>If the shift would put us back before beat one, we will jump forward a bar to
    /// correct that. It is thus not safe to jump backwards more than a bar's worth of time.</para>
    /// </summary>
    /// <param name="ms">the number of milliseconds to shift the simulated playback position</param>
    public void AdjustPlaybackPosition(int ms)
    {
        if (ms != 0)
        {
            _metronome.AdjustStart(-ms);
            if (_metronome.GetBeat() < 1)
            {
                _metronome.AdjustStart(
                    (long)Math.Round(Metronome.BeatsToMilliseconds(_metronome.BeatsPerBar, _metronome.Tempo),
                        MidpointRounding.AwayFromZero));
            }
            NotifyBeatSenderOfChange();
        }
    }

    /// <summary>
    /// Indicates whether we are currently the tempo master. Will only be meaningful (and get set)
    /// if we are sending status packets.
    /// </summary>
    private volatile bool _master;

    /// <summary>
    /// The bytes after the device name in a master handoff request packet.
    /// </summary>
    private static readonly byte[] MasterHandoffRequestPayload =
    [
        0x01,
        0x00, 0x0d, 0x00, 0x04, 0x00, 0x00, 0x00, 0x0d
    ];

    /// <summary>
    /// When we have started the process of requesting the tempo master role from another player,
    /// this gets set to its device number. Otherwise, it has the value 0.
    /// </summary>
    private volatile int _requestingMasterRoleFromPlayer;

    /// <summary>
    /// Arrange to become the tempo master. Starts a sequence of interactions with the other
    /// players that should end up with us in charge of the group tempo and beat alignment.
    /// </summary>
    /// <exception cref="InvalidOperationException">if we are not sending status updates</exception>
    /// <exception cref="SocketException">if there is a problem sending the master yield request</exception>
    public void BecomeTempoMaster()
    {
        lock (_lifecycleLock)
        {
            Logger.LogDebug("Trying to become master.");
            if (!IsSendingStatus)
            {
                throw new InvalidOperationException("Must be sending status updates to become the tempo master.");
            }

            // Is there someone we need to ask to yield to us?
            var currentMaster = TempoMaster;
            if (currentMaster != null)
            {
                // Send the yield request; we will become master when we get a successful
                // response.
                var payload = (byte[])MasterHandoffRequestPayload.Clone();
                payload[2] = (byte)DeviceNumber;
                payload[8] = (byte)DeviceNumber;
                Logger.LogDebug("Sending master yield request to player {Master}", currentMaster);
                _requestingMasterRoleFromPlayer = currentMaster.DeviceNumber;
                AssembleAndSendPacket(PacketType.MasterHandoffRequest, payload, currentMaster.Address, BeatSendPort);
            }
            else if (!_master)
            {
                // There is no other master, we can just become it immediately.
                _requestingMasterRoleFromPlayer = 0;
                SetMasterTempo(Tempo);
                _master = true;
            }
        }
    }

    /// <summary>
    /// Check whether we are currently in charge of the tempo and beat alignment.
    /// </summary>
    public bool IsTempoMaster => _master;

    /// <summary>
    /// Tracks whether the handlers which slave our metronome to the tempo master when we are
    /// synced (the Java implementation's <c>ourSyncMasterListener</c>) are currently attached to
    /// our own <see cref="TempoChanged"/> and <see cref="MasterBeat"/> events. Needed because the
    /// Java listener collection was a set, so re-registration was idempotent, while C# events are
    /// not. Guarded by <see cref="_lifecycleLock"/>.
    /// </summary>
    private bool _ourSyncListenerRegistered;

    /// <summary>
    /// Responds to master tempo changes when we are synced, aligning our own metronome.
    /// </summary>
    private void OurSyncTempoChanged(object? sender, double tempo)
    {
        if (!IsTempoMaster)
        {
            _metronome.Tempo = tempo;
        }
    }

    /// <summary>
    /// Responds to master beats when we are synced, aligning our own metronome.
    /// </summary>
    private void OurSyncMasterBeat(object? sender, Beat beat)
    {
        if (!IsTempoMaster)
        {
            _metronome.SetBeatPhase(0.0);
        }
    }

    /// <summary>
    /// Attaches the sync-follower handlers if they are not already attached.
    /// </summary>
    private void AddOurSyncListener()
    {
        lock (_lifecycleLock)
        {
            if (!_ourSyncListenerRegistered)
            {
                TempoChanged += OurSyncTempoChanged;
                MasterBeat += OurSyncMasterBeat;
                _ourSyncListenerRegistered = true;
            }
        }
    }

    /// <summary>
    /// Detaches the sync-follower handlers if they are attached.
    /// </summary>
    private void RemoveOurSyncListener()
    {
        lock (_lifecycleLock)
        {
            if (_ourSyncListenerRegistered)
            {
                TempoChanged -= OurSyncTempoChanged;
                MasterBeat -= OurSyncMasterBeat;
                _ourSyncListenerRegistered = false;
            }
        }
    }

    /// <summary>
    /// Indicates whether we are currently staying in sync with the tempo master. Will only be
    /// meaningful if we are sending status packets.
    /// </summary>
    private volatile bool _synced;

    /// <summary>
    /// Controls whether we are currently staying in sync with the tempo master. Will only be
    /// meaningful if we are sending status packets.
    /// </summary>
    /// <param name="sync">if <c>true</c>, our status packets will be tempo and beat aligned with the tempo master</param>
    public void SetSynced(bool sync)
    {
        lock (_lifecycleLock)
        {
            if (_synced != sync)
            {
                // We are changing sync state, so add or remove our master listener as
                // appropriate.
                if (sync && IsSendingStatus)
                {
                    AddOurSyncListener();
                }
                else
                {
                    RemoveOurSyncListener();
                }

                // Also, if there is a tempo master, and we just got synced, adopt its tempo.
                if (!IsTempoMaster && Volatile.Read(ref _tempoMaster) != null)
                {
                    Tempo = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _masterTempoBits));
                }
            }
            _synced = sync;
        }
    }

    /// <summary>
    /// Check whether we are currently staying in sync with the tempo master. Will only be
    /// meaningful if we are sending status packets.
    /// </summary>
    public bool IsSynced => _synced;

    /// <summary>
    /// Indicates whether we believe our channel is currently on the air (audible in the mixer
    /// output). Will only be meaningful if we are sending status packets.
    /// </summary>
    private volatile bool _onAir;

    /// <summary>
    /// Change whether we believe our channel is currently on the air (audible in the mixer
    /// output). Only meaningful if we are sending status packets. If there is a real DJM mixer on
    /// the network, it will rapidly override any value established by this method with its actual
    /// report about the channel state.
    /// </summary>
    /// <param name="audible"><c>true</c> if we should report ourselves as being on the air in our status packets</param>
    public void SetOnAir(bool audible) => _onAir = audible;

    /// <summary>
    /// Checks whether we believe our channel is currently on the air (audible in the mixer
    /// output). Only meaningful if we are sending status packets. If there is a real DJM mixer on
    /// the network, it will be controlling the state of this property.
    /// </summary>
    public bool IsOnAir => _onAir;

    /// <summary>
    /// The tempo at which we report ourselves to be playing, in beats per minute. Only meaningful
    /// if we are sending status packets. If <see cref="IsSynced"/> is <c>true</c> and we are not
    /// the tempo master, any value set here will be overridden by the next tempo master change.
    /// </summary>
    /// <exception cref="ArgumentException">if set to zero</exception>
    public double Tempo
    {
        get => _metronome.Tempo;
        set
        {
            if (value == 0.0)
            {
                throw new ArgumentException("Tempo cannot be zero.", nameof(value));
            }

            var oldTempo = _metronome.Tempo;
            _metronome.Tempo = value;
            NotifyBeatSenderOfChange();

            if (IsTempoMaster && Math.Abs(value - oldTempo) > TempoEpsilon)
            {
                EventDispatcher.DeliverNow(TempoChanged, this, value, Logger);
            }
        }
    }

    /// <summary>
    /// Used to keep our beat number from growing indefinitely; we wrap it after a little over
    /// nine hours of playback; maybe we are playing a giant loop?
    /// </summary>
    /// <param name="beat">the beat number to be wrapped</param>
    /// <returns>the beat number, wrapped into the range 1 through <see cref="MaxBeat"/></returns>
    private static int WrapBeat(int beat)
    {
        if (beat <= MaxBeat)
        {
            return beat;
        }
        // This math is a little funky because beats are one-based rather than zero-based.
        return (beat - 1) % MaxBeat + 1;
    }

    /// <summary>
    /// Moves our current playback position to the specified beat; this will be reflected in any
    /// status and beat packets that we are sending. An incoming value less than one will jump us
    /// to the first beat.
    /// </summary>
    /// <param name="beat">the beat that we should pretend to be playing</param>
    public void JumpToBeat(int beat)
    {
        lock (_lifecycleLock)
        {
            beat = beat < 1 ? 1 : WrapBeat(beat);

            if (_playing)
            {
                _metronome.JumpToBeat(beat);
            }
            else
            {
                _whereStopped = _metronome.GetSnapshot(_metronome.GetTimeOfBeat(beat));
            }
        }
    }

    /// <summary>
    /// Used in the process of handing off the tempo master role to another player.
    /// </summary>
    private volatile int _syncCounter = 1;

    /// <summary>
    /// Tracks the largest sync counter we have seen on the network, used in the process of
    /// handing off the tempo master role to another player.
    /// </summary>
    private volatile int _largestSyncCounter = 1;

    /// <summary>
    /// Used in the process of handing off the tempo master role to another player. Usually has
    /// the value 0xff, meaning no handoff is taking place. But when we are in the process of
    /// handing off the role, will hold the device number of the player that is taking over as
    /// tempo master.
    /// </summary>
    private volatile int _nextMaster = 0xff;

    /// <summary>
    /// Used in the process of being handed the tempo master role from another player. Usually has
    /// the value 0, meaning no handoff is taking place. But when we have received a successful
    /// yield response, will hold the device number of the player that is yielding to us, so we
    /// can watch for the next stage in its status updates.
    /// </summary>
    private volatile int _masterYieldedFrom;

    /// <summary>
    /// Keeps track of the number of status packets we send.
    /// </summary>
    private int _packetCounter;

    /// <summary>
    /// The template used to assemble a status packet when we are sending them.
    /// </summary>
    private static readonly byte[] StatusPayload =
    [
        0x01,
        0x04, 0x00, 0x00, 0xf8, 0x00, 0x00, 0x01, 0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01,  // 0x020
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xa0, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x030
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x040
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x050
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x04, 0x04, 0x00, 0x00, 0x00, 0x04,  // 0x060
        0x00, 0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x31, 0x2e, 0x34, 0x33,  // 0x070
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0x00, 0x00, 0x10, 0x00, 0x00,  // 0x080
        0x80, 0x00, 0x00, 0x00, 0x7f, 0xff, 0xff, 0xff, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x090
        0x00, 0x00, 0x00, 0x00, 0x01, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x0a0
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x0b0
        0x00, 0x10, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0f, 0x01, 0x00, 0x00,  // 0x0c0
        0x12, 0x34, 0x56, 0x78, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,  // 0x0d0
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x0e0
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x0f0
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // 0x100
        0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x07, 0x61, 0x00, 0x00, 0x06, 0x2f  // 0x110
    ];

    /// <summary>
    /// Gets the current playback position, then checks if we are within
    /// <see cref="BeatSender.SleepThreshold"/> ms before an upcoming beat, or
    /// <see cref="BeatSender.BeatThreshold"/> ms after one, sleeping until that is no longer the
    /// case.
    /// </summary>
    /// <returns>the current playback position, potentially after having delayed a bit so that is not too near a beat</returns>
    private Snapshot AvoidBeatPacket()
    {
        var playState = GetPlaybackPosition();
        var distance = playState.DistanceFromBeat;
        while (_playing &&
               ((distance < 0.0 && Math.Abs(distance) <= BeatSender.SleepThreshold) ||
                (distance >= 0.0 && distance <= BeatSender.BeatThreshold + 1)))
        {
            try
            {
                Thread.Sleep(2);
            }
            catch (ThreadInterruptedException e)
            {
                Logger.LogWarning(e, "Interrupted while sleeping to avoid beat packet; ignoring.");
            }
            playState = GetPlaybackPosition();
            distance = playState.DistanceFromBeat;
        }
        return playState;
    }

    /// <summary>
    /// Builds a complete status packet reflecting the supplied playback state. Separated out (and
    /// made internal) so the packet layout can be verified by tests.
    /// </summary>
    /// <param name="playState">the playback position the packet should describe</param>
    /// <param name="playing">whether we should report ourselves as playing</param>
    /// <returns>the bytes of the packet that would be sent</returns>
    internal byte[] BuildStatusPacket(Snapshot playState, bool playing)
    {
        var payload = (byte[])StatusPayload.Clone();
        var deviceNumber = (byte)DeviceNumber;
        payload[0x02] = deviceNumber;
        payload[0x05] = deviceNumber;
        payload[0x08] = (byte)(playing ? 1 : 0);        // a, playing flag
        payload[0x09] = deviceNumber;                   // Dr, the player from which the track was loaded
        payload[0x5c] = (byte)(playing ? 3 : 5);        // P1, playing flag
        Util.NumberToBytes(_syncCounter, payload, 0x65, 4);
        payload[0x6a] = (byte)(0x84 +                   // F, main status bit vector
            (playing ? 0x40 : 0) + (_master ? 0x20 : 0) + (_synced ? 0x10 : 0) + (_onAir ? 0x08 : 0));
        payload[0x6c] = (byte)(playing ? 0x7a : 0x7e);  // P2, playing flag
        Util.NumberToBytes((long)Math.Round(Tempo * 100, MidpointRounding.AwayFromZero), payload, 0x73, 2);
        payload[0x7e] = (byte)(playing ? 9 : 1);        // P3, playing flag
        payload[0x7f] = (byte)(_master ? 1 : 0);        // Mm, tempo master flag
        payload[0x80] = (byte)_nextMaster;              // Mh, tempo master handoff indicator
        Util.NumberToBytes((int)playState.Beat, payload, 0x81, 4);
        payload[0x87] = (byte)playState.BeatWithinBar;
        Util.NumberToBytes(Interlocked.Increment(ref _packetCounter), payload, 0xa9, 4);

        return Util.BuildPacket(PacketType.CdjStatus, DeviceNameBytes(), payload);
    }

    /// <summary>
    /// Send a status packet to all devices on the network. Used when we are actively sending
    /// status, presumably so we can be the tempo master. Avoids sending one within twice
    /// <see cref="BeatSender.BeatThreshold"/> milliseconds of a beat, to make sure the beat
    /// packet announces the new beat before an early status packet confuses matters.
    /// </summary>
    private void SendStatus()
    {
        var playState = AvoidBeatPacket();
        var packet = BuildStatusPacket(playState, _playing);
        var socket = _socket;
        if (socket == null)
        {
            return;  // We are shutting down.
        }
        foreach (var device in DeviceFinder.Instance.CurrentDevices)
        {
            try
            {
                socket.SendTo(packet, new IPEndPoint(device.Address, UpdateSendPort));
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Unable to send status packet to {Device}", device);
            }
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static VirtualCdj Instance { get; } = new();

    /// <summary>
    /// Register any relevant listeners; private to prevent instantiation. As in the Java
    /// implementation, we hook up all our reactions to <see cref="BeatFinder"/> packets here,
    /// starting with the internal priority beat listener seam so we always see beats before any
    /// other component or subscriber.
    /// </summary>
    private VirtualCdj()
    {
        _whereStopped = _metronome.GetSnapshot(_metronome.StartTime);

        // Register to receive beats through the priority seam FIRST, so we process them before
        // the TimeFinder and any public subscribers.
        BeatFinder.Instance.AddPriorityBeatListener(new PriorityBeatListener(this));

        // Arrange to have our status accurately reflect any relevant updates and commands from
        // the mixer.
        BeatFinder.Instance.ChannelsOnAir += (_, audibleChannels) => SetOnAir(audibleChannels.Contains(DeviceNumber));

        BeatFinder.Instance.FaderStartCommanded += (_, command) =>
        {
            if (command.PlayersToStart.Contains(DeviceNumber))
            {
                SetPlaying(true);
            }
            else if (command.PlayersToStop.Contains(DeviceNumber))
            {
                SetPlaying(false);
            }
        };

        BeatFinder.Instance.SyncModeCommanded += (_, synced) => SetSynced(synced);

        BeatFinder.Instance.MasterCommanded += (_, _) => HandleMasterCommand();

        BeatFinder.Instance.MasterYieldRequested += (_, deviceNumber) => HandleMasterYieldRequest(deviceNumber);

        BeatFinder.Instance.MasterYieldResponded += (_, response) => HandleMasterYieldResponse(response.Player, response.Yielded);
    }

    /// <summary>
    /// Reacts to a packet telling us to become the tempo master; since that involves network
    /// conversations which must not occur on the packet receive thread, the actual work happens
    /// on a new thread, as in the Java implementation.
    /// </summary>
    private void HandleMasterCommand()
    {
        Logger.LogDebug("Received packet telling us to become master.");
        if (IsSendingStatus)
        {
            new Thread(() =>
            {
                try
                {
                    BecomeTempoMaster();
                }
                catch (Exception t)
                {
                    Logger.LogError(t, "Problem becoming tempo master in response to sync command packet");
                }
            })
            {
                IsBackground = true
            }.Start();
        }
        else
        {
            Logger.LogWarning("Ignoring sync command to become tempo master, since we are not sending status packets.");
        }
    }

    /// <summary>
    /// Reacts to a request from an incoming tempo master that we yield the role to it, starting
    /// the handoff process if we are indeed the current master. Internal (rather than a private
    /// event handler body) so the state machine can be exercised directly by tests.
    /// </summary>
    /// <param name="deviceNumber">the device number which wants to take over the tempo master role</param>
    internal void HandleMasterYieldRequest(int deviceNumber)
    {
        Logger.LogDebug("Received instruction to yield master to device {Device}", deviceNumber);
        if (IsTempoMaster)
        {
            if (IsSendingStatus && DeviceNumber != deviceNumber)
            {
                _nextMaster = deviceNumber;
                var lastStatusFromNewMaster = GetLatestStatusFor(deviceNumber);
                if (lastStatusFromNewMaster == null)
                {
                    Logger.LogWarning("Unable to send master yield response to device {Device}: no status updates have been received from it!", deviceNumber);
                }
                else
                {
                    var payload = (byte[])YieldAckPayload.Clone();
                    payload[0x02] = (byte)DeviceNumber;
                    payload[0x08] = (byte)DeviceNumber;
                    try
                    {
                        AssembleAndSendPacket(PacketType.MasterHandoffResponse, payload, lastStatusFromNewMaster.Address, UpdateSendPort);
                    }
                    catch (Exception t)
                    {
                        Logger.LogError(t, "Problem sending master yield acknowledgment to player {Device}", deviceNumber);
                    }
                }
            }
        }
        else
        {
            Logger.LogWarning("Ignoring instruction to yield master to device {Device}: we were not tempo master.", deviceNumber);
        }
    }

    /// <summary>
    /// Reacts to a player's response to our request that it yield the tempo master role to us.
    /// Internal (rather than a private event handler body) so the state machine can be exercised
    /// directly by tests.
    /// </summary>
    /// <param name="deviceNumber">the device number that responded</param>
    /// <param name="yielded">whether it agreed to yield the role to us</param>
    internal void HandleMasterYieldResponse(int deviceNumber, bool yielded)
    {
        Logger.LogDebug("Received yield response of {Yielded} from device {Device}", yielded, deviceNumber);
        if (yielded)
        {
            if (IsSendingStatus)
            {
                if (deviceNumber == _requestingMasterRoleFromPlayer)
                {
                    _requestingMasterRoleFromPlayer = 0;
                    _masterYieldedFrom = deviceNumber;
                }
                else if (_requestingMasterRoleFromPlayer == 0)
                {
                    Logger.LogWarning("Ignoring master yield response from player {Device} because we are not trying to become tempo master.", deviceNumber);
                }
                else
                {
                    Logger.LogWarning("Ignoring master yield response from player {Device} because we asked player {Asked}",
                        deviceNumber, _requestingMasterRoleFromPlayer);
                }
            }
            else
            {
                Logger.LogWarning("Ignoring master yield response because we are not sending status.");
            }
        }
        else
        {
            Logger.LogWarning("Ignoring master yield response with unexpected non-yielding value.");
        }
    }

    /// <summary>
    /// We have received a packet from a device trying to claim a device number, see if we should
    /// defend it.
    /// </summary>
    /// <param name="packet">the packet received</param>
    /// <param name="deviceOffset">the index of the byte within the packet holding the device number being claimed</param>
    /// <param name="sourceAddress">the address from which the packet was received</param>
    private void HandleDeviceClaimPacket(ReadOnlySpan<byte> packet, int deviceOffset, IPAddress sourceAddress)
    {
        if (packet.Length < deviceOffset + 1)
        {
            Logger.LogWarning("Ignoring too-short device claim packet.");
            return;
        }
        if (IsRunning && DeviceNumber == packet[deviceOffset])
        {
            DefendDeviceNumber(sourceAddress);
        }
    }

    /// <summary>
    /// The <see cref="DeviceFinder"/> delegates packets it doesn't know how to deal with to us
    /// using this method, because they relate to claiming or defending device numbers, which is
    /// our responsibility.
    /// </summary>
    /// <param name="kind">the kind of packet that was received</param>
    /// <param name="packet">the actual bytes of the packet</param>
    /// <param name="sourceAddress">the address from which the packet was received</param>
    internal void HandleSpecialAnnouncementPacket(PacketType kind, ReadOnlySpan<byte> packet, IPAddress sourceAddress)
    {
        if (kind == PacketType.DeviceNumberStage1)
        {
            Logger.LogDebug("Received device number claim stage 1 packet.");
        }
        else if (kind == PacketType.DeviceNumberStage2)
        {
            HandleDeviceClaimPacket(packet, 0x2e, sourceAddress);
        }
        else if (kind == PacketType.DeviceNumberStage3)
        {
            HandleDeviceClaimPacket(packet, 0x24, sourceAddress);
        }
        else if (kind == PacketType.DeviceNumberWillAssign)
        {
            Logger.LogDebug("The mixer at address {Address} wants to assign us a specific device number.", sourceAddress);
            if (_claimingNumber != 0)
            {
                RequestNumberFromMixer(sourceAddress);
            }
            else
            {
                Logger.LogWarning("Ignoring mixer device number assignment offer; we are not claiming a device number!");
            }
        }
        else if (kind == PacketType.DeviceNumberAssign)
        {
            _mixerAssigned = packet.Length > 0x24 ? packet[0x24] : 0;
            if (_mixerAssigned == 0)
            {
                Logger.LogDebug("Mixer at address {Address} told us to use any device.", sourceAddress);
            }
            else
            {
                Logger.LogInformation("Mixer at address {Address} told us to use device number {Number}", sourceAddress, _mixerAssigned);
            }
        }
        else if (kind == PacketType.DeviceNumberAssignmentFinished)
        {
            _mixerAssigned = _claimingNumber;
            Logger.LogInformation("Mixer confirmed device assignment.");
        }
        else if (kind == PacketType.DeviceNumberInUse)
        {
            var defendedDevice = packet.Length > 0x24 ? packet[0x24] : 0;
            if (defendedDevice == 0)
            {
                Logger.LogWarning("Ignoring unexplained attempt to defend device 0.");
            }
            else if (defendedDevice == _claimingNumber)
            {
                Logger.LogWarning("Another device is defending device number {Device}, so we can't use it.", defendedDevice);
                _claimRejected = true;
            }
            else if (IsRunning)
            {
                if (defendedDevice == DeviceNumber)
                {
                    Logger.LogWarning("Another device has claimed it owns our device number, shutting down.");
                    Stop();
                }
                else if (defendedDevice == 25)
                {
                    Logger.LogDebug("Another device is defending device 25, which we are not using; this is probably a CDJ-3000 fighting over its embedded Kuvo server.");
                }
                else
                {
                    Logger.LogWarning("Another device is defending a number we are not using, ignoring: {Device}", defendedDevice);
                }
            }
            else
            {
                Logger.LogWarning("Received device number defense message for device number {Device} when we are not even running!", defendedDevice);
            }
        }
        else
        {
            Logger.LogWarning("Received unrecognized special announcement packet type: {Kind}", kind);
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("VirtualCdj[number:").Append(DeviceNumber).Append(", name:").Append(DeviceName);
        sb.Append(", announceInterval:").Append(AnnounceInterval);
        sb.Append(", useStandardPlayerNumber:").Append(UseStandardPlayerNumber);
        sb.Append(", tempoEpsilon:").Append(TempoEpsilon).Append(", active:").Append(IsRunning);
        if (IsRunning)
        {
            sb.Append(", localAddress:").Append(LocalAddress);
            sb.Append(", broadcastAddress:").Append(BroadcastAddress);
            sb.Append(", latestStatus:[").Append(string.Join(", ", LatestStatus)).Append(']');
            sb.Append(", masterTempo:").Append(MasterTempo);
            sb.Append(", tempoMaster:").Append(TempoMaster);
            sb.Append(", isSendingStatus:").Append(IsSendingStatus);
            if (IsSendingStatus)
            {
                sb.Append(", isSynced:").Append(IsSynced);
                sb.Append(", isTempoMaster:").Append(IsTempoMaster);
                sb.Append(", isPlaying:").Append(IsPlaying);
                sb.Append(", isOnAir:").Append(IsOnAir);
                sb.Append(", metronome:").Append(_metronome);
            }
        }
        return sb.Append(']').ToString();
    }

    // -- Test seams ------------------------------------------------------------------------

    /// <summary>
    /// Test seam: forces (or stops forcing) <see cref="IsRunning"/> to report <c>true</c> without
    /// any socket being open, so the tempo master state machine can be exercised with injected
    /// updates.
    /// </summary>
    /// <param name="running">whether the service should pretend to be running</param>
    internal void SetRunningStateForTests(bool running) => _testForcedRunning = running;

    /// <summary>
    /// Test seam: marks (or unmarks) the service as sending status packets, without starting any
    /// sender threads, so the master handoff state machine can be exercised.
    /// </summary>
    /// <param name="sending">whether the service should pretend to be sending status packets</param>
    internal void SetSendingStatusStateForTests(bool sending) =>
        _statusSender = sending ? new StatusSenderHandle() : null;

    /// <summary>
    /// Test seam: installs the network details (matched interface address and the MAC/IP fields
    /// of the keep-alive template) that would normally be established while starting up, so
    /// packet-construction golden tests can run without any sockets.
    /// </summary>
    /// <param name="matched">the interface details to install</param>
    internal void SetNetworkDetailsForTests(MatchedAddressInfo matched)
    {
        _matchedAddress = matched;
        _broadcastAddress = matched.BroadcastAddress;
        lock (_keepAliveBytes)
        {
            if (matched.HardwareAddress.Length >= 6)
            {
                Array.Copy(matched.HardwareAddress, 0, _keepAliveBytes, MacAddressOffset, 6);
            }
            matched.Address.GetAddressBytes().CopyTo(_keepAliveBytes, 44);
        }
    }

    /// <summary>Test seam: the current value of the master-handoff "next master" tracker.</summary>
    internal int NextMasterForTests => _nextMaster;

    /// <summary>Test seam: the current value of the sync counter used in master handoff.</summary>
    internal int SyncCounterForTests => _syncCounter;

    /// <summary>Test seam: the device number that yielded the master role to us, or zero.</summary>
    internal int MasterYieldedFromForTests => _masterYieldedFrom;

    /// <summary>Test seam: the device number we are requesting the master role from, or zero.</summary>
    internal int RequestingMasterRoleFromPlayerForTests => _requestingMasterRoleFromPlayer;

    /// <summary>Test seam: the device number we are currently trying to claim, or zero.</summary>
    internal int ClaimingNumberForTests
    {
        get => _claimingNumber;
        set => _claimingNumber = value;
    }

    /// <summary>Test seam: whether our claim has been rejected by another device's defense.</summary>
    internal bool ClaimRejectedForTests => _claimRejected;

    /// <summary>Test seam: the device number a mixer has assigned us, or zero.</summary>
    internal int MixerAssignedForTests => _mixerAssigned;

    /// <summary>
    /// Test seam: forces (or stops forcing) <see cref="InOpusQuadCompatibilityMode"/> to report
    /// <c>true</c>, so behavior that is suppressed in that mode (such as the
    /// <see cref="DbServer.ConnectionManager"/>'s port queries) can be exercised without an
    /// actual Opus Quad on the network.
    /// </summary>
    /// <param name="enabled">whether the service should pretend to be in Opus Quad compatibility mode</param>
    internal void SetOpusQuadCompatibilityModeForTests(bool enabled) => _inOpusQuadSQLiteMode = enabled;

    /// <summary>
    /// Test seam: restores all mutable singleton state to its defaults so tests remain
    /// independent of each other. Event subscriptions are not cleared; tests must unsubscribe
    /// their own handlers.
    /// </summary>
    internal void ResetForTests()
    {
        _testForcedRunning = false;
        Stop();
        lock (_lifecycleLock)
        {
            _proxyingForVirtualRekordbox = false;
            _inOpusQuadSQLiteMode = false;
            _statusSender = null;
            RemoveOurSyncListener();
            _updates.Clear();
            Volatile.Write(ref _tempoMaster, null);
            Interlocked.Exchange(ref _masterTempoBits, BitConverter.DoubleToInt64Bits(0.0));
            Interlocked.Exchange(ref _tempoEpsilonBits, BitConverter.DoubleToInt64Bits(0.0001));
            _master = false;
            _nextMaster = 0xff;
            _masterYieldedFrom = 0;
            _syncCounter = 1;
            _largestSyncCounter = 1;
            _requestingMasterRoleFromPlayer = 0;
            _claimingNumber = 0;
            _claimRejected = false;
            _mixerAssigned = 0;
            _packetCounter = 0;
            _playing = false;
            _synced = false;
            _onAir = false;
            _announceInterval = 1500;
            _statusInterval = 200;
            UseStandardPlayerNumber = false;
            _portOverride = null;
            AnnouncementSendPort = Ports.AnnouncementPort;
            BeatSendPort = Ports.BeatPort;
            UpdateSendPort = Ports.UpdatePort;
            ClaimSleepMillis = 300;
            SelfAssignmentWatchMillis = SelfAssignmentWatchPeriod;
            InterfaceScanOverride = null;
            _matchedAddress = null;
            _matchingInterfaces = null;
            _broadcastAddress = null;
            _localAddress = null;
            DeviceNumber = 0;
            DeviceName = "beat-link";
            _metronome.Tempo = 120.0;
            _metronome.JumpToBeat(1);
            _whereStopped = _metronome.GetSnapshot(_metronome.StartTime);
        }
    }
}
