using System.Collections.Frozen;

namespace BeatLink;

/// <summary>
/// The known packet types used in the protocol, along with the byte values which identify them,
/// the names by which we describe them, and the port on which they are received. The same type
/// byte can mean different things on different ports, so lookups are always keyed by both.
/// </summary>
public sealed class PacketType
{
    /// <summary>Used by the mixer to tell a set of players to start and/or stop playing.</summary>
    public static readonly PacketType FaderStartCommand = new(0x02, "Fader Start", Ports.BeatPort);

    /// <summary>Used by the mixer to tell the players which channels are on and off the air.</summary>
    public static readonly PacketType ChannelsOnAir = new(0x03, "Channels On Air", Ports.BeatPort);

    /// <summary>Used to ask a player for information about the media mounted in a slot.</summary>
    public static readonly PacketType MediaQuery = new(0x05, "Media Query", Ports.UpdatePort);

    /// <summary>The response sent when a Media Query is received.</summary>
    public static readonly PacketType MediaResponse = new(0x06, "Media Response", Ports.UpdatePort);

    /// <summary>
    /// An initial series of three of these packets is sent when a device first joins the network,
    /// at 300ms intervals.
    /// </summary>
    public static readonly PacketType DeviceHello = new(0x0a, "Device Hello", Ports.AnnouncementPort);

    /// <summary>
    /// These are the bytes that all-in-one players like the Opus-Quad send to announce to Rekordbox
    /// Lighting that they are available on the network. Once VirtualRekordbox sends its hello
    /// message to the player, it stops sending these and starts sending CdjStatus updates.
    /// </summary>
    public static readonly PacketType DeviceRekordboxLightingHelloBytes = new(0x10, "Rekordbox Lighting Hello Bytes", Ports.UpdatePort);

    /// <summary>
    /// A series of three of these is sent at 300ms intervals when a device is starting to establish
    /// its device number.
    /// </summary>
    public static readonly PacketType DeviceNumberStage1 = new(0x00, "Device Number Claim Stage 1", Ports.AnnouncementPort);

    /// <summary>
    /// This packet is sent by a mixer directly to a device which has just sent a device number
    /// self-assignment packet when that device is plugged into a channel-specific Ethernet jack on
    /// the mixer (or XDJ-XZ) to let the device know the sender of this packet is responsible for
    /// assigning its number.
    /// </summary>
    public static readonly PacketType DeviceNumberWillAssign = new(0x01, "Device Number Will Be Assigned", Ports.AnnouncementPort);

    /// <summary>
    /// A second series of three packets sent at 300ms intervals when the device is claiming its
    /// device number.
    /// </summary>
    public static readonly PacketType DeviceNumberStage2 = new(0x02, "Device Number Claim Stage 2", Ports.AnnouncementPort);

    /// <summary>
    /// This packet is sent by a mixer (or XDJ-XZ) when a player has acknowledged that it is ready
    /// to be assigned the device number that belongs to the jack to which it is connected.
    /// </summary>
    public static readonly PacketType DeviceNumberAssign = new(0x03, "Device Number Assignment", Ports.AnnouncementPort);

    /// <summary>
    /// Third and final series of three packets sent at 300ms intervals when a device is claiming
    /// its device number. If the device is configured to use a specific number, only one is sent.
    /// </summary>
    public static readonly PacketType DeviceNumberStage3 = new(0x04, "Device Number Claim Stage 3", Ports.AnnouncementPort);

    /// <summary>
    /// This packet is sent by a mixer (or XDJ-XZ) once it sees that device number assignment has
    /// concluded successfully, to the player plugged into a channel-specific jack.
    /// </summary>
    public static readonly PacketType DeviceNumberAssignmentFinished = new(0x05, "Device Number Assignment Finished", Ports.AnnouncementPort);

    /// <summary>Used to report that a device is still present on the DJ Link network.</summary>
    public static readonly PacketType DeviceKeepAlive = new(0x06, "Device Keep-Alive", Ports.AnnouncementPort);

    /// <summary>Used to defend a device number that is already in use.</summary>
    public static readonly PacketType DeviceNumberInUse = new(0x08, "Device Number In Use", Ports.AnnouncementPort);

