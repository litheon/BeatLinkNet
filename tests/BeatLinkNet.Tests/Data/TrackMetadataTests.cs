using BeatLink.Data;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Exercises construction of <see cref="TrackMetadata"/> from a synthetic list of dbserver menu
/// item messages, mimicking a REKORDBOX_METADATA_REQ render response.
/// </summary>
public class TrackMetadataTests
{
    private static readonly DataReference Reference =
        new(2, CdjStatus.TrackSourceSlot.UsbSlot, 1234, CdjStatus.TrackType.Rekordbox);

    /// <summary>
    /// Builds a rendered menu item message of the specified type, following the argument layout
    /// used by real players: numeric 1 (parent id), numeric 2 (this id), label 1 byte size,
    /// label 1, label 2 byte size, label 2, item type, flags, album art id, playlist position.
    /// </summary>
    private static Message MakeMenuItem(Message.MenuItemType type, long id, string label,
        long artworkId = 0, long itemTypeExtraBits = 0)
    {
        return new Message(42, Message.KnownType.MenuItem,
            new NumberField(0),                          // Numeric 1 (parent id).
            new NumberField(id),                         // Numeric 2 (this id).
            new NumberField((label.Length + 1) * 2),     // Label 1 byte size.
            new StringField(label),                      // Label 1.
            new NumberField(2),                          // Label 2 byte size.
            new StringField(""),                         // Label 2.
            new NumberField(type.ProtocolValue | itemTypeExtraBits),  // Item type.
            new NumberField(0),                          // Flags.
            new NumberField(artworkId),                  // Album art id.
            new NumberField(0));                         // Playlist position.
    }

    private static List<Message> BuildFullMetadataResponse() =>
    [
        MakeMenuItem(Message.MenuItemType.TrackTitle, 1234, "Language", artworkId: 767),
        MakeMenuItem(Message.MenuItemType.Artist, 3080, "Porter Robinson"),
        MakeMenuItem(Message.MenuItemType.OriginalArtist, 4001, "Original Person"),
        MakeMenuItem(Message.MenuItemType.Remixer, 4002, "Some Remixer"),
        MakeMenuItem(Message.MenuItemType.AlbumTitle, 1710, "Language (Original Mix)"),
        MakeMenuItem(Message.MenuItemType.Label, 91, "Big Beat Records"),
        MakeMenuItem(Message.MenuItemType.Duration, 373, ""),
        MakeMenuItem(Message.MenuItemType.Tempo, 12800, ""),
        MakeMenuItem(Message.MenuItemType.Comment, 0, "Test comment"),
        MakeMenuItem(Message.MenuItemType.Key, 20, "Ab"),
        MakeMenuItem(Message.MenuItemType.Rating, 4, ""),
        MakeMenuItem(Message.MenuItemType.ColorGreen, 5, "Green"),
        MakeMenuItem(Message.MenuItemType.Genre, 4, "Progressive House"),
        MakeMenuItem(Message.MenuItemType.DateAdded, 0, "2019-03-06"),
        MakeMenuItem(Message.MenuItemType.Year, 2012, ""),
        MakeMenuItem(Message.MenuItemType.BitRate, 320, "")
    ];

