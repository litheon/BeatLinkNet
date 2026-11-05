using BeatLink.DbServer;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// Provides support for navigating the menu hierarchy offered by the dbserver on a player for a
/// particular media slot. Note that for historical reasons, loading track metadata, playlists, and
/// the full track list are performed by the <see cref="MetadataFinder"/>, even though those are
/// technically menu operations.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>MenuLoader</c>, by James Elliott. The Java
/// implementation repeats the lock/request/render pattern in every method; this port centralizes
/// it in <see cref="RequestMenu"/>, but each public method sends byte-identical requests.
/// </remarks>
public sealed class MenuLoader
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<MenuLoader>();

    /// <summary>
    /// The shared implementation of the simple menu requests: locks the client for menu
    /// operations, sends the specified menu request for rekordbox tracks with the supplied
    /// numeric arguments (sort order first), and renders all the resulting menu items.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="description">describes the request, for error reporting</param>
    /// <param name="logLabel">the name of the menu, used in the debug log message</param>
    /// <param name="requestType">identifies what kind of menu request to send</param>
    /// <param name="numericArguments">the numeric arguments the request takes, starting with the
    /// sort order</param>
    /// <returns>the entries in the requested menu</returns>
    /// <exception cref="IOException">if there is a problem communicating</exception>
    /// <exception cref="TimeoutException">if we are unable to lock the client for menu operations</exception>
    /// <exception cref="InvalidOperationException">if the connection manager is not running or the
    /// player cannot be located</exception>
    private static IReadOnlyList<Message> RequestMenu(SlotReference slotReference, string description,
        string logLabel, Message.KnownType requestType, params long[] numericArguments)
    {
        ArgumentNullException.ThrowIfNull(slotReference);
        return ConnectionManager.Instance.InvokeWithClientSession(slotReference.Player, client =>
        {
            if (client.TryLockingForMenuOperations(MetadataFinder.MenuTimeout))
            {
                try
                {
                    Logger.LogDebug("Requesting {Menu} menu.", logLabel);
                    var arguments = new Field[numericArguments.Length];
                    for (var i = 0; i < numericArguments.Length; i++)
                    {
                        arguments[i] = new NumberField(numericArguments[i]);
                    }
                    var response = client.MenuRequest(requestType, Message.MenuIdentifier.MainMenu,
                        slotReference.Slot, arguments);
                    return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slotReference.Slot,
                        CdjStatus.TrackType.Rekordbox, response);
                }
                finally
                {
                    client.UnlockForMenuOperations();
                }
            }
            throw new TimeoutException("Unable to lock player for menu operations.");
        }, description);
    }

    #region Root, playlist, history, and track menus

    /// <summary>
    /// Ask the specified player for its top-level menu of menus. The <see cref="MetadataFinder"/>
    /// must be running for us to know the right kind of message to send, because it depends on
    /// whether the slot holds a rekordbox database or not. If we can't tell (because it's not
    /// running), we will just guess that there is one, and perhaps get back nothing.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default, see the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/track_metadata.html#alternate-track-sort-orders">Packet Analysis document</a>
    /// for details, although it does not seem to have an effect on the root menu</param>
    /// <returns>the entries in the top level menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestRootMenuFrom(SlotReference slotReference, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(slotReference);
        return ConnectionManager.Instance.InvokeWithClientSession(slotReference.Player, client =>
        {
            if (client.TryLockingForMenuOperations(MetadataFinder.MenuTimeout))
            {
                try
                {
                    var details = MetadataFinder.Instance.GetMediaDetailsFor(slotReference);
                    var mediaType = details == null ? CdjStatus.TrackType.Rekordbox : details.MediaType;

                    var response = client.MenuRequestTyped(Message.KnownType.RootMenuReq,
                        Message.MenuIdentifier.MainMenu, slotReference.Slot, mediaType,
                        new NumberField(sortOrder), new NumberField(0xffffff));
                    return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slotReference.Slot,
                        mediaType, response);
                }
                finally
                {
                    client.UnlockForMenuOperations();
                }
            }
            throw new TimeoutException("Unable to lock player for menu operations.");
        }, "requesting root menu");
    }

    /// <summary>
    /// Ask the specified player for a Playlist menu. This boils down to a call to
    /// <see cref="MetadataFinder.RequestPlaylistItemsFrom"/> asking for the playlist folder with
    /// ID 0, but it is also made available here since this is likely where people will be looking
    /// for the capability. To get the contents of individual playlists or sub-folders, pass the
    /// playlist or folder ID obtained by calling this to that function.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the playlists and folders in the playlist menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    /// <seealso cref="MetadataFinder.RequestPlaylistItemsFrom"/>
    public IReadOnlyList<Message> RequestPlaylistMenuFrom(SlotReference slotReference, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(slotReference);
        return MetadataFinder.Instance.RequestPlaylistItemsFrom(slotReference.Player, slotReference.Slot,
            sortOrder, 0, true);
    }

    /// <summary>
    /// Ask the specified player for a History menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default,
    /// although it does not seem to have an effect on the history menu</param>
    /// <returns>the entries in the history menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestHistoryMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting history menu", "History",
            Message.KnownType.HistoryMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a History playlist.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the playlist is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="historyId">identifies which history session's playlist is desired</param>
    /// <returns>the entries in the history playlist</returns>
    /// <exception cref="Exception">if there is a problem obtaining the playlist</exception>
    public IReadOnlyList<Message> RequestHistoryPlaylistFrom(SlotReference slotReference, int sortOrder, int historyId) =>
        RequestMenu(slotReference, "requesting history playlist", "History playlist",
            Message.KnownType.TrackMenuForHistoryReq, sortOrder, historyId);

    /// <summary>
    /// Ask the specified player for a Track menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the track menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTrackMenuFrom(SlotReference slotReference, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(slotReference);
        return ConnectionManager.Instance.InvokeWithClientSession(slotReference.Player,
            client => MetadataFinder.Instance.GetFullTrackList(slotReference.Slot, client, sortOrder),
            "requesting track menu");
    }

    #endregion

    #region Artist menus

    /// <summary>
    /// Ask the specified player for an Artist menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the artist menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestArtistMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting artist menu", "Artist",
            Message.KnownType.ArtistMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for an Artist Album menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="artistId">the artist whose album menu is desired</param>
    /// <returns>the entries in the artist album menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestArtistAlbumMenuFrom(SlotReference slotReference, int sortOrder, int artistId) =>
        RequestMenu(slotReference, "requesting artist album menu", "Artist Album",
            Message.KnownType.AlbumMenuForArtistReq, sortOrder, artistId);

    /// <summary>
    /// Ask the specified player for an Artist Album Tracks menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="artistId">the artist whose album track menu is desired</param>
    /// <param name="albumId">the album whose track menu is desired, or -1 for all albums</param>
    /// <returns>the entries in the artist album tracks menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestArtistAlbumTrackMenuFrom(SlotReference slotReference, int sortOrder,
        int artistId, int albumId) =>
        RequestMenu(slotReference, "requesting artist album tracks menu", "Artist Album Track",
            Message.KnownType.TrackMenuForArtistAndAlbum, sortOrder, artistId, albumId);

    /// <summary>
    /// Ask the specified player for an Original Artist menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the original artist menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestOriginalArtistMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting artist menu", "Original Artist",
            Message.KnownType.OriginalArtistMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for an Original Artist Album menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="artistId">the original artist whose album menu is desired</param>
    /// <returns>the entries in the original artist album menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestOriginalArtistAlbumMenuFrom(SlotReference slotReference, int sortOrder,
        int artistId) =>
        RequestMenu(slotReference, "requesting artist album menu", "Original Artist Album",
            Message.KnownType.AlbumMenuForOriginalArtistReq, sortOrder, artistId);

    /// <summary>
    /// Ask the specified player for an Original Artist Album Tracks menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="artistId">the original artist whose album track menu is desired</param>
    /// <param name="albumId">the album whose track menu is desired, or -1 for all albums</param>
    /// <returns>the entries in the original artist album tracks menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestOriginalArtistAlbumTrackMenuFrom(SlotReference slotReference, int sortOrder,
        int artistId, int albumId) =>
        RequestMenu(slotReference, "requesting artist album tracks menu", "Original Artist Album Track",
            Message.KnownType.TrackMenuForOriginalArtistAndAlbum, sortOrder, artistId, albumId);

    /// <summary>
    /// Ask the specified player for a Remixer menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the remixer menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestRemixerMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting artist menu", "Remixer",
            Message.KnownType.RemixerMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a Remixer Album menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="artistId">the remixer whose album menu is desired</param>
    /// <returns>the entries in the remixer album menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestRemixerAlbumMenuFrom(SlotReference slotReference, int sortOrder, int artistId) =>
        RequestMenu(slotReference, "requesting artist album menu", "Remixer Album",
            Message.KnownType.AlbumMenuForRemixerReq, sortOrder, artistId);

    /// <summary>
    /// Ask the specified player for a Remixer Album Tracks menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="artistId">the remixer whose album track menu is desired</param>
    /// <param name="albumId">the album whose track menu is desired, or -1 for all albums</param>
    /// <returns>the entries in the remixer album tracks menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestRemixerAlbumTrackMenuFrom(SlotReference slotReference, int sortOrder,
        int artistId, int albumId) =>
        RequestMenu(slotReference, "requesting artist album tracks menu", "Remixer Album Track",
            Message.KnownType.TrackMenuForRemixerAndAlbum, sortOrder, artistId, albumId);

    #endregion

    #region Album and genre menus

    /// <summary>
    /// Ask the specified player for an Album Track menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="albumId">the album whose track menu is desired</param>
    /// <returns>the entries in the album track menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestAlbumTrackMenuFrom(SlotReference slotReference, int sortOrder, int albumId) =>
        RequestMenu(slotReference, "requesting album tracks menu", "Album Track",
            Message.KnownType.TrackMenuForAlbumReq, sortOrder, albumId);

    /// <summary>
    /// Ask the specified player for an Album menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the album menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestAlbumMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting album menu", "Album",
            Message.KnownType.AlbumMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a Genre menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the genre menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestGenreMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting genre menu", "Genre",
            Message.KnownType.GenreMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a Genre Artists menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="genreId">the genre whose artist menu is desired</param>
    /// <returns>the entries in the genre artists menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestGenreArtistMenuFrom(SlotReference slotReference, int sortOrder, int genreId) =>
        RequestMenu(slotReference, "requesting genre artists menu", "Genre Artist",
            Message.KnownType.ArtistMenuForGenreReq, sortOrder, genreId);

    /// <summary>
    /// Ask the specified player for a Genre Artist Albums menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="genreId">the genre whose artist album menu is desired</param>
    /// <param name="artistId">the artist whose album menu is desired, or -1 for all artists</param>
    /// <returns>the entries in the genre artist albums menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestGenreArtistAlbumMenuFrom(SlotReference slotReference, int sortOrder,
        int genreId, int artistId) =>
        RequestMenu(slotReference, "requesting genre artist albums menu", "Genre Artist Album",
            Message.KnownType.AlbumMenuForGenreAndArtist, sortOrder, genreId, artistId);

    /// <summary>
    /// Ask the specified player for a Genre Artist Album Tracks menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="genreId">the genre whose artist album track menu is desired</param>
    /// <param name="artistId">the artist whose album track menu is desired, or -1 for all artists</param>
    /// <param name="albumId">the album whose track menu is desired, or -1 for all albums</param>
    /// <returns>the entries in the genre artist album tracks menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestGenreArtistAlbumTrackMenuFrom(SlotReference slotReference, int sortOrder,
        int genreId, int artistId, int albumId) =>
        RequestMenu(slotReference, "requesting genre artist album tracks menu", "Genre Artist Album Track",
            Message.KnownType.TrackMenuForGenreArtistAndAlbum, sortOrder, genreId, artistId, albumId);

    #endregion

    #region Label menus

    /// <summary>
    /// Ask the specified player for a Label menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the label menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestLabelMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting genre menu", "Label",
            Message.KnownType.LabelMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a Label Artists menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="labelId">the label whose artist menu is desired</param>
    /// <returns>the entries in the label artists menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestLabelArtistMenuFrom(SlotReference slotReference, int sortOrder, int labelId) =>
        RequestMenu(slotReference, "requesting genre artists menu", "Label Artist",
            Message.KnownType.ArtistMenuForLabelReq, sortOrder, labelId);

    /// <summary>
    /// Ask the specified player for a Label Artist Albums menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="labelId">the label whose artist album menu is desired</param>
    /// <param name="artistId">the artist whose album menu is desired, or -1 for all artists</param>
    /// <returns>the entries in the label artist albums menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestLabelArtistAlbumMenuFrom(SlotReference slotReference, int sortOrder,
        int labelId, int artistId) =>
        RequestMenu(slotReference, "requesting genre artist albums menu", "Label Artist Album",
            Message.KnownType.AlbumMenuForLabelAndArtist, sortOrder, labelId, artistId);

    /// <summary>
    /// Ask the specified player for a Label Artist Album Tracks menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="labelId">the label whose artist album track menu is desired</param>
    /// <param name="artistId">the artist whose album track menu is desired, or -1 for all artists</param>
    /// <param name="albumId">the album whose track menu is desired, or -1 for all albums</param>
    /// <returns>the entries in the label artist album tracks menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestLabelArtistAlbumTrackMenuFrom(SlotReference slotReference, int sortOrder,
        int labelId, int artistId, int albumId) =>
        RequestMenu(slotReference, "requesting genre artist album tracks menu", "Label Artist Album Track",
            Message.KnownType.TrackMenuForLabelArtistAndAlbum, sortOrder, labelId, artistId, albumId);

    #endregion

    #region Key, BPM, rating, color, time, bit rate, and year menus

    /// <summary>
    /// Ask the specified player for a Key menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the key menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestKeyMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting key menu", "Key",
            Message.KnownType.KeyMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a key neighbor menu for a given key.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="keyId">the key whose available compatible keys are desired</param>
    /// <returns>the entries in the key neighbor menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestKeyNeighborMenuFrom(SlotReference slotReference, int sortOrder, int keyId) =>
        RequestMenu(slotReference, "requesting key neighbor menu", "key neighbor",
            Message.KnownType.NeighborMenuForKey, sortOrder, keyId);

    /// <summary>
    /// Ask the specified player for a track menu for an allowed distance from a given key.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="keyId">the key whose compatible tracks are desired</param>
    /// <param name="distance">how far along the circle of fifths the tracks are allowed to be from
    /// the specified key</param>
    /// <returns>the matching tracks</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTracksByKeyAndDistanceFrom(SlotReference slotReference, int sortOrder,
        int keyId, int distance) =>
        RequestMenu(slotReference, "requesting tracks by key and distance menu", "tracks by key and distance",
            Message.KnownType.TrackMenuForKeyAndDistance, sortOrder, keyId, distance);

    /// <summary>
    /// Ask the specified player for a BPM menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the BPM menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestBpmMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting BPM menu", "BPM",
            Message.KnownType.BpmMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a tempo range menu for a given BPM.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="bpm">the tempo whose nearby ranges are desired</param>
    /// <returns>the entries in the tempo range menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestBpmRangeMenuFrom(SlotReference slotReference, int sortOrder, int bpm) =>
        RequestMenu(slotReference, "requesting tempo range menu", "tempo neighbor",
            Message.KnownType.BpmRangeReq, sortOrder, bpm);

    /// <summary>
    /// Ask the specified player for tracks whose tempo falls within a specific percentage of a
    /// given BPM.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="bpm">the tempo that tracks must be close to</param>
    /// <param name="range">the percentage by which the actual tempo may differ for a track to
    /// still be returned</param>
    /// <returns>the tracks whose tempo falls within the specified range</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTracksByBpmRangeFrom(SlotReference slotReference, int sortOrder,
        int bpm, int range) =>
        RequestMenu(slotReference, "requesting tracks within tempo range menu", "tracks by bpm range",
            Message.KnownType.TrackMenuForBpmAndDistance, sortOrder, bpm, range);

    /// <summary>
    /// Ask the specified player for a Rating menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the rating menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestRatingMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting rating menu", "Rating",
            Message.KnownType.RatingMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a track menu for a given rating.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="rating">the desired rating for tracks to be returned</param>
    /// <returns>the matching tracks</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTracksByRatingFrom(SlotReference slotReference, int sortOrder, int rating) =>
        RequestMenu(slotReference, "requesting tracks by rating menu", "tracks by rating",
            Message.KnownType.TrackMenuForRatingReq, sortOrder, rating);

    /// <summary>
    /// Ask the specified player for a Color menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the color menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestColorMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting color menu", "Color",
            Message.KnownType.ColorMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a track menu for a given color.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="color">the desired color for tracks to be returned</param>
    /// <returns>the matching tracks</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTracksByColorFrom(SlotReference slotReference, int sortOrder, int color) =>
        RequestMenu(slotReference, "requesting tracks by color menu", "tracks by color",
            Message.KnownType.TrackMenuForColorReq, sortOrder, color);

    /// <summary>
    /// Ask the specified player for a Time menu, grouping tracks by their length in minutes.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the time menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTimeMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting time menu", "Time",
            Message.KnownType.TimeMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a track menu for a given time (track length in minutes).
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="time">the length in minutes of tracks to be returned</param>
    /// <returns>the matching tracks</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTracksByTimeFrom(SlotReference slotReference, int sortOrder, int time) =>
        RequestMenu(slotReference, "requesting tracks by time menu", "tracks by time",
            Message.KnownType.TrackMenuForTimeReq, sortOrder, time);

    /// <summary>
    /// Ask the specified player for a Bit Rate menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the bit rate menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestBitRateMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting genre menu", "Bit Rate",
            Message.KnownType.BitRateMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a track menu for a given track bit rate (in Kbps).
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="bitRate">the bit rate, in kilobits per second, of tracks to be returned</param>
    /// <returns>the matching tracks</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTracksByBitRateFrom(SlotReference slotReference, int sortOrder, int bitRate) =>
        RequestMenu(slotReference, "requesting tracks by time menu", "tracks by bit rate",
            Message.KnownType.TrackMenuForBitRateReq, sortOrder, bitRate);

    /// <summary>
    /// Ask the specified player for a Year menu, grouping years by decade.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the year menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestYearMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting year menu", "Year",
            Message.KnownType.YearMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a year menu for a given decade.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="decade">narrows the years of tracks to be returned</param>
    /// <returns>the matching years</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestYearsByDecadeFrom(SlotReference slotReference, int sortOrder, int decade) =>
        RequestMenu(slotReference, "requesting years by decade menu", "years by decade",
            Message.KnownType.YearMenuForDecadeReq, sortOrder, decade);

    /// <summary>
    /// Ask the specified player for a track menu for a decade and year.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="decade">the decade for which tracks are desired</param>
    /// <param name="year">the specific year for which tracks are desired, or -1 for all years
    /// within the specified decade</param>
    /// <returns>the matching tracks</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestTracksByDecadeAndYear(SlotReference slotReference, int sortOrder,
        int decade, int year) =>
        RequestMenu(slotReference, "requesting tracks by decade and year menu", "tracks by decade and year",
            Message.KnownType.TrackMenuForDecadeYearReq, sortOrder, decade, year);

    #endregion

    #region Filename and folder menus

    /// <summary>
    /// Ask the specified player for a Filename menu.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the entries in the filename menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestFilenameMenuFrom(SlotReference slotReference, int sortOrder) =>
        RequestMenu(slotReference, "requesting filename menu", "Filename",
            Message.KnownType.FilenameMenuReq, sortOrder);

    /// <summary>
    /// Ask the specified player for a Folder menu for exploring its raw filesystem. This is a
    /// request for unanalyzed items, so we do a typed menu request.
    /// </summary>
    /// <param name="slotReference">the player and slot for which the menu is desired</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="folderId">identifies the folder whose contents should be listed, use -1 to get
    /// the root folder</param>
    /// <returns>the entries in the folder menu</returns>
    /// <exception cref="Exception">if there is a problem obtaining the menu</exception>
    public IReadOnlyList<Message> RequestFolderMenuFrom(SlotReference slotReference, int sortOrder, int folderId)
    {
        ArgumentNullException.ThrowIfNull(slotReference);
        return ConnectionManager.Instance.InvokeWithClientSession(slotReference.Player, client =>
        {
            if (client.TryLockingForMenuOperations(MetadataFinder.MenuTimeout))
            {
                try
                {
                    Logger.LogDebug("Requesting Folder menu.");
                    var response = client.MenuRequestTyped(Message.KnownType.FolderMenuReq,
                        Message.MenuIdentifier.MainMenu, slotReference.Slot, CdjStatus.TrackType.Unanalyzed,
                        new NumberField(sortOrder), new NumberField(folderId), new NumberField(0xffffff));
                    return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slotReference.Slot,
                        CdjStatus.TrackType.Unanalyzed, response);
                }
                finally
                {
                    client.UnlockForMenuOperations();
                }
            }
            throw new TimeoutException("Unable to lock player for menu operations.");
        }, "requesting folder menu");
    }

    #endregion

    #region Search

    /// <summary>
    /// Ask the connected dbserver about database records whose names contain <paramref name="text"/>.
    /// If <paramref name="limit"/> is not <c>null</c>, no more than that many results will be
    /// returned; the total number of matches found is reported through
    /// <paramref name="totalAvailable"/> either way.
    /// </summary>
    /// <param name="slot">the slot in which the database can be found</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default,
    /// although it does not seem to have an effect on searches</param>
    /// <param name="text">the search text used to filter the results (must already be uppercase)</param>
    /// <param name="limit">an upper limit on the number of results to return, or <c>null</c> to
    /// return them all</param>
    /// <param name="totalAvailable">receives the total number of matches that were available</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the items that match the specified search string; they may be a variety of
    /// different types</returns>
    /// <exception cref="IOException">if there is a problem communicating</exception>
    /// <exception cref="TimeoutException">if we are unable to lock the client for menu operations</exception>
    private static IReadOnlyList<Message> GetSearchItems(CdjStatus.TrackSourceSlot slot, int sortOrder,
        string text, int? limit, out int totalAvailable, Client client)
    {
        if (client.TryLockingForMenuOperations(MetadataFinder.MenuTimeout))
        {
            try
            {
                var textField = new StringField(text);
                var response = client.MenuRequest(Message.KnownType.SearchMenu, Message.MenuIdentifier.MainMenu, slot,
                    new NumberField(sortOrder), new NumberField(textField.Size), textField, NumberField.Word0);
                var reportedCount = response.GetMenuResultsCount();
                if (reportedCount == Message.NoMenuResultsAvailable || reportedCount == 0)
                {
                    totalAvailable = 0;
                    return [];
                }
                var actualCount = (int)reportedCount;

                // Gather the requested number of search menu items.
                totalAvailable = actualCount;
                if (limit == null)
                {
                    return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slot,
                        CdjStatus.TrackType.Rekordbox, response);
                }
                var desiredCount = Math.Min(limit.Value, actualCount);
                if (desiredCount < 1)
                {
                    return [];
                }
                return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slot,
                    CdjStatus.TrackType.Rekordbox, 0, desiredCount);
            }
            finally
            {
                client.UnlockForMenuOperations();
            }
        }
        throw new TimeoutException("Unable to lock player for menu operations.");
    }

    /// <summary>
    /// Ask the specified player for database records whose names contain <paramref name="text"/>.
    /// All results are returned; to limit the number of results returned and learn how many more
    /// are available, use the overload accepting a limit. (In the Java implementation both
    /// behaviors share one method whose <c>AtomicInteger</c> argument is both the limit and the
    /// result count.)
    /// </summary>
    /// <param name="player">the player number whose database is to be searched</param>
    /// <param name="slot">the slot in which the database can be found</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default,
    /// although it does not seem to have an effect on searches</param>
    /// <param name="text">the search text used to filter the results</param>
    /// <returns>the items that match the specified search string; they may be a variety of
    /// different types</returns>
    /// <exception cref="Exception">if there is a problem performing the search</exception>
    public IReadOnlyList<Message> RequestSearchResultsFrom(int player, CdjStatus.TrackSourceSlot slot,
        int sortOrder, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ConnectionManager.Instance.InvokeWithClientSession(player,
            client => GetSearchItems(slot, sortOrder, text.ToUpperInvariant(), null, out _, client),
            "performing search");
    }

    /// <summary>
    /// Ask the specified player for database records whose names contain <paramref name="text"/>.
    /// No more than <paramref name="limit"/> results will be returned, and
    /// <paramref name="totalAvailable"/> will be set to the total number of results that were
    /// available.
    /// </summary>
    /// <param name="player">the player number whose database is to be searched</param>
    /// <param name="slot">the slot in which the database can be found</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default,
    /// although it does not seem to have an effect on searches</param>
    /// <param name="text">the search text used to filter the results</param>
    /// <param name="limit">an upper limit on the number of results to return</param>
    /// <param name="totalAvailable">receives the total number of matches that were available</param>
    /// <returns>the items that match the specified search string; they may be a variety of
    /// different types</returns>
    /// <exception cref="Exception">if there is a problem performing the search</exception>
    public IReadOnlyList<Message> RequestSearchResultsFrom(int player, CdjStatus.TrackSourceSlot slot,
        int sortOrder, string text, int limit, out int totalAvailable)
    {
        ArgumentNullException.ThrowIfNull(text);
        var total = 0;
        var results = ConnectionManager.Instance.InvokeWithClientSession(player,
            client => GetSearchItems(slot, sortOrder, text.ToUpperInvariant(), limit, out total, client),
            "performing search");
        totalAvailable = total;
        return results;
    }

    /// <summary>
    /// Ask the connected dbserver about database records whose names contain <paramref name="text"/>,
    /// returning the specified range of results.
    /// </summary>
    /// <param name="slot">the slot in which the database can be found</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <param name="text">the search text used to filter the results (must already be uppercase)</param>
    /// <param name="offset">the first result desired (the first available result has offset 0)</param>
    /// <param name="count">the number of results to return</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the items that match the specified search string</returns>
    /// <exception cref="IOException">if there is a problem communicating</exception>
    /// <exception cref="TimeoutException">if we are unable to lock the client for menu operations</exception>
    /// <exception cref="ArgumentException">if the requested range extends past the end of the menu</exception>
    private static IReadOnlyList<Message> GetMoreSearchItems(CdjStatus.TrackSourceSlot slot, int sortOrder,
        string text, int offset, int count, Client client)
    {
        if (client.TryLockingForMenuOperations(MetadataFinder.MenuTimeout))
        {
            try
            {
                var textField = new StringField(text);
                var response = client.MenuRequest(Message.KnownType.SearchMenu, Message.MenuIdentifier.MainMenu, slot,
                    new NumberField(sortOrder), new NumberField(textField.Size), textField, NumberField.Word0);
                var actualCount = (int)response.GetMenuResultsCount();
                if (offset + count > actualCount)
                {
                    throw new ArgumentException("Cannot request items past the end of the menu.");
                }

                // Gather the requested search menu items.
                return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slot,
                    CdjStatus.TrackType.Rekordbox, offset, count);
            }
            finally
            {
                client.UnlockForMenuOperations();
            }
        }
        throw new TimeoutException("Unable to lock player for menu operations.");
    }

    /// <summary>
    /// Ask the specified player for more database records whose names contain
    /// <paramref name="text"/>. This can be used after calling
    /// <see cref="RequestSearchResultsFrom(int, CdjStatus.TrackSourceSlot, int, string, int, out int)"/>
    /// to obtain a partial result and the total count available, to gradually expand the search
    /// under direction from the user.
    /// </summary>
    /// <param name="player">the player number whose database is to be searched</param>
    /// <param name="slot">the slot in which the database can be found</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default,
    /// although it does not seem to have an effect on searches</param>
    /// <param name="text">the search text used to filter the results</param>
    /// <param name="offset">the first result desired (the first available result has offset 0)</param>
    /// <param name="count">the number of results to return (if more than the number available,
    /// fewer will simply be returned)</param>
    /// <returns>the items that match the specified search string; they may be a variety of
    /// different types</returns>
    /// <exception cref="Exception">if there is a problem performing the search</exception>
    public IReadOnlyList<Message> RequestMoreSearchResultsFrom(int player, CdjStatus.TrackSourceSlot slot,
        int sortOrder, string text, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ConnectionManager.Instance.InvokeWithClientSession(player,
            client => GetMoreSearchItems(slot, sortOrder, text.ToUpperInvariant(), offset, count, client),
            "performing search");
    }

    #endregion

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static MenuLoader Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private MenuLoader()
    {
        // Nothing to do.
    }
}
