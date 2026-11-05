using System.Net;

namespace BeatLink;

/// <summary>
/// A device update that reports the exact playback position of a CDJ-3000 (or newer?) player, even
/// if it is not currently playing. These are sent very frequently (roughly every 30 milliseconds)
/// as long as the player has a track loaded.
/// </summary>
public class PrecisePosition : DeviceUpdate
{
    private readonly int _pitch;

    private readonly int _bpm;

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content.
    /// </summary>
    /// <param name="packet">the precise position packet that was received</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <exception cref="ArgumentException">if the packet is not exactly 60 bytes long</exception>
    public PrecisePosition(ReadOnlySpan<byte> packet, IPAddress address)
        // Use the special parent constructor because the normal offset does not contain our device number.
        : base(packet, address, "Precise position", 0x3c, 0x21)
    {
        TrackLength = (int)Util.BytesToNumber(PacketData, 0x24, 4);
        PlaybackPosition = (int)Util.BytesToNumber(PacketData, 0x28, 4);
        var rawPitch = Util.BytesToNumber(PacketData, 0x2c, 4);
        if (rawPitch > 0x80000000L)  // This is a negative effective tempo
        {
            rawPitch -= 0x100000000L;
        }
        _pitch = (int)Util.PercentageToPitch(rawPitch / 100.0);
        _bpm = (int)Util.BytesToNumber(PacketData, 0x38, 4) * 10;
    }

    /// <summary>
    /// Get the length of the track that is loaded in the player, in seconds, rounded down to the
    /// nearest second.
    /// </summary>
    public int TrackLength { get; }

    /// <summary>
    /// Get the current position of the player's playback head within the track, in milliseconds.
    /// </summary>
    public int PlaybackPosition { get; }

    /// <inheritdoc/>
    public override int Pitch => _pitch;

    /// <inheritdoc/>
    public override int Bpm => (int)Math.Round(_bpm / Util.PitchToMultiplier(_pitch), MidpointRounding.AwayFromZero);

    /// <inheritdoc/>
    public override string ToString() =>
        $"Precise position: Device {DeviceNumber}, name: {DeviceName}" +
        $", pitch: {Util.PitchToPercentage(_pitch):+0.00;-0.00}%" +
        $", track BPM: {_bpm / 100.0:F1}" +
        $", effective BPM: {EffectiveTempo:F1}";

    /// <summary>
    /// Was this update sent by the current tempo master?
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not running</exception>
    public override bool IsTempoMaster
    {
        get
        {
            var master = VirtualCdj.Instance.TempoMaster;
            return master != null && master.Address.Equals(Address) && master.DeviceNumber == DeviceNumber;
        }
    }

    /// <summary>
    /// Was this update sent by a device that is synced to the tempo master?
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not running</exception>
    public override bool IsSynced => VirtualCdj.Instance.GetLatestStatusFor(this)!.IsSynced;

    /// <summary>
    /// Precise position packets never yield the master role, so this is always <c>null</c>.
    /// </summary>
    public override int? DeviceMasterIsBeingYieldedTo => null;

    /// <inheritdoc/>
    public override double EffectiveTempo => _bpm / 100.0;

    /// <summary>
    /// Get the position within a measure of music at which the most recent beat fell (a value from
    /// 1 to 4, where 1 represents the downbeat), by consulting the latest status update received
    /// from this device.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not running</exception>
    public override int BeatWithinBar => VirtualCdj.Instance.GetLatestStatusFor(this)!.BeatWithinBar;

    /// <summary>
    /// Returns <c>true</c> if this update is coming from a device where <see cref="BeatWithinBar"/>
    /// can reasonably be expected to have musical significance, by consulting the latest status
    /// update received from this device.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not running</exception>
    public override bool IsBeatWithinBarMeaningful =>
        VirtualCdj.Instance.GetLatestStatusFor(this)!.IsBeatWithinBarMeaningful;
}
