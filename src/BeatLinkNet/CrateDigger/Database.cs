using System.Collections.ObjectModel;
using BeatLink.CrateDigger.Pdb;
using Kaitai;
using Microsoft.Extensions.Logging;

namespace BeatLink.CrateDigger;

/// <summary>
/// Parses rekordbox database <c>export.pdb</c> files, providing access to the information they contain.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's crate-digger <c>Database</c> (which delegates its table-scanning
/// plumbing to the package-private <c>DatabaseUtil</c> helper; that logic is folded into this
/// class), by James Elliott.
/// </remarks>
public class Database : IDisposable
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<Database>();

    /// <summary>
    /// The parsed database file, whose lazy structures can be traversed until <see cref="Dispose"/>
    /// is called.
    /// </summary>
    private readonly RekordboxPdb _pdb;

    /// <summary>
    /// The stream from which the database is parsed; closed by <see cref="Dispose"/>.
    /// </summary>
    private readonly KaitaiStream _stream;

    /// <summary>
    /// Holds the path of the file this database was constructed from.
    /// </summary>
    public string SourceFile { get; }

    /// <summary>
    /// <para>Construct a database access instance from the specified rekordbox export file.
    /// The file can be obtained either from the SD or USB media, or directly from a player
    /// using the Crate Digger file fetcher.</para>
    ///
    /// <para>Be sure to call <see cref="Dispose"/> when you are done using the parsed database
    /// to close the underlying file or users will be unable to unmount the drive holding
    /// it until they quit your program.</para>
    /// </summary>
    /// <param name="sourceFile">the path of an export.pdb file</param>
    /// <exception cref="IOException">if there is a problem reading the file</exception>
    /// <exception cref="InvalidOperationException">if a table which should be present in every
    /// database export cannot be found, or occurs more than once</exception>
    public Database(string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        SourceFile = sourceFile;
        _stream = new KaitaiStream(sourceFile);
        _pdb = new RekordboxPdb(false, _stream);

        var mutableTrackTitleIndex = NewStringIndex();
        var mutableTrackArtistIndex = new SortedDictionary<long, SortedSet<long>>();
        var mutableTrackAlbumIndex = new SortedDictionary<long, SortedSet<long>>();
        var mutableTrackGenreIndex = new SortedDictionary<long, SortedSet<long>>();
        TrackIndex = IndexTracks(mutableTrackTitleIndex, mutableTrackArtistIndex, mutableTrackAlbumIndex, mutableTrackGenreIndex);
        TrackTitleIndex = FreezeSecondaryIndex(mutableTrackTitleIndex);
        TrackAlbumIndex = FreezeSecondaryIndex(mutableTrackAlbumIndex);
        TrackArtistIndex = FreezeSecondaryIndex(mutableTrackArtistIndex);
        TrackGenreIndex = FreezeSecondaryIndex(mutableTrackGenreIndex);

        var mutableArtistNameIndex = NewStringIndex();
        ArtistIndex = IndexArtists(mutableArtistNameIndex);
        ArtistNameIndex = FreezeSecondaryIndex(mutableArtistNameIndex);

        var mutableColorNameIndex = NewStringIndex();
        ColorIndex = IndexColors(mutableColorNameIndex);
        ColorNameIndex = FreezeSecondaryIndex(mutableColorNameIndex);

        var mutableAlbumNameIndex = NewStringIndex();
        var mutableAlbumArtistIndex = new SortedDictionary<long, SortedSet<long>>();
        AlbumIndex = IndexAlbums(mutableAlbumNameIndex, mutableAlbumArtistIndex);
        AlbumNameIndex = FreezeSecondaryIndex(mutableAlbumNameIndex);
        AlbumArtistIndex = FreezeSecondaryIndex(mutableAlbumArtistIndex);

        var mutableLabelNameIndex = NewStringIndex();
        LabelIndex = IndexLabels(mutableLabelNameIndex);
        LabelNameIndex = FreezeSecondaryIndex(mutableLabelNameIndex);

        var mutableMusicalKeyNameIndex = NewStringIndex();
        MusicalKeyIndex = IndexKeys(mutableMusicalKeyNameIndex);
        MusicalKeyNameIndex = FreezeSecondaryIndex(mutableMusicalKeyNameIndex);

        var mutableGenreNameIndex = NewStringIndex();
        GenreIndex = IndexGenres(mutableGenreNameIndex);
        GenreNameIndex = FreezeSecondaryIndex(mutableGenreNameIndex);

        ArtworkIndex = IndexArtwork();

        PlaylistIndex = IndexPlaylists();
        PlaylistFolderIndex = IndexPlaylistFolders();

        HistoryPlaylistIndex = IndexHistoryPlaylists();
        HistoryPlaylistNameIndex = IndexHistoryPlaylistNames();
    }

    /// <summary>
    /// Creates a sorted map for a secondary index that is keyed by name, using the case-insensitive
    /// ordering the Java library gets from <c>String.CASE_INSENSITIVE_ORDER</c>.
    /// </summary>
    private static SortedDictionary<string, SortedSet<long>> NewStringIndex() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A map from track ID to the actual track row. Iteration order is unspecified, matching the
    /// hash map the Java library exposes.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.TrackRow> TrackIndex { get; }

    /// <summary>
    /// A sorted map from track title to the set of track IDs with that title.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<long>> TrackTitleIndex { get; }

    /// <summary>
    /// A sorted map from artist ID to the set of track IDs associated with that artist.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlySet<long>> TrackArtistIndex { get; }

    /// <summary>
    /// A sorted map from album ID to the set of track IDs associated with that album.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlySet<long>> TrackAlbumIndex { get; }

    /// <summary>
    /// A sorted map from genre ID to the set of track IDs associated with that genre.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlySet<long>> TrackGenreIndex { get; }

    /// <summary>
    /// A sorted map from history playlist name to the ID by which its entries can be found.
    /// </summary>
    public IReadOnlyDictionary<string, long> HistoryPlaylistNameIndex { get; }

    /// <summary>
    /// A map from playlist ID to the list of track IDs making up a history playlist.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlyList<long>> HistoryPlaylistIndex { get; }

    /// <summary>
    /// Parse and index all the tracks found in the database export.
    /// </summary>
    /// <param name="titleIndex">the sorted map in which the secondary track title index should be built</param>
    /// <param name="artistIndex">the map in which the secondary track artist index should be built</param>
    /// <param name="albumIndex">the map in which the secondary track album index should be built</param>
    /// <param name="genreIndex">the map in which the secondary track genre index should be built</param>
    /// <returns>the populated and unmodifiable primary track index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.TrackRow> IndexTracks(
        SortedDictionary<string, SortedSet<long>> titleIndex,
        SortedDictionary<long, SortedSet<long>> artistIndex,
        SortedDictionary<long, SortedSet<long>> albumIndex,
        SortedDictionary<long, SortedSet<long>> genreIndex)
    {
        var index = new Dictionary<long, RekordboxPdb.TrackRow>();

        IndexRows(RekordboxPdb.PageType.Tracks, row =>
        {
            // We found a track; index it by its ID.
            var trackRow = (RekordboxPdb.TrackRow)row;
            long id = trackRow.Id;
            index[id] = trackRow;

            // Index the track ID by title, artist (in all roles), album, and genre as well.
            var title = GetText(trackRow.Title);
            if (title.Length > 0)
            {
                AddToSecondaryIndex(titleIndex, title, id);
            }
            if (trackRow.ArtistId > 0)
            {
                AddToSecondaryIndex(artistIndex, trackRow.ArtistId, id);
            }
            if (trackRow.ComposerId > 0)
            {
                AddToSecondaryIndex(artistIndex, trackRow.ComposerId, id);
            }
            if (trackRow.OriginalArtistId > 0)
            {
                AddToSecondaryIndex(artistIndex, trackRow.OriginalArtistId, id);
            }
            if (trackRow.RemixerId > 0)
            {
                AddToSecondaryIndex(artistIndex, trackRow.RemixerId, id);
            }
            if (trackRow.AlbumId > 0)
            {
                AddToSecondaryIndex(albumIndex, trackRow.AlbumId, id);
            }
            if (trackRow.GenreId > 0)
            {
                AddToSecondaryIndex(genreIndex, trackRow.GenreId, id);
            }
        });

        Logger.LogInformation("Indexed {Count} Tracks.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.TrackRow>(index);
    }

    /// <summary>
    /// A map from artist ID to the actual artist row.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.ArtistRow> ArtistIndex { get; }

    /// <summary>
    /// A sorted map from artist name to the set of artist IDs with that name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<long>> ArtistNameIndex { get; }

    /// <summary>
    /// Parse and index all the artists found in the database export.
    /// </summary>
    /// <param name="nameIndex">the sorted map in which the secondary artist name index should be built</param>
    /// <returns>the populated and unmodifiable primary artist index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.ArtistRow> IndexArtists(
        SortedDictionary<string, SortedSet<long>> nameIndex)
    {
        var index = new Dictionary<long, RekordboxPdb.ArtistRow>();

        IndexRows(RekordboxPdb.PageType.Artists, row =>
        {
            var artistRow = (RekordboxPdb.ArtistRow)row;
            long id = artistRow.Id;
            index[id] = artistRow;

            // Index the artist ID by name as well.
            var name = GetText(artistRow.Name);
            if (name.Length > 0)
            {
                AddToSecondaryIndex(nameIndex, name, id);
            }
        });

        Logger.LogInformation("Indexed {Count} Artists.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.ArtistRow>(index);
    }

    /// <summary>
    /// A map from color ID to the actual color row.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.ColorRow> ColorIndex { get; }

    /// <summary>
    /// A sorted map from color name to the set of color IDs with that name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<long>> ColorNameIndex { get; }

    /// <summary>
    /// Parse and index all the colors found in the database export.
    /// </summary>
    /// <param name="nameIndex">the sorted map in which the secondary color name index should be built</param>
    /// <returns>the populated and unmodifiable primary color index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.ColorRow> IndexColors(
        SortedDictionary<string, SortedSet<long>> nameIndex)
    {
        var index = new Dictionary<long, RekordboxPdb.ColorRow>();

        IndexRows(RekordboxPdb.PageType.Colors, row =>
        {
            var colorRow = (RekordboxPdb.ColorRow)row;
            long id = colorRow.Id;
            index[id] = colorRow;

            // Index the color by name as well.
            var name = GetText(colorRow.Name);
            if (name.Length > 0)
            {
                AddToSecondaryIndex(nameIndex, name, id);
            }
        });

        Logger.LogInformation("Indexed {Count} Colors.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.ColorRow>(index);
    }

    /// <summary>
    /// A map from album ID to the actual album row.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.AlbumRow> AlbumIndex { get; }

    /// <summary>
    /// A sorted map from album name to the set of album IDs with that name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<long>> AlbumNameIndex { get; }

    /// <summary>
    /// A sorted map from artist ID to the set of album IDs associated with that artist.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlySet<long>> AlbumArtistIndex { get; }

    /// <summary>
    /// Parse and index all the albums found in the database export.
    /// </summary>
    /// <param name="nameIndex">the sorted map in which the secondary album name index should be built</param>
    /// <param name="artistIndex">the map in which the secondary album artist index should be built</param>
    /// <returns>the populated and unmodifiable primary album index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.AlbumRow> IndexAlbums(
        SortedDictionary<string, SortedSet<long>> nameIndex,
        SortedDictionary<long, SortedSet<long>> artistIndex)
    {
        var index = new Dictionary<long, RekordboxPdb.AlbumRow>();

        IndexRows(RekordboxPdb.PageType.Albums, row =>
        {
            var albumRow = (RekordboxPdb.AlbumRow)row;
            long id = albumRow.Id;
            index[id] = albumRow;

            // Index the album ID by name and artist as well.
            var name = GetText(albumRow.Name);
            if (name.Length > 0)
            {
                AddToSecondaryIndex(nameIndex, name, id);
            }
            if (albumRow.ArtistId > 0)
            {
                AddToSecondaryIndex(artistIndex, albumRow.ArtistId, id);
            }
        });

        Logger.LogInformation("Indexed {Count} Albums.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.AlbumRow>(index);
    }

    /// <summary>
    /// A map from label ID to the actual label row.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.LabelRow> LabelIndex { get; }

    /// <summary>
    /// A sorted map from label name to the set of label IDs with that name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<long>> LabelNameIndex { get; }

    /// <summary>
    /// Parse and index all the labels found in the database export.
    /// </summary>
    /// <param name="nameIndex">the sorted map in which the secondary label name index should be built</param>
    /// <returns>the populated and unmodifiable primary label index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.LabelRow> IndexLabels(
        SortedDictionary<string, SortedSet<long>> nameIndex)
    {
        var index = new Dictionary<long, RekordboxPdb.LabelRow>();

        IndexRows(RekordboxPdb.PageType.Labels, row =>
        {
            var labelRow = (RekordboxPdb.LabelRow)row;
            long id = labelRow.Id;
            index[id] = labelRow;

            // Index the label ID by name as well.
            var name = GetText(labelRow.Name);
            if (name.Length > 0)
            {
                AddToSecondaryIndex(nameIndex, name, id);
            }
        });

        Logger.LogInformation("Indexed {Count} Labels.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.LabelRow>(index);
    }

    /// <summary>
    /// A map from (musical) key ID to the actual key row.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.KeyRow> MusicalKeyIndex { get; }

    /// <summary>
    /// A sorted map from musical key name to the set of musical key IDs with that name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<long>> MusicalKeyNameIndex { get; }

    /// <summary>
    /// Parse and index all the musical keys found in the database export.
    /// </summary>
    /// <param name="nameIndex">the sorted map in which the secondary musical key name index should be built</param>
    /// <returns>the populated and unmodifiable primary musical key index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.KeyRow> IndexKeys(
        SortedDictionary<string, SortedSet<long>> nameIndex)
    {
        var index = new Dictionary<long, RekordboxPdb.KeyRow>();

        IndexRows(RekordboxPdb.PageType.Keys, row =>
        {
            var keyRow = (RekordboxPdb.KeyRow)row;
            long id = keyRow.Id;
            index[id] = keyRow;

            // Index the musical key ID by name as well.
            var name = GetText(keyRow.Name);
            if (name.Length > 0)
            {
                AddToSecondaryIndex(nameIndex, name, id);
            }
        });

        Logger.LogInformation("Indexed {Count} Musical Keys.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.KeyRow>(index);
    }

    /// <summary>
    /// A map from genre ID to the actual genre row.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.GenreRow> GenreIndex { get; }

    /// <summary>
    /// A sorted map from genre name to the set of genre IDs with that name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<long>> GenreNameIndex { get; }

    /// <summary>
    /// Parse and index all the genres found in the database export.
    /// </summary>
    /// <param name="nameIndex">the sorted map in which the secondary genre name index should be built</param>
    /// <returns>the populated and unmodifiable primary genre index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.GenreRow> IndexGenres(
        SortedDictionary<string, SortedSet<long>> nameIndex)
    {
        var index = new Dictionary<long, RekordboxPdb.GenreRow>();

        IndexRows(RekordboxPdb.PageType.Genres, row =>
        {
            var genreRow = (RekordboxPdb.GenreRow)row;
            long id = genreRow.Id;
            index[id] = genreRow;

            // Index the genre by name as well.
            var name = GetText(genreRow.Name);
            if (name.Length > 0)
            {
                AddToSecondaryIndex(nameIndex, name, id);
            }
        });

        Logger.LogInformation("Indexed {Count} Genres.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.GenreRow>(index);
    }

    /// <summary>
    /// A map from artwork ID to the artwork row containing its file path.
    /// </summary>
    public IReadOnlyDictionary<long, RekordboxPdb.ArtworkRow> ArtworkIndex { get; }

    /// <summary>
    /// Parse and index all the artwork paths found in the database export.
    /// </summary>
    /// <returns>the populated and unmodifiable artwork path index</returns>
    private IReadOnlyDictionary<long, RekordboxPdb.ArtworkRow> IndexArtwork()
    {
        var index = new Dictionary<long, RekordboxPdb.ArtworkRow>();

        IndexRows(RekordboxPdb.PageType.Artwork, row =>
        {
            var artworkRow = (RekordboxPdb.ArtworkRow)row;
            index[artworkRow.Id] = artworkRow;
        });

        Logger.LogInformation("Indexed {Count} Artwork Paths.", index.Count);
        return new ReadOnlyDictionary<long, RekordboxPdb.ArtworkRow>(index);
    }

    /// <summary>
    /// A map from playlist ID to the list of track IDs making up a playlist.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlyList<long>> PlaylistIndex { get; }

    /// <summary>
    /// Playlist folders can either contain playlists or other folders. Each
    /// entry has a flag explaining how the ID should be interpreted.
    /// </summary>
    public sealed class PlaylistFolderEntry
    {
        /// <summary>The name by which this playlist or folder is known.</summary>
        public string Name { get; }

        /// <summary>Indicates whether this entry links to another folder or a playlist.</summary>
        public bool IsFolder { get; }

        /// <summary>The ID of the folder or playlist linked to by this entry.</summary>
        public long Id { get; }

        /// <summary>
        /// Constructor simply sets the immutable properties.
        /// </summary>
        /// <param name="name">the name by which this folder is known</param>
        /// <param name="isFolder">indicates whether this entry links to another folder or a playlist</param>
        /// <param name="id">the id of the folder or playlist linked to by this entry</param>
        internal PlaylistFolderEntry(string name, bool isFolder, long id)
        {
            Name = name;
            IsFolder = isFolder;
            Id = id;
        }

        /// <inheritdoc/>
        public override string ToString() => $"PlaylistFolderEntry[name:{Name}, id:{Id}, isFolder? {IsFolder}]";
    }

    /// <summary>
    /// A map from folder ID to the list of playlists or folders in a playlist folder. Elements can
    /// be <c>null</c> if the sort orders recorded in the file were not contiguous.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlyList<PlaylistFolderEntry?>> PlaylistFolderIndex { get; }

    /// <summary>
    /// Parse and index all the playlists found in the database export.
    /// </summary>
    /// <returns>the populated and unmodifiable playlist index</returns>
    private IReadOnlyDictionary<long, IReadOnlyList<long>> IndexPlaylists()
    {
        var mutable = new Dictionary<long, List<long>>();
        IndexRows(RekordboxPdb.PageType.PlaylistEntries, row =>
        {
            var entryRow = (RekordboxPdb.PlaylistEntryRow)row;
            if (!mutable.TryGetValue(entryRow.PlaylistId, out var playlist))
            {
                playlist = [];
                mutable[entryRow.PlaylistId] = playlist;
            }
            while (playlist.Count <= entryRow.EntryIndex)
            {
                // Grow to hold the new entry we are going to set.
                playlist.Add(0L);
            }
            playlist[(int)entryRow.EntryIndex] = entryRow.TrackId;
        });
        // Freeze the finished lists and overall map.
        var result = mutable.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<long>)pair.Value.AsReadOnly());
        Logger.LogInformation("Indexed {Count} playlists.", result.Count);
        return new ReadOnlyDictionary<long, IReadOnlyList<long>>(result);
    }

    /// <summary>
    /// Parse and index the tree that organizes playlists into folders found in the database export.
    /// </summary>
    /// <returns>the populated and unmodifiable playlist folder index</returns>
    private IReadOnlyDictionary<long, IReadOnlyList<PlaylistFolderEntry?>> IndexPlaylistFolders()
    {
        var mutable = new Dictionary<long, List<PlaylistFolderEntry?>>();
        IndexRows(RekordboxPdb.PageType.PlaylistTree, row =>
        {
            var treeRow = (RekordboxPdb.PlaylistTreeRow)row;
            if (!mutable.TryGetValue(treeRow.ParentId, out var parent))
            {
                parent = [];
                mutable[treeRow.ParentId] = parent;
            }
            while (parent.Count <= treeRow.SortOrder)
            {
                // Grow to hold the new entry we are going to set.
                parent.Add(null);
            }
            parent[(int)treeRow.SortOrder] = new PlaylistFolderEntry(GetText(treeRow.Name),
                treeRow.IsFolder, treeRow.Id);
        });
        // Freeze the finished lists and overall map.
        var result = mutable.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<PlaylistFolderEntry?>)pair.Value.AsReadOnly());
        Logger.LogInformation("Indexed {Count} playlist folders.", result.Count);
        return new ReadOnlyDictionary<long, IReadOnlyList<PlaylistFolderEntry?>>(result);
    }

    /// <summary>
    /// Parse and index the names of all available history playlists.
    /// </summary>
    /// <returns>a map sorted by the history playlist names identifying the IDs by which their
    /// entries can be found</returns>
    private IReadOnlyDictionary<string, long> IndexHistoryPlaylistNames()
    {
        // The Java library uses a natural-ordering TreeMap here, so the ordinal comparer is used
        // rather than the case-insensitive one used by the other name indices.
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        IndexRows(RekordboxPdb.PageType.HistoryPlaylists, row =>
        {
            var historyRow = (RekordboxPdb.HistoryPlaylistRow)row;
            result[GetText(historyRow.Name)] = historyRow.Id;
        });
        Logger.LogInformation("Indexed {Count} history playlist names.", result.Count);
        return new ReadOnlyDictionary<string, long>(result);
    }

    /// <summary>
    /// Parse and index all the history playlists found in the database export.
    /// </summary>
    /// <returns>the populated and unmodifiable history playlist index</returns>
    private IReadOnlyDictionary<long, IReadOnlyList<long>> IndexHistoryPlaylists()
    {
        var mutable = new Dictionary<long, List<long>>();
        IndexRows(RekordboxPdb.PageType.HistoryEntries, row =>
        {
            var entryRow = (RekordboxPdb.HistoryEntryRow)row;
            if (!mutable.TryGetValue(entryRow.PlaylistId, out var playlist))
            {
                playlist = [];
                mutable[entryRow.PlaylistId] = playlist;
            }
            while (playlist.Count <= entryRow.EntryIndex)
            {
                // Grow to hold the new entry we are going to set.
                playlist.Add(0L);
            }
            playlist[(int)entryRow.EntryIndex] = entryRow.TrackId;
        });
        // Freeze the finished lists and overall map.
        var result = mutable.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<long>)pair.Value.AsReadOnly());
        Logger.LogInformation("Indexed {Count} history playlists.", result.Count);
        return new ReadOnlyDictionary<long, IReadOnlyList<long>>(result);
    }

    /// <summary>
    /// Parse and index all the rows found in a particular <c>export.pdb</c> table. This method
    /// performs a scan of the specified table, passing all rows that are encountered to a delegate
    /// that knows what to do with them. (Ported from crate-digger's <c>DatabaseUtil.indexRows</c>.)
    /// </summary>
    /// <param name="type">the type of table present in export.pdb files to be scanned and parsed</param>
    /// <param name="handler">the code that knows how to index that kind of row</param>
    /// <exception cref="InvalidOperationException">if there is more than (or less than) one table
    /// of that type in the file</exception>
    private void IndexRows(RekordboxPdb.PageType type, Action<KaitaiStruct> handler)
    {
        var done = false;
        foreach (var table in _pdb.Tables)
        {
            if (table.Type == type)
            {
                if (done)
                {
                    throw new InvalidOperationException("More than one table found with type " + type);
                }
                var lastIndex = table.LastPage.Index;  // This is how we know when to stop.
                var currentRef = table.FirstPage;
                bool moreLeft;
                do
                {
                    var page = currentRef.Body;

                    // Process only ordinary data pages.
                    if (page.IsDataPage)
                    {
                        foreach (var rowGroup in page.RowGroups)
                        {
                            foreach (var rowRef in rowGroup.Rows)
                            {
                                if (rowRef.Present)
                                {
                                    // We found a row, pass it to the handler to be indexed appropriately.
                                    handler(rowRef.Body);
                                }
                            }
                        }
                    }

                    // Was this the final page in the table? If so, stop, otherwise move on to the next page.
                    moreLeft = currentRef.Index != lastIndex;
                    if (moreLeft)
                    {
                        currentRef = page.NextPage;
                    }
                } while (moreLeft);
                done = true;
            }
        }

        if (!done)
        {
            throw new InvalidOperationException("No table found of type " + type);
        }
    }

    /// <summary>
    /// Adds a row ID to a secondary index which is sorted by some other attribute of the record
    /// (for example, add a track ID to the title index, so the track can be found by title).
    /// </summary>
    /// <typeparam name="TKey">the type of the key (often string, but may be long, e.g. to index
    /// tracks by artist ID)</typeparam>
    /// <param name="index">the secondary index, which holds all the row IDs that have the specified key</param>
    /// <param name="key">the secondary index value by which this row can be looked up</param>
    /// <param name="id">the ID of the row to index under the specified key</param>
    private static void AddToSecondaryIndex<TKey>(SortedDictionary<TKey, SortedSet<long>> index, TKey key, long id)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var existingIds))
        {
            existingIds = [];
            index[key] = existingIds;
        }
        existingIds.Add(id);
    }

    /// <summary>
    /// Protects a secondary index against further changes once we have finished indexing all the
    /// rows that are going into it. The sorted iteration order of the underlying map and sets is
    /// preserved by the read-only view.
    /// </summary>
    /// <typeparam name="TKey">the type of the key (often string, but may be long, e.g. to index
    /// tracks by artist ID)</typeparam>
    /// <param name="index">the index that should no longer be modified</param>
    /// <returns>an unmodifiable top-level view of the unmodifiable children</returns>
    private static IReadOnlyDictionary<TKey, IReadOnlySet<long>> FreezeSecondaryIndex<TKey>(
        SortedDictionary<TKey, SortedSet<long>> index)
        where TKey : notnull
    {
        var frozen = new SortedDictionary<TKey, IReadOnlySet<long>>(index.Comparer);
        foreach (var pair in index)
        {
            frozen[pair.Key] = pair.Value;
        }
        return new ReadOnlyDictionary<TKey, IReadOnlySet<long>>(frozen);
    }

    /// <summary>
    /// Close the file underlying the parsed database. This needs to be called if you want to be
    /// able to unmount the media on which that file resides, but once it is done, you can no
    /// longer access lazy elements within the database which have not already been parsed.
    /// </summary>
    public void Dispose()
    {
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Helper function to extract the text value from one of the strings found in the database,
    /// which have a variety of obscure representations.
    /// </summary>
    /// <param name="deviceString">the string-encoding structure</param>
    /// <returns>the text it contains, which may have zero length, but will never be <c>null</c></returns>
    /// <exception cref="ArgumentNullException">if <paramref name="deviceString"/> is <c>null</c></exception>
    public static string GetText(RekordboxPdb.DeviceSqlString deviceString)
    {
        ArgumentNullException.ThrowIfNull(deviceString);
        string? text = deviceString.Body switch
        {
            RekordboxPdb.DeviceSqlShortAscii shortAscii => shortAscii.Text,
            RekordboxPdb.DeviceSqlLongAscii longAscii => longAscii.Text,
            RekordboxPdb.DeviceSqlLongUtf16le longUtf16 => longUtf16.Text,
            _ => null
        };
        if (text != null)
        {
            return text;
        }
        Logger.LogWarning("Received unusable DeviceSqlString, returning empty string; lengthAndKind: {LengthAndKind}",
            deviceString.LengthAndKind);
        return "";
    }
}
