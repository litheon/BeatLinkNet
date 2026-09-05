using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using BeatLink.Data;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Represents a status update sent by a CDJ (or perhaps other player) on a DJ Link network.
/// </summary>
public class CdjStatus : DeviceUpdate
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<CdjStatus>();

    /// <summary>
    /// The byte within the status packet which contains useful status information, labeled <i>F</i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public const int StatusFlags = 0x89;

    /// <summary>
    /// <para>The byte within a status packet which indicates that the device is in the process of handing off the
    /// tempo master role to another device, labeled <i>M<sub>h</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.</para>
    ///
    /// <para>Normally it holds the value 0xff, but during a tempo master hand-off, it holds the device number of the
    /// incoming tempo master, until that device asserts the master state, after which this device will stop doing so.</para>
    /// </summary>
    public const int MasterHandOff = 0x9f;

    /// <summary>
    /// The bit within the status flag that indicates the player has degraded to BPM Sync mode, as described in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-flag-bits">Packet Analysis document</a>.
    /// </summary>
    public const int BpmSyncFlag = 0x02;

    /// <summary>
    /// The bit within the status flag that indicates the player is on the air, as illustrated in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-flag-bits">Packet Analysis document</a>.
    /// A player is considered to be on the air when it is connected to a mixer channel that is not faded out.
    /// Only Nexus mixers seem to support this capability.
    /// </summary>
    public const int OnAirFlag = 0x08;

    /// <summary>
    /// The bit within the status flag that indicates the player is synced, as illustrated in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-flag-bits">Packet Analysis document</a>.
    /// </summary>
    public const int SyncedFlag = 0x10;

    /// <summary>
    /// The bit within the status flag that indicates the player is the tempo master, as illustrated in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-flag-bits">Packet Analysis document</a>.
    /// </summary>
    public const int MasterFlag = 0x20;

    /// <summary>
    /// The bit within the status flag that indicates the player is playing, as illustrated in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-flag-bits">Packet Analysis document</a>.
    /// </summary>
    public const int PlayingFlag = 0x40;

    /// <summary>
    /// The byte which reports the state of the player's CD slot, labeled <i>d<sub>l</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public const int LocalCdState = 0x37;

    /// <summary>
    /// The byte which reports the state of the player's USB slot, labeled <i>U<sub>l</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public const int LocalUsbState = 0x6f;

    /// <summary>
    /// The byte which reports the state of the player's SD slot, labeled <i>S<sub>l</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public const int LocalSdState = 0x73;

    /// <summary>
    /// Get the device number of the player from which the track was loaded, if any; labeled <i>D<sub>r</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public int TrackSourcePlayer { get; }

    /// <summary>
    /// The possible values describing from where the track was loaded, labeled <i>S<sub>r</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public sealed class TrackSourceSlot
    {
        /// <summary>Nothing has been loaded.</summary>
        public static readonly TrackSourceSlot NoTrack = new(0, "NO_TRACK");

        /// <summary>The track was loaded from a CD.</summary>
        public static readonly TrackSourceSlot CdSlot = new(1, "CD_SLOT");

        /// <summary>The track was loaded from the Secure Digital media slot.</summary>
        public static readonly TrackSourceSlot SdSlot = new(2, "SD_SLOT");

        /// <summary>The track was loaded from the USB socket.</summary>
        public static readonly TrackSourceSlot UsbSlot = new(3, "USB_SLOT");

        /// <summary>The track was loaded from a computer's rekordbox collection over the network.</summary>
        public static readonly TrackSourceSlot Collection = new(4, "COLLECTION");

        /// <summary>The track was loaded from USB 2 on an XDJ-XZ in four-deck mode.</summary>
        public static readonly TrackSourceSlot Usb2Slot = new(7, "USB_2_SLOT");

        /// <summary>We saw a value that we did not recognize, so we don't know where the track came from.</summary>
        public static readonly TrackSourceSlot Unknown = new(unchecked((byte)-1), "UNKNOWN");

        /// <summary>The value that represents this media source in a status update or dbserver request.</summary>
        public byte ProtocolValue { get; }

        private readonly string _name;

        private TrackSourceSlot(byte protocolValue, string name)
        {
            ProtocolValue = protocolValue;
            _name = name;
        }

        /// <summary>All the known track source slot values.</summary>
        public static IReadOnlyList<TrackSourceSlot> Values { get; } =
            [NoTrack, CdSlot, SdSlot, UsbSlot, Collection, Usb2Slot, Unknown];

        /// <summary>
        /// Allows a known track source slot value to be looked up based on the byte that was seen in a status update.
        /// </summary>
        public static FrozenDictionary<byte, TrackSourceSlot> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(v => v.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => _name;
    }

    /// <summary>
    /// Get the slot from which the track was loaded, if any; labeled <i>S<sub>r</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// (This corresponds to the Java <c>getTrackSourceSlot()</c>; C# does not allow a property to
    /// share its name with the nested <see cref="TrackSourceSlot"/> type.)
    /// </summary>
    public TrackSourceSlot SourceSlot { get; }

    /// <summary>
    /// The possible values describing the track type, labeled <i>t<sub>r</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public sealed class TrackType
    {
        /// <summary>No track has been loaded.</summary>
        public static readonly TrackType NoTrack = new(0, "NO_TRACK");

        /// <summary>The track was loaded from a rekordbox database, and has been analyzed for beat grid and waveforms.</summary>
        public static readonly TrackType Rekordbox = new(1, "REKORDBOX");

        /// <summary>
        /// The track was loaded from digital media (including a data disc), but was not from rekordbox,
        /// and so has not been analyzed for beat grid and waveforms.
        /// </summary>
        public static readonly TrackType Unanalyzed = new(2, "UNANALYZED");

        /// <summary>The track was loaded from an audio CD, and so has not been analyzed for beat grid and waveforms.</summary>
        public static readonly TrackType CdDigitalAudio = new(5, "CD_DIGITAL_AUDIO");

        /// <summary>We received a value that we did not recognize, so we don't know what it means.</summary>
        public static readonly TrackType Unknown = new(unchecked((byte)-1), "UNKNOWN");

        /// <summary>The value that represents this track type in a status update.</summary>
        public byte ProtocolValue { get; }

        private readonly string _name;

        private TrackType(byte protocolValue, string name)
        {
            ProtocolValue = protocolValue;
            _name = name;
        }

        /// <summary>All the known track type values.</summary>
        public static IReadOnlyList<TrackType> Values { get; } =
            [NoTrack, Rekordbox, Unanalyzed, CdDigitalAudio, Unknown];

        /// <summary>
        /// Allows a known track type value to be looked up based on the byte that was seen in a status update.
        /// </summary>
        public static FrozenDictionary<byte, TrackType> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(v => v.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => _name;
    }

    /// <summary>
    /// Get the type of the track that was loaded, if any; labeled <i>T<sub>r</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// (This corresponds to the Java <c>getTrackType()</c>; C# does not allow a property to share
    /// its name with the nested <see cref="TrackType"/> type.)
    /// </summary>
    public TrackType LoadedTrackType { get; }

    /// <summary>
    /// Get the rekordbox ID of the track that was loaded, if any; labeled <i>rekordbox</i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// Will be zero if no track is loaded, and is simply the track number when an ordinary audio CD
    /// track has been loaded.
    /// </summary>
    public int RekordboxId { get; }

    /// <summary>
    /// The possible values of the first play state found in the packet, labeled <i>P<sub>1</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public sealed class PlayState1
    {
        /// <summary>No track has been loaded.</summary>
        public static readonly PlayState1 NoTrack = new(0, "NO_TRACK");

        /// <summary>A track is in the process of being loaded.</summary>
        public static readonly PlayState1 Loading = new(2, "LOADING");

        /// <summary>The player is playing normally.</summary>
        public static readonly PlayState1 Playing = new(3, "PLAYING");

        /// <summary>The player is playing a loop.</summary>
        public static readonly PlayState1 Looping = new(4, "LOOPING");

        /// <summary>The player is paused anywhere other than the cue point.</summary>
        public static readonly PlayState1 Paused = new(5, "PAUSED");

        /// <summary>The player is paused at the cue point.</summary>
        public static readonly PlayState1 Cued = new(6, "CUED");

        /// <summary>Cue play is in progress (playback while the cue button is held down).</summary>
        public static readonly PlayState1 CuePlaying = new(7, "CUE_PLAYING");

        /// <summary>Cue scratch is in progress; the player will return to the cue point when the jog wheel is released.</summary>
        public static readonly PlayState1 CueScratching = new(8, "CUE_SCRATCHING");

        /// <summary>The player is searching forwards or backwards.</summary>
        public static readonly PlayState1 Searching = new(9, "SEARCHING");

        /// <summary>The player reached the end of the track and stopped.</summary>
        public static readonly PlayState1 Ended = new(0x11, "ENDED");

        /// <summary>The player is in an emergency loop, e.g. because of loss of network access to music data.</summary>
        public static readonly PlayState1 EmergencyLoop = new(0x12, "EMERGENCY_LOOP");

        /// <summary>We received a value we don't recognize, so we don't know what it means.</summary>
        public static readonly PlayState1 Unknown = new(unchecked((byte)-1), "UNKNOWN");

        /// <summary>The value that represents this play state in a status update.</summary>
        public byte ProtocolValue { get; }

        private readonly string _name;

        private PlayState1(byte protocolValue, string name)
        {
            ProtocolValue = protocolValue;
            _name = name;
        }

        /// <summary>All the known first play state values.</summary>
        public static IReadOnlyList<PlayState1> Values { get; } =
            [NoTrack, Loading, Playing, Looping, Paused, Cued, CuePlaying, CueScratching, Searching, Ended, EmergencyLoop, Unknown];

        /// <summary>
        /// Allows a known <i>P<sub>1</sub></i> value to be looked up based on the byte that was seen in a status update.
        /// </summary>
        public static FrozenDictionary<byte, PlayState1> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(v => v.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => _name;
    }

    /// <summary>
    /// Get the first play state found in the packet, labeled <i>P<sub>1</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// (This corresponds to the Java <c>getPlayState1()</c>; C# does not allow a property to share
    /// its name with the nested <see cref="PlayState1"/> type.)
    /// </summary>
    public PlayState1 FirstPlayState { get; }

    /// <summary>
    /// The possible values of the second play state found in the packet, labeled <i>P<sub>2</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public sealed class PlayState2
    {
        /// <summary>The player is moving through a track.</summary>
        public static readonly PlayState2 Moving = new(0x7a, "MOVING");

        /// <summary>The player is stopped.</summary>
        public static readonly PlayState2 Stopped = new(0x7e, "STOPPED");

        /// <summary>
        /// We have seen the Opus Quad report this value when it is playing but the main status flag lies about that fact.
        /// </summary>
        public static readonly PlayState2 OpusMoving = new(0xfa, "OPUS_MOVING");

        /// <summary>We saw an unknown value, so we don't know what it means.</summary>
        public static readonly PlayState2 Unknown = new(unchecked((byte)-1), "UNKNOWN");

        /// <summary>The value that represents this play state in a status update.</summary>
        public byte ProtocolValue { get; }

        private readonly string _name;

        private PlayState2(byte protocolValue, string name)
        {
            ProtocolValue = protocolValue;
            _name = name;
        }

        /// <summary>All the known second play state values.</summary>
        public static IReadOnlyList<PlayState2> Values { get; } = [Moving, Stopped, OpusMoving, Unknown];

        /// <summary>
        /// Allows a known <i>P<sub>2</sub></i> value to be looked up based on the byte that was seen in a status update.
        /// </summary>
        public static FrozenDictionary<byte, PlayState2> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(v => v.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => _name;
    }

    /// <summary>
    /// Get the second play state found in the packet, labeled <i>P<sub>2</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// (This corresponds to the Java <c>getPlayState2()</c>; C# does not allow a property to share
    /// its name with the nested <see cref="PlayState2"/> type.)
    /// </summary>
    public PlayState2 SecondPlayState { get; }

    /// <summary>
    /// The possible values of the third play state found in the packet, labeled <i>P<sub>3</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// </summary>
    public sealed class PlayState3
    {
        /// <summary>No track has been loaded.</summary>
        public static readonly PlayState3 NoTrack = new(0, "NO_TRACK");

        /// <summary>The player is paused or playing in Reverse mode.</summary>
        public static readonly PlayState3 PausedOrReverse = new(1, "PAUSED_OR_REVERSE");

        /// <summary>The player is playing in Forward mode with jog mode set to Vinyl.</summary>
        public static readonly PlayState3 ForwardVinyl = new(9, "FORWARD_VINYL");

        /// <summary>The player is looping while in slip mode, so it is tracking multiple playback positions.</summary>
        public static readonly PlayState3 Slipping = new(0x0b, "SLIPPING");

        /// <summary>The player is playing in Forward mode with jog mode set to CDJ.</summary>
        public static readonly PlayState3 ForwardCdj = new(0x0d, "FORWARD_CDJ");

        /// <summary>We received a value that we did not recognize, so we don't know what it means.</summary>
        public static readonly PlayState3 Unknown = new(unchecked((byte)-1), "UNKNOWN");

        /// <summary>The value that represents this play state in a status update.</summary>
        public byte ProtocolValue { get; }

        private readonly string _name;

        private PlayState3(byte protocolValue, string name)
        {
            ProtocolValue = protocolValue;
            _name = name;
        }

        /// <summary>All the known third play state values.</summary>
        public static IReadOnlyList<PlayState3> Values { get; } =
            [NoTrack, PausedOrReverse, ForwardVinyl, Slipping, ForwardCdj, Unknown];

        /// <summary>
        /// Allows a known <i>P<sub>3</sub></i> value to be looked up based on the byte that was seen in a status update.
        /// </summary>
        public static FrozenDictionary<byte, PlayState3> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(v => v.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => _name;
    }

    /// <summary>
    /// Get the third play state found in the packet, labeled <i>P<sub>3</sub></i> in
    /// the <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// (This corresponds to the Java <c>getPlayState3()</c>; C# does not allow a property to share
    /// its name with the nested <see cref="PlayState3"/> type.)
    /// </summary>
    public PlayState3 ThirdPlayState { get; }

    private readonly int _pitch;

    private readonly int _bpm;

    /// <summary>
    /// If we are in the process of handing the tempo master role to another device, this will be
    /// the device number of that device, otherwise it will have the value 255.
    /// </summary>
    private readonly int _handingMasterToDevice;

    /// <summary>
    /// Determine the enum value corresponding to the track source slot found in the packet.
    /// </summary>
    private TrackSourceSlot FindTrackSourceSlot() =>
        TrackSourceSlot.ProtocolValueMap.TryGetValue(PacketData[0x29], out var result) ? result : TrackSourceSlot.Unknown;

    /// <summary>
    /// Determine the enum value corresponding to the track type found in the packet.
    /// </summary>
    private TrackType FindTrackType() =>
        TrackType.ProtocolValueMap.TryGetValue(PacketData[0x2a], out var result) ? result : TrackType.Unknown;

    /// <summary>
    /// Determine the enum value corresponding to the first play state found in the packet.
    /// </summary>
    private PlayState1 FindPlayState1() =>
        PlayState1.ProtocolValueMap.TryGetValue(PacketData[0x7b], out var result) ? result : PlayState1.Unknown;

    /// <summary>
    /// Determine the enum value corresponding to the second play state found in the packet.
    /// </summary>
    private PlayState2 FindPlayState2() => PacketData[0x8b] switch
    {
        0x6a or 0x7a or 0xfa => PlayState2.Moving,
        0x6e or 0x7e or 0xfe => PlayState2.Stopped,
        _ => PlayState2.Unknown
    };

    /// <summary>
    /// Determine the enum value corresponding to the third play state found in the packet.
    /// </summary>
    private PlayState3 FindPlayState3() =>
        PlayState3.ProtocolValueMap.TryGetValue(PacketData[0x9d], out var result) ? result : PlayState3.Unknown;

    /// <summary>
    /// Contains the sizes we expect CDJ status packets to have, so we can log a warning if we get
    /// an unusual one. We will then add the new size to the set, so it only gets logged once per run.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> ExpectedStatusPacketSizes = new(
        new[] { 0xd0, 0xd4, 0x11c, 0x124 }.ToDictionary(size => size, _ => (byte)0));

    /// <summary>
    /// It turns out that some players, notably the XDJ-1000, send status packets which report one
    /// payload size but actually have a different payload size. I know, right? This keeps track of
    /// such combinations (the reported size, a comma, and the actual size), so we can report it
    /// just once and don't fill up the log file with endless copies of the same warning.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> MisreportedPacketSizes = new();

    /// <summary>
    /// The smallest packet size from which we can be constructed. Anything less than this and we
    /// are missing crucial information.
    /// </summary>
    public const int MinimumPacketSize = 0xcc;

    /// <summary>
    /// Indicates whether the status flags in this instance had to be reused from the previous
    /// valid packet received from this device, because it is an Opus Quad which sent a zero value
    /// in the current packet. Experimental.
    /// </summary>
    public bool StatusFlagsWereReplayed { get; }

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content.
    /// </summary>
    /// <param name="packet">the CDJ status packet that was received</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <exception cref="ArgumentException">if the packet is smaller than <see cref="MinimumPacketSize"/></exception>
    public CdjStatus(ReadOnlySpan<byte> packet, IPAddress address)
        : this(packet, address, false)
    {
    }

    /// <summary>
    /// Constructor that records whether the status flag is a replay of a previous, valid status
    /// flag, because we are receiving corrupt data from an Opus Quad.
    /// </summary>
    /// <param name="packet">the CDJ status packet that was received, except that a zero
    /// <see cref="StatusFlags"/> byte may have been replaced by a valid one from the previous
    /// packet received for this device</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <param name="statusFlagsReplayed">indicates whether the status flag replacement has occurred</param>
    internal CdjStatus(ReadOnlySpan<byte> packet, IPAddress address, bool statusFlagsReplayed)
        : base(packet, address, "CDJ status", packet.Length)
    {
        StatusFlagsWereReplayed = statusFlagsReplayed;

        if (PacketData.Length < MinimumPacketSize)
        {
            throw new ArgumentException("Unable to create a CdjStatus object, packet too short: we need " +
                MinimumPacketSize + " bytes and were given only " + PacketData.Length, nameof(packet));
        }

        var payloadLength = (int)Util.BytesToNumber(PacketData, 0x22, 2);
        var reportedPacketSize = payloadLength + 0x24;
        if (PacketData.Length != reportedPacketSize)
        {
            var reportedKey = reportedPacketSize + "," + PacketData.Length;
            if (MisreportedPacketSizes.TryAdd(reportedKey, 0))
            {
                Logger.LogWarning("Received CDJ status packet with reported payload length of {Reported} and actual payload length of {Actual}",
                    payloadLength, PacketData.Length - 0x24);
            }
        }

        if (ExpectedStatusPacketSizes.TryAdd(PacketData.Length, 0))
        {
            Logger.LogWarning("Processing CDJ Status packets with unexpected lengths {Length}.", PacketData.Length);
        }
        LoadedTrackType = FindTrackType();
        var maybeRekordboxId = (int)Util.BytesToNumber(PacketData, 0x2c, 4);
        _pitch = (int)Util.BytesToNumber(PacketData, 0x8d, 3);
        _bpm = (int)Util.BytesToNumber(PacketData, 0x92, 2);
        FirstPlayState = FindPlayState1();
        SecondPlayState = FindPlayState2();
        ThirdPlayState = FindPlayState3();
        FirmwareVersion = Util.DecodeAndTrim(PacketData, 0x7c, 4);
        _handingMasterToDevice = PacketData[MasterHandOff];

        var trackSourceByte = PacketData[0x28];
        if (IsFromOpusQuad && unchecked((sbyte)trackSourceByte) < 16)
        {
            // The sourcePlayer variable will be changed to the slot number; it's not the deck number.
            var sourcePlayer = Util.TranslateOpusPlayerNumbers(trackSourceByte);
            var player = Util.TranslateOpusPlayerNumbers(trackSourceByte);
            if (OpusProvider.Instance.UsingDeviceLibraryPlus)
            {
                // Only one USB is supported, since we don't have any precise metadata to match
                // to verify USB slot number.
                sourcePlayer = 1;
            }
            else if (sourcePlayer != 0)
            {
                var matchedSourceSlot = VirtualRekordbox.Instance.FindMatchedTrackSourceSlotForPlayer(DeviceNumber);
                if (matchedSourceSlot != null)
                {
                    sourcePlayer = matchedSourceSlot.Player;
                }
                maybeRekordboxId = VirtualRekordbox.Instance.FindDeviceSqlRekordboxIdForPlayer(player);
            }
            TrackSourcePlayer = sourcePlayer;
            SourceSlot = TrackSourceSlot.UsbSlot;
            // Indicate whether we have a metadata archive available for the USB slot:
            PacketData[LocalUsbState] = (byte)(OpusProvider.Instance.HasArchive(DeviceNumber) ? 0 : 4);
        }
        else
        {
            TrackSourcePlayer = trackSourceByte;
            SourceSlot = FindTrackSourceSlot();
        }

        RekordboxId = maybeRekordboxId;
    }

    /// <summary>
    /// Get the device pitch at the time of the update. This is an integer ranging from 0 to
    /// 2097152, which corresponds to a range between completely stopping playback to playing at
    /// twice normal tempo. The equivalent percentage value can be obtained by passing the pitch to
    /// <see cref="Util.PitchToPercentage"/>, and the corresponding fractional scaling value by
    /// passing it to <see cref="Util.PitchToMultiplier"/>.
    ///
    /// <para>CDJ update packets actually have four copies of the pitch value which behave slightly
    /// differently under different circumstances. This property returns one which seems to provide
    /// the most useful information (labeled <i>Pitch<sub>1</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>),
    /// reflecting the effective BPM currently being played when it is combined with the track BPM.
    /// To probe the other pitch values that were reported, you can use <see cref="GetPitch(int)"/>.</para>
    /// </summary>
    public override int Pitch => _pitch;

    /// <summary>
    /// Get a specific copy of the device pitch information at the time of the update. This is an
    /// integer ranging from 0 to 2097152, which corresponds to a range between completely stopping
    /// playback to playing at twice normal tempo. The equivalent percentage value can be obtained
    /// by passing the pitch to <see cref="Util.PitchToPercentage"/>, and the corresponding
    /// fractional scaling value by passing it to <see cref="Util.PitchToMultiplier"/>.
    ///
    /// <para>CDJ update packets contain four copies of the pitch value which behave slightly
    /// differently under different circumstances, labeled <i>Pitch<sub>1</sub></i> through
    /// <i>Pitch<sub>4</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// This method returns the one you choose by specifying <paramref name="number"/>. If all you
    /// want is the current effective pitch, you can use <see cref="Pitch"/>.</para>
    /// </summary>
    /// <param name="number">the subscript identifying the copy of the pitch information you are interested in</param>
    /// <returns>the specified raw device pitch information copy found in the update</returns>
    /// <exception cref="ArgumentException">if <paramref name="number"/> is not between 1 and 4</exception>
    public int GetPitch(int number) => number switch
    {
        1 => _pitch,
        2 => (int)Util.BytesToNumber(PacketData, 153, 3),
        3 => (int)Util.BytesToNumber(PacketData, 193, 3),
        4 => (int)Util.BytesToNumber(PacketData, 197, 3),
        _ => throw new ArgumentException("Pitch number must be between 1 and 4", nameof(number))
    };

    /// <summary>
    /// Get the track BPM at the time of the update. This is an integer representing the BPM times
    /// 100, so a track running at 120.5 BPM would be represented by the value 12050.
    ///
    /// <para>When the CDJ has just started up and no track has been loaded, it will report a BPM
    /// of 65535.</para>
    /// </summary>
    public override int Bpm => _bpm;

    /// <summary>
    /// Get the position within a measure of music at which the most recent beat occurred (a value
    /// from 1 to 4, where 1 represents the downbeat). This value will be accurate when the track
    /// was properly configured within rekordbox (and if the music follows a standard House 4/4
    /// time signature).
    ///
    /// <para>When the track being played has not been analyzed by rekordbox, or is playing on a
    /// non-nexus player, this value will always be zero.</para>
    /// </summary>
    public override int BeatWithinBar => PacketData[0xa6];

    /// <summary>
    /// Returns <c>true</c> if this beat is coming from a device where <see cref="BeatWithinBar"/>
    /// can reasonably be expected to have musical significance, because it respects the way a track
    /// was configured within rekordbox. For CDJs this is true whenever the value is not zero (i.e.
    /// this is a nexus player playing a track that was analyzed by rekordbox), because players
    /// report their beats according to rekordbox-identified measures.
    /// </summary>
    public override bool IsBeatWithinBarMeaningful => BeatWithinBar > 0;

    /// <summary>
    /// Is this CDJ reporting itself to be the current tempo master?
    /// </summary>
    public override bool IsTempoMaster => (PacketData[StatusFlags] & MasterFlag) > 0;

    /// <inheritdoc/>
    public override int? DeviceMasterIsBeingYieldedTo =>
        _handingMasterToDevice == 0xff ? null : _handingMasterToDevice;

    /// <inheritdoc/>
    public override double EffectiveTempo => _bpm * Util.PitchToMultiplier(_pitch) / 100.0;

    /// <summary>
    /// Was the CDJ playing a track when this update was sent? Has special logic to try to
    /// accommodate the quirks of pre-nexus players: true if the play flag was set, or, if this
    /// seems to be a non-nexus player, if <i>P<sub>1</sub></i> and <i>P<sub>2</sub></i> have
    /// values corresponding to a playing state.
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            if (PacketData.Length >= 0xd4)
            {
                return (PacketData[StatusFlags] & PlayingFlag) > 0;
            }

            // Pre-nexus players don't send this critical flag byte at all, so we always have to infer play state.
            return FirstPlayState == PlayState1.Playing || FirstPlayState == PlayState1.Looping ||
                   (FirstPlayState == PlayState1.Searching && SecondPlayState == PlayState2.Moving);
        }
    }

    /// <summary>
    /// Was the CDJ in degraded BPM Sync mode when this update was sent? This indicates that the
    /// player had been in Sync mode, but the DJ used pitch bend (for example by nudging the jog
    /// wheel), so while it is still tracking the tempo of the master player, it is no longer
    /// aligning beats.
    /// </summary>
    public bool IsBpmOnlySynced => (PacketData[StatusFlags] & BpmSyncFlag) > 0;

    /// <summary>
    /// Was the CDJ in Sync mode when this update was sent?
    /// </summary>
    public override bool IsSynced => (PacketData[StatusFlags] & SyncedFlag) > 0;

    /// <summary>
    /// Was the CDJ on the air when this update was sent? A player is considered to be on the air
    /// when it is connected to a mixer channel that is not faded out. Only Nexus mixers seem to
    /// support this capability.
    /// </summary>
    public bool IsOnAir => (PacketData[StatusFlags] & OnAirFlag) > 0;

    /// <summary>
    /// Is USB media loaded in this particular CDJ?
    /// </summary>
    public bool IsLocalUsbLoaded => PacketData[LocalUsbState] == 0;

    /// <summary>
    /// Is USB media being unloaded from this particular CDJ?
    /// </summary>
    public bool IsLocalUsbUnloading => PacketData[LocalUsbState] == 2;

    /// <summary>
    /// Is USB media absent from this particular CDJ?
    /// </summary>
    public bool IsLocalUsbEmpty => PacketData[LocalUsbState] == 4;

    /// <summary>
    /// Is SD media loaded in this particular CDJ?
    /// </summary>
    public bool IsLocalSdLoaded => PacketData[LocalSdState] == 0;

    /// <summary>
    /// Is SD media being unloaded from this particular CDJ?
    /// </summary>
    public bool IsLocalSdUnloading => PacketData[LocalSdState] == 2;

    /// <summary>
    /// Is SD media absent from this particular CDJ?
    /// </summary>
    public bool IsLocalSdEmpty => PacketData[LocalSdState] == 4;

    /// <summary>
    /// Is disc media absent from this particular CDJ? Also returns <c>true</c> if the CD drive has
    /// powered off from being unused for too long, in which case <see cref="IsDiscSlotAsleep"/>
    /// will also return <c>true</c>. Note that it's not really accurate to call the slot "empty"
    /// because it might still have a disc in it, but it is unmounted when the player is using a
    /// playlist or menu of tracks loaded from somewhere else.
    /// </summary>
    public bool IsDiscSlotEmpty => PacketData[LocalCdState] != 0x1e && PacketData[LocalCdState] != 0x11;

    /// <summary>
    /// Has this player's CD drive powered down due to prolonged disuse? When this returns
    /// <c>true</c>, the other members asking about the disc slot do not provide reliable values.
    /// </summary>
    public bool IsDiscSlotAsleep => PacketData[LocalCdState] == 1;

    /// <summary>
    /// How many tracks are on the mounted disc (or in the playlist or player menu from which the
    /// current track was loaded)? Audio CDs will reflect the audio track count, while data discs
    /// will generally have one track regardless of how many usable audio files they contain when
    /// mounted. Also, if the CD drive has powered off because of an extended period of not being
    /// used, when a track had been loaded from that disc, this seems to return 1 (you can check
    /// for that condition with <see cref="IsDiscSlotAsleep"/>). Zero if no disc is mounted nor is
    /// a playlist/menu in use.
    /// </summary>
    public int DiscTrackCount => (int)Util.BytesToNumber(PacketData, 0x46, 2);

    /// <summary>
    /// Is a track loaded?
    /// </summary>
    public bool IsTrackLoaded => FirstPlayState != PlayState1.NoTrack;

    /// <summary>
    /// Is the player currently playing a loop?
    /// </summary>
    public bool IsLooping => FirstPlayState == PlayState1.Looping;

    /// <summary>
    /// Is the player currently paused (regardless of whether at the cue point)?
    /// </summary>
    public bool IsPaused => FirstPlayState == PlayState1.Paused || FirstPlayState == PlayState1.Cued;

    /// <summary>
    /// Is the player currently paused at the cue point?
    /// </summary>
    public bool IsCued => FirstPlayState == PlayState1.Cued;

    /// <summary>
    /// Is the player currently searching forwards or backwards?
    /// </summary>
    public bool IsSearching => FirstPlayState == PlayState1.Searching;

    /// <summary>
    /// Is the player currently stopped at the end of a track?
    /// </summary>
    public bool IsAtEnd => FirstPlayState == PlayState1.Ended;

    /// <summary>
    /// Is the player currently playing forwards?
    /// </summary>
    public bool IsPlayingForwards =>
        FirstPlayState == PlayState1.Playing && ThirdPlayState != PlayState3.PausedOrReverse;

    /// <summary>
    /// Is the player currently playing backwards?
    /// </summary>
    public bool IsPlayingBackwards =>
        FirstPlayState == PlayState1.Playing && ThirdPlayState == PlayState3.PausedOrReverse;

    /// <summary>
    /// Is the player currently playing with the jog wheel in Vinyl mode?
    /// </summary>
    public bool IsPlayingVinylMode => ThirdPlayState == PlayState3.ForwardVinyl;

    /// <summary>
    /// Is the player currently playing with the jog wheel in CDJ mode?
    /// </summary>
    public bool IsPlayingCdjMode => ThirdPlayState == PlayState3.ForwardCdj;

    /// <summary>
    /// Is link media available somewhere on the network (does some player have USB, SD, or other
    /// media that can be linked to)?
    /// </summary>
    public bool IsLinkMediaAvailable => PacketData[0x75] != 0;

    /// <summary>
    /// Check if the player is doing anything (playing, searching, or loading a track).
    /// </summary>
    public bool IsBusy => PacketData[0x27] != 0;

    /// <summary>
    /// Get the track number of the loaded track. Identifies the track within a playlist or other
    /// scrolling list of tracks in the CDJ's browse interface.
    /// </summary>
    public int TrackNumber => (int)Util.BytesToNumber(PacketData, 0x32, 2);

    /// <summary>
    /// Get the sync counter used in the tempo master handoff: a number that becomes one greater
    /// than the value reported by any other player when a player gives up its role as the tempo
    /// master.
    /// </summary>
    public int SyncNumber => (int)Util.BytesToNumber(PacketData, 0x84, 4);

    /// <summary>
    /// Identify the beat of the track that is being played. This counter starts at beat 1 as the
    /// track is played, and increments on each beat. When the player is paused at the start of the
    /// track before playback begins, the value reported is 0.
    ///
    /// <para>When the track being played has not been analyzed by rekordbox, or is being played on
    /// a non-nexus player, this information is not available, and the value -1 is returned.</para>
    /// </summary>
    public int BeatNumber
    {
        get
        {
            var result = Util.BytesToNumber(PacketData, 0xa0, 4);
            if (result != 0xffffffffL)
            {
                return (int)result;
            }
            return -1;
        }
    }

    /// <summary>
    /// How many beats away is the next cue point in the track? If there is no saved cue point
    /// after the current play location, or if it is further than 64 bars ahead, the value 511 is
    /// returned (and the CDJ will display "--.- bars"). As soon as there are just 64 bars (256
    /// beats) to go before the next cue point, this value becomes 256. This is the point at which
    /// the CDJ starts to display a countdown, which it displays as "63.4 Bars". As each beat goes
    /// by, this value decrements by 1, until the cue point is about to be reached, at which point
    /// the value is 1 and the CDJ displays "00.1 Bars". On the beat on which the cue point was
    /// saved the value is 0 and the CDJ displays "00.0 Bars". On the next beat, the value becomes
    /// determined by the next cue point (if any) in the track.
    /// </summary>
    /// <seealso cref="FormatCueCountdown"/>
    public int CueCountdown => (int)Util.BytesToNumber(PacketData, 0xa4, 2);

    /// <summary>
    /// Format a cue countdown indicator in the same way as the CDJ would at this point in the track.
    /// </summary>
    /// <returns>the value that the CDJ would display to indicate the distance to the next cue</returns>
    /// <seealso cref="CueCountdown"/>
    public string FormatCueCountdown()
    {
        var count = CueCountdown;

        if (count == 511)
        {
            return "--.-";
        }

        if (count >= 1 && count <= 256)
        {
            var bars = (count - 1) / 4;
            var beats = ((count - 1) % 4) + 1;
            return $"{bars:D2}.{beats}";
        }

        if (count == 0)
        {
            return "00.0";
        }

        return "??.?";
    }

    /// <summary>
    /// Return the firmware version string reported in the packet.
    /// </summary>
    public string FirmwareVersion { get; }

    /// <summary>
    /// Return the sequence number of this update packet, a value that increments with each packet sent.
    /// </summary>
    public long PacketNumber => Util.BytesToNumber(PacketData, 0xc8, 4);

    /// <summary>
    /// Checks whether the packet is large enough (from a new enough player, such as the CDJ-3000)
    /// that it can report the player's currently active loop boundaries.
    /// </summary>
    public bool CanReportLooping => PacketData.Length >= 0x1ca;

    /// <summary>
    /// If there is an active loop (and <see cref="CanReportLooping"/> is <c>true</c>), returns the
    /// number of beats in the loop; 0 if no loop is active (or can be reported).
    /// </summary>
    public int ActiveLoopBeats => CanReportLooping ? (int)Util.BytesToNumber(PacketData, 0x1c8, 2) : 0;

    /// <summary>
    /// If there is an active loop (and <see cref="CanReportLooping"/> is <c>true</c>), returns the
    /// position within the track (in milliseconds) at which the loop begins; 0 if no loop is
    /// active (or can be reported). Because this can conceivably be zero even when a loop is
    /// active, code should check <see cref="LoopEnd"/> to determine that loop information is
    /// being reported.
    /// </summary>
    public long LoopStart => CanReportLooping ? Util.BytesToNumber(PacketData, 0x1b6, 4) * 65536 / 1000 : 0;

    /// <summary>
    /// If there is an active loop (and <see cref="CanReportLooping"/> is <c>true</c>), returns the
    /// position within the track (in milliseconds) at which the loop ends; 0 if no loop is active
    /// (or can be reported).
    /// </summary>
    public long LoopEnd => CanReportLooping ? Util.BytesToNumber(PacketData, 0x1be, 4) * 65536 / 1000 : 0;

    /// <inheritdoc/>
    public override string ToString() =>
        $"CdjStatus[device:{DeviceNumber}, name:{DeviceName}" +
        $", address:{Address}, timestamp:{Timestamp}, busy? {IsBusy}" +
        $", pitch:{Util.PitchToPercentage(_pitch):+0.00;-0.00}%" +
        $", rekordboxId:{RekordboxId}, from player:{TrackSourcePlayer}" +
        $", in slot:{SourceSlot}, track type:{LoadedTrackType}" +
        $", track:{TrackNumber}, track BPM:{_bpm / 100.0:F1}" +
        $", effective BPM:{EffectiveTempo:F1}" +
        $", beat:{BeatNumber}, beatWithinBar:{BeatWithinBar}" +
        $", isBeatWithinBarMeaningful? {IsBeatWithinBarMeaningful}, cue: {FormatCueCountdown()}" +
        $", Playing? {IsPlaying}, Master? {IsTempoMaster}" +
        $", Synced? {IsSynced}, On-Air? {IsOnAir}" +
        $", handingMasterToDevice: {_handingMasterToDevice}" +
        $", statusFlagsWereReplayed: {StatusFlagsWereReplayed}]";
}
