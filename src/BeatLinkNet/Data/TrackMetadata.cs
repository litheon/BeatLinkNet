using BeatLink.CrateDigger.Pdb;
using BeatLink.DbServer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Database = BeatLink.CrateDigger.Database;

namespace BeatLink.Data;

/// <summary>
/// Represents rekordbox metadata (title, artist, etc.) about tracks loaded into players on a
/// DJ Link network.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>TrackMetadata</c>, by James Elliott.
/// </remarks>
public class TrackMetadata
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<TrackMetadata>();

    /// <summary>The unique track identifier that was used to request this track metadata.</summary>
    public DataReference TrackReference { get; }

    /// <summary>The type of track described by this metadata.</summary>
    public CdjStatus.TrackType TrackType { get; }

    /// <summary>
    /// Tracks when this metadata was created (as a monotonic nanosecond timestamp, see
    /// <see cref="Util.CurrentTimestampNanos"/>), which can be used to know how long a finder
    /// should wait for a track to be analyzed by a CDJ-3000 before giving up on obtaining other
    /// metadata for it.
    /// </summary>
    public long Timestamp { get; } = Util.CurrentTimestampNanos();

    /// <summary>
    /// The raw dbserver messages containing the metadata when it was read over the network. Can be
    /// used to analyze fields that have not yet been reliably understood, and was previously used
    /// for storing the metadata in a file. Will be <c>null</c> once metadata can also be
    /// constructed from rekordbox database exports (arriving in a later phase) and was obtained
    /// that way.
    /// </summary>
    public IReadOnlyList<Message>? RawItems { get; }

    /// <summary>
    /// Constructor for when reading from a Device Library Plus SQLite export database.
    /// Finds the desired track in the exported database, along with all the related records,
    /// and sets all the interpreted properties based on the queried database structures.
    /// (Corresponds to the Java JDBC constructor <c>TrackMetadata(DataReference, Connection,
    /// CueList)</c>; experimental, like its Java counterpart.)
    /// </summary>
    /// <param name="reference">the unique track reference that was used to request this track metadata</param>
    /// <param name="connection">the open connection to the exported database</param>
    /// <param name="cueList">the cues associated with the track, if any</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="connection"/> is <c>null</c></exception>
    /// <exception cref="KeyNotFoundException">if the specified track is not found in the database</exception>
    /// <exception cref="SqliteException">if there is a problem querying the database</exception>
    public TrackMetadata(DataReference reference, SqliteConnection connection, CueList? cueList)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        RawRow = null;    // We did not create this from a PDB file.
        RawItems = null;  // Nor did it come from a DeviceSQL dbserver network connection.
        TrackReference = reference;
        TrackType = CdjStatus.TrackType.Rekordbox;  // These are the only kind of tracks in a rekordbox database.
        CueList = cueList;

        int artistId, originalArtistId, remixerId, albumId, labelId, keyId, genreId, colorId;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select * from content where content_id = $id";
            command.Parameters.AddWithValue("$id", reference.RekordboxId);
            using var trackSet = command.ExecuteReader();
            if (!trackSet.Read())
            {
                throw new KeyNotFoundException("Track " + reference.RekordboxId + " not found in export database");
            }
            ArtworkId = GetIntColumn(trackSet, "image_id");
            Title = GetStringColumn(trackSet, "title");
            Duration = GetIntColumn(trackSet, "length");
            Tempo = GetIntColumn(trackSet, "bpmx100");
            Comment = GetStringColumn(trackSet, "djComment");
            DateAdded = GetStringColumn(trackSet, "dateAdded");
            BitRate = GetIntColumn(trackSet, "bitrate");
            Year = GetIntColumn(trackSet, "releaseYear");

            // Note the IDs of any related rows that are supposed to be present, so we can load
            // them once this reader has been closed.
            artistId = GetIntColumn(trackSet, "artist_id_artist");
            originalArtistId = GetIntColumn(trackSet, "artist_id_originalArtist");
            remixerId = GetIntColumn(trackSet, "artist_id_remixer");
            albumId = GetIntColumn(trackSet, "album_id");
            labelId = GetIntColumn(trackSet, "label_id");
            keyId = GetIntColumn(trackSet, "key_id");
            genreId = GetIntColumn(trackSet, "genre_id");
            colorId = GetIntColumn(trackSet, "color_id");
        }

        // And load any related rows that are supposed to be present.
        Artist = ExtractRelatedName(connection, "artist", artistId);
        OriginalArtist = ExtractRelatedName(connection, "artist", originalArtistId);
        Remixer = ExtractRelatedName(connection, "artist", remixerId);

        Album = ExtractRelatedName(connection, "album", albumId);
        Label = ExtractRelatedName(connection, "label", labelId);
        Key = ExtractRelatedName(connection, "key", keyId);
        Genre = ExtractRelatedName(connection, "genre", genreId);

        var colorTemp = ExtractRelatedName(connection, "color", colorId);
        if (colorTemp != null)
        {
            Color = new ColorItem(colorTemp.Id, colorTemp.Label);
        }
    }

    /// <summary>
    /// Reads an integer column from a Device Library Plus query result, treating SQL
    /// <c>NULL</c> as zero the way the Java JDBC <c>getInt</c> method does.
    /// </summary>
    /// <param name="reader">the open data reader positioned on the row of interest</param>
    /// <param name="name">the name of the column to read</param>
    /// <returns>the column value, or zero if it was <c>NULL</c></returns>
    private static int GetIntColumn(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    /// <summary>
    /// Reads a string column from a Device Library Plus query result, treating SQL
    /// <c>NULL</c> as an empty string (our string properties are never <c>null</c>).
    /// </summary>
    /// <param name="reader">the open data reader positioned on the row of interest</param>
    /// <param name="name">the name of the column to read</param>
    /// <returns>the column value, or an empty string if it was <c>NULL</c></returns>
    private static string GetStringColumn(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    }

    /// <summary>
    /// Helper method to load a related row name when loading a track using SQLite.
    /// </summary>
    /// <param name="connection">used to talk to the database</param>
    /// <param name="table">the name of the table in which the row should be sought (always one of
    /// a fixed set of trusted literals, never user input)</param>
    /// <param name="id">the ID of the related row, or zero if none is expected</param>
    /// <returns>the loaded item, if one was found</returns>
    private static SearchableItem? ExtractRelatedName(SqliteConnection connection, string table, int id)
    {
        if (id != 0)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"select name from \"{table}\" where \"{table}_id\" = $id";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteScalar() is string name)
                {
                    return new SearchableItem(id, name);
                }
                Logger.LogWarning("{Table} row with id {Id} not found when loading track", table, id);
            }
            catch (SqliteException e)
            {
                Logger.LogWarning(e, "{Table} row with id {Id} not found when loading track", table, id);
            }
        }
        return null;
    }

    /// <summary>
    /// The raw row within a rekordbox database export from which the metadata was created, if it
    /// was obtained by Crate Digger from a PDB file. Can be used to access fields that are not
    /// otherwise available; will be <c>null</c> when the metadata came from a dbserver response.
    /// </summary>
    public RekordboxPdb.TrackRow? RawRow { get; }

    /// <summary>
    /// Constructor for when reading from a rekordbox database export downloaded from a player.
    /// Finds the row for the specified track in the exported database, along with all the related
    /// rows, and sets all the interpreted properties based on the parsed database structures.
    /// </summary>
    /// <param name="reference">the unique track reference for which track metadata is desired</param>
    /// <param name="database">the database from which the row was loaded, in order to find related rows</param>
    /// <param name="cueList">the cues associated with the track, if any</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="database"/> is <c>null</c></exception>
    /// <exception cref="KeyNotFoundException">if the specified track is not found in the database</exception>
    public TrackMetadata(DataReference reference, Database database, CueList? cueList)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(database);
        RawItems = null;  // We did not create this from a dbserver response.
        if (!database.TrackIndex.TryGetValue(reference.RekordboxId, out var rawRow))
        {
            throw new KeyNotFoundException("Track " + reference.RekordboxId + " not found in PDB file.");
        }
        RawRow = rawRow;
        TrackReference = reference;
        TrackType = CdjStatus.TrackType.Rekordbox;  // These are the only kind of tracks in a rekordbox database.
        CueList = cueList;

        Title = Database.GetText(rawRow.Title);
        ArtworkId = (int)rawRow.ArtworkId;

        // Look up the track artist, if there is one.
        if (database.ArtistIndex.TryGetValue(rawRow.ArtistId, out var artistRow))
        {
            Artist = new SearchableItem((int)artistRow.Id, Database.GetText(artistRow.Name));
        }

        // Look up the original artist, if there is one.
        if (database.ArtistIndex.TryGetValue(rawRow.OriginalArtistId, out artistRow))
        {
            OriginalArtist = new SearchableItem((int)artistRow.Id, Database.GetText(artistRow.Name));
        }

        // Look up the remixer, if there is one. (Note: like the Java library, this looks up the
        // original artist ID rather than the remixer ID, faithfully preserving upstream behavior.)
        if (database.ArtistIndex.TryGetValue(rawRow.OriginalArtistId, out artistRow))
        {
            Remixer = new SearchableItem((int)artistRow.Id, Database.GetText(artistRow.Name));
        }

        // Look up the album, if there is one.
        if (database.AlbumIndex.TryGetValue(rawRow.AlbumId, out var albumRow))
        {
            Album = new SearchableItem((int)albumRow.Id, Database.GetText(albumRow.Name));
        }

        // Look up the label, if there is one.
        if (database.LabelIndex.TryGetValue(rawRow.LabelId, out var labelRow))
        {
            Label = new SearchableItem((int)labelRow.Id, Database.GetText(labelRow.Name));
        }

        Duration = rawRow.Duration;
        Tempo = (int)rawRow.Tempo;
        Comment = Database.GetText(rawRow.Comment);

        // Look up the musical key, if there is one.
        if (database.MusicalKeyIndex.TryGetValue(rawRow.KeyId, out var keyRow))
        {
            Key = new SearchableItem((int)keyRow.Id, Database.GetText(keyRow.Name));
        }

        Rating = rawRow.Rating;

        // Associate the track color, if there is one.
        if (database.ColorIndex.TryGetValue(rawRow.ColorId, out var colorRow))
        {
            Color = new ColorItem(rawRow.ColorId, Database.GetText(colorRow.Name));
        }
        else
        {
            Color = new ColorItem(rawRow.ColorId, "");  // For backwards compatibility with "No Color".
        }

        // Look up the genre, if there is one.
        if (database.GenreIndex.TryGetValue(rawRow.GenreId, out var genreRow))
        {
            Genre = new SearchableItem((int)genreRow.Id, Database.GetText(genreRow.Name));
        }

        DateAdded = Database.GetText(rawRow.DateAdded);
        BitRate = (int)rawRow.Bitrate;
        Year = rawRow.Year;
    }

    /// <summary>The album on which the track was released.</summary>
    public SearchableItem? Album { get; private set; }

    /// <summary>The artist that created the track.</summary>
    public SearchableItem? Artist { get; private set; }

    /// <summary>The color assigned to the track.</summary>
    public ColorItem? Color { get; private set; }

    /// <summary>The comment assigned to the track.</summary>
    public string Comment { get; private set; } = "";

    /// <summary>
    /// The date the track was added to the collection, in the form "YYYY-MM-DD". This information
    /// seems to propagate from iTunes.
    /// </summary>
    public string DateAdded { get; private set; } = "";

    /// <summary>The length, in seconds, of the track, when played at 100% pitch.</summary>
    public int Duration { get; private set; }

    /// <summary>The musical genre of the track.</summary>
    public SearchableItem? Genre { get; private set; }

    /// <summary>The dominant key of the track.</summary>
    public SearchableItem? Key { get; private set; }

    /// <summary>The recording label that issued the track.</summary>
    public SearchableItem? Label { get; private set; }

    /// <summary>The artist that originally recorded the track.</summary>
    public SearchableItem? OriginalArtist { get; private set; }

    /// <summary>The rating assigned to the track.</summary>
    public int Rating { get; private set; }

    /// <summary>The producer who remixed the track.</summary>
    public SearchableItem? Remixer { get; private set; }

    /// <summary>The initial tempo of the track, BPM times 100.</summary>
    public int Tempo { get; private set; }

    /// <summary>The year the track was created, or 0 if rekordbox is not indexing by year.</summary>
    public int Year { get; private set; }

    /// <summary>The audio bit rate of the track, in kilobits per second, or 0 if unknown or variable.</summary>
    public int BitRate { get; private set; }

    /// <summary>The title of the track.</summary>
    public string Title { get; private set; } = "";

    /// <summary>
    /// The value by which the track's artwork image can be requested. Will be zero for tracks
    /// that have no artwork.
    /// </summary>
    public int ArtworkId { get; private set; }

    /// <summary>
    /// The cue list, if any, associated with the track. Will be <c>null</c> if no hot cues, loops,
    /// or memory points are found in the track.
    /// </summary>
    public CueList? CueList { get; }

    /// <summary>
    /// Creates a searchable item that represents a metadata field found for a track.
    /// </summary>
    /// <param name="menuItem">the rendered menu item containing the searchable metadata field</param>
    /// <returns>the searchable metadata field</returns>
    private static SearchableItem BuildSearchableItem(Message menuItem) =>
        new((int)((NumberField)menuItem.Arguments[1]).Value,
            ((StringField)menuItem.Arguments[3]).Value);

    /// <summary>
    /// Constructor for when reading from the network or from a file. Sets all the interpreted
    /// properties based on the received response messages.
    /// </summary>
    /// <param name="reference">the unique track reference that was used to request this track metadata</param>
    /// <param name="trackType">the type of track described by this metadata</param>
    /// <param name="items">the menu item responses that were received in response to the render
    /// menu request</param>
    /// <param name="cueList">the cues associated with the track, if any</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/>,
    /// <paramref name="trackType"/>, or <paramref name="items"/> is <c>null</c></exception>
    public TrackMetadata(DataReference reference, CdjStatus.TrackType trackType,
        IEnumerable<Message> items, CueList? cueList)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(trackType);
        ArgumentNullException.ThrowIfNull(items);
        TrackReference = reference;
        TrackType = trackType;
        CueList = cueList;
        var itemsCopy = new List<Message>(items);
        RawItems = itemsCopy;
        foreach (var item in itemsCopy)
        {
            ParseMetadataItem(item);
        }
    }

    /// <summary>
    /// Processes one of the menu responses that jointly constitute the track metadata, updating
    /// our properties accordingly.
    /// </summary>
    /// <param name="item">the menu response to be considered</param>
    private void ParseMetadataItem(Message item)
    {
        var itemType = item.GetMenuItemType();
        if (itemType == Message.MenuItemType.TrackTitle)
        {
            Title = ((StringField)item.Arguments[3]).Value;
            ArtworkId = (int)((NumberField)item.Arguments[8]).Value;
        }
        else if (itemType == Message.MenuItemType.Artist)
        {
            Artist = BuildSearchableItem(item);
        }
        else if (itemType == Message.MenuItemType.OriginalArtist)
        {
            OriginalArtist = BuildSearchableItem(item);
        }
        else if (itemType == Message.MenuItemType.Remixer)
        {
            // Note: the Java version accidentally falls through here and also assigns the album
            // field; that fall-through bug is not replicated in this port.
            Remixer = BuildSearchableItem(item);
        }
        else if (itemType == Message.MenuItemType.AlbumTitle)
        {
            Album = BuildSearchableItem(item);
        }
        else if (itemType == Message.MenuItemType.Label)
        {
            Label = BuildSearchableItem(item);
        }
        else if (itemType == Message.MenuItemType.Duration)
        {
            Duration = (int)((NumberField)item.Arguments[1]).Value;
        }
        else if (itemType == Message.MenuItemType.Tempo)
        {
            Tempo = (int)((NumberField)item.Arguments[1]).Value;
        }
        else if (itemType == Message.MenuItemType.Comment)
        {
            Comment = ((StringField)item.Arguments[3]).Value;
        }
        else if (itemType == Message.MenuItemType.Key)
        {
            Key = BuildSearchableItem(item);
        }
        else if (itemType == Message.MenuItemType.Rating)
        {
            Rating = (int)((NumberField)item.Arguments[1]).Value;
        }
        else if (itemType == Message.MenuItemType.ColorNone || itemType == Message.MenuItemType.ColorAqua ||
                 itemType == Message.MenuItemType.ColorBlue || itemType == Message.MenuItemType.ColorGreen ||
                 itemType == Message.MenuItemType.ColorOrange || itemType == Message.MenuItemType.ColorPink ||
                 itemType == Message.MenuItemType.ColorPurple || itemType == Message.MenuItemType.ColorRed ||
                 itemType == Message.MenuItemType.ColorYellow)
        {
            var colorId = (int)((NumberField)item.Arguments[1]).Value;
            var label = ((StringField)item.Arguments[3]).Value;
            Color = new ColorItem(colorId, label);
        }
        else if (itemType == Message.MenuItemType.Genre)
        {
            Genre = BuildSearchableItem(item);
        }
        else if (itemType == Message.MenuItemType.DateAdded)
        {
            DateAdded = ((StringField)item.Arguments[3]).Value;
        }
        else if (itemType == Message.MenuItemType.Year)
        {
            Year = (int)((NumberField)item.Arguments[1]).Value;
        }
        else if (itemType == Message.MenuItemType.BitRate)
        {
            BitRate = (int)((NumberField)item.Arguments[1]).Value;
        }
        else
        {
            Logger.LogWarning("Ignoring track metadata item with unknown type: {Item}", item);
        }
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(obj, this))
        {
            return true;
        }
        if (obj == null || obj.GetType() != GetType())
        {
            return false;
        }
        var other = (TrackMetadata)obj;
        return TrackReference.Equals(other.TrackReference) &&
            ReferenceEquals(TrackType, other.TrackType) &&
            Duration == other.Duration &&
            Rating == other.Rating &&
            Tempo == other.Tempo &&
            ArtworkId == other.ArtworkId &&
            Equals(Album, other.Album) &&
            Equals(Artist, other.Artist) &&
            Equals(Color, other.Color) &&
            Comment == other.Comment &&
            DateAdded == other.DateAdded &&
            Equals(Genre, other.Genre) &&
            Equals(Key, other.Key) &&
            Equals(Label, other.Label) &&
            Equals(OriginalArtist, other.OriginalArtist) &&
            Equals(Remixer, other.Remixer) &&
            Title == other.Title &&
            Equals(CueList, other.CueList);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Album);
        hash.Add(Artist);
        hash.Add(BitRate);
        hash.Add(Color);
        hash.Add(Comment);
        hash.Add(DateAdded);
        hash.Add(Duration);
        hash.Add(Genre);
        hash.Add(Key);
        hash.Add(Label);
        hash.Add(OriginalArtist);
        hash.Add(Rating);
        hash.Add(Remixer);
        hash.Add(Tempo);
        hash.Add(Title);
        hash.Add(ArtworkId);
        hash.Add(CueList);
        hash.Add(TrackReference);
        hash.Add(TrackType);
        hash.Add(Year);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Track Metadata[trackReference: {TrackReference}, TrackType: {TrackType}, Title: {Title}, " +
        $"Artist: {Artist}, Album: {Album}, Remixer: {Remixer}, Label: {Label}, " +
        $"Original Artist: {OriginalArtist}, Date Added: {DateAdded}, Duration: {Duration}, " +
        $"Tempo: {Tempo}, Comment: {Comment}, Key: {Key}, Rating: {Rating}, Color: {Color}, " +
        $"Genre: {Genre}, Year: {Year}, Bit Rate: {BitRate}, Artwork ID: {ArtworkId}, Cue List: {CueList}]";
}
