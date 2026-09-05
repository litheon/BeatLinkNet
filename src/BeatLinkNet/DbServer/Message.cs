using System.Collections.Frozen;
using System.Text;

namespace BeatLink.DbServer;

/// <summary>
/// <para>Encapsulates a full dbserver message, made up of a list of <see cref="Field"/> objects,
/// and having a particular structure, as described in the
/// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/track_metadata.html#messages">Packet Analysis</a>
/// document.</para>
///
/// <para>Known message types are found in <see cref="KnownType"/>. Many requests return a series of messages
/// that represent menu items; the known versions of these are reflected in <see cref="MenuItemType"/>.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>Message</c>, by James Elliott.
/// </remarks>
public class Message
{
    /// <summary>
    /// The special field that marks the start of a new message.
    /// </summary>
    public static readonly NumberField MessageStart = new(0x872349aeL, 4);

    /// <summary>
    /// Defines all the message types we know about, with any information we know about their arguments.
    /// </summary>
    public sealed class KnownType
    {
        /// <summary>
        /// The special initial request that is sent as the second action after opening a new connection, to
        /// enable further database queries.
        /// </summary>
        public static readonly KnownType SetupReq = new(0x0000, "SETUP_REQ", "setup request", "requesting player");

        /// <summary>A response indicating that a request could not be fulfilled for some reason.</summary>
        public static readonly KnownType InvalidData = new(0x0001, "INVALID_DATA", "invalid data");

        /// <summary>The special final request that is sent before closing the connection.</summary>
        public static readonly KnownType TeardownReq = new(0x0100, "TEARDOWN_REQ", "teardown request");

        /// <summary>Asks for the top-level menu of the player.</summary>
        public static readonly KnownType RootMenuReq = new(0x1000, "ROOT_MENU_REQ", "root menu request", "r:m:s:t", "sort order", "magic constant?");

