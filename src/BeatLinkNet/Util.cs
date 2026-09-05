using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Provides utility functions used throughout the library, ported from the beat-link
/// <c>Util</c> class.
/// </summary>
public static partial class Util
{
    private static readonly ILogger Logger = BeatLinkLogging.LoggerFactory.CreateLogger("BeatLink.Util");

    /// <summary>
    /// The sequence of ten bytes which begins all UDP packets sent in the protocol.
    /// </summary>
    private static readonly byte[] MagicHeaderBytes = [0x51, 0x73, 0x70, 0x74, 0x31, 0x57, 0x6d, 0x4a, 0x4f, 0x4c];

    /// <summary>
    /// The sequence of ten bytes which begins all UDP packets sent in the protocol ("Qspt1WmJOL").
    /// </summary>
    public static ReadOnlySpan<byte> MagicHeader => MagicHeaderBytes;

    /// <summary>
    /// The offset into protocol packets which identifies the content of the packet.
    /// </summary>
    public const int PacketTypeOffset = 0x0a;

    /// <summary>
    /// Build a standard-format UDP packet body for sending to port 50001 or 50002 in the protocol.
    /// </summary>
    /// <param name="type">the type of packet to create</param>
    /// <param name="deviceName">the 0x14 (twenty) bytes of the device name to send in the packet</param>
    /// <param name="payload">the remaining bytes which come after the device name</param>
    /// <returns>the bytes of the packet to send</returns>
    /// <exception cref="ArgumentException">if the device name is not exactly twenty bytes long</exception>
    public static byte[] BuildPacket(PacketType type, ReadOnlySpan<byte> deviceName, ReadOnlySpan<byte> payload)
    {
        if (deviceName.Length != 0x14)
        {
            throw new ArgumentException("Device name must be exactly 20 bytes long.", nameof(deviceName));
        }
        var content = new byte[0x1f + payload.Length];
        MagicHeader.CopyTo(content);
        content[PacketTypeOffset] = type.ProtocolValue;
        deviceName.CopyTo(content.AsSpan(0x0b));
        payload.CopyTo(content.AsSpan(0x1f));
        return content;
    }

    /// <summary>
    /// Helper function to set a byte in the payload for a packet being sent to port 50001 or 50002,
    /// while working with the byte addresses shown in the protocol analysis document (adjusts the
    /// address to account for the fact that the standard packet header up through the device name
    /// has not yet been prepended).
    /// </summary>
    /// <param name="payload">the array in which the packet payload is being built</param>
    /// <param name="address">the overall packet address (taking account of the header) of the byte to be set</param>
    /// <param name="value">the byte value to store at that packet address</param>
    public static void SetPayloadByte(byte[] payload, int address, byte value)
    {
        payload[address - 0x1f] = value;
    }

    /// <summary>
    /// Used to keep track of when we report seeing a packet on an unexpected port, so we only do that once.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> UnknownPortsReported = new();

    /// <summary>
    /// Used to keep track of when we report seeing a packet of an unknown type on a port, so we do that only once.
    /// </summary>
    private static readonly ConcurrentDictionary<(int, byte), byte> UnknownPortTypesReported = new();

    /// <summary>
    /// Check whether a packet starts with the standard header bytes, followed by a known byte
    /// identifying it. If so, return the kind of packet that has been recognized.
    /// </summary>
    /// <param name="packet">the content of a packet that has just been received</param>
    /// <param name="port">the port on which the packet was received</param>
    /// <returns>the type of packet that was recognized, or <c>null</c> if the packet was not recognized</returns>
    public static PacketType? ValidateHeader(ReadOnlySpan<byte> packet, int port)
    {
        if (packet.Length < PacketTypeOffset)
        {
            Logger.LogWarning("Packet is too short to be a Pro DJ Link packet; must be at least {Minimum} bytes long, was only {Length}.",
                PacketTypeOffset, packet.Length);
            return null;
        }

        if (!packet[..MagicHeader.Length].SequenceEqual(MagicHeader))
        {
            Logger.LogWarning("Packet did not have correct ten-byte header for the Pro DJ Link protocol.");
            return null;
        }

        var typeByte = packet[PacketTypeOffset];
        if (PacketType.PacketTypeMap.TryGetValue((port, typeByte), out var result))
        {
            return result;
        }

        var knownPort = false;
        foreach (var candidate in PacketType.Values)
        {
            if (candidate.Port == port)
            {
                knownPort = true;
                break;
            }
        }

        if (!knownPort)
        {
            if (UnknownPortsReported.TryAdd(port, 0))
            {
                Logger.LogWarning("Do not know any Pro DJ Link packets that are received on port {Port} (this will be reported only once).", port);
            }
        }
        else if (UnknownPortTypesReported.TryAdd((port, typeByte), 0))
        {
            Logger.LogWarning("Do not know any Pro DJ Link packets received on port {Port} with type 0x{Type:x2} (this will be reported only once).",
                port, typeByte);
        }

        return null;
    }

