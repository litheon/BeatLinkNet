// Experimental Opus Quad support: this file both defines and consumes the experimental surface,
// so the experimental-API diagnostic is disabled for the whole file.
#pragma warning disable BLNET001

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BeatLink.Data;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// <para>Provides the ability to emulate the rekordbox lighting application, which causes devices
/// like the Opus Quad (which do not participate in a true Pro DJ Link network) to share their
/// player state and PSSI (track phrase data). This is limited information, so we enrich it using
/// metadata archives of the rekordbox USB data attached through the <see cref="OpusProvider"/>.
/// We can then augment the limited updates from the players to provide more Beat Link
/// functionality.</para>
///
/// <para>Ported from the beat-link <c>VirtualRekordbox</c> class by Kris Prep. Like all Opus Quad
/// support, this class is experimental.</para>
/// </summary>
public sealed partial class VirtualRekordbox : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<VirtualRekordbox>();

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
    /// The number of milliseconds for which the <see cref="DeviceFinder"/> needs to have been
    /// watching the network in order for us to be confident we can choose a device number that
    /// will not conflict.
    /// </summary>
    private const long SelfAssignmentWatchPeriod = 4000;

    /// <summary>
    /// The value that comes in update packet byte 0x25 for PSSI data.
    /// </summary>
    private const int MetadataTypeIdentifierPssi = 10;

    /// <summary>
    /// Used to serialize starting and stopping the service, standing in for the Java
    /// <c>synchronized</c> methods.
    /// </summary>
    private readonly Lock _lifecycleLock = new();

    /// <summary>
    /// The socket used to receive device status packets while we are active, or <c>null</c> when
    /// we are not.
    /// </summary>
    private volatile Socket? _socket;

    /// <summary>
    /// Check whether we are presently posing as a virtual rekordbox and receiving device status
    /// updates: <c>true</c> if our socket is open, sending presence announcements, and receiving
    /// status packets.
    /// </summary>
    public override bool IsRunning => _socket != null;

    /// <summary>
    /// The local address to which our socket is bound while we are active.
    /// </summary>
    private volatile IPAddress? _localAddress;

    /// <summary>
    /// The address being used by the virtual rekordbox to send its own presence announcement
    /// broadcasts, or <c>null</c> if we are not active. (The Java implementation throws when not
    /// running; the <see cref="VirtualCdj"/> proxy accessors here rely on the null instead.)
    /// </summary>
    internal IPAddress? LocalAddress => _localAddress;

    /// <summary>
    /// The broadcast address on which we can reach the DJ Link devices, determined when we start
    /// up by finding the network interface address on which we are receiving the other devices'
    /// announcement broadcasts, or <c>null</c> if we are not active.
    /// </summary>
    private volatile IPAddress? _broadcastAddress;

    /// <summary>
    /// The broadcast address used to reach the DJ Link network, or <c>null</c> if we are not
    /// active.
    /// </summary>
    internal IPAddress? BroadcastAddress => _broadcastAddress;

    /// <summary>
    /// Keep track of the most recent updates we have seen, indexed by the device they came from.
    /// </summary>
    private readonly ConcurrentDictionary<DeviceReference, DeviceUpdate> _updates = new();

    /// <summary>
    /// <para>Get or set the device number used when sending presence announcements on the network
    /// to pose as a virtual rekordbox. Defaults to 0x17; if we find this device number is in use,
    /// we will pick another number in the range 0x13 to 0x27. Cannot be set while we are running,
    /// and is reset to zero (enabling self-assignment) each time the service is stopped.</para>
    /// <para>Experimental, like all Opus Quad support.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">if set while we are currently running</exception>
    [Experimental("BLNET001")]
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
                SetDeviceNumberInternal((byte)value);
            }
        }
    }

    /// <summary>
    /// Stores a device number into the keep-alive packet template without the running check, for
    /// use during startup and shutdown while the lifecycle lock is already held.
    /// </summary>
    /// <param name="number">the device number to store</param>
    private void SetDeviceNumberInternal(byte number)
    {
        lock (_keepAliveBytes)
        {
            _keepAliveBytes[DeviceNumberOffset] = number;
        }
    }

    /// <summary>
    /// The interval, in milliseconds, at which we post presence announcements on the network.
    /// </summary>
    private volatile int _announceInterval = 1500;

    /// <summary>
    /// The interval, in milliseconds, at which we broadcast presence announcements on the network
    /// to pose as rekordbox lighting. Experimental, like all Opus Quad support.
    /// </summary>
    /// <exception cref="ArgumentException">if set to a value not between 200 and 2000</exception>
    [Experimental("BLNET001")]
    public int AnnounceInterval
    {
        get => _announceInterval;
        set
        {
            if (value is < 200 or > 2000)
            {
                throw new ArgumentException("Interval must be between 200 and 2000", nameof(value));
            }
            _announceInterval = value;
        }
    }

    /// <summary>
    /// The pristine keep-alive template, so tests can restore the mutable working copy.
    /// </summary>
    private static readonly byte[] KeepAliveTemplate =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,  0x4f, 0x4c, 0x06, 0x00, 0x72, 0x65, 0x6b, 0x6f, 0x72,
        0x64, 0x62, 0x6f, 0x78,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x03,
        0x00, 0x36, 0x17, 0x01,  0x18, 0x3e, 0xef, 0xda,  0x5b, 0xca, 0xc0, 0xa8,
        0x02, 0x0b, 0x04, 0x01,  0x00, 0x00, 0x04, 0x08
    ];

    /// <summary>
    /// Used to construct the keep-alive packet we broadcast in order to participate in the DJ
    /// Link network. Some of these bytes are fixed, some get replaced by things like our device
    /// number, MAC address, and IP address, as described in the
    /// <see href="https://djl-analysis.deepsymmetry.org/djl-analysis/startup.html#cdj-keep-alive">Packet Analysis document</see>.
    /// Also acts as the lock guarding packet-template mutation.
    /// </summary>
    private readonly byte[] _keepAliveBytes = (byte[])KeepAliveTemplate.Clone();

    /// <summary>
    /// The pristine lighting-request template, so tests can restore the mutable working copy.
    /// </summary>
    private static readonly byte[] LightingRequestTemplate =
    [
        0x51, 0x73, 0x70, 0x74,  0x31, 0x57, 0x6d, 0x4a,  0x4f, 0x4c, 0x11, 0x72,  0x65, 0x6b, 0x6f, 0x72,
        0x64, 0x62, 0x6f, 0x78,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x01,
        0x01, 0x17, 0x01, 0x04,  0x17, 0x01, 0x00, 0x00,  0x00, 0x6d, 0x00, 0x61,  0x00, 0x63, 0x00, 0x62,
        0x00, 0x6f, 0x00, 0x6f,  0x00, 0x6b, 0x00, 0x20,  0x00, 0x70, 0x00, 0x72,  0x00, 0x6f, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// Used to construct the packet we send to request lighting information from the Opus Quad,
    /// which is how we can figure out what tracks it is playing. The MAC and IP address fields
    /// are filled in during startup; the rest is fixed (including a hard-coded UTF-16 "macbook
    /// pro" computer name, matching the Java implementation).
    /// </summary>
    private readonly byte[] _lightingRequestStatusBytes = (byte[])LightingRequestTemplate.Clone();

    /// <summary>
    /// Packet used to tell the Opus device we want PSSI data once for all players with loaded songs.
    /// </summary>
    private static readonly byte[] RequestPssiBytes =
    [
        0x51, 0x73, 0x70, 0x74, 0x31, 0x57, 0x6d, 0x4a, 0x4f, 0x4c, 0x55, 0x72, 0x65, 0x6b, 0x6f, 0x72,
        0x64, 0x62, 0x6f, 0x78, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
        0x00, 0x17, 0x00, 0x08, 0x36, 0x00, 0x00, 0x00, 0x0a, 0x02, 0x03, 0x01
    ];

    /// <summary>
    /// Get the name used in announcing our presence on the network (always "rekordbox", so the
    /// players believe they are talking to rekordbox lighting). Experimental, like all Opus Quad
    /// support.
    /// </summary>
    [Experimental("BLNET001")]
    public string DeviceName
    {
        get
        {
            lock (_keepAliveBytes)
            {
                return Util.DecodeAndTrim(_keepAliveBytes, DeviceNameOffset, DeviceNameLength);
            }
        }
    }

    /// <summary>
    /// This imitates the request rekordbox lighting sends to get PSSI data from the Opus Quad
    /// device (and maybe more devices in the future). Does nothing if we do not currently have an
    /// open socket, or the <see cref="DeviceFinder"/> is not running or has found no devices.
    /// Experimental, like all Opus Quad support.
    /// </summary>
    /// <exception cref="SocketException">if there is a problem sending the request</exception>
    [Experimental("BLNET001")]
    public void RequestPssi()
    {
        var socket = _socket;
        if (socket != null && DeviceFinder.Instance.IsRunning)
        {
            var device = DeviceFinder.Instance.CurrentDevices.FirstOrDefault();
            if (device != null)
            {
                socket.SendTo(RequestPssiBytes, new IPEndPoint(device.Address, UpdateSendPort));
            }
        }
    }

    /// <summary>
    /// Keeps track of the source slots we've matched to metadata archives for each player.
    /// </summary>
    private readonly ConcurrentDictionary<int, SlotReference> _playerTrackSourceSlots = new();

    /// <summary>
    /// Keeps track of the current DeviceSQL rekordbox ID for each player. See
    /// <see cref="FindDeviceSqlRekordboxIdForPlayer"/> for more details.
    /// </summary>
    private readonly ConcurrentDictionary<int, int> _playerToDeviceSqlRekordboxId = new();

    /// <summary>
    /// Clear the player source-slot cache so that we can reload the data. This usually happens
    /// when an archive is attached or detached in the <see cref="OpusProvider"/>. Experimental,
    /// like all Opus Quad support.
    /// </summary>
    /// <param name="usbSlotNumber">the slot whose archive has changed</param>
    [Experimental("BLNET001")]
    public void ClearPlayerCaches(int usbSlotNumber) => _playerTrackSourceSlots.TryRemove(usbSlotNumber, out _);

    /// <summary>
    /// Given a player number (normalized to the range 1-4), returns the track source slot
    /// associated with the metadata archive that we have matched that player's track to, if any,
    /// so we can report it in a meaningful way in <see cref="CdjStatus"/> packets.
    /// </summary>
    /// <param name="player">the player whose track we are interested in</param>
    /// <returns>the Opus Quad USB slot that has a metadata archive mounted that matched that
    /// player's current track, or <c>null</c> if no match has been found</returns>
    internal SlotReference? FindMatchedTrackSourceSlotForPlayer(int player) =>
        _playerTrackSourceSlots.GetValueOrDefault(player);

    /// <summary>
    /// Given a player number (normalized to the range 1-4), returns the DeviceSQL rekordbox ID
    /// for the track that is currently loaded on that player. Returns 0 if we are still searching
    /// for a PSSI match on a new track load, if a PSSI match wasn't found, or if the player does
    /// not have a track loaded.
    /// </summary>
    /// <param name="player">the player whose DeviceSQL rekordbox ID we are interested in</param>
    /// <returns>the DeviceSQL rekordbox ID for the track currently loaded on that player</returns>
    internal int FindDeviceSqlRekordboxIdForPlayer(int player) =>
        _playerToDeviceSqlRekordboxId.GetValueOrDefault(player, 0);

    /// <summary>
    /// Keeps track of the most recent valid (non-zero) status flag byte we have received from
    /// each device number, so we can reuse it in cases where a corrupt (zero) value has been sent
    /// to us.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _lastValidStatusFlagBytes = new();

    /// <summary>
    /// Keeps track of the previous raw rekordbox IDs we've seen from each player, so we can
    /// detect when a track changes.
    /// </summary>
    private readonly ConcurrentDictionary<int, int> _previousRawRekordboxIds = new();

    /// <summary>
    /// Tracks packets received from devices to reconstruct complete messages that span multiple
    /// packets. Used primarily for reconstructing binary data (specifically PSSI) from the Opus
    /// Quad, which arrives fragmented across multiple packets. Maintains state between packet
    /// arrivals until a complete message is assembled, at which point the data can be processed.
    /// </summary>
    private sealed class PacketTracker
    {
        private readonly List<byte> _data = [];

        /// <summary>
        /// Receives and processes a new packet in the sequence.
        /// </summary>
        /// <param name="packetData">the data from the new packet</param>
        /// <param name="packetNumber">which packet this is in the sequence</param>
        /// <param name="totalPackets">how many packets make up the complete message</param>
        /// <returns><c>true</c> if this was the final packet in the sequence</returns>
        public bool ReceivePacket(ReadOnlySpan<byte> packetData, int packetNumber, int totalPackets)
        {
            foreach (var b in packetData)
            {
                _data.Add(b);
            }
            return packetNumber == totalPackets;
        }

        /// <summary>
        /// Reset the tracker state for a new packet stream.
        /// </summary>
        public void ResetForNewPacketStream() => _data.Clear();

        /// <summary>
        /// Convert the accumulated data to a byte array, trimming trailing zeros. We are not sure
        /// if the OPUS_METADATA packets send the length of the entire PSSI back, so for now we
        /// just trim the trailing zeros.
        /// </summary>
        /// <returns>the complete message data with trailing zeros removed</returns>
        public byte[] GetDataAsBytesAndTrimTrailingZeros()
        {
            var count = _data.Count;
            while (count > 0 && _data[count - 1] == 0)
            {
                count--;
            }
            return _data[..count].ToArray();
        }
    }

    /// <summary>
    /// Packet trackers for each player. See <see cref="PacketTracker"/> for more details.
    /// </summary>
    private readonly ConcurrentDictionary<int, PacketTracker> _playerPacketTrackers = new();

    /// <summary>
    /// Given an update packet sent to us, create the appropriate object to describe it. Internal
    /// (rather than private, as in Java) so packet-routing behavior can be exercised by tests.
    /// The buffer may be modified in place, because a corrupt (zero) Opus Quad status-flag byte
    /// is replaced with the last valid one seen from the same device before parsing.
    /// </summary>
    /// <param name="data">the buffer holding a packet received on our update port</param>
    /// <param name="length">the number of valid bytes in the buffer</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <returns>the corresponding <see cref="DeviceUpdate"/> subclass, or <c>null</c> if the
    /// packet was not recognizable or requires no update delivery</returns>
    internal DeviceUpdate? BuildUpdate(byte[] data, int length, IPAddress address)
    {
        var packet = data.AsSpan(0, length);
        var kind = Util.ValidateHeader(packet, UpdatePort);
        if (kind == null)
        {
            Logger.LogDebug("Ignoring unrecognized packet sent to update port.");  // ValidateHeader will already warn once.
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
            if (length < CdjStatus.MinimumPacketSize)
            {
                Logger.LogWarning("Ignoring too-short CDJ Status packet with length {Length} (we need {Minimum} bytes).",
                    length, CdjStatus.MinimumPacketSize);
                return null;
            }

            // Try to recover from a malformed Opus Quad packet with a zero value in its status
            // flags by reusing the last valid one we saw from the same device.
            var reportedStatusFlags = data[CdjStatus.StatusFlags];
            var hadToRecoverStatusFlags = reportedStatusFlags == 0;
            int rawDeviceNumber = data[0x21];
            if (hadToRecoverStatusFlags)
            {
                if (_lastValidStatusFlagBytes.TryGetValue(rawDeviceNumber, out var recoveredStatusFlags))
                {
                    data[CdjStatus.StatusFlags] = recoveredStatusFlags;
                }
                else
                {
                    Logger.LogWarning("Unable to recover from malformed Opus Quad status packet because we have not yet received a valid packet from device {Device}.",
                        rawDeviceNumber);
                    return null;  // Discard this packet.
                }
            }
            else
            {
                // Record in case the next packet for this device is malformed.
                _lastValidStatusFlagBytes[rawDeviceNumber] = reportedStatusFlags;
            }

            // The raw ID sent from the Opus; we just need this to detect changes. We can't use
            // CdjStatus.RekordboxId because it refers to the playerToDeviceSqlRekordboxId map
            // that hasn't been updated yet.
            var rawRekordboxId = (int)Util.BytesToNumber(packet, 0x2c, 4);

            var status = new CdjStatus(packet, address, hadToRecoverStatusFlags);

            // Get the player/device number.
            var deviceNumber = status.DeviceNumber;

            // Get the previous ID for comparison (defaulting to 0 if we haven't tracked this
            // player before), and update the map.
            var previousId = _previousRawRekordboxIds.GetValueOrDefault(deviceNumber, 0);
            _previousRawRekordboxIds[deviceNumber] = rawRekordboxId;

            // Clear slot and ID mappings if the track has changed.
            if (previousId != rawRekordboxId)
            {
                _playerTrackSourceSlots.TryRemove(deviceNumber, out _);
                _playerToDeviceSqlRekordboxId.TryRemove(deviceNumber, out _);

                // Only request PSSI if the deck is loaded.
                if (rawRekordboxId != 0)
                {
                    try
                    {
                        RequestPssi();
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning(e, "Cannot send PSSI request");
                    }
                }
            }

            return status;
        }

        if (kind == PacketType.DeviceRekordboxLightingHelloBytes)
        {
            if (length >= CdjStatus.MinimumPacketSize)
            {
                return new CdjStatus(packet, address);
            }
            Logger.LogDebug("Opus Hello bytes packet.");
            return null;
        }

        if (kind == PacketType.OpusMetadata)
        {
            var playerNumber = Util.TranslateOpusPlayerNumbers(data[0x21]);
            var rekordboxIdFromOpus = (int)Util.BytesToNumber(packet, 0x28, 4);

            // Get or create the tracker for this player.
            var tracker = _playerPacketTrackers.GetOrAdd(playerNumber, static _ => new PacketTracker());

            // PSSI data arrives split across several packets.
            if (data[0x25] == MetadataTypeIdentifierPssi)
            {
                var totalPackets = data[0x33] - 1;
                var packetNumber = data[0x31];

                var dataComplete = tracker.ReceivePacket(packet[0x34..], packetNumber, totalPackets);
                if (dataComplete)
                {
                    // We have a complete PSSI message.
                    var pssiFromOpus = tracker.GetDataAsBytesAndTrimTrailingZeros();

                    // Reset the packet tracker since we have the required data now.
                    tracker.ResetForNewPacketStream();

                    // Get the actual rekordbox DeviceSQL ID and slot number.
                    var match = OpusProvider.Instance.GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(pssiFromOpus, rekordboxIdFromOpus);

                    // Record the match in the player maps.
                    if (match != null)
                    {
                        _playerToDeviceSqlRekordboxId[playerNumber] = match.RekordboxId;
                        _playerTrackSourceSlots[playerNumber] =
                            SlotReference.GetSlotReference(match.UsbSlot, CdjStatus.TrackSourceSlot.UsbSlot);
                    }
                }
            }
            return null;
        }

        Logger.LogWarning("Ignoring {Kind} packet sent to update port.", kind.Name);
        return null;
    }

    /// <summary>
    /// This will send the bytes rekordbox lighting sends to a player to acknowledge its existence
    /// on the network and trigger it to begin sending CdjStatus packets. Experimental, like all
    /// Opus Quad support.
    /// </summary>
    [Experimental("BLNET001")]
    public void SendRekordboxLightingPacket()
    {
        var socket = _socket;
        var broadcast = _broadcastAddress;
        if (socket == null || broadcast == null)
        {
            return;
        }
        byte[] packet;
        lock (_lightingRequestStatusBytes)
        {
            packet = (byte[])_lightingRequestStatusBytes.Clone();
        }
        try
        {
            socket.SendTo(packet, new IPEndPoint(broadcast, UpdateSendPort));
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Unable to send rekordbox lighting hello packet. Will try again when next announcement is due.");
        }
    }

    /// <summary>
    /// This will send the announcement that makes players think that they are talking to
    /// rekordbox. After we send these announcement packets other players will start to send out
    /// status packets. We need to send these every second or two, otherwise we will be
    /// disconnected from the Pro DJ Link network. Experimental, like all Opus Quad support.
    /// </summary>
    [Experimental("BLNET001")]
    public void SendRekordboxAnnouncement()
    {
        var socket = _socket;
        var broadcast = _broadcastAddress;
        if (socket == null || broadcast == null)
        {
            return;
        }
        try
        {
            socket.SendTo(BuildKeepAlivePacket(), new IPEndPoint(broadcast, AnnouncementSendPort));
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Exception sending announcement, will try again.");
        }
    }

    /// <summary>
    /// Builds a copy of the current keep-alive (presence announcement) packet, including the
    /// device number, MAC address, and IP address fields. Separated out (and made internal) so
    /// the packet layout can be verified by tests.
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
    /// Builds a copy of the current rekordbox lighting request packet, including the MAC address
    /// and IP address fields. Separated out (and made internal) so the packet layout can be
    /// verified by tests.
    /// </summary>
    /// <returns>the bytes of the packet to send</returns>
    internal byte[] BuildLightingRequestPacket()
    {
        lock (_lightingRequestStatusBytes)
        {
            return (byte[])_lightingRequestStatusBytes.Clone();
        }
    }

    /// <summary>
    /// Test seam exposing the fixed PSSI request packet so its layout can be verified.
    /// </summary>
    internal static byte[] RequestPssiPacketForTests => (byte[])RequestPssiBytes.Clone();

    /// <summary>
    /// Process a device update once it has been received: track it as the most recent update from
    /// its device, and notify any registered listeners (which includes the
    /// <see cref="VirtualCdj"/>, so that all its usual state tracking and events work while we
    /// are proxying for it).
    /// </summary>
    /// <param name="update">the update packet that was received</param>
    private void ProcessUpdate(DeviceUpdate update)
    {
        _updates[DeviceReference.GetDeviceReference(update.DeviceNumber, update.Address)] = update;
        EventDispatcher.DeliverNow(DeviceUpdateReceived, this, update, Logger);
    }

    /// <summary>
    /// <para>Raised whenever a device status update is received while we are active.</para>
    ///
    /// <para>To reduce latency, this event is delivered to handlers directly on the thread that
    /// is receiving packets from the network; handlers must finish very quickly.</para>
    /// </summary>
    public event EventHandler<DeviceUpdate>? DeviceUpdateReceived;

    /// <summary>
    /// Tracks whether we have hooked up the handler which forwards the updates we receive into
    /// the <see cref="VirtualCdj"/>'s processing, which is where clients are used to getting them.
    /// </summary>
    private bool _virtualCdjForwardingRegistered;

    /// <summary>
    /// Forwards updates we receive to the <see cref="VirtualCdj"/>, which is where all clients
    /// are used to getting them (this plays the role of the Java implementation registering the
    /// VirtualCdj's update listener with us).
    /// </summary>
    private static void ForwardUpdateToVirtualCdj(object? sender, DeviceUpdate update) =>
        VirtualCdj.Instance.ProcessUpdate(update);

    /// <summary>
    /// Test hook allowing the period the <see cref="DeviceFinder"/> must have been watching the
    /// network before we are willing to self-assign a device number to be shortened. Defaults to
    /// <see cref="SelfAssignmentWatchPeriod"/> (four seconds).
    /// </summary>
    internal long SelfAssignmentWatchMillis = SelfAssignmentWatchPeriod;

    /// <summary>
    /// Test hook allowing the port to which our presence announcements are sent to be redirected,
    /// so tests can observe them on an ephemeral loopback port instead of the real announcement
    /// port.
    /// </summary>
    internal int AnnouncementSendPort = Ports.AnnouncementPort;

    /// <summary>
    /// Test hook allowing the port to which update-family packets (the lighting hello and PSSI
    /// requests) are sent to be redirected.
    /// </summary>
    internal int UpdateSendPort = Ports.UpdatePort;

    /// <summary>
    /// Test hook allowing the service's own socket to be bound to an alternate port (or an
    /// ephemeral port, by supplying zero) so tests can run without the real update port, and in
    /// parallel with any actual DJ Link traffic. May only be changed while the service is
    /// stopped; <c>null</c> (the default) means the real <see cref="UpdatePort"/> is used.
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
                throw new InvalidOperationException("PortOverride can only be changed while the VirtualRekordbox is stopped.");
            }
            _portOverride = value;
        }
    }

    /// <summary>
    /// Reports the local port to which our status socket is actually bound, or <c>null</c> if we
    /// are not running. Used by tests which bind to an ephemeral port.
    /// </summary>
    internal int? LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port;

    /// <summary>
    /// Test hook which, when assigned, replaces the scan of real network interfaces during
    /// startup: given the address of the first DJ Link device we found, it must return the
    /// interface address details to use (or <c>null</c> to simulate no match). When it is used,
    /// <see cref="MatchingInterfaces"/> will report an empty list, since no real
    /// <see cref="NetworkInterface"/> objects are involved.
    /// </summary>
    internal Func<IPAddress, MatchedAddressInfo?>? InterfaceScanOverride;

    /// <summary>
    /// <para>Choose a device number which we have not seen on the network, sleeping first if the
    /// <see cref="DeviceFinder"/> has not been watching long enough for us to be confident we
    /// have seen all the devices. If our configured number is free we keep it; otherwise we try
    /// finding an unused player number higher than two rekordbox laptops would use, and less than
    /// rekordbox mobile uses (0x13 through 0x27).</para>
    /// </summary>
    /// <returns><c>true</c> if there was a number available for us to try claiming</returns>
    private bool SelfAssignDeviceNumber()
    {
        var now = Util.CurrentTimestampNanos() / 1_000_000;
        var started = DeviceFinder.Instance.FirstDeviceTime;
        if (now - started < SelfAssignmentWatchMillis)
        {
            try
            {
                Thread.Sleep((int)(SelfAssignmentWatchMillis - (now - started)));  // Sleep until we hit the right time.
            }
            catch (ThreadInterruptedException)
            {
                Logger.LogWarning("Interrupted waiting to self-assign device number, giving up.");
                return false;
            }
        }

        // Record what numbers we have already seen, since there is no point trying one of them.
        var numbersUsed = new HashSet<int>();
        foreach (var device in DeviceFinder.Instance.CurrentDevices)
        {
            numbersUsed.Add(device.DeviceNumber);
        }

        // If we are able to use the number we were configured to use before startup, great!
        if (!numbersUsed.Contains(DeviceNumber))
        {
            return true;
        }

        // Try finding an unused player number higher than two rekordbox laptops would use, and
        // less than rekordbox mobile uses.
        for (var result = 0x13; result < 0x28; result++)
        {
            if (!numbersUsed.Contains(result))  // We found one that is not used, so we can use it.
            {
                SetDeviceNumberInternal((byte)result);
                return true;
            }
        }
        Logger.LogWarning("Found no unused device numbers between 0x13 and 0x27, giving up.");
        return false;
    }

    /// <summary>
    /// Hold the network interfaces which match the address on which we found player traffic.
    /// Should only be one, or we will likely receive duplicate packets, which will cause
    /// problematic behavior.
    /// </summary>
    private volatile List<NetworkInterface>? _matchingInterfaces;

    /// <summary>
    /// Holds the interface address we chose to communicate with the DJ Link device we found
    /// during startup, so we can check if there are any unreachable ones (and so the
    /// <see cref="VirtualCdj"/> can copy it when proxying for us).
    /// </summary>
    private volatile MatchedAddressInfo? _matchedAddress;

    /// <summary>
    /// The interface address we chose to communicate with the DJ Link device we found during
    /// startup, or <c>null</c> before startup succeeds.
    /// </summary>
    internal MatchedAddressInfo? MatchedAddress => _matchedAddress;

    /// <summary>
    /// The network interfaces which match the address on which we found player traffic. (Reports
    /// an empty list when the internal interface-injection test seam supplied the matched
    /// address, since no real interface objects are involved then.)
    /// </summary>
    internal IReadOnlyList<NetworkInterface> MatchingInterfaces =>
        _matchingInterfaces?.AsReadOnly() ?? (IReadOnlyList<NetworkInterface>)[];

    /// <summary>
    /// Makes sure we get shut down if the <see cref="DeviceFinder"/> does, because we rely on it.
    /// </summary>
    private void OnDeviceFinderStopped(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            Logger.LogInformation("VirtualRekordbox stopping because DeviceFinder has stopped.");
            Stop();
        }
    }

    /// <summary>
    /// Tracks whether we have hooked up the handler that makes sure we get shut down if the
    /// <see cref="DeviceFinder"/> does.
    /// </summary>
    private bool _deviceFinderListenerRegistered;

    /// <summary>
    /// This method will start up all the required pieces to emulate rekordbox lighting to Pioneer
    /// devices on the network. This is not as powerful as emulating a CDJ, but rather is for
    /// devices that don't support Pro DJ Link properly but can be coaxed to send status packets
    /// when they talk to rekordbox lighting (the Opus Quad being the only such device at the time
    /// of this port).
    /// </summary>
    /// <returns><c>true</c> if we found DJ Link devices and were able to create the virtual rekordbox</returns>
    /// <exception cref="SocketException">if there is a problem opening a socket on the right network</exception>
    private bool CreateVirtualRekordbox()
    {
        OpusProvider.Instance.Start();

        // Forward updates to VirtualCdj. That's where all clients are used to getting them.
        if (!_virtualCdjForwardingRegistered)
        {
            DeviceUpdateReceived += ForwardUpdateToVirtualCdj;
            _virtualCdjForwardingRegistered = true;
        }

        // Find the network interface and address to use to communicate with the first device we
        // found.
        var matchingInterfaces = new List<NetworkInterface>();
        _matchingInterfaces = matchingInterfaces;
        _matchedAddress = null;
        var announcement = DeviceFinder.Instance.CurrentDevices.First();
        var scanOverride = InterfaceScanOverride;
        if (scanOverride != null)
        {
            _matchedAddress = scanOverride(announcement.Address);
        }
        else
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                var candidate = Util.FindMatchingAddress(announcement.Address, networkInterface);
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
            Logger.LogWarning("Unable to find network interface to communicate with {Device}, giving up.", announcement);
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

        // Copy the chosen interface's hardware and IP addresses into the packet templates.
        lock (_keepAliveBytes)
        {
            if (matched.HardwareAddress.Length >= 6)
            {
                Array.Copy(matched.HardwareAddress, 0, _keepAliveBytes, MacAddressOffset, 6);
            }
            matched.Address.GetAddressBytes().CopyTo(_keepAliveBytes, 44);
        }
        lock (_lightingRequestStatusBytes)
        {
            if (matched.HardwareAddress.Length >= 6)
            {
                Array.Copy(matched.HardwareAddress, 0, _lightingRequestStatusBytes, MacAddressOffset, 6);
            }
            matched.Address.GetAddressBytes().CopyTo(_lightingRequestStatusBytes, 44);
        }

        _broadcastAddress = matched.BroadcastAddress;

        // Inform the DeviceFinder to ignore our own rekordbox lighting announcement broadcast
        // packets, and our own device announcement packets.
        if (matched.BroadcastAddress != null)
        {
            DeviceFinder.Instance.AddIgnoredAddress(matched.BroadcastAddress);
        }
        DeviceFinder.Instance.AddIgnoredAddress(matched.Address);

        _localAddress = matched.Address;
        _socket = socket;

        // Determine a device number we can use.
        if (!SelfAssignDeviceNumber())
        {
            // We couldn't get a device number, so clean up and report failure.
            Logger.LogWarning("Unable to find an unused device number for the virtual rekordbox, giving up.");
            DeviceFinder.Instance.RemoveIgnoredAddress(matched.Address);
            _socket = null;
            _localAddress = null;
            _broadcastAddress = null;
            socket.Close();
            return false;
        }

        // Start the thread that will wait for and process status update packets sent to our
        // socket.
        var receiver = new Thread(() => ReceiveLoop(socket))
        {
            Name = "beat-link VirtualRekordbox status receiver",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        receiver.Start();

        // Create the thread which announces our participation in the DJ Link network, to request
        // update packets.
        var announcer = new Thread(() => AnnounceLoop(socket))
        {
            Name = "beat-link VirtualRekordbox announcement/updates sender",
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
        var buffer = new byte[1420];
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
                    var update = BuildUpdate(buffer, received, sourceAddress);
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
    /// The body of the announcement sender thread: periodically sends the announcement packets
    /// which make devices see us as rekordbox lighting and send us updates.
    /// </summary>
    /// <param name="socket">the socket used to send the announcements</param>
    private void AnnounceLoop(Socket socket)
    {
        while (ReferenceEquals(_socket, socket))
        {
            try
            {
                SendRekordboxAnnouncement();
                SendRekordboxLightingPacket();
                Thread.Sleep(AnnounceInterval);
            }
            catch (Exception t)
            {
                // Don't tear things down if the failure was simply our socket closing at shutdown.
                if (ReferenceEquals(_socket, socket))
                {
                    Logger.LogWarning(t, "Unable to send announcement packets, flushing DeviceFinder due to likely network change and shutting down.");
                    DeviceFinder.Instance.Flush();
                    Stop();
                }
            }
        }
    }

    /// <summary>
    /// Start announcing ourselves and listening for status packets. If already active, has no
    /// effect. Requires the <see cref="DeviceFinder"/> to be active in order to find out how to
    /// communicate with other devices, so will start that if it is not already. Only accessible
    /// within the assembly because the <see cref="VirtualCdj"/> is responsible for starting and
    /// stopping this service when it detects an Opus Quad.
    /// </summary>
    /// <returns><c>true</c> if we found DJ Link devices and were able to create the virtual
    /// rekordbox, or it was already running</returns>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not in Opus
    /// Quad compatibility mode</exception>
    /// <exception cref="SocketException">if the socket to listen on port 50002 cannot be created</exception>
    internal bool Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return true;  // We were already active.
            }

            if (!VirtualCdj.Instance.InOpusQuadCompatibilityMode)
            {
                throw new InvalidOperationException("Can only be started when VirtualCdj is in Opus Quad compatibility mode.");
            }

            // Set up so we know we have to shut down if the DeviceFinder shuts down.
            if (!_deviceFinderListenerRegistered)
            {
                DeviceFinder.Instance.Stopped += OnDeviceFinderStopped;
                _deviceFinderListenerRegistered = true;
            }

            // Find some DJ Link devices, so we can figure out the interface and address to use to
            // talk to them.
            DeviceFinder.Instance.Start();
            for (var i = 0; DeviceFinder.Instance.CurrentDevices.Count == 0 && i < 20; i++)
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

            return CreateVirtualRekordbox();
        }
    }

    /// <summary>
    /// Stop announcing ourselves and listening for status updates.
    /// </summary>
    internal void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                return;
            }
            var socket = _socket;
            _socket = null;
            var localAddress = _localAddress;
            if (localAddress != null)
            {
                DeviceFinder.Instance.RemoveIgnoredAddress(localAddress);
            }
            if (socket != null)
            {
                Util.WakeUdpReceiver(socket);
                socket.Close();
            }
            _localAddress = null;
            _broadcastAddress = null;
            _updates.Clear();
            _lastValidStatusFlagBytes.Clear();
            SetDeviceNumberInternal(0);  // Set up for self-assignment if restarted.
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// Look up the most recent status we have seen for a device identifying itself with the
    /// specified device number, if any. Experimental, like all Opus Quad support.
    ///
    /// <para><em>Note:</em> if you are trying to determine the current tempo or beat being played
    /// by the device, you should use the <c>TimeFinder</c> instead, because that combines both
    /// status updates and beat messages, and so is more likely to be current and definitive.</para>
    /// </summary>
    /// <param name="deviceNumber">the device number of interest</param>
    /// <returns>the matching detailed status update or <c>null</c> if none have been received</returns>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualRekordbox"/> is not active</exception>
    [Experimental("BLNET001")]
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
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static VirtualRekordbox Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private VirtualRekordbox()
    {
    }

    /// <summary>
    /// Test seam: restores all mutable singleton state to its defaults so tests remain
    /// independent of each other. Event subscriptions are not cleared; tests must unsubscribe
    /// their own handlers.
    /// </summary>
    internal void ResetForTests()
    {
        Stop();
        lock (_lifecycleLock)
        {
            _playerTrackSourceSlots.Clear();
            _playerToDeviceSqlRekordboxId.Clear();
            _previousRawRekordboxIds.Clear();
            _playerPacketTrackers.Clear();
            _lastValidStatusFlagBytes.Clear();
            _updates.Clear();
            _announceInterval = 1500;
            _portOverride = null;
            AnnouncementSendPort = Ports.AnnouncementPort;
            UpdateSendPort = Ports.UpdatePort;
            SelfAssignmentWatchMillis = SelfAssignmentWatchPeriod;
            InterfaceScanOverride = null;
            _matchedAddress = null;
            _matchingInterfaces = null;
            lock (_keepAliveBytes)
            {
                KeepAliveTemplate.CopyTo(_keepAliveBytes, 0);  // Restore the template defaults.
            }
            lock (_lightingRequestStatusBytes)
            {
                LightingRequestTemplate.CopyTo(_lightingRequestStatusBytes, 0);
            }
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("VirtualRekordbox[number:").Append(DeviceNumber)
            .Append(", name:").Append(DeviceName)
            .Append(", announceInterval:").Append(AnnounceInterval)
            .Append(", active:").Append(IsRunning);
        if (IsRunning)
        {
            sb.Append(", localAddress:").Append(LocalAddress)
                .Append(", broadcastAddress:").Append(BroadcastAddress);
        }
        return sb.Append(']').ToString();
    }
}
