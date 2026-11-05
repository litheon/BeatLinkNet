using System.Net;

namespace BeatLink;

/// <summary>
/// <para>A device update that announces the start of a new beat on a DJ Link network. Even though
/// beats contain far less detailed information than status updates, they can be passed to
/// <see cref="VirtualCdj.GetLatestStatusFor(DeviceUpdate)"/> to find the current detailed status
/// for that device, as long as the Virtual CDJ is active.</para>
///
/// <para>They also provide information about the timing of a variety of upcoming beats and bars,
/// which may be helpful for implementing Sync in a player, but the full beat grid can be obtained
/// as well.</para>
/// </summary>
public class Beat : DeviceUpdate
{
    private readonly int _pitch;

    private readonly int _bpm;

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content.
    /// </summary>
    /// <param name="packet">the beat announcement packet that was received</param>
    /// <param name="address">the address from which the packet was received</param>
    /// <exception cref="ArgumentException">if the packet is not exactly 96 bytes long</exception>
    public Beat(ReadOnlySpan<byte> packet, IPAddress address)
        : base(packet, address, "Beat announcement", 0x60)
    {
        _pitch = (int)Util.BytesToNumber(PacketData, 0x55, 3);
        _bpm = (int)Util.BytesToNumber(PacketData, 0x5a, 2);
    }

    /// <summary>
    /// Get the device pitch at the time of the beat. This is an integer ranging from 0 to 2097152,
    /// which corresponds to a range between completely stopping playback to playing at twice normal
    /// tempo. The equivalent percentage value can be obtained by passing the pitch to
    /// <see cref="Util.PitchToPercentage"/>, and the corresponding fractional scaling value by
    /// passing it to <see cref="Util.PitchToMultiplier"/>.
    /// </summary>
    public override int Pitch => _pitch;

    /// <summary>
    /// Get the track BPM at the time of the beat. This is an integer representing the BPM times
    /// 100, so a track running at 120.5 BPM would be represented by the value 12050.
    /// </summary>
    public override int Bpm => _bpm;

    /// <summary>
    /// Get the position within a measure of music at which this beat falls (a value from 1 to 4,
    /// where 1 represents the downbeat). This value will be accurate for players when the track was
    /// properly configured within rekordbox (and if the music follows a standard House 4/4 time
    /// signature). The mixer makes no effort to synchronize downbeats with players, however, so
    /// this value is meaningless when coming from the mixer.
    /// </summary>
    public override int BeatWithinBar => PacketData[0x5c];

    /// <summary>
    /// Get the time at which the next beat would arrive, in milliseconds, if the track were being
    /// played at normal speed (a pitch of +0%). If the track ends before that beat, returns
    /// <c>0xffffffff</c>.
    /// </summary>
    public long NextBeat => Util.BytesToNumber(PacketData, 0x24, 4);

    /// <summary>
    /// Get the time at which the second upcoming beat would arrive, in milliseconds, if the track
    /// were being played at normal speed (a pitch of +0%). If the track ends before that beat,
    /// returns <c>0xffffffff</c>.
    /// </summary>
    public long SecondBeat => Util.BytesToNumber(PacketData, 0x28, 4);

    /// <summary>
    /// Get the time at which the next bar would begin (the next downbeat would arrive), in
    /// milliseconds, if the track were being played at normal speed (a pitch of +0%). If the track
    /// ends before that bar, returns <c>0xffffffff</c>.
    /// </summary>
    public long NextBar => Util.BytesToNumber(PacketData, 0x2c, 4);

    /// <summary>
    /// Get the time at which the fourth beat would arrive, in milliseconds, if the track were being
    /// played at normal speed (a pitch of +0%). If the track ends before that beat, returns
    /// <c>0xffffffff</c>.
    /// </summary>
    public long FourthBeat => Util.BytesToNumber(PacketData, 0x30, 4);

    /// <summary>
    /// Get the time at which the second upcoming bar would begin (the second downbeat would
    /// arrive), in milliseconds, if the track were being played at normal speed (a pitch of +0%).
    /// If the track ends before that bar, returns <c>0xffffffff</c>.
    /// </summary>
    public long SecondBar => Util.BytesToNumber(PacketData, 0x34, 4);

    /// <summary>
    /// Get the time at which the eighth beat would arrive, in milliseconds, if the track were being
    /// played at normal speed (a pitch of +0%). If the track ends before that beat, returns
    /// <c>0xffffffff</c>.
    /// </summary>
    public long EighthBeat => Util.BytesToNumber(PacketData, 0x38, 4);

    /// <summary>
    /// <para>Returns <c>true</c> if this beat is coming from a device where
    /// <see cref="BeatWithinBar"/> can reasonably be expected to have musical significance, because
    /// it respects the way a track was configured within rekordbox.</para>
    ///
    /// <para>If the <see cref="VirtualCdj"/> is running, we can check the latest status update
    /// received from this device to get a definitive answer. Otherwise, we guess based on the
    /// device number; mixers seem to fall in the range 33 and up.</para>
    /// </summary>
    public override bool IsBeatWithinBarMeaningful
    {
        get
        {
            if (VirtualCdj.Instance.IsRunning)
            {
                return VirtualCdj.Instance.GetLatestStatusFor(this)!.IsBeatWithinBarMeaningful;
            }

            return DeviceNumber < 33;
        }
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Beat: Device {DeviceNumber}, name: {DeviceName}" +
        $", pitch: {Util.PitchToPercentage(_pitch):+0.00;-0.00}%" +
        $", track BPM: {_bpm / 100.0:F1}" +
        $", effective BPM: {EffectiveTempo:F1}" +
        $", beat within bar: {BeatWithinBar}";

    /// <summary>
    /// Was this beat sent by the current tempo master?
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
    /// Was this beat sent by a device that is synced to the tempo master?
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not running</exception>
    public override bool IsSynced => VirtualCdj.Instance.GetLatestStatusFor(this)!.IsSynced;

    /// <summary>
    /// Beats never yield the master role, so this is always <c>null</c>.
    /// </summary>
    public override int? DeviceMasterIsBeingYieldedTo => null;

    /// <inheritdoc/>
    public override double EffectiveTempo => _bpm * Util.PitchToMultiplier(_pitch) / 100.0;
}