    [Fact]
    public void ParsesAllFieldsFromMenuItems()
    {
        var items = BuildFullMetadataResponse();
        var metadata = new TrackMetadata(Reference, CdjStatus.TrackType.Rekordbox, items, null);

        Assert.Same(Reference, metadata.TrackReference);
        Assert.Same(CdjStatus.TrackType.Rekordbox, metadata.TrackType);
        Assert.Equal("Language", metadata.Title);
        Assert.Equal(767, metadata.ArtworkId);
        Assert.Equal(new SearchableItem(3080, "Porter Robinson"), metadata.Artist);
        Assert.Equal(new SearchableItem(4001, "Original Person"), metadata.OriginalArtist);
        Assert.Equal(new SearchableItem(4002, "Some Remixer"), metadata.Remixer);
        Assert.Equal(new SearchableItem(1710, "Language (Original Mix)"), metadata.Album);
        Assert.Equal(new SearchableItem(91, "Big Beat Records"), metadata.Label);
        Assert.Equal(373, metadata.Duration);
        Assert.Equal(12800, metadata.Tempo);
        Assert.Equal("Test comment", metadata.Comment);
        Assert.Equal(new SearchableItem(20, "Ab"), metadata.Key);
        Assert.Equal(4, metadata.Rating);
        Assert.Equal(new ColorItem(5, "Green"), metadata.Color);
        Assert.Equal(ArgbColor.FromRgb(0, 255, 0), metadata.Color!.Color);
        Assert.Equal(new SearchableItem(4, "Progressive House"), metadata.Genre);
        Assert.Equal("2019-03-06", metadata.DateAdded);
        Assert.Equal(2012, metadata.Year);
        Assert.Equal(320, metadata.BitRate);
        Assert.Null(metadata.CueList);
    }

    [Fact]
    public void RetainsRawItems()
    {
        var items = BuildFullMetadataResponse();
        var metadata = new TrackMetadata(Reference, CdjStatus.TrackType.Rekordbox, items, null);
        Assert.NotNull(metadata.RawItems);
        Assert.Equal(items.Count, metadata.RawItems!.Count);
        Assert.Same(items[0], metadata.RawItems[0]);
    }

    [Fact]
    public void MissingStringFieldsDefaultToEmpty()
    {
        var items = new List<Message> { MakeMenuItem(Message.MenuItemType.Duration, 300, "") };
        var metadata = new TrackMetadata(Reference, CdjStatus.TrackType.Rekordbox, items, null);
        Assert.Equal("", metadata.Title);
        Assert.Equal("", metadata.Comment);
        Assert.Equal("", metadata.DateAdded);
        Assert.Equal(300, metadata.Duration);
        Assert.Null(metadata.Artist);
        Assert.Null(metadata.Color);
        Assert.Equal(0, metadata.ArtworkId);
    }

    [Fact]
    public void MenuItemTypeHighBitsAreMaskedOff()
    {
        // The high bytes of the item type argument can carry other information, and must be ignored.
        var items = new List<Message>
        {
            MakeMenuItem(Message.MenuItemType.TrackTitle, 1234, "Language", artworkId: 767,
                itemTypeExtraBits: 0x10000)
        };
        var metadata = new TrackMetadata(Reference, CdjStatus.TrackType.Rekordbox, items, null);
        Assert.Equal("Language", metadata.Title);
        Assert.Equal(767, metadata.ArtworkId);
    }

    [Fact]
    public void RemixerDoesNotClobberAlbum()
    {
        // The Java version has a switch fall-through bug which makes a remixer item also overwrite
        // the album; this port intentionally fixes that, which this test locks in.
        var items = new List<Message>
        {
            MakeMenuItem(Message.MenuItemType.AlbumTitle, 1710, "Worlds"),
            MakeMenuItem(Message.MenuItemType.Remixer, 4002, "Some Remixer")
        };
        var metadata = new TrackMetadata(Reference, CdjStatus.TrackType.Rekordbox, items, null);
        Assert.Equal(new SearchableItem(1710, "Worlds"), metadata.Album);
        Assert.Equal(new SearchableItem(4002, "Some Remixer"), metadata.Remixer);
    }

    [Fact]
    public void ImplementsValueEquality()
    {
        var a = new TrackMetadata(Reference, CdjStatus.TrackType.Rekordbox, BuildFullMetadataResponse(), null);
        var b = new TrackMetadata(
            new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 1234, CdjStatus.TrackType.Rekordbox),
            CdjStatus.TrackType.Rekordbox, BuildFullMetadataResponse(), null);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var different = new TrackMetadata(Reference, CdjStatus.TrackType.Rekordbox,
            [MakeMenuItem(Message.MenuItemType.TrackTitle, 1234, "Other Title", artworkId: 767)], null);
        Assert.NotEqual(a, different);
    }
}