        /// <summary>Asks for a list of genres in the specified media slot.</summary>
        public static readonly KnownType GenreMenuReq = new(0x1001, "GENRE_MENU_REQ", "genre menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of artists in the specified media slot.</summary>
        public static readonly KnownType ArtistMenuReq = new(0x1002, "ARTIST_MENU_REQ", "artist menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of albums in the specified media slot.</summary>
        public static readonly KnownType AlbumMenuReq = new(0x1003, "ALBUM_MENU_REQ", "album menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of all the tracks in the specified media slot.</summary>
        public static readonly KnownType TrackMenuReq = new(0x1004, "TRACK_MENU_REQ", "track menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of track tempos found in the specified media slot.</summary>
        public static readonly KnownType BpmMenuReq = new(0x1006, "BPM_MENU_REQ", "bpm menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of ratings in the specified media slot.</summary>
        public static readonly KnownType RatingMenuReq = new(0x1007, "RATING_MENU_REQ", "rating menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of years (at the decade level) in the specified media slot.</summary>
        public static readonly KnownType YearMenuReq = new(0x1008, "YEAR_MENU_REQ", "year menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of record labels in the specified media slot.</summary>
        public static readonly KnownType LabelMenuReq = new(0x100a, "LABEL_MENU_REQ", "label menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of colors in the specified media slot.</summary>
        public static readonly KnownType ColorMenuReq = new(0x100d, "COLOR_MENU_REQ", "color menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of track times (in minutes) in the specified media slot.</summary>
        public static readonly KnownType TimeMenuReq = new(0x1010, "TIME_MENU_REQ", "time menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of track bit rates (in kilobits per second) in the specified media slot.</summary>
        public static readonly KnownType BitRateMenuReq = new(0x1011, "BIT_RATE_MENU_REQ", "bit rate menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of performance histories found in the specified media slot.</summary>
        public static readonly KnownType HistoryMenuReq = new(0x1012, "HISTORY_MENU_REQ", "history menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of track file names found in the specified media slot.</summary>
        public static readonly KnownType FilenameMenuReq = new(0x1013, "FILENAME_MENU_REQ", "filename menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for a list of track keys found in the specified media slot.</summary>
        public static readonly KnownType KeyMenuReq = new(0x1014, "KEY_MENU_REQ", "key menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for an artist menu for a particular genre in the specified media slot.</summary>
        public static readonly KnownType ArtistMenuForGenreReq = new(0x1101, "ARTIST_MENU_FOR_GENRE_REQ", "artist menu for genre request", "r:m:s:t", "sort", "genre ID");

        /// <summary>Asks for an album menu for a particular artist in the specified media slot.</summary>
        public static readonly KnownType AlbumMenuForArtistReq = new(0x1102, "ALBUM_MENU_FOR_ARTIST_REQ", "album menu for artist request", "r:m:s:t", "sort", "artist ID");

        /// <summary>Asks for a track menu for a particular album in the specified media slot.</summary>
        public static readonly KnownType TrackMenuForAlbumReq = new(0x1103, "TRACK_MENU_FOR_ALBUM_REQ", "track menu for album request", "r:m:s:t", "sort", "album ID");

        /// <summary>Asks for a playlist or folder by ID.</summary>
        public static readonly KnownType PlaylistReq = new(0x1105, "PLAYLIST_REQ", "playlist/folder request", "r:m:s:t", "sort order", "playlist/folder ID", "0=playlist, 1=folder");

        /// <summary>Asks for a list of tempo ranges around a given tempo.</summary>
        public static readonly KnownType BpmRangeReq = new(0x1106, "BPM_RANGE_REQ", "bpm range request", "r:m:s:t", "sort order", "tempo");

        /// <summary>Asks for a track menu for a particular rating in the specified media slot.</summary>
        public static readonly KnownType TrackMenuForRatingReq = new(0x1107, "TRACK_MENU_FOR_RATING_REQ", "track menu for rating request", "r:m:s:t", "sort", "rating ID");

        /// <summary>Asks for a year menu for a particular decade in the specified media slot.</summary>
        public static readonly KnownType YearMenuForDecadeReq = new(0x1108, "YEAR_MENU_FOR_DECADE_REQ", "year menu for decade request", "r:m:s:t", "sort", "decade");

        /// <summary>Asks for an artist menu for a particular label in the specified media slot.</summary>
        public static readonly KnownType ArtistMenuForLabelReq = new(0x110a, "ARTIST_MENU_FOR_LABEL_REQ", "artist menu for genre request", "r:m:s:t", "sort", "label ID, or -1 for ALL");

        /// <summary>Asks for a track menu for a particular color in the specified media slot.</summary>
        public static readonly KnownType TrackMenuForColorReq = new(0x110d, "TRACK_MENU_FOR_COLOR_REQ", "track menu for color request", "r:m:s:t", "sort", "color ID");

        /// <summary>Asks for a track menu for a particular track time (length in minutes) in the specified media slot.</summary>
        public static readonly KnownType TrackMenuForTimeReq = new(0x1110, "TRACK_MENU_FOR_TIME_REQ", "track menu for time request", "r:m:s:t", "sort", "minutes");

        /// <summary>Asks for a track menu for a particular track bit rate (in Kbps) in the specified media slot.</summary>
        public static readonly KnownType TrackMenuForBitRateReq = new(0x1111, "TRACK_MENU_FOR_BIT_RATE_REQ", "track menu for bit rate request", "r:m:s:t", "sort", "bit rate");

        /// <summary>Asks for a track menu for a particular history entry in the specified media slot.</summary>
        public static readonly KnownType TrackMenuForHistoryReq = new(0x1112, "TRACK_MENU_FOR_HISTORY_REQ", "track menu for history entry request", "r:m:s:t", "sort", "history ID");

        /// <summary>
        /// Asks for a key neighbor menu (showing harmonically compatible keys and their distance, ranging from
        /// 0 to 2) available in the specified media slot.
        /// </summary>
        public static readonly KnownType NeighborMenuForKey = new(0x1114, "NEIGHBOR_MENU_FOR_KEY", "neighbor menu for key request", "r:m:s:t", "sort", "key ID");

        /// <summary>Asks for an album menu by genre and artist, can specify all artists by passing -1 for artist ID.</summary>
        public static readonly KnownType AlbumMenuForGenreAndArtist = new(0x1201, "ALBUM_MENU_FOR_GENRE_AND_ARTIST", "album menu for genre and artist request", "r:m:s:t:", "sort", "genre ID", "artist ID, or -1 for ALL");

        /// <summary>Asks for a track menu by artist and album, can specify all albums by passing -1 for album ID.</summary>
        public static readonly KnownType TrackMenuForArtistAndAlbum = new(0x1202, "TRACK_MENU_FOR_ARTIST_AND_ALBUM", "track menu for artist and album request", "r:m:s:t:", "sort", "artist ID", "album ID, or -1 for ALL");

        /// <summary>
        /// Asks for a track menu by BPM and distance, which represents a percentage tolerance by which the BPM
        /// can differ, ranging from 0 to 6.
        /// </summary>
        public static readonly KnownType TrackMenuForBpmAndDistance = new(0x1206, "TRACK_MENU_FOR_BPM_AND_DISTANCE", "track menu for BPM and distance request", "r:m:s:t:", "sort", "bpm ID", "distance (+/- %, can range from 0-6)");

        /// <summary>Asks for a track menu for a particular decade and year in the specified media slot.</summary>
        public static readonly KnownType TrackMenuForDecadeYearReq = new(0x1208, "TRACK_MENU_FOR_DECADE_YEAR_REQ", "track menu for decade and year request", "r:m:s:t", "sort", "decade", "year, or -1 for ALL");

        /// <summary>Asks for an album menu by label and artist, can specify all artists by passing -1 for artist ID.</summary>
        public static readonly KnownType AlbumMenuForLabelAndArtist = new(0x120a, "ALBUM_MENU_FOR_LABEL_AND_ARTIST", "album menu for label and artist request", "r:m:s:t:", "sort", "label ID", "artist ID, or -1 for ALL");

        /// <summary>
        /// Asks for a track menu by key and distance (which represents harmonic compatibility as allowed
        /// movement around the circle of fifths), ranging from 0 to 2.
        /// </summary>
        public static readonly KnownType TrackMenuForKeyAndDistance = new(0x1214, "TRACK_MENU_FOR_KEY_AND_DISTANCE", "track menu for key and distance request", "r:m:s:t:", "sort", "key ID", "distance (around circle of fifths)");

        /// <summary>
        /// Asks to search the database for records matching the specified text (artist, album, track names,
        /// possibly other things). Returns a variety of different menu item types.
        /// </summary>
        public static readonly KnownType SearchMenu = new(0x1300, "SEARCH_MENU", "search by substring request", "r:m:s:t", "sort", "search string byte size", "search string (must be uppercase", "unknown (0)");

        /// <summary>
        /// Asks for a track menu by genre, artist, and album, can specify all artists and/or albums by passing
        /// -1 for the artist and/or album IDs.
        /// </summary>
        public static readonly KnownType TrackMenuForGenreArtistAndAlbum = new(0x1301, "TRACK_MENU_FOR_GENRE_ARTIST_AND_ALBUM", "track menu for genre, artist and album request", "r:m:s:t:", "sort", "genre ID", "artist ID, or -1 for ALL", "album ID, or -1 for ALL");

        /// <summary>Asks for the original artist menu for the specified media slot.</summary>
        public static readonly KnownType OriginalArtistMenuReq = new(0x1302, "ORIGINAL_ARTIST_MENU_REQ", "original artist menu request", "r:m:s:t", "sort order");

        /// <summary>
        /// Asks for a track menu by label, artist, and album, can specify all artists and/or albums by passing
        /// -1 for the artist and/or album IDs.
        /// </summary>
        public static readonly KnownType TrackMenuForLabelArtistAndAlbum = new(0x130a, "TRACK_MENU_FOR_LABEL_ARTIST_AND_ALBUM", "track menu for label, artist and album request", "r:m:s:t:", "sort", "label ID", "artist ID, or -1 for ALL", "album ID, or -1 for ALL");

        /// <summary>Asks for an album menu for a particular original artist in the specified media slot.</summary>
        public static readonly KnownType AlbumMenuForOriginalArtistReq = new(0x1402, "ALBUM_MENU_FOR_ORIGINAL_ARTIST_REQ", "album menu for original artist request", "r:m:s:t", "sort", "artist ID");

        /// <summary>Asks for a track menu by original artist and album, can specify all albums by passing -1 for the album ID.</summary>
        public static readonly KnownType TrackMenuForOriginalArtistAndAlbum = new(0x1502, "TRACK_MENU_FOR_ORIGINAL_ARTIST_AND_ALBUM", "track menu for original artist and album request", "r:m:s:t:", "sort", "artist ID", "album ID, or -1 for ALL");

        /// <summary>Asks for the remixer menu for the specified media slot.</summary>
        public static readonly KnownType RemixerMenuReq = new(0x1602, "REMIXER_MENU_REQ", "remixer menu request", "r:m:s:t", "sort order");

        /// <summary>Asks for an album menu for a particular remixer in the specified media slot.</summary>
        public static readonly KnownType AlbumMenuForRemixerReq = new(0x1702, "ALBUM_MENU_FOR_REMIXER_REQ", "album menu for remixer request", "r:m:s:t", "sort", "artist ID");

        /// <summary>Asks for a track menu by remixer and album, can specify all albums by passing -1 for the album ID.</summary>
        public static readonly KnownType TrackMenuForRemixerAndAlbum = new(0x1802, "TRACK_MENU_FOR_REMIXER_AND_ALBUM", "track menu for remixer and album request", "r:m:s:t:", "sort", "artist ID", "album ID, or -1 for ALL");

        /// <summary>Asks for the metadata associated with a particular track, by rekordbox ID.</summary>
        public static readonly KnownType RekordboxMetadataReq = new(0x2002, "REKORDBOX_METADATA_REQ", "rekordbox track metadata request", "r:m:s:t", "rekordbox id");

        /// <summary>Asks for an album artwork image, by artwork ID.</summary>
        public static readonly KnownType AlbumArtReq = new(0x2003, "ALBUM_ART_REQ", "album art request", "r:m:s:t", "artwork id");

        /// <summary>Asks for the preview (summary) waveform data for a track, by rekordbox ID.</summary>
        public static readonly KnownType WavePreviewReq = new(0x2004, "WAVE_PREVIEW_REQ", "track waveform preview request", "r:m:s:t", "unknown (4)", "rekordbox id", "unknown (0)");

        /// <summary>Asks for a folder menu, of the raw media filesystem in the specified slot.</summary>
        public static readonly KnownType FolderMenuReq = new(0x2006, "FOLDER_MENU_REQ", "folder menu request", "r:m:s:t", "sort order?", "folder id (-1 for root)", "unknown (0)");

        /// <summary>Asks for the memory points, loops, and hot cues of a track, by rekordbox ID.</summary>
        public static readonly KnownType CueListReq = new(0x2104, "CUE_LIST_REQ", "track cue list request", "r:m:s:t", "rekordbox id");

        /// <summary>Asks for metadata about a CD track, by track number.</summary>
        public static readonly KnownType UnanalyzedMetadataReq = new(0x2202, "UNANALYZED_METADATA_REQ", "unanalyzed track metadata request", "r:m:s:t", "track number");

        /// <summary>Asks for the beat grid of a track, by rekordbox id.</summary>
        public static readonly KnownType BeatGridReq = new(0x2204, "BEAT_GRID_REQ", "beat grid request", "r:m:s:t", "rekordbox id");

        /// <summary>Asks for the detailed waveform data for a track, by rekordbox ID.</summary>
        public static readonly KnownType WaveDetailReq = new(0x2904, "WAVE_DETAIL_REQ", "track waveform detail request", "r:m:s:t", "rekordbox id");

        /// <summary>Asks for the memory points, loops, and hot cues of a track, including comments and colors, by rekordbox ID.</summary>
        public static readonly KnownType CueListExtReq = new(0x2b04, "CUE_LIST_EXT_REQ", "track extended cue list request", "r:m:s:t", "rekordbox id", "unknown (0)");

        /// <summary>
        /// This is a multipurpose request added for the nxs2 players which allows a specific tagged element of
        /// an ANLZnnnn.DAT or ANLZnnnn.EXT file to be retrieved. The tag type is the four-character-code
        /// identifying the desired file section (with bytes in reverse order), and the file extension is the
        /// same for the extension identifying the desired file. (For an EXT file they are 00 54 58 45, or "EXT"
        /// padded with a NUL and reversed.)
        /// </summary>
        public static readonly KnownType AnlzTagReq = new(0x2c04, "ANLZ_TAG_REQ", "anlz file tag content request", "r:m:s:t", "rekordbox id", "tag type", "file extension");

        /// <summary>
        /// Once a specific type of request has been made and acknowledged, this allows the results to be
        /// retrieved, possibly in paginated chunks starting at <em>offset</em>, returning up to <em>limit</em>
        /// results.
        /// </summary>
        public static readonly KnownType RenderMenuReq = new(0x3000, "RENDER_MENU_REQ", "render items from last requested menu", "r:m:s:t", "offset", "limit", "unknown (0)", "len_a (=limit)?", "unknown (0)");

        /// <summary>
        /// This response indicates that a query has been accepted, and reports how many results are available.
        /// They are now ready to be retrieved using <see cref="RenderMenuReq"/>.
        /// </summary>
        public static readonly KnownType MenuAvailable = new(0x4000, "MENU_AVAILABLE", "requested menu is available", "request type", "# items available");

        /// <summary>
        /// When <see cref="RenderMenuReq"/> is used to retrieve a set of results, this message will be sent as
        /// the first response, followed by as many <see cref="MenuItem"/> messages as were requested.
        /// </summary>
        public static readonly KnownType MenuHeader = new(0x4001, "MENU_HEADER", "rendered menu header");

        /// <summary>This response contains the binary image data of requested album art.</summary>
        public static readonly KnownType AlbumArt = new(0x4002, "ALBUM_ART", "album art", "request type", "unknown (0)", "image length", "image bytes");

        /// <summary>Indicates that the item that was just requested cannot be found.</summary>
        public static readonly KnownType Unavailable = new(0x4003, "UNAVAILABLE", "requested media unavailable", "request type");

        /// <summary>
        /// A series of messages of this type are the payload returned in response to
        /// <see cref="RenderMenuReq"/>. The number requested will be delivered, in between a
        /// <see cref="MenuHeader"/> and a <see cref="MenuFooter"/> message. Each message will be of a
        /// particular subtype, which is identified by the value of the 7th argument; see
        /// <see cref="MenuItemType"/> for known values.
        /// </summary>
        public static readonly KnownType MenuItem = new(0x4101, "MENU_ITEM", "rendered menu item", "numeric 1 (parent id, e.g. artist for track)", "numeric 2 (this id)",
            "label 1 byte size", "label 1", "label 2 byte size", "label 2", "item type", "flags? byte 3 is 1 when track played",
            "album art id", "playlist position");

        /// <summary>
        /// When <see cref="RenderMenuReq"/> is used to retrieve a set of results, this message will be sent as
        /// the final response, following any <see cref="MenuItem"/> messages that were requested.
        /// </summary>
        public static readonly KnownType MenuFooter = new(0x4201, "MENU_FOOTER", "rendered menu footer");

        /// <summary>
        /// Returns the bytes of the small waveform preview to be displayed at the bottom of the player display,
        /// or in rekordbox track lists.
        /// </summary>
        public static readonly KnownType WavePreview = new(0x4402, "WAVE_PREVIEW", "track waveform preview", "request type", "unknown (0)", "waveform length", "waveform bytes");

        /// <summary>
        /// Returns information about the beat number (within a bar) and millisecond position within the track
        /// of each beat in a track.
        /// </summary>
        public static readonly KnownType BeatGrid = new(0x4602, "BEAT_GRID", "beat grid", "request type", "unknown (0)", "beat grid length", "beat grid bytes", "unknown (0)");

        /// <summary>Returns information about any memory points, loops, and hot cues set in the track.</summary>
        public static readonly KnownType CueList = new(0x4702, "CUE_LIST", "memory points, loops, and hot cues", "request type", "unknown", "blob 1 length", "blob 1",
            "unknown (0x24)", "unknown", "unknown", "blob 2 length", "blob 2");

        /// <summary>Returns the bytes of the detailed waveform which is scrolled through while the track is playing.</summary>
        public static readonly KnownType WaveDetail = new(0x4a02, "WAVE_DETAIL", "track waveform detail", "request type", "unknown (0)", "waveform length", "waveform bytes");

        /// <summary>
        /// Returns extended information about any memory points, loops, and hot cues set in the track,
        /// including DJ comments, colors, and hot cues beyond C.
        /// </summary>
        public static readonly KnownType CueListExt = new(0x4e02, "CUE_LIST_EXT", "extended memory points, loops, and hot cues", "request type", "unknown (0)",
            "blob length", "blob", "entry count");

        /// <summary>Returns the bytes of the requested tag from an ANLZnnnn.DAT or ANLZnnnn.EXT file.</summary>
        public static readonly KnownType AnlzTag = new(0x4f02, "ANLZ_TAG", "anlz file tag content", "request type", "unknown (0)", "tag length", "tag bytes", "unknown (1)");

        /// <summary>
        /// The numeric value that identifies this message type, by its presence in a 4-byte number field
        /// immediately following the message start indicator.
        /// </summary>
        public long ProtocolValue { get; }

        /// <summary>
        /// The name of this message type, corresponding to the Java enum constant name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The descriptive name of the message type.
        /// </summary>
        public string Description { get; }

        private readonly string[] _arguments;

        private KnownType(long protocolValue, string name, string description, params string[] arguments)
        {
            ProtocolValue = protocolValue;
            Name = name;
            Description = description;
            _arguments = arguments;
        }

        /// <summary>
        /// Get the descriptive name of the specified message argument, if one is known.
        /// </summary>
        /// <param name="index">the zero-based index identifying the argument whose description is desired</param>
        /// <returns>either the description found, or "unknown" if none was found</returns>
        public string DescribeArgument(int index)
        {
            if (index < 0 || index >= _arguments.Length)
            {
                return "unknown";
            }
            return _arguments[index];
        }

        /// <summary>
        /// The descriptions of all known arguments, in order.
        /// </summary>
        public IReadOnlyList<string> Arguments => _arguments;

        /// <summary>All the known message types.</summary>
        public static IReadOnlyList<KnownType> Values { get; } =
        [
            SetupReq, InvalidData, TeardownReq, RootMenuReq, GenreMenuReq, ArtistMenuReq, AlbumMenuReq,
            TrackMenuReq, BpmMenuReq, RatingMenuReq, YearMenuReq, LabelMenuReq, ColorMenuReq, TimeMenuReq,
            BitRateMenuReq, HistoryMenuReq, FilenameMenuReq, KeyMenuReq, ArtistMenuForGenreReq,
            AlbumMenuForArtistReq, TrackMenuForAlbumReq, PlaylistReq, BpmRangeReq, TrackMenuForRatingReq,
            YearMenuForDecadeReq, ArtistMenuForLabelReq, TrackMenuForColorReq, TrackMenuForTimeReq,
            TrackMenuForBitRateReq, TrackMenuForHistoryReq, NeighborMenuForKey, AlbumMenuForGenreAndArtist,
            TrackMenuForArtistAndAlbum, TrackMenuForBpmAndDistance, TrackMenuForDecadeYearReq,
            AlbumMenuForLabelAndArtist, TrackMenuForKeyAndDistance, SearchMenu,
            TrackMenuForGenreArtistAndAlbum, OriginalArtistMenuReq, TrackMenuForLabelArtistAndAlbum,
            AlbumMenuForOriginalArtistReq, TrackMenuForOriginalArtistAndAlbum, RemixerMenuReq,
            AlbumMenuForRemixerReq, TrackMenuForRemixerAndAlbum, RekordboxMetadataReq, AlbumArtReq,
            WavePreviewReq, FolderMenuReq, CueListReq, UnanalyzedMetadataReq, BeatGridReq, WaveDetailReq,
            CueListExtReq, AnlzTagReq, RenderMenuReq, MenuAvailable, MenuHeader, AlbumArt, Unavailable,
            MenuItem, MenuFooter, WavePreview, BeatGrid, CueList, WaveDetail, CueListExt, AnlzTag
        ];

        /// <summary>
        /// Allows a known message type to be looked up by the message type number.
        /// </summary>
        public static FrozenDictionary<long, KnownType> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(t => t.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => Name;
    }

    /// <summary>
    /// The value to pass for the file type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain an element of an ANLZnnnn.DAT file. (The characters "DAT" and NUL as a byte-swapped integer.)
    /// </summary>
    /// <remarks>The misspelled "ALNZ" prefix is carried over faithfully from the Java library.</remarks>
    public const int AlnzFileTypeDat = 0x00544144;

    /// <summary>
    /// The value to pass for the file type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain an element of an ANLZnnnn.EXT file. (The characters "EXT" and NUL as a byte-swapped integer.)
    /// </summary>
    /// <remarks>The misspelled "ALNZ" prefix is carried over faithfully from the Java library.</remarks>
    public const int AlnzFileTypeExt = 0x00545845;

    /// <summary>
    /// The value to pass for the file type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain an element of an ANLZnnnn.2EX file. (The characters "2EX" and NUL as a byte-swapped integer.)
    /// </summary>
    /// <remarks>The misspelled "ALNZ" prefix is carried over faithfully from the Java library.</remarks>
    public const int AlnzFileType2Ex = 0x00584532;

    /// <summary>
    /// The value to pass for the tag type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain the color waveform preview data. (The characters "PWV4" as a byte-swapped integer.)
    /// </summary>
    public const int AnlzFileTagColorWaveformPreview = 0x34565750;

    /// <summary>
    /// The value to pass for the tag type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain the scrollable color waveform data. (The characters "PWV5" as a byte-swapped integer.)
    /// </summary>
    public const int AnlzFileTagColorWaveformDetail = 0x35565750;

    /// <summary>
    /// The value to pass for the tag type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain the CDJ-3000 style 3-band waveform preview data. (The characters "PWV6" as a byte-swapped integer.)
    /// </summary>
    public const int AnlzFileTag3BandWaveformPreview = 0x36565750;

    /// <summary>
    /// The value to pass for the tag type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain the scrollable CDJ-3000 style 3-band waveform data. (The characters "PWV7" as a byte-swapped integer.)
    /// </summary>
    public const int AnlzFileTag3BandWaveformDetail = 0x37565750;

    /// <summary>
    /// The value to pass for the tag type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain the song structure information data. (The characters "PSSI" as a byte-swapped integer.)
    /// </summary>
    public const int AnlzFileTagSongStructure = 0x49535350;

    /// <summary>
    /// The value to pass for the tag type argument of a <see cref="KnownType.AnlzTagReq"/> request in order
    /// to obtain the enhanced cue and loop data, but does not seem to work. (The characters "PCO2" as a
    /// byte-swapped integer.)
    /// </summary>
    public const int AnlzFileTagCueComment = 0x324f4350;

    /// <summary>
    /// Defines all the known types of entries that can be returned for a menu request.
    /// </summary>
    public sealed class MenuItemType
    {
        /// <summary>A potentially-nested grouping of other objects, such as a group of playlists in the playlists menu.</summary>
        public static readonly MenuItemType Folder = new(0x0001, "FOLDER");

        /// <summary>
        /// The string identifying the name of an album, part of the track metadata response. Also contains the
        /// album ID for listing by album.
        /// </summary>
        public static readonly MenuItemType AlbumTitle = new(0x0002, "ALBUM_TITLE");

        /// <summary>We don't yet know where this appears or what it means.</summary>
        public static readonly MenuItemType Disc = new(0x0003, "DISC");

        /// <summary>
        /// The string identifying the name of a track, part of the track metadata response, as well as track
        /// lists and play lists.
        /// </summary>
        public static readonly MenuItemType TrackTitle = new(0x0004, "TRACK_TITLE");

        /// <summary>
        /// The string identifying the musical genre of a track, part of the track metadata response. Also
        /// contains the genre ID for listing by genre.
        /// </summary>
        public static readonly MenuItemType Genre = new(0x0006, "GENRE");

        /// <summary>
        /// The string identifying the name of the artist for a track, part of the track metadata response, as
        /// well as track lists and play lists. Also contains the artist ID for listing by artist.
        /// </summary>
        public static readonly MenuItemType Artist = new(0x0007, "ARTIST");

        /// <summary>When listing playlists, reports the name and ID of an actual playlist, as opposed to a sub-folder.</summary>
        public static readonly MenuItemType Playlist = new(0x0008, "PLAYLIST");

        /// <summary>The rating assigned a track by the DJ, part of the track metadata response.</summary>
        public static readonly MenuItemType Rating = new(0x000a, "RATING");

        /// <summary>The duration, in seconds, of a track, part of the track metadata response.</summary>
        public static readonly MenuItemType Duration = new(0x000b, "DURATION");

        /// <summary>The tempo of a track, BPM times 100, part of the track metadata response.</summary>
        public static readonly MenuItemType Tempo = new(0x000d, "TEMPO");

        /// <summary>
        /// A string containing the label that issued a track, part of the track metadata response. Also
        /// contains the label ID for listing by label.
        /// </summary>
        public static readonly MenuItemType Label = new(0x000e, "LABEL");

        /// <summary>
        /// A string containing the musical key of a track, part of the track metadata response. Also contains
        /// the key ID for listing by key.
        /// </summary>
        public static readonly MenuItemType Key = new(0x000f, "KEY");

        /// <summary>Track bit rate in Kbps; also appears in non-rekordbox metadata.</summary>
        public static readonly MenuItemType BitRate = new(0x0010, "BIT_RATE");

        /// <summary>A number identifying a year in which a track was created.</summary>
        public static readonly MenuItemType Year = new(0x0011, "YEAR");

        /// <summary>Indicates the DJ has not assigned a color label to a track, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorNone = new(0x0013, "COLOR_NONE");

        /// <summary>A label assigned a track by the DJ, marked with a pink dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorPink = new(0x0014, "COLOR_PINK");

        /// <summary>A label assigned a track by the DJ, marked with a red dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorRed = new(0x0015, "COLOR_RED");

        /// <summary>A label assigned a track by the DJ, marked with an orange dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorOrange = new(0x0016, "COLOR_ORANGE");

        /// <summary>A label assigned a track by the DJ, marked with a yellow dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorYellow = new(0x0017, "COLOR_YELLOW");

        /// <summary>A label assigned a track by the DJ, marked with a green dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorGreen = new(0x0018, "COLOR_GREEN");

        /// <summary>A label assigned a track by the DJ, marked with an aqua dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorAqua = new(0x0019, "COLOR_AQUA");

        /// <summary>A label assigned a track by the DJ, marked with a blue dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorBlue = new(0x001a, "COLOR_BLUE");

        /// <summary>A label assigned a track by the DJ, marked with a purple dot, part of the track metadata response.</summary>
        public static readonly MenuItemType ColorPurple = new(0x001b, "COLOR_PURPLE");

        /// <summary>An arbitrary text string assigned to a track by the DJ. Also contains a comment ID, for listing by comment.</summary>
        public static readonly MenuItemType Comment = new(0x0023, "COMMENT");

        /// <summary>A list of tracks that were performed in a past session.</summary>
        public static readonly MenuItemType HistoryPlaylist = new(0x0024, "HISTORY_PLAYLIST");

        /// <summary>
        /// The artist who originally recorded a track. Also contains an original artist ID, for listing by
        /// original artist.
        /// </summary>
        public static readonly MenuItemType OriginalArtist = new(0x0028, "ORIGINAL_ARTIST");

        /// <summary>The producer who remixed a track. Also contains a remixer ID, for listing by remixer.</summary>
        public static readonly MenuItemType Remixer = new(0x0029, "REMIXER");

        /// <summary>
        /// A string reporting when the track was added to the collection, in the form "YYYY-MM-DD", part of
        /// the track metadata response. This seems to propagate from iTunes. Also contains a date added ID,
        /// for listing by date added.
        /// </summary>
        public static readonly MenuItemType DateAdded = new(0x002e, "DATE_ADDED");

        /// <summary>The root menu item that takes you to the genre list.</summary>
        public static readonly MenuItemType GenreMenu = new(0x0080, "GENRE_MENU");

        /// <summary>The root menu item that takes you to the artist list.</summary>
        public static readonly MenuItemType ArtistMenu = new(0x0081, "ARTIST_MENU");

        /// <summary>The root menu item that takes you to the album list.</summary>
        public static readonly MenuItemType AlbumMenu = new(0x0082, "ALBUM_MENU");

        /// <summary>The root menu item that takes you to the track list.</summary>
        public static readonly MenuItemType TrackMenu = new(0x0083, "TRACK_MENU");

        /// <summary>The root menu item that takes you to the playlist hierarchy.</summary>
        public static readonly MenuItemType PlaylistMenu = new(0x0084, "PLAYLIST_MENU");

        /// <summary>The root menu item that takes you to the tempo list.</summary>
        public static readonly MenuItemType BpmMenu = new(0x0085, "BPM_MENU");

        /// <summary>The root menu item that takes you to the rating list.</summary>
        public static readonly MenuItemType RatingMenu = new(0x0086, "RATING_MENU");

        /// <summary>The root menu item that takes you to the years by decade list.</summary>
        public static readonly MenuItemType YearMenu = new(0x0087, "YEAR_MENU");

        /// <summary>The root menu item that takes you to the remixer menu.</summary>
        public static readonly MenuItemType RemixerMenu = new(0x0088, "REMIXER_MENU");

        /// <summary>The root menu item that takes you to the label menu.</summary>
        public static readonly MenuItemType LabelMenu = new(0x0089, "LABEL_MENU");

        /// <summary>The root menu item that takes you to the original artist menu.</summary>
        public static readonly MenuItemType OriginalArtistMenu = new(0x008a, "ORIGINAL_ARTIST_MENU");

        /// <summary>The root menu item that takes you to the key list.</summary>
        public static readonly MenuItemType KeyMenu = new(0x008b, "KEY_MENU");

        /// <summary>The root menu item that takes you to the date added list.</summary>
        public static readonly MenuItemType DateAddedMenu = new(0x008c, "DATE_ADDED_MENU");

        /// <summary>The root menu item that takes you to the color list.</summary>
        public static readonly MenuItemType ColorMenu = new(0x008e, "COLOR_MENU");

        /// <summary>The root menu item that takes you to the folder list.</summary>
        public static readonly MenuItemType FolderMenu = new(0x0090, "FOLDER_MENU");

        /// <summary>The root menu item that takes you to the search interface.</summary>
        public static readonly MenuItemType SearchMenu = new(0x0091, "SEARCH_MENU");

        /// <summary>The root menu item that takes you to the time (track length in minutes) list.</summary>
        public static readonly MenuItemType TimeMenu = new(0x0092, "TIME_MENU");

        /// <summary>The root menu item that takes you to the track bit rate (Kbps) list.</summary>
        public static readonly MenuItemType BitRateMenu = new(0x0093, "BIT_RATE_MENU");

        /// <summary>The root menu item that takes you to the file name list.</summary>
        public static readonly MenuItemType FilenameMenu = new(0x0094, "FILENAME_MENU");

        /// <summary>The root menu item that takes you to the history list.</summary>
        public static readonly MenuItemType HistoryMenu = new(0x0095, "HISTORY_MENU");

        /// <summary>The root menu item that takes you to the hot cue bank list.</summary>
        public static readonly MenuItemType HotCueBankMenu = new(0x0098, "HOT_CUE_BANK_MENU");

        /// <summary>The menu item that lets you choose all values in a subcategory.</summary>
        public static readonly MenuItemType All = new(0x00a0, "ALL");

        /// <summary>
        /// Reports the title and album of a track, returned when listing playlists or all tracks sorted by
        /// album, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndAlbum = new(0x0204, "TRACK_TITLE_AND_ALBUM");

        /// <summary>
        /// Reports the title and genre of a track, returned when listing playlists or all tracks sorted by
        /// genre, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndGenre = new(0x0604, "TRACK_TITLE_AND_GENRE");

        /// <summary>
        /// Reports the title and artist of a track, returned when listing playlists or all tracks sorted by
        /// artist, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndArtist = new(0x0704, "TRACK_TITLE_AND_ARTIST");

        /// <summary>
        /// Reports the title and rating of a track, returned when listing playlists or all tracks sorted by
        /// rating, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndRating = new(0x0a04, "TRACK_TITLE_AND_RATING");

        /// <summary>
        /// Reports the title and duration of a track, returned when listing playlists or all tracks sorted by
        /// time, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndTime = new(0x0b04, "TRACK_TITLE_AND_TIME");

        /// <summary>
        /// Reports the title and BPM of a track, returned when listing playlists or all tracks sorted by BPM,
        /// or in their default sort order when the DJ has set this as the default second column for track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndBpm = new(0x0d04, "TRACK_TITLE_AND_BPM");

        /// <summary>
        /// Reports the title and label of a track, returned when listing playlists or all tracks sorted by
        /// label, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndLabel = new(0x0e04, "TRACK_TITLE_AND_LABEL");

        /// <summary>
        /// Reports the title and key of a track, returned when listing playlists or all tracks sorted by key,
        /// or in their default sort order when the DJ has set this as the default second column for track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndKey = new(0x0f04, "TRACK_TITLE_AND_KEY");

        /// <summary>
        /// Reports the title and bit rate of a track, returned when listing playlists or all tracks sorted by
        /// bit rate, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndRate = new(0x1004, "TRACK_TITLE_AND_RATE");

        /// <summary>
        /// Reports the title and color of a track, returned when listing playlists or all tracks sorted by
        /// color, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackListEntryByColor = new(0x1a04, "TRACK_LIST_ENTRY_BY_COLOR");

        /// <summary>
        /// Reports the title and comment of a track, returned when listing playlists or all tracks sorted by
        /// comment, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndComment = new(0x2304, "TRACK_TITLE_AND_COMMENT");

        /// <summary>
        /// Reports the title and original artist of a track, returned when listing playlists sorted by original
        /// artist, or in their default sort order when listing all tracks and the DJ has set the original artist
        /// as the second column for track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndOriginalArtist = new(0x2804, "TRACK_TITLE_AND_ORIGINAL_ARTIST");

        /// <summary>
        /// Reports the title and remixer of a track, returned when listing playlists sorted by remixer, or in
        /// their default sort order when listing all tracks and the DJ has set this as the second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndRemixer = new(0x2904, "TRACK_TITLE_AND_REMIXER");

        /// <summary>
        /// Reports the title and play count of a track, returned when listing playlists or all tracks sorted by
        /// how many times the DJ has played the track, or in their default sort order when the DJ has set this
        /// as the default second column for track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndDjPlayCount = new(0x2a04, "TRACK_TITLE_AND_DJ_PLAY_COUNT");

        /// <summary>
        /// Reports the title and date added of a track, returned when listing playlists or all tracks sorted by
        /// date added, or in their default sort order when the DJ has set this as the default second column for
        /// track lists.
        /// </summary>
        public static readonly MenuItemType TrackTitleAndDateAdded = new(0x2e04, "TRACK_TITLE_AND_DATE_ADDED");

        /// <summary>We received a value that we don't recognize, so we don't know what it contains.</summary>
        public static readonly MenuItemType Unknown = new(-1, "UNKNOWN");

        /// <summary>
        /// The value which identifies this type of menu item by appearing in the seventh argument of a
        /// <see cref="KnownType.MenuItem"/> response.
        /// </summary>
        public long ProtocolValue { get; }

        /// <summary>
        /// The name of this menu item type, corresponding to the Java enum constant name.
        /// </summary>
        public string Name { get; }

        private MenuItemType(long protocolValue, string name)
        {
            ProtocolValue = protocolValue;
            Name = name;
        }

        /// <summary>All the known menu item types.</summary>
        public static IReadOnlyList<MenuItemType> Values { get; } =
        [
            Folder, AlbumTitle, Disc, TrackTitle, Genre, Artist, Playlist, Rating, Duration, Tempo, Label,
            Key, BitRate, Year, ColorNone, ColorPink, ColorRed, ColorOrange, ColorYellow, ColorGreen,
            ColorAqua, ColorBlue, ColorPurple, Comment, HistoryPlaylist, OriginalArtist, Remixer, DateAdded,
            GenreMenu, ArtistMenu, AlbumMenu, TrackMenu, PlaylistMenu, BpmMenu, RatingMenu, YearMenu,
            RemixerMenu, LabelMenu, OriginalArtistMenu, KeyMenu, DateAddedMenu, ColorMenu, FolderMenu,
            SearchMenu, TimeMenu, BitRateMenu, FilenameMenu, HistoryMenu, HotCueBankMenu, All,
            TrackTitleAndAlbum, TrackTitleAndGenre, TrackTitleAndArtist, TrackTitleAndRating,
            TrackTitleAndTime, TrackTitleAndBpm, TrackTitleAndLabel, TrackTitleAndKey, TrackTitleAndRate,
            TrackListEntryByColor, TrackTitleAndComment, TrackTitleAndOriginalArtist, TrackTitleAndRemixer,
            TrackTitleAndDjPlayCount, TrackTitleAndDateAdded, Unknown
        ];

        /// <summary>
        /// Allows a menu item type to be looked up by the value seen in the seventh argument of a
        /// <see cref="KnownType.MenuItem"/> response.
        /// </summary>
        public static FrozenDictionary<long, MenuItemType> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(t => t.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => Name;
    }

    /// <summary>
    /// For many types of query messages, the first argument of the message is a 4-byte integer which we
    /// currently refer to as <em>r:m:s:t</em>, because the first byte is the player number of the player making
    /// the <em>request</em>, the second byte identifies the <em>menu</em> or destination for which information
    /// is being loaded, the third byte identifies the media <em>slot</em> (USB or SD) being asked about (as
    /// described in <see cref="CdjStatus.TrackSourceSlot"/>), and the fourth byte identifies the type of track
    /// being worked with (for most requests this is 1, meaning rekordbox). This class lists the known values
    /// for the second, menu, byte.
    /// </summary>
    public sealed class MenuIdentifier
    {
        /// <summary>The primary menu which appears on the left half of the player display.</summary>
        public static readonly MenuIdentifier MainMenu = new(1, "MAIN_MENU");

        /// <summary>The secondary menu which sometimes appears down the right half of the player display.</summary>
        public static readonly MenuIdentifier SubMenu = new(2, "SUB_MENU");

        /// <summary>The pseudo-menu of track metadata.</summary>
        public static readonly MenuIdentifier TrackInfo = new(3, "TRACK_INFO");

        /// <summary>Types of sorting available? We are not entirely sure when this is used.</summary>
        public static readonly MenuIdentifier SortMenu = new(5, "SORT_MENU");

        /// <summary>
        /// Values which do not display in a menu, such as track waveforms, beat grids, album art, etc. are
        /// loaded "into" this menu/destination.
        /// </summary>
        public static readonly MenuIdentifier Data = new(8, "DATA");

        /// <summary>
        /// The value which identifies this menu or destination by appearing in the second byte of the first
        /// argument of many request messages.
        /// </summary>
        public byte ProtocolValue { get; }

        /// <summary>
        /// The name of this menu identifier, corresponding to the Java enum constant name.
        /// </summary>
        public string Name { get; }

        private MenuIdentifier(byte protocolValue, string name)
        {
            ProtocolValue = protocolValue;
            Name = name;
        }

        /// <summary>All the known menu/destination identifiers.</summary>
        public static IReadOnlyList<MenuIdentifier> Values { get; } =
            [MainMenu, SubMenu, TrackInfo, SortMenu, Data];

        /// <summary>
        /// Allows a menu/destination to be looked up by the value seen in the second byte of the first argument
        /// of many request messages.
        /// </summary>
        public static FrozenDictionary<byte, MenuIdentifier> ProtocolValueMap { get; } =
            Values.ToFrozenDictionary(t => t.ProtocolValue);

        /// <inheritdoc/>
        public override string ToString() => Name;
    }

    /// <summary>
    /// The 4-byte number field that provides the sequence number tying a query to its response messages,
    /// immediately following the message start field.
    /// </summary>
    public NumberField Transaction { get; }

    /// <summary>
    /// The 2-byte number field that identifies what type of message this is, immediately following the
    /// transaction sequence number.
    /// </summary>
    public NumberField MessageType { get; }

    /// <summary>
    /// The recognized type, if any, of this message. (This corresponds to the Java <c>knownType</c> field;
    /// C# does not allow a property to share its name with the nested <see cref="KnownType"/> type.)
    /// </summary>
    public KnownType? RecognizedType { get; }

    /// <summary>
    /// The 1-byte number field that specifies how many arguments the message has.
    /// </summary>
    public NumberField ArgumentCount { get; }

    /// <summary>
    /// The arguments being sent as part of this message.
    /// </summary>
    public IReadOnlyList<Field> Arguments { get; }

    /// <summary>
    /// The entire list of fields that make up the message.
    /// </summary>
    public IReadOnlyList<Field> Fields { get; }

    /// <summary>
    /// Constructor for experimenting with new message types.
    /// </summary>
    /// <param name="transaction">the transaction ID (sequence number) that ties a message to its responses</param>
    /// <param name="messageType">identifies the purpose and structure of the message</param>
    /// <param name="arguments">the arguments to send with the message</param>
    public Message(long transaction, long messageType, params Field[] arguments) :
        this(new NumberField(transaction, 4), new NumberField(messageType, 2), arguments)
    {
    }

    /// <summary>
    /// Constructor from code using known message types.
    /// </summary>
    /// <param name="transaction">the transaction ID (sequence number) that ties a message to its responses</param>
    /// <param name="messageType">identifies the purpose and structure of the message</param>
    /// <param name="arguments">the arguments to send with the message</param>
    public Message(long transaction, KnownType messageType, params Field[] arguments) :
        this(transaction, messageType.ProtocolValue, arguments)
    {
    }

    /// <summary>
    /// Constructor when being read from the network, so we already have all the fields created.
    /// </summary>
    /// <param name="transaction">the transaction ID (sequence number) that ties a message to its responses</param>
    /// <param name="messageType">identifies the purpose and structure of the message</param>
    /// <param name="arguments">the arguments to send with the message</param>
    /// <exception cref="ArgumentException">if the transaction or message type fields have the wrong size,
    /// or more than twelve arguments were supplied</exception>
    public Message(NumberField transaction, NumberField messageType, params Field[] arguments)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(arguments);
        if (transaction.Size != 4)
        {
            throw new ArgumentException("Message transaction sequence number must be 4 bytes long", nameof(transaction));
        }
        if (messageType.Size != 2)
        {
            throw new ArgumentException("Message type must be 2 bytes long", nameof(messageType));
        }
        if (arguments.Length > 12)
        {
            throw new ArgumentException("Messages cannot have more than 12 arguments", nameof(arguments));
        }
        Transaction = transaction;
        MessageType = messageType;
        RecognizedType = KnownType.ProtocolValueMap.TryGetValue(messageType.Value, out var found) ? found : null;
        ArgumentCount = new NumberField(arguments.Length, 1);
        var argumentsCopy = (Field[])arguments.Clone();
        Arguments = argumentsCopy;

        // Build the list of argument type tags.
        var argTags = new byte[12];
        for (var i = 0; i < argumentsCopy.Length; i++)
        {
            argTags[i] = argumentsCopy[i].ArgumentTag;
        }

        // And build the full list of fields that will be sent when this message is sent.
        var allFields = new Field[argumentsCopy.Length + 5];
        allFields[0] = MessageStart;
        allFields[1] = transaction;
        allFields[2] = messageType;
        allFields[3] = ArgumentCount;
        allFields[4] = new BinaryField(argTags);
        Array.Copy(argumentsCopy, 0, allFields, 5, argumentsCopy.Length);
        Fields = allFields;
    }

    /// <summary>
    /// Read the next message from the stream.
    /// </summary>
    /// <param name="stream">a stream connected to a dbserver which is expected to be sending a message</param>
    /// <returns>the next full message found on the stream</returns>
    /// <exception cref="IOException">if there is a problem reading the message</exception>
    public static Message Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var start = Field.Read(stream);
        if (start is not NumberField startNumber)
        {
            throw new IOException($"Did not find number field reading start of message; got: {start}");
        }
        if (startNumber.Size != 4)
        {
            throw new IOException($"Number field to start message must be of size 4, got: {start}");
        }
        if (startNumber.Value != MessageStart.Value)
        {
            throw new IOException($"Number field had wrong value to start message. Expected: {MessageStart}, got: {start}");
        }

        var transaction = Field.Read(stream);
        if (transaction is not NumberField transactionNumber)
        {
            throw new IOException($"Did not find number field reading transaction ID of message; got: {transaction}");
        }
        if (transactionNumber.Size != 4)
        {
            throw new IOException($"Transaction number field of message must be of size 4, got: {transaction}");
        }

        var type = Field.Read(stream);
        if (type is not NumberField typeNumber)
        {
            throw new IOException($"Did not find number field reading type of message; got: {type}");
        }
        if (typeNumber.Size != 2)
        {
            throw new IOException($"Type field of message must be of size 2, got: {type}");
        }

        var argCountField = Field.Read(stream);
        if (argCountField is not NumberField argCountNumber)
        {
            throw new IOException($"Did not find number field reading argument count of message; got: {argCountField}");
        }
        if (argCountNumber.Size != 1)
        {
            throw new IOException($"Argument count field of message must be of size 1, got: {argCountField}");
        }
        var argCount = (int)argCountNumber.Value;
        if (argCount < 0 || argCount > 12)
        {
            throw new IOException($"Illegal argument count while reading message; must be between 0 and 12, got: {argCount}");
        }

        var argTypes = Field.Read(stream);
        if (argTypes is not BinaryField argTypesBinary)
        {
            throw new IOException($"Did not find binary field reading argument types of message, got: {argTypes}");
        }
        var argTags = argTypesBinary.Value.Span[..argCount].ToArray();

        var arguments = new Field[argCount];
        Field? lastArg = null;
        for (var i = 0; i < argCount; i++)
        {
            if (argTags[i] == 3 && lastArg is NumberField { Value: 0 })
            {
                arguments[i] = new BinaryField([]);  // Do not attempt to read a zero-length binary field.
            }
            else
            {
                arguments[i] = Field.Read(stream);
            }
            lastArg = arguments[i];
            if (lastArg.ArgumentTag != argTags[i])
            {
                throw new IOException($"Found argument of wrong type reading message. Expected tag: {argTags[i]} and got: {arguments[i].ArgumentTag}");
            }
        }
        return new Message(transactionNumber, typeNumber, arguments);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var result = new StringBuilder();
        result.Append("Message: [transaction: ").Append(Transaction.Value);
        result.Append($", type: 0x{MessageType.Value:x4} (");
        result.Append(RecognizedType != null ? RecognizedType.Description : "unknown");
        result.Append("), arg count: ").Append(ArgumentCount.Value).Append(", arguments:").AppendLine();
        for (var i = 0; i < Arguments.Count; i++)
        {
            var arg = Arguments[i];
            result.Append($"{i + 1,4}: ");
            switch (arg)
            {
                case NumberField number:
                    result.Append($"number: {number.Value,10} (0x{number.Value:x8})");
                    break;
                case BinaryField binary:
                    result.Append($"blob length {arg.Size}:");
                    foreach (var b in binary.Value.Span)
                    {
                        result.Append($" {b:x2}");
                    }
                    break;
                case StringField text:
                    result.Append($"string length {arg.Size}: \"{text.Value}\"");
                    break;
                default:
                    result.Append("unknown: ").Append(arg);
                    break;
            }
            var argDescription = "unknown";
            if (RecognizedType != null)
            {
                argDescription = RecognizedType.DescribeArgument(i);
                if (RecognizedType == KnownType.MenuItem && i == 6 && arg is NumberField itemTypeField)
                {
                    var itemType = MenuItemType.ProtocolValueMap.TryGetValue(itemTypeField.Value, out var match) ?
                        match.Name : "unknown";
                    argDescription = $"{argDescription}: {itemType}";
                }
            }
            result.Append($" [{argDescription}]").AppendLine();
        }
        return result.Append(']').ToString();
    }

    /// <summary>
    /// The value returned by <see cref="GetMenuResultsCount"/> when there is no data available for the request
    /// that was made.
    /// </summary>
    public const long NoMenuResultsAvailable = 0xffffffffL;

    /// <summary>
    /// Extracts the result count from a <see cref="KnownType.MenuAvailable"/> response.
    /// </summary>
    /// <returns>the reported count of available results</returns>
    /// <exception cref="InvalidOperationException">if this is not a <see cref="KnownType.MenuAvailable"/> response</exception>
    public long GetMenuResultsCount()
    {
        if (RecognizedType != KnownType.MenuAvailable)
        {
            throw new InvalidOperationException("GetMenuResultsCount() can only be used with MENU_AVAILABLE responses.");
        }
        return ((NumberField)Arguments[1]).Value;
    }

    /// <summary>
    /// Extracts the menu item type from a <see cref="KnownType.MenuItem"/> response. Only the low sixteen bits
    /// of the seventh argument are considered, because the high bytes can carry other information.
    /// </summary>
    /// <returns>the reported type of this menu item</returns>
    /// <exception cref="InvalidOperationException">if this is not a <see cref="KnownType.MenuItem"/> response</exception>
    public MenuItemType GetMenuItemType()
    {
        if (RecognizedType != KnownType.MenuItem)
        {
            throw new InvalidOperationException("GetMenuItemType() can only be used with MENU_ITEM responses.");
        }
        var type = (NumberField)Arguments[6];
        return MenuItemType.ProtocolValueMap.TryGetValue(type.Value & 0xffff, out var result) ?
            result : MenuItemType.Unknown;
    }

    /// <summary>
    /// Writes the message to the specified stream, for example when saving to a file (like a Beat Link Trigger
    /// show).
    /// </summary>
    /// <param name="stream">the stream to which it should be written</param>
    /// <exception cref="IOException">if there is a problem writing to the stream</exception>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        foreach (var field in Fields)
        {
            field.Write(stream);
        }
    }
}
