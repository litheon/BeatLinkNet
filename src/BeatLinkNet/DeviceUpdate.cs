using System.Net;
using BeatLink.Data;

namespace BeatLink;

/// <summary>
/// Represents a device status update seen on a DJ Link network.
/// </summary>
public abstract class DeviceUpdate
{
    /// <summary>
    /// The offset at which the device number that sent this packet can be found.
    /// </summary>
    public const int DeviceNumberOffset = 0x21;

    /// <summary>
    /// The packet data containing the device update. Subclasses may read (and, in rare quirky
    /// protocol situations, adjust) these bytes; they are never shared outside the library except
    /// through the read-only <see cref="PacketBytes"/> view.
    /// </summary>
    private protected readonly byte[] PacketData;

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content, works
    /// for all subclasses other than <see cref="PrecisePosition"/> which sadly has to use a
    /// different byte to determine the device number.
    /// </summary>
    /// <param name="packet">the device update packet that was received</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <param name="name">the type of packet that is being processed, in case a problem needs to be reported</param>
    /// <param name="length">the expected length of the packet</param>
    /// <exception cref="ArgumentException">if the packet does not have the expected length</exception>
    protected DeviceUpdate(ReadOnlySpan<byte> packet, IPAddress address, string name, int length)
        : this(packet, address, name, length, DeviceNumberOffset)
    {
    }

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content, works
    /// for subclasses like <see cref="PrecisePosition"/> which sadly use a different byte to
    /// determine the device number.
    /// </summary>
    /// <param name="packet">the device update packet that was received</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <param name="name">the type of packet that is being processed, in case a problem needs to be reported</param>
    /// <param name="length">the expected length of the packet</param>
    /// <param name="deviceNumberOffset">the offset in the packet at which the device number can be found</param>
    /// <exception cref="ArgumentException">if the packet does not have the expected length</exception>
    protected DeviceUpdate(ReadOnlySpan<byte> packet, IPAddress address, string name, int length, int deviceNumberOffset)
    {
        Timestamp = Util.CurrentTimestampNanos();
        if (packet.Length != length)
        {
            throw new ArgumentException($"{name} packet must be {length} bytes long", nameof(packet));
        }
        Address = address;
        PacketData = packet.ToArray();
        DeviceName = Util.DecodeAndTrim(PacketData, 0x0b, 20);
        IsFromOpusQuad = DeviceName == OpusProvider.OpusName;
        IsPreNexusCdj = DeviceName.StartsWith("CDJ", StringComparison.Ordinal) &&
                        (DeviceName.EndsWith("900", StringComparison.Ordinal) ||
                         DeviceName.EndsWith("2000", StringComparison.Ordinal));

        DeviceNumber = IsFromOpusQuad
            ? Util.TranslateOpusPlayerNumbers(PacketData[deviceNumberOffset])
            : PacketData[deviceNumberOffset];
    }

    /// <summary>
    /// Get the address of the device from which this update was seen.
    /// </summary>
    public IPAddress Address { get; }

    /// <summary>
    /// Get the monotonic nanosecond timestamp (from <see cref="Util.CurrentTimestampNanos"/>)
    /// recording when the device update was received.
    /// </summary>
    public long Timestamp { get; }

    /// <summary>
    /// Get the name reported by the device sending the update.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Check whether this packet seems to have come from a CDJ older than the original Nexus
    /// series (which means, for example, that beat numbers will not be available, so the
    /// <c>TimeFinder</c> can't work with it). True if the device name starts with "CDJ" and ends
    /// with "900" or "2000".
    /// </summary>
    public bool IsPreNexusCdj { get; }

    /// <summary>
    /// Get the player/device number reporting the update.
    /// </summary>
    public int DeviceNumber { get; }

    /// <summary>
    /// Indicates whether this device update came from an Opus Quad, which behaves very
    /// differently from true Pro DJ Link hardware. Experimental.
    /// </summary>
    public bool IsFromOpusQuad { get; }

    /// <summary>
    /// Get the raw data bytes of the device update packet.
    /// </summary>
    public ReadOnlyMemory<byte> PacketBytes => PacketData;

    /// <summary>
    /// Get the device pitch at the time of the update. This is an integer ranging from 0 to
    /// 2097152, which corresponds to a range between completely stopping playback to playing at
    /// twice normal tempo. The equivalent percentage value can be obtained by passing the pitch to
    /// <see cref="Util.PitchToPercentage"/>, and the corresponding fractional scaling value by
    /// passing it to <see cref="Util.PitchToMultiplier"/>. Mixers always report a pitch of +0%, so
    /// tempo changes are purely reflected in the BPM value.
    /// </summary>
    public abstract int Pitch { get; }

    /// <summary>
    /// Get the playback BPM at the time of the update. This is an integer representing the BPM
    /// times 100, so a track running at 120.5 BPM would be represented by the value 12050. Mixers
    /// always report a pitch of +0%, so tempo changes are purely reflected in the BPM value.
    ///
    /// <para>When the CDJ has just started up and no track has been loaded, it will report a BPM
    /// of 65535.</para>
    /// </summary>
    public abstract int Bpm { get; }

    /// <summary>
    /// Is this device reporting itself to be the current tempo master?
    /// </summary>
    /// <exception cref="InvalidOperationException">if called with a <see cref="Beat"/> and the
    /// <see cref="VirtualCdj"/> is not running, since that is needed to find the latest status
    /// update from the device which sent the beat packet</exception>
    public abstract bool IsTempoMaster { get; }

    /// <summary>
    /// Is this device reporting itself synced to the current tempo master?
    /// </summary>
    /// <exception cref="InvalidOperationException">if called with a <see cref="Beat"/> and the
    /// <see cref="VirtualCdj"/> is not running, since that is needed to find the latest status
    /// update from the device which sent the beat packet</exception>
    public abstract bool IsSynced { get; }

    /// <summary>
    /// If this packet indicates the device is in the process of yielding the tempo master role to
    /// another player, this will hold the device number of that player, otherwise it will be
    /// <c>null</c>.
    /// </summary>
    public abstract int? DeviceMasterIsBeingYieldedTo { get; }

    /// <summary>
    /// Get the effective tempo reflected by this update, which reflects both its track BPM and
    /// pitch as needed.
    /// </summary>
    public abstract double EffectiveTempo { get; }

    /// <summary>
    /// Get the position within a measure of music at which the most recent beat fell (a value
    /// from 1 to 4, where 1 represents the downbeat). This value will be accurate for players when
    /// the track was properly configured within rekordbox (and if the music follows a standard
    /// House 4/4 time signature). The mixer makes no effort to synchronize downbeats with players,
    /// however, so this value is meaningless when coming from the mixer. The usefulness of this
    /// value can be checked with <see cref="IsBeatWithinBarMeaningful"/>.
    /// </summary>
    public abstract int BeatWithinBar { get; }

    /// <summary>
    /// Returns <c>true</c> if this update is coming from a device where <see cref="BeatWithinBar"/>
    /// can reasonably be expected to have musical significance, because it respects the way a track
    /// was configured within rekordbox. True for status packets from players, false for status
    /// packets from mixers.
    /// </summary>
    public abstract bool IsBeatWithinBarMeaningful { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"DeviceUpdate[deviceNumber:{DeviceNumber}, deviceName:{DeviceName}, address:{Address}" +
        $", timestamp:{Timestamp}, beatWithinBar:{BeatWithinBar}" +
        $", isBeatWithinBarMeaningful: {IsBeatWithinBarMeaningful}, effectiveTempo:{EffectiveTempo}" +
        $", isTempoMaster:{IsTempoMaster}";
}