    /// <summary>
    /// Reconstructs a number that is represented by more than one byte in a network packet in
    /// big-endian order.
    /// </summary>
    /// <param name="buffer">the bytes containing the packet data</param>
    /// <param name="start">the index of the first byte containing the numeric value</param>
    /// <param name="length">the number of bytes making up the value</param>
    /// <returns>the reconstructed number</returns>
    public static long BytesToNumber(ReadOnlySpan<byte> buffer, int start, int length)
    {
        long result = 0;
        for (var index = start; index < start + length; index++)
        {
            result = (result << 8) + buffer[index];
        }
        return result;
    }

    /// <summary>
    /// Reconstructs a number that is represented by more than one byte in a network packet in
    /// little-endian order, for the very few protocol values that are sent in this quirky way.
    /// </summary>
    /// <param name="buffer">the bytes containing the packet data</param>
    /// <param name="start">the index of the first byte containing the numeric value</param>
    /// <param name="length">the number of bytes making up the value</param>
    /// <returns>the reconstructed number</returns>
    public static long BytesToNumberLittleEndian(ReadOnlySpan<byte> buffer, int start, int length)
    {
        long result = 0;
        for (var index = start + length - 1; index >= start; index--)
        {
            result = (result << 8) + buffer[index];
        }
        return result;
    }

    /// <summary>
    /// Writes a number to the specified span, breaking it into its component bytes in big-endian
    /// order. If the number is too large to fit in the specified number of bytes, only the
    /// low-order bytes are written.
    /// </summary>
    /// <param name="number">the number to be written</param>
    /// <param name="buffer">the buffer to which the number should be written</param>
    /// <param name="start">where the high-order byte should be written</param>
    /// <param name="length">how many bytes of the number should be written</param>
    public static void NumberToBytes(long number, Span<byte> buffer, int start, int length)
    {
        for (var index = start + length - 1; index >= start; index--)
        {
            buffer[index] = (byte)(number & 0xff);
            number >>= 8;
        }
    }

    /// <summary>
    /// Finds the first index at which a byte sequence occurs within a buffer.
    /// </summary>
    /// <param name="buffer">the buffer of bytes to search</param>
    /// <param name="pattern">the byte sequence being looked for</param>
    /// <returns>-1 if not found or <paramref name="pattern"/> is empty, otherwise the index found</returns>
    public static int IndexOfBytes(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> pattern)
    {
        if (pattern.IsEmpty)
        {
            return -1;
        }
        return buffer.IndexOf(pattern);
    }

    /// <summary>
    /// Converts the bytes that make up an IPv4 internet address into the corresponding integer
    /// value to make it easier to perform bit-masking operations on them.
    /// </summary>
    /// <param name="address">an address whose integer equivalent is desired</param>
    /// <returns>the integer corresponding to that address</returns>
    public static long AddressToLong(IPAddress address)
    {
        long result = 0;
        foreach (var element in address.GetAddressBytes())
        {
            result = (result << 8) + element;
        }
        return result;
    }

    /// <summary>
    /// Checks whether two internet addresses are on the same subnet.
    /// </summary>
    /// <param name="prefixLength">the number of bits within an address that identify the network</param>
    /// <param name="address1">the first address to be compared</param>
    /// <param name="address2">the second address to be compared</param>
    /// <returns>true if both addresses share the same network bits</returns>
    public static bool SameNetwork(int prefixLength, IPAddress address1, IPAddress address2)
    {
        Logger.LogDebug("Comparing address {Address1} with {Address2}, prefixLength={PrefixLength}", address1, address2, prefixLength);
        var prefixMask = 0xffffffffL & (-1L << (32 - prefixLength));
        return (AddressToLong(address1) & prefixMask) == (AddressToLong(address2) & prefixMask);
    }

