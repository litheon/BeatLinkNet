using System.Net;
using BeatLink.Data;

namespace BeatLink;

/// <summary>
/// Represents a device announcement seen on a DJ Link network. A device announcement can be used
/// with the <see cref="VirtualCdj"/> to find the current detailed status for that device, as long
/// as the Virtual CDJ is active.
/// </summary>
public class DeviceAnnouncement
{
    private readonly byte[] _packetBytes;

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content.
    /// </summary>
    /// <param name="packet">the device announcement packet that was received</param>
    /// <param name="address">the address on which the device was seen</param>
    /// <exception cref="ArgumentException">if the packet is not exactly 54 bytes long</exception>
    public DeviceAnnouncement(ReadOnlySpan<byte> packet, IPAddress address)
    {
        if (packet.Length != 0x36)
        {
            throw new ArgumentException("Device announcement packet must be 54 bytes long", nameof(packet));
        }
        Address = address;
        _packetBytes = packet.ToArray();
        Timestamp = Util.CurrentTimestampNanos();
        DeviceName = Util.DecodeAndTrim(_packetBytes, 0x0c, 20);
        IsOpusQuad = DeviceName == OpusProvider.OpusName;
        IsXdjAz = DeviceName == OpusProvider.XdjAzName;
        IsUsingDeviceLibraryPlus = IsOpusQuad || IsXdjAz;
        DeviceNumber = _packetBytes[0x24];
    }

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content, except
    /// that it allows a synthetic device number to be injected (used when fanning out an Opus Quad
    /// announcement into announcements for its four decks). Experimental, like all Opus Quad
    /// support.
    /// </summary>
    /// <param name="packet">the device announcement packet that was received</param>
    /// <param name="address">the address on which the device was seen</param>
    /// <param name="deviceNumber">the device number you want to emulate</param>
    /// <exception cref="ArgumentException">if the packet is not exactly 54 bytes long</exception>
    public DeviceAnnouncement(ReadOnlySpan<byte> packet, IPAddress address, int deviceNumber)
    {
        if (packet.Length != 0x36)
        {
            throw new ArgumentException("Device announcement packet must be 54 bytes long", nameof(packet));
        }
        Address = address;
        _packetBytes = packet.ToArray();
        Timestamp = Util.CurrentTimestampNanos();
        DeviceName = Util.DecodeAndTrim(_packetBytes, 0x0c, 20);
        IsOpusQuad = DeviceName == OpusProvider.OpusName;
        IsXdjAz = DeviceName == OpusProvider.XdjAzName;
        IsUsingDeviceLibraryPlus = IsOpusQuad || IsXdjAz;
        DeviceNumber = deviceNumber;
    }

    /// <summary>
    /// Get the address on which this device was seen.
    /// </summary>
    public IPAddress Address { get; }

    /// <summary>
    /// Get the last time the device was heard from, as a monotonic timestamp in nanoseconds
    /// (obtained from <see cref="Util.CurrentTimestampNanos"/>).
    /// </summary>
    public long Timestamp { get; }

    /// <summary>
    /// Get the name reported by the device.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Get the player/device number reported by the device.
    /// </summary>
    public int DeviceNumber { get; }

    /// <summary>
    /// Get the MAC address reported by the device.
    /// </summary>
    /// <returns>the device's Ethernet address</returns>
    public byte[] GetHardwareAddress() => _packetBytes.AsSpan(0x26, 6).ToArray();

    /// <summary>
    /// Get the number of peer devices this one currently sees on the network. This count
    /// decrements about ten seconds after a device disappears from the network. Experimental.
    /// </summary>
    public int PeerCount => _packetBytes[0x30];

    /// <summary>
    /// Get the raw data bytes of the device announcement packet.
    /// </summary>
    public ReadOnlyMemory<byte> PacketBytes => _packetBytes;

    /// <summary>
    /// Indicates whether the announcement came from an Opus Quad, which behaves very differently
    /// from true Pro DJ Link hardware. Experimental.
    /// </summary>
    public bool IsOpusQuad { get; }

    /// <summary>
    /// Indicates whether the announcement came from an XDJ-AZ, which can also be in a weird,
    /// non-Pro DJ Link mode. Experimental.
    /// </summary>
    public bool IsXdjAz { get; }

    /// <summary>
    /// Indicates whether the device that sent this announcement uses Device Library Plus (SQLite)
    /// track IDs rather than DeviceSQL IDs. Experimental.
    /// </summary>
    public bool IsUsingDeviceLibraryPlus { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"DeviceAnnouncement[device:{DeviceNumber}, name:{DeviceName}, address:{Address}, peers:{PeerCount}]";
}
