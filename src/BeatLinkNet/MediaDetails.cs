using System.Collections.Concurrent;
using System.Text;
using BeatLink.Data;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Represents information about the media mounted in a player's slot; returned in response to a
/// media query packet.
/// </summary>
public class MediaDetails
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<MediaDetails>();

    /// <summary>
    /// The player and slot in which this media resides.
    /// </summary>
    public SlotReference SlotReference { get; }

    /// <summary>
    /// The type of tracks stored in this media.
    /// </summary>
    public CdjStatus.TrackType MediaType { get; }

    /// <summary>
    /// The name assigned to the media within rekordbox.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The creation date of the media.
    /// </summary>
    public string CreationDate { get; }

    /// <summary>
    /// The number of rekordbox tracks in the media database. Will be zero if <see cref="MediaType"/>
    /// is not rekordbox.
    /// </summary>
    public int TrackCount { get; }

    /// <summary>
    /// The number of rekordbox playlists in the media database. Will be zero if
    /// <see cref="MediaType"/> is not rekordbox.
    /// </summary>
    public int PlaylistCount { get; }

    /// <summary>
    /// The color tint that should be applied to the user interface when a player mounts this media.
    /// </summary>
    public ArgbColor Color { get; }

    /// <summary>
    /// Indicates that the DJ has stored device setting preferences on the media, so the player
    /// should display an alert showing how to apply them when at the root menu.
    /// </summary>
    public bool HasMySettings { get; }

    /// <summary>
    /// The size of the storage space, in bytes.
    /// </summary>
    public long TotalSize { get; }

    /// <summary>
    /// The amount of storage remaining, in bytes.
    /// </summary>
    public long FreeSpace { get; }

    /// <summary>
    /// The bytes from which we were constructed, to support saving for later recreation.
    /// </summary>
    private readonly byte[] _rawBytes;

    /// <summary>
    /// Get the raw bytes of the media details as returned by the player.
    /// </summary>
    public ReadOnlyMemory<byte> RawBytes => _rawBytes;

    /// <summary>
    /// Contains the sizes we expect Media response packets to have, so we can log a warning if we
    /// get an unusual one. We will then add the new size to the set, so it only gets logged once
    /// per run.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> ExpectedMediaPacketSizes = new(
        new Dictionary<int, byte> { [0xc0] = 0 });

    /// <summary>
    /// The smallest packet size from which we can be constructed. Anything less than this and we
    /// are missing crucial information.
    /// </summary>
    public const int MinimumPacketSize = 0xc0;

    /// <summary>
    /// Given a source byte array, return the UTF-16 string length in bytes by finding a UTF-16 NUL
    /// sequence. If a UTF-16 NUL sequence is not found, <paramref name="maxLength"/> or
    /// <c>source.Length - offset</c> is returned, whichever is smaller.
    /// </summary>
    /// <param name="source">the source byte array representing a UTF-16 string</param>
    /// <param name="offset">the byte offset to start at in the source byte array</param>
    /// <param name="maxLength">the maximum length of the UTF-16 string (in bytes)</param>
    /// <returns>the length in bytes excluding the UTF-16 NUL sequence, else <paramref name="maxLength"/>
    /// or <c>source.Length - offset</c>, whichever is smaller</returns>
    private static int GetUtf16StringLength(byte[] source, int offset, int maxLength)
    {
        var numBytes = maxLength;
        var clampedMaxLength = Math.Min(maxLength, source.Length - offset);

        for (var i = offset; i < offset + clampedMaxLength - 1; i += 2)
        {
            if (source[i] == 0x00 && source[i + 1] == 0x00)
            {
                numBytes = i - offset;
                break;
            }
        }
        return numBytes;
    }

    /// <summary>
    /// Constructor to emulate an actual MediaDetails object from CDJs. This allows our status
    /// packets to use <see cref="OpusProvider"/> to enrich the track data.
    /// <see cref="OpusProvider"/> must be started to use this constructor.
    /// </summary>
    /// <param name="slotReference">slot reference to which these media details belong</param>
    /// <param name="mediaType">media type to emulate</param>
    /// <param name="name">name of the emulated media volume</param>
    /// <param name="trackCount">how many tracks are present in the media</param>
    /// <param name="playlistCount">how many playlists are present in the media</param>
    /// <param name="lastModified">when the media was last changed</param>
    /// <exception cref="InvalidOperationException">if the <see cref="OpusProvider"/> is not running</exception>
    public MediaDetails(SlotReference slotReference, CdjStatus.TrackType mediaType, string name,
        int trackCount, int playlistCount, long lastModified)
    {
        if (!OpusProvider.Instance.IsRunning)
        {
            throw new InvalidOperationException("OpusProvider must be started up to use this Constructor");
        }
        SlotReference = slotReference;
        MediaType = mediaType;
        Name = name;
        CreationDate = lastModified.ToString();
        TrackCount = trackCount;
        TotalSize = 0;
        PlaylistCount = playlistCount;
        _rawBytes = [];
        Color = ArgbColor.Black;  // Equivalent to Java's opaque new Color(0).
        FreeSpace = 0;
        HasMySettings = false;
    }

    /// <summary>
    /// Constructor sets all the immutable interpreted fields based on the packet content.
    /// </summary>
    /// <param name="packet">the media response packet that was received or cached</param>
    /// <exception cref="ArgumentException">if the packet is smaller than <see cref="MinimumPacketSize"/>,
    /// or contains an unrecognized slot or media type</exception>
    public MediaDetails(ReadOnlySpan<byte> packet)
    {
        var packetCopy = packet.ToArray();  // Make a defensive copy.
        _rawBytes = packetCopy;  // Save for safe sharing (only exposed read-only).

        if (packetCopy.Length < MinimumPacketSize)
        {
            throw new ArgumentException("Unable to create a MediaDetails object, packet too short: we need " +
                MinimumPacketSize + " bytes and were given only " + packetCopy.Length, nameof(packet));
        }

        var payloadLength = (int)Util.BytesToNumber(packetCopy, 0x22, 2);
        if (packetCopy.Length != payloadLength + 0x24)
        {
            Logger.LogWarning("Received Media response packet with reported payload length of {Reported} and actual payload length of {Actual}",
                payloadLength, packetCopy.Length - 0x24);
        }

        if (ExpectedMediaPacketSizes.TryAdd(packetCopy.Length, 0))
        {
            Logger.LogWarning("Processing Media response packets with unexpected lengths {Length}.", packetCopy.Length);
        }

        var hostPlayer = packetCopy[0x27];
        if (!CdjStatus.TrackSourceSlot.ProtocolValueMap.TryGetValue(packetCopy[0x2b], out var hostSlot))
        {
            throw new ArgumentException("Unrecognized slot for media response:" + packetCopy[0x2b], nameof(packet));
        }
        SlotReference = SlotReference.GetSlotReference(hostPlayer, hostSlot);

        if (!CdjStatus.TrackType.ProtocolValueMap.TryGetValue(packetCopy[0xaa], out var type))
        {
            throw new ArgumentException("Unrecognized media type for media response:" + packetCopy[0xaa], nameof(packet));
        }
        MediaType = type;

        if (hostPlayer >= 40)  // Rekordbox mobile does not send media name or creation date.
        {
            Name = "rekordbox mobile";
            CreationDate = "";
        }
        else
        {
            var mediaNameLength = GetUtf16StringLength(packetCopy, 0x2c, 0x40);
            var creationDateLength = GetUtf16StringLength(packetCopy, 0x6c, 0x18);

            Name = Encoding.BigEndianUnicode.GetString(packetCopy, 0x2c, mediaNameLength).Trim();
            CreationDate = Encoding.BigEndianUnicode.GetString(packetCopy, 0x6c, creationDateLength).Trim();
        }

        TrackCount = (int)Util.BytesToNumber(packetCopy, 0xa6, 2);
        Color = ColorItem.ColorForId(packetCopy[0xa8]);
        PlaylistCount = (int)Util.BytesToNumber(packetCopy, 0xae, 2);
        HasMySettings = packetCopy[0xab] != 0;
        TotalSize = Util.BytesToNumber(packetCopy, 0xb0, 8);
        FreeSpace = Util.BytesToNumber(packetCopy, 0xb8, 8);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"MediaDetails[slotReference:{SlotReference}, name:{Name}, creationDate:{CreationDate}" +
        $", mediaType:{MediaType}, trackCount:{TrackCount}, playlistCount:{PlaylistCount}" +
        $", totalSize:{TotalSize}, freeSpace:{FreeSpace}]";

    /// <summary>
    /// Build a string containing the things that we do not expect to change about the media, to
    /// help us recognize it when it gets mounted in the future.
    /// </summary>
    /// <returns>a colon-delimited string made up of the creation date, media type, total size, and name</returns>
    public string HashKey() => $"{CreationDate}:{MediaType}:{TotalSize}:{Name}";

    /// <summary>
    /// Check whether the media seems to have changed since a saved version of it was used. We
    /// ignore changes in free space because those probably just reflect history entries being
    /// added.
    /// </summary>
    /// <param name="originalMedia">the media details when information about it was saved</param>
    /// <returns>true if there have been detectable significant changes to the media since it was saved</returns>
    /// <exception cref="ArgumentException">if the <see cref="HashKey"/> values of the media detail objects differ</exception>
    public bool HasChanged(MediaDetails originalMedia)
    {
        if (HashKey() != originalMedia.HashKey())
        {
            throw new ArgumentException("Can't compare media details with different hashKey values", nameof(originalMedia));
        }
        return PlaylistCount != originalMedia.PlaylistCount || TrackCount != originalMedia.TrackCount;
    }
}
