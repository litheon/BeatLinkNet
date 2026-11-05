using System.Collections.Concurrent;
using System.Net;

namespace BeatLink;

/// <summary>
/// Uniquely identifies a device on the network. Just the IP address is not enough, because the
/// XDJ-XZ reports two CDJs and a mixer all on the same IP address, so this immutable class combines
/// the device number and IP address as a joint key. The factory method ensures that for a given
/// device number and address pair the same instance will always be returned, so reference equality
/// can be used to distinguish devices in sets and dictionaries (value equality is also implemented,
/// so interning is a performance nicety rather than a correctness requirement).
/// </summary>
public sealed class DeviceReference : IEquatable<DeviceReference>
{
    /// <summary>The device number reported by the device.</summary>
    public int DeviceNumber { get; }

    /// <summary>The IP address at which the device can be found.</summary>
    public IPAddress Address { get; }

    private readonly int _hashCode;

    private DeviceReference(int number, IPAddress address)
    {
        DeviceNumber = number;
        Address = address;
        _hashCode = HashCode.Combine(number, address);
    }

    /// <summary>
    /// Holds all the instances of this class as they get created by the static factory method.
    /// </summary>
    private static readonly ConcurrentDictionary<(int, IPAddress), DeviceReference> Instances = new();

    /// <summary>
    /// Get a unique device identifier by device number and address.
    /// </summary>
    /// <param name="number">the device number reported by the device</param>
    /// <param name="address">the IP address at which the device can be found</param>
    /// <returns>the reference uniquely identifying the device with that number and address</returns>
    public static DeviceReference GetDeviceReference(int number, IPAddress address) =>
        Instances.GetOrAdd((number, address), static key => new DeviceReference(key.Item1, key.Item2));

    /// <inheritdoc/>
    public bool Equals(DeviceReference? other) =>
        other != null && other.DeviceNumber == DeviceNumber && other.Address.Equals(Address);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DeviceReference);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <inheritdoc/>
    public override string ToString() => $"DeviceReference[deviceNumber:{DeviceNumber}, address:{Address}]";
}
