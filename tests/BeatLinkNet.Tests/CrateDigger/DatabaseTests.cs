using System.Text;
using BeatLink.CrateDigger;
using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;

namespace BeatLink.Tests.CrateDigger;

/// <summary>
/// Exercises <see cref="Database"/> (including its page/row-walking and DeviceSQL string
/// decoding) against a synthetic minimal <c>export.pdb</c> file built byte-by-byte following the
/// layout documented in <c>rekordbox_pdb.ksy</c>.
/// </summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly string _pdbPath;
    private readonly Database _database;

    public DatabaseTests()
    {
        _pdbPath = Path.Combine(Path.GetTempPath(), $"beat-link-net-test-{Guid.NewGuid():N}.pdb");
        File.WriteAllBytes(_pdbPath, PdbBuilder.BuildExportFile());
        _database = new Database(_pdbPath);
    }

    public void Dispose()
    {
        _database.Dispose();
        File.Delete(_pdbPath);
    }

    [Fact]
    public void TracksAreIndexedById()
    {
        Assert.Equal(2, _database.TrackIndex.Count);
        var track = _database.TrackIndex[1];
        Assert.Equal("alpha song", Database.GetText(track.Title));
        Assert.Equal(300, track.Duration);
        Assert.Equal(12800u, track.Tempo);
        Assert.Equal(1997, track.Year);
        Assert.Equal(4, track.Rating);
        Assert.Equal(320u, track.Bitrate);
        Assert.Equal(8u, track.ArtworkId);
        Assert.Equal("Beta Song", Database.GetText(_database.TrackIndex[2].Title));
    }

    [Fact]
    public void DeletedRowsAreSkipped()
    {
        // The tracks page contains a third row offset whose present flag is cleared; the row it
        // points to (track ID 3) must not be indexed.
        Assert.False(_database.TrackIndex.ContainsKey(3));
    }

    [Fact]
    public void TrackTitleIndexIsSortedCaseInsensitively()
    {
        // "alpha song" must sort before "Beta Song", which only happens case-insensitively.
        Assert.Equal(new[] { "alpha song", "Beta Song" }, _database.TrackTitleIndex.Keys.ToArray());
        Assert.Equal(new[] { 1L }, _database.TrackTitleIndex["alpha song"]);
        Assert.Equal(new[] { 2L }, _database.TrackTitleIndex["Beta Song"]);
    }

    [Fact]
    public void SecondaryTrackIndexesLinkRelatedRows()
    {
        Assert.Equal(new[] { 1L }, _database.TrackArtistIndex[7]);
        Assert.Equal(new[] { 2L }, _database.TrackArtistIndex[9]);
        Assert.Equal(new[] { 1L }, _database.TrackAlbumIndex[5]);
        Assert.Equal(new[] { 1L, 2L }, _database.TrackGenreIndex[3]);
    }

    [Fact]
    public void ArtistsAreIndexedWithUtf16Names()
    {
        Assert.Equal(2, _database.ArtistIndex.Count);
        // Artist 7 has a name that can only be represented in the long UTF-16LE encoding.
        Assert.Equal("Björk", Database.GetText(_database.ArtistIndex[7].Name));
        Assert.Equal("Aphex Twin", Database.GetText(_database.ArtistIndex[9].Name));
        Assert.Equal(new[] { 7L }, _database.ArtistNameIndex["Björk"]);
        Assert.Equal(new[] { 9L }, _database.ArtistNameIndex["Aphex Twin"]);
    }

    [Fact]
    public void OtherReferenceTablesAreIndexed()
    {
        Assert.Equal("Homogenic", Database.GetText(_database.AlbumIndex[5].Name));
        Assert.Equal(7u, _database.AlbumIndex[5].ArtistId);
        Assert.Equal(new[] { 5L }, _database.AlbumArtistIndex[7]);

        Assert.Equal("IDM", Database.GetText(_database.GenreIndex[3].Name));
        Assert.Equal("Am", Database.GetText(_database.MusicalKeyIndex[4].Name));
        Assert.Equal("Pink", Database.GetText(_database.ColorIndex[1].Name));

        // The label name is stored in the long-ASCII DeviceSQL encoding.
        Assert.Equal("Warp Records", Database.GetText(_database.LabelIndex[6].Name));
        Assert.Equal(new[] { 6L }, _database.LabelNameIndex["Warp Records"]);

        Assert.Equal("/PIONEER/ARTWORK/00001/a.jpg", Database.GetText(_database.ArtworkIndex[8].Path));
    }

    [Fact]
    public void PlaylistsAreIndexedInEntryOrder()
    {
        // The playlist entries were written out of order (entry index 1 before entry index 0),
        // so this also verifies positional insertion.
        Assert.Equal(new[] { 2L, 1L }, _database.PlaylistIndex[10]);

        var folderEntries = _database.PlaylistFolderIndex[0];
        var entry = Assert.Single(folderEntries);
        Assert.NotNull(entry);
        Assert.Equal("My List", entry.Name);
        Assert.False(entry.IsFolder);
        Assert.Equal(10, entry.Id);
    }

    [Fact]
    public void HistoryPlaylistsAreIndexed()
    {
        Assert.Equal(11, _database.HistoryPlaylistNameIndex["HISTORY 001"]);
        Assert.Equal(new[] { 1L }, _database.HistoryPlaylistIndex[11]);
    }

    [Fact]
    public void GetTextDecodesAllThreeEncodings()
    {
        var shortAscii = ParseDeviceSqlString(PdbBuilder.ShortAsciiString("Hello"));
        Assert.Equal("Hello", Database.GetText(shortAscii));

        var longAscii = ParseDeviceSqlString(PdbBuilder.LongAsciiString("A somewhat longer label name"));
        Assert.Equal("A somewhat longer label name", Database.GetText(longAscii));

        var utf16 = ParseDeviceSqlString(PdbBuilder.LongUtf16String("Björk – Jóga"));
        Assert.Equal("Björk – Jóga", Database.GetText(utf16));

        var empty = ParseDeviceSqlString(PdbBuilder.ShortAsciiString(""));
        Assert.Equal("", Database.GetText(empty));
    }

    [Fact]
    public void TrackMetadataBuildsFromDatabase()
    {
        var reference = new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 1);
        var metadata = new TrackMetadata(reference, _database, null);

        Assert.Same(_database.TrackIndex[1], metadata.RawRow);
        Assert.Null(metadata.RawItems);
        Assert.Same(CdjStatus.TrackType.Rekordbox, metadata.TrackType);
        Assert.Equal("alpha song", metadata.Title);
        Assert.Equal(new SearchableItem(7, "Björk"), metadata.Artist);
        Assert.Equal(new SearchableItem(5, "Homogenic"), metadata.Album);
        Assert.Equal(new SearchableItem(6, "Warp Records"), metadata.Label);
        Assert.Equal(new SearchableItem(4, "Am"), metadata.Key);
        Assert.Equal(new SearchableItem(3, "IDM"), metadata.Genre);
        Assert.Equal(new ColorItem(1, "Pink"), metadata.Color);
        Assert.Equal(300, metadata.Duration);
        Assert.Equal(12800, metadata.Tempo);
        Assert.Equal(1997, metadata.Year);
        Assert.Equal(4, metadata.Rating);
        Assert.Equal(320, metadata.BitRate);
        Assert.Equal(8, metadata.ArtworkId);
        Assert.Equal("Nice track", metadata.Comment);
        Assert.Equal("2024-01-15", metadata.DateAdded);
        Assert.Null(metadata.CueList);
    }

    [Fact]
    public void TrackMetadataThrowsForUnknownTrack()
    {
        var reference = new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 999);
        Assert.Throws<KeyNotFoundException>(() => _ = new TrackMetadata(reference, _database, null));
    }

    /// <summary>
    /// Parses a stand-alone DeviceSQL string from its raw byte representation.
    /// </summary>
    private static RekordboxPdb.DeviceSqlString ParseDeviceSqlString(byte[] bytes) =>
        new(new Kaitai.KaitaiStream(bytes));
}