    /// <summary>
    /// Computes the IPv4 broadcast address for an interface address, since .NET does not expose one
    /// directly the way Java's <c>InterfaceAddress.getBroadcast()</c> did.
    /// </summary>
    /// <param name="addressInformation">the unicast address assigned to a network interface</param>
    /// <returns>the broadcast address for that address's subnet, or <c>null</c> if the address is not IPv4</returns>
    public static IPAddress? ComputeBroadcastAddress(UnicastIPAddressInformation addressInformation)
    {
        if (addressInformation.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }
        var addressBytes = addressInformation.Address.GetAddressBytes();
        var maskBytes = addressInformation.IPv4Mask.GetAddressBytes();
        var broadcastBytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
        }
        return new IPAddress(broadcastBytes);
    }

    /// <summary>
    /// Scan a network interface to find if it has an address space which matches a device we are
    /// trying to reach. If so, return the matching address information.
    /// </summary>
    /// <param name="deviceAddress">the address of the DJ Link device we are trying to communicate with</param>
    /// <param name="networkInterface">the network interface we are testing</param>
    /// <returns>the address which can be used to communicate with the device on the interface, or <c>null</c></returns>
    public static UnicastIPAddressInformation? FindMatchingAddress(IPAddress deviceAddress, NetworkInterface networkInterface)
    {
        foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
        {
            if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                SameNetwork(address.PrefixLength, deviceAddress, address.Address))
            {
                return address;
            }
        }
        return null;
    }

    /// <summary>
    /// The value sent in beat and status packets to represent playback at normal speed.
    /// </summary>
    public const long NeutralPitch = 1048576;

    /// <summary>
    /// Convert a pitch value reported by a device to the corresponding percentage (-100% to +100%,
    /// where normal, unadjusted pitch has the value 0%).
    /// </summary>
    /// <param name="pitch">the reported device pitch</param>
    /// <returns>the pitch as a percentage</returns>
    public static double PitchToPercentage(long pitch) => (pitch - NeutralPitch) / (NeutralPitch / 100.0);

    /// <summary>
    /// Convert a human-oriented pitch percentage (-100% to +100%, where normal, unadjusted playback
    /// speed has the value 0%) to the raw numeric value used to represent it in beat and CDJ status
    /// packets.
    /// </summary>
    /// <param name="percentage">the pitch as a percentage</param>
    /// <returns>the value that would represent that pitch in a beat or CDJ status packet</returns>
    public static long PercentageToPitch(double percentage) => (long)Math.Round(percentage * NeutralPitch / 100.0, MidpointRounding.AwayFromZero) + NeutralPitch;

    /// <summary>
    /// Convert a pitch value reported by a device to the corresponding multiplier (0.0 to 2.0,
    /// where normal, unadjusted pitch has the multiplier 1.0).
    /// </summary>
    /// <param name="pitch">the reported device pitch</param>
    /// <returns>the implied pitch multiplier</returns>
    public static double PitchToMultiplier(long pitch) => pitch / (double)NeutralPitch;

    /// <summary>
    /// Figure out the track time that corresponds to a half-frame number (75 frames per second, so
    /// 150 half-frames).
    /// </summary>
    /// <param name="halfFrame">the half-frame that we are interested in knowing the time for</param>
    /// <returns>the number of milliseconds into a track that the specified half-frame begins</returns>
    public static long HalfFrameToTime(long halfFrame) => halfFrame * 100 / 15;

    /// <summary>
    /// Convert a track position (time) into the corresponding half-frame value (75 frames per
    /// second, so 150 half-frames).
    /// </summary>
    /// <param name="milliseconds">how long a track has been playing for</param>
    /// <returns>the half-frame that contains that part of the track</returns>
    public static int TimeToHalfFrame(long milliseconds) => (int)(milliseconds * 15 / 100);

    /// <summary>
    /// Convert a track position (time) into the corresponding rounded half-frame value (75 frames
    /// per second, so 150 half-frames).
    /// </summary>
    /// <param name="milliseconds">how long a track has been playing for</param>
    /// <returns>the nearest half-frame that contains that part of the track</returns>
    public static int TimeToHalfFrameRounded(long milliseconds) => (int)Math.Round(milliseconds * 0.15f, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A monotonic timestamp in nanoseconds, playing the role of Java's <c>System.nanoTime()</c>.
    /// Values are only meaningful relative to each other within the current process.
    /// </summary>
    /// <returns>the number of nanoseconds elapsed on a monotonic clock</returns>
    public static long CurrentTimestampNanos() =>
        (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    /// <summary>
    /// Used to allow locking operations against named resources, such as files being fetched by
    /// <c>CrateDigger</c>, to protect against race conditions where one thread creates the file and
    /// another thinks it has already been downloaded and tries to parse the partial file.
    /// </summary>
    private static readonly Dictionary<string, object> NamedLocks = new();

    /// <summary>
    /// Counts the threads that are currently using a named lock, so we can know when it can be
    /// removed from the map.
    /// </summary>
    private static readonly Dictionary<string, int> NamedLockUseCounts = new();

    /// <summary>
    /// Obtain an object that can be locked on to provide exclusive access to a named resource,
    /// given its unique name. Once the exclusive lock is no longer needed,
    /// <see cref="FreeNamedLock(string)"/> should be called with the same name so the lock can be
    /// garbage collected if no other threads are now using it.
    /// </summary>
    /// <param name="name">uniquely identifies some resource to which exclusive access is needed</param>
    /// <returns>an object that can be used with a <c>lock</c> block to guarantee exclusive access to the resource</returns>
    public static object AllocateNamedLock(string name)
    {
        lock (NamedLocks)
        {
            if (NamedLocks.TryGetValue(name, out var result))
            {
                NamedLockUseCounts[name] += 1;
                return result;
            }
            NamedLockUseCounts[name] = 1;
            result = new object();
            NamedLocks[name] = result;
            return result;
        }
    }

    /// <summary>
    /// Indicate that an object obtained from <see cref="AllocateNamedLock(string)"/> is no longer
    /// needed by the caller, so it is eligible for garbage collection if no other threads have it
    /// allocated.
    /// </summary>
    /// <param name="name">uniquely identifies some resource to which exclusive access was previously needed</param>
    public static void FreeNamedLock(string name)
    {
        lock (NamedLocks)
        {
            var count = NamedLockUseCounts[name];
            if (count > 1)
            {
                NamedLockUseCounts[name] = count - 1;
            }
            else
            {
                NamedLocks.Remove(name);
                NamedLockUseCounts.Remove(name);
            }
        }
    }

    /// <summary>
    /// Transforms an album art path to the version that will contain high resolution art, if that
    /// is available.
    /// </summary>
    /// <param name="artPath">standard resolution album art path</param>
    /// <returns>path at which high resolution art might be found</returns>
    public static string HighResolutionPath(string artPath) => HighResolutionPathRegex().Replace(artPath, "_m$1", 1);

    [GeneratedRegex(@"(\.\w+$)")]
    private static partial Regex HighResolutionPathRegex();

    /// <summary>
    /// Decodes a string embedded in a packet and trims it the way Java's <c>String.trim()</c>
    /// would, removing all leading and trailing characters whose value is not greater than
    /// <c>' '</c> (which includes the NUL padding bytes used in device names).
    /// </summary>
    /// <param name="buffer">the bytes containing the packet data</param>
    /// <param name="start">the index of the first byte of the string</param>
    /// <param name="length">the number of bytes making up the string field</param>
    /// <returns>the decoded and trimmed string</returns>
    internal static string DecodeAndTrim(ReadOnlySpan<byte> buffer, int start, int length)
    {
        var text = System.Text.Encoding.UTF8.GetString(buffer.Slice(start, length));
        var begin = 0;
        var end = text.Length;
        while (begin < end && text[begin] <= ' ')
        {
            begin++;
        }
        while (end > begin && text[end - 1] <= ' ')
        {
            end--;
        }
        return text[begin..end];
    }

    /// <summary>
    /// Wakes a receiver thread that is blocked in a synchronous <c>ReceiveFrom</c> call on a UDP
    /// socket by sending it a zero-length datagram over the loopback interface. This is needed
    /// because on macOS closing a UDP socket neither wakes a thread blocked in a synchronous
    /// receive nor completes until that receive finishes, so services must call this before
    /// closing their sockets when shutting down (the Java implementation relied on
    /// <c>DatagramSocket.close()</c> interrupting the receive, which .NET cannot do here).
    /// Zero-length datagrams are ignored by the receive loops.
    /// </summary>
    /// <param name="socket">the bound UDP socket whose receiver should be woken</param>
    internal static void WakeUdpReceiver(System.Net.Sockets.Socket socket)
    {
        try
        {
            if (socket.LocalEndPoint is IPEndPoint local)
            {
                // A socket bound to the wildcard address can be reached via loopback, but one bound
                // to a specific interface address (like the VirtualCdj's status socket) only
                // receives datagrams addressed to that address, so target it directly in that case.
                var target = local.Address.Equals(IPAddress.Any) ? IPAddress.Loopback : local.Address;
                using var waker = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp);
                waker.SendTo([], new IPEndPoint(target, local.Port));
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "Problem waking a UDP receiver thread during shutdown; proceeding to close its socket anyway.");
        }
    }

    /// <summary>
    /// Adjust the player numbers from the Opus-Quad so that they are 1-4 as expected by users and
    /// the rest of the ecosystem. (Experimental, like all Opus Quad support.)
    /// </summary>
    /// <param name="reportedPlayerNumber">the device number actually reported by the Opus Quad in the range 9-12</param>
    /// <returns>the logical value in the range 1-4 corresponding to the deck label</returns>
    public static int TranslateOpusPlayerNumbers(int reportedPlayerNumber) => reportedPlayerNumber & 7;
}