    /// <summary>
    /// A status update from a player, with a great many status flags, pitch, tempo, and
    /// beat-within-bar details. Sadly, the same number is used (on port 50000) as part of the CDJ
    /// startup process.
    /// </summary>
    public static readonly PacketType CdjStatus = new(0x0a, "CDJ Status", Ports.UpdatePort);

    /// <summary>
    /// Metadata that includes track album art, possibly waveforms and more. We do not use this
    /// information at the moment because it is not complete enough to support all the Beat Link
    /// Trigger functionality. Instead, we download the track data from a Rekordbox USB.
    /// </summary>
    public static readonly PacketType OpusMetadata = new(0x56, "OPUS Metadata", Ports.UpdatePort);

    /// <summary>A command to load a particular track; usually sent by rekordbox.</summary>
    public static readonly PacketType LoadTrackCommand = new(0x19, "Load Track Command", Ports.UpdatePort);

    /// <summary>A response indicating that the specified track is being loaded.</summary>
    public static readonly PacketType LoadTrackAck = new(0x1a, "Load Track Acknowledgment", Ports.UpdatePort);

    /// <summary>Used by an incoming tempo master to ask the current tempo master to relinquish that role.</summary>
    public static readonly PacketType MasterHandoffRequest = new(0x26, "Master Handoff Request", Ports.BeatPort);

    /// <summary>Used by the active tempo master to respond to a request to relinquish that role.</summary>
    public static readonly PacketType MasterHandoffResponse = new(0x27, "Master Handoff Response", Ports.BeatPort);

    /// <summary>
    /// Announces a beat has been played in a rekordbox-analyzed track, with lots of useful
    /// synchronization information.
    /// </summary>
    public static readonly PacketType Beat = new(0x28, "Beat", Ports.BeatPort);

    /// <summary>
    /// Reports the exact playback position of a CDJ-3000 or newer player, on a very frequent
    /// basis, even if it is not currently playing.
    /// </summary>
    public static readonly PacketType PrecisePosition = new(0x0b, "Precise Position", Ports.BeatPort);

    /// <summary>
    /// A status update from the mixer, with status flags, pitch, and tempo, and beat-within-bar
    /// information.
    /// </summary>
    public static readonly PacketType MixerStatus = new(0x29, "Mixer Status", Ports.UpdatePort);

    /// <summary>Used to tell a player to turn sync on or off, or that it should become the tempo master.</summary>
    public static readonly PacketType SyncControl = new(0x2a, "Sync Control", Ports.BeatPort);

    /// <summary>A command to apply settings to a player; usually sent by rekordbox.</summary>
    public static readonly PacketType LoadSettingsCommand = new(0x34, "Load Settings Command", Ports.UpdatePort);

    /// <summary>The value that appears in the type byte which identifies this type of packet.</summary>
    public byte ProtocolValue { get; }

    /// <summary>The name by which we describe this kind of packet.</summary>
    public string Name { get; }

    /// <summary>The port on which this kind of packet is received.</summary>
    public int Port { get; }

    private PacketType(byte protocolValue, string name, int port)
    {
        ProtocolValue = protocolValue;
        Name = name;
        Port = port;
    }

    /// <summary>All the known packet types.</summary>
    public static IReadOnlyList<PacketType> Values { get; } =
    [
        FaderStartCommand, ChannelsOnAir, MediaQuery, MediaResponse, DeviceHello,
        DeviceRekordboxLightingHelloBytes, DeviceNumberStage1, DeviceNumberWillAssign,
        DeviceNumberStage2, DeviceNumberAssign, DeviceNumberStage3, DeviceNumberAssignmentFinished,
        DeviceKeepAlive, DeviceNumberInUse, CdjStatus, OpusMetadata, LoadTrackCommand, LoadTrackAck,
        MasterHandoffRequest, MasterHandoffResponse, Beat, PrecisePosition, MixerStatus,
        SyncControl, LoadSettingsCommand
    ];

    /// <summary>
    /// Allows a known packet type to be looked up given the port number it was received on and the
    /// packet type byte.
    /// </summary>
    public static FrozenDictionary<(int Port, byte TypeByte), PacketType> PacketTypeMap { get; } =
        Values.ToFrozenDictionary(t => (t.Port, t.ProtocolValue));

    /// <inheritdoc/>
    public override string ToString() => Name;
}
