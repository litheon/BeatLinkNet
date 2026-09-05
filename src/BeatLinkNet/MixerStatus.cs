using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Represents a status update sent by a mixer on a DJ Link network.
/// </summary>
public class MixerStatus : DeviceUpdate
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<MixerStatus>();

    /// <summary>
    /// The byte within the status packet which contains useful status information, labeled <i>F</i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#mixer-status-packets">Packet Analysis document</a>.
    /// </summary>
    public const int StatusFlags = 0x27;

    /// <summary>
    /// <para>The byte within a status packet which indicates that the device is in the process of
    /// handing off the tempo master role to another device, labeled <i>M<sub>h</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#mixer-status-packets">Packet Analysis document</a>.</para>
    ///
    /// <para>Normally it holds the value 0xff, but during a tempo master hand-off, it holds the
    /// device number of the incoming tempo master, until that device asserts the master state,
    /// after which this device will stop doing so.</para>
    /// </summary>
    public const int MasterHandOff = 0x36;

    private readonly int _pitch;

    private readonly int _bpm;

    /// <summary>
    /// If we are in the process of handing the tempo master role to another device, this will be
    /// the device number of that device, otherwise it will have the value 255.
    /// </summary>
    private readonly int _handingMasterToDevice;

    /// <summary>
    /// The smallest packet size from which we can be constructed. Anything less than this and we
    /// are missing crucial information.
    /// </summary>
    public const int MinimumPacketSize = 0x38;

    /// <summary>
    /// Contains the sizes we expect mixer status packets to have, so we can log a warning if we
    /// get an unusual one. We will then add the new size to the set, so it only gets logged once
    /// per run.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> ExpectedStatusPacketSizes = new(
        new Dictionary<int, byte> { [MinimumPacketSize] = 0 });

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content.
    /// </summary>
    /// <param name="packet">the mixer status packet that was received</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <exception cref="ArgumentException">if the packet is smaller than <see cref="MinimumPacketSize"/></exception>
    public MixerStatus(ReadOnlySpan<byte> packet, IPAddress address)
        : base(packet, address, "Mixer update", packet.Length)
    {
        if (PacketData.Length < MinimumPacketSize)
        {
            throw new ArgumentException("Unable to create a MixerStatus object, packet too short: we need " +
                MinimumPacketSize + " bytes and were given only " + PacketData.Length, nameof(packet));
        }

        var payloadLength = (int)Util.BytesToNumber(PacketData, 0x22, 2);
        var subtype = PacketData[0x20];
        switch (subtype)
        {
            case 0:  // Actual mixer status packets use this subtype
                if (PacketData.Length != payloadLength + 0x24)
                {
                    Logger.LogWarning("Received Mixer status packet with reported payload length of {Reported} and actual payload length of {Actual}: {Packet}",
                        payloadLength, PacketData.Length - 0x24, this);
                }
                break;

            case 1:  // rekordbox seems to sometimes send packets with this subtype
                if (PacketData.Length != payloadLength)
                {
                    Logger.LogWarning("Received Mixer status packet with reported length of {Reported} and actual length of {Actual}: {Packet}",
                        payloadLength, PacketData.Length, this);
                }
                break;

            default:
                Logger.LogWarning("Received Mixer status packet with unexpected subtype {Subtype}: {Packet}", subtype, this);
                break;
        }

        if (ExpectedStatusPacketSizes.TryAdd(PacketData.Length, 0))
        {
            Logger.LogWarning("Processing Mixer Status packets with unexpected lengths {Length}.", PacketData.Length);
        }

        _pitch = (int)Util.BytesToNumber(PacketData, 0x28, 4);
        _bpm = (int)Util.BytesToNumber(PacketData, 0x2e, 2);
        _handingMasterToDevice = PacketData[MasterHandOff];
    }

    /// <summary>
    /// Get the BPM at the time of the update. This is an integer representing the BPM times 100,
    /// so 120.5 BPM would be represented by the value 12050.
    /// </summary>
    public override int Bpm => _bpm;

    /// <summary>
    /// Get the position within a measure of music at which the most recent beat occurred (a value
    /// from 1 to 4, where 1 represents the downbeat). The mixer makes no effort to synchronize
    /// downbeats with players, however, so this value is of little use.
    /// </summary>
    public override int BeatWithinBar => PacketData[55];

    /// <summary>
    /// Returns <c>false</c> because mixers make no effort to line up their beats with
    /// rekordbox-identified measures.
    /// </summary>
    public override bool IsBeatWithinBarMeaningful => false;

    /// <inheritdoc/>
    public override int Pitch => _pitch;

    /// <summary>
    /// Is this mixer reporting itself to be the current tempo master?
    /// </summary>
    public override bool IsTempoMaster => (PacketData[StatusFlags] & CdjStatus.MasterFlag) > 0;

    /// <summary>
    /// Was the mixer in Sync mode when this update was sent? Unless someone has been sending sync
    /// control packets using a package like Beat Link, this will generally return <c>true</c>.
    /// </summary>
    public override bool IsSynced => (PacketData[StatusFlags] & CdjStatus.SyncedFlag) > 0;

    /// <inheritdoc/>
    public override int? DeviceMasterIsBeingYieldedTo =>
        _handingMasterToDevice == 0xff ? null : _handingMasterToDevice;

    /// <inheritdoc/>
    public override double EffectiveTempo => _bpm * Util.PitchToMultiplier(_pitch) / 100.0;

    /// <inheritdoc/>
    public override string ToString() =>
        $"MixerStatus[device:{DeviceNumber}, name:{DeviceName}, address:{Address}" +
        $", timestamp:{Timestamp}, BPM:{_bpm / 100.0:F1}" +
        $", beat within bar:{BeatWithinBar}, isBeatWithBarMeaningful? {IsBeatWithinBarMeaningful}" +
        $", handingMasterToDevice:{_handingMasterToDevice}]";
}
