using System.Net;
using System.Net.Sockets;
using BeatLink.Data;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Exercises a representative subset of the <see cref="MenuLoader"/> menu requests against a
/// scripted fake dbserver reached through the full <see cref="ConnectionManager"/> stack (with
/// the port query redirected to a fake loopback server), verifying the request messages that are
/// sent (R:M:S:T packing, request types, and arguments) and the parsing of the rendered menu.
/// </summary>
[Collection("Network services")]
public sealed class MenuLoaderTests : IDisposable
{
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(15);

    private const int Player = FinderTestHelpers.Player;

    private static SlotReference UsbSlot => FinderTestHelpers.UsbSlot;

    public MenuLoaderTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    private static void ResetState()
    {
        ConnectionManager.Instance.Stop();
        DeviceFinder.Instance.Stop();
        DeviceFinder.Instance.PortOverride = null;
        DeviceFinder.Instance.RemoveIgnoredAddress(IPAddress.Loopback);
        ConnectionManager.Instance.DbServerQueryPortOverride = null;
        VirtualCdj.Instance.ResetForTests();
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Starts a fake dbserver on an ephemeral loopback port which accepts a single connection,
    /// serves the handshake, and then serves menu requests: each menu request is answered with a
    /// MENU_AVAILABLE reporting <paramref name="itemCount"/> items, and each render request is
    /// answered with a header, the requested number of items (titled "Item N"), and a footer.
    /// Returns every message the server read.
    /// </summary>
    private static (int Port, Task<List<Message>> Log) StartMenuDbServer(int itemCount)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(() =>
        {
            var log = new List<Message>();
            try
            {
                using var socket = listener.AcceptSocket();
                using var stream = new NetworkStream(socket, ownsSocket: false);

                // Serve the greeting and setup exchanges.
                var greeting = Assert.IsType<NumberField>(Field.Read(stream));
                Assert.Equal(1, greeting.Value);
                new NumberField(1, 4).Write(stream);
                var setup = Message.Read(stream);
                log.Add(setup);
                new Message(0xfffffffeL, Message.KnownType.MenuAvailable,
                    new NumberField(Message.KnownType.SetupReq.ProtocolValue),
                    new NumberField(Player)).Write(stream);

                // Serve menu and render requests generically until the teardown message (or an
                // abrupt disconnection) ends the conversation.
                while (true)
                {
                    Message request;
                    try
                    {
                        request = Message.Read(stream);
                    }
                    catch (IOException)
                    {
                        break;  // The client disconnected without a polite teardown.
                    }
                    log.Add(request);
                    if (request.RecognizedType == Message.KnownType.TeardownReq)
                    {
                        break;
                    }
                    if (request.RecognizedType == Message.KnownType.RenderMenuReq)
                    {
                        var offset = (int)((NumberField)request.Arguments[1]).Value;
                        var limit = (int)((NumberField)request.Arguments[2]).Value;
                        new Message(request.Transaction.Value, Message.KnownType.MenuHeader).Write(stream);
                        for (var i = 0; i < limit; i++)
                        {
                            new Message(request.Transaction.Value, Message.KnownType.MenuItem,
                                new NumberField(0), new NumberField(offset + i), new NumberField(0),
                                new StringField($"Item {offset + i}"), new NumberField(0), new StringField(""),
                                new NumberField(0x0004)).Write(stream);
                        }
                        new Message(request.Transaction.Value, Message.KnownType.MenuFooter).Write(stream);
                    }
                    else
                    {
                        new Message(request.Transaction.Value, Message.KnownType.MenuAvailable,
                            new NumberField(request.MessageType.Value), new NumberField(itemCount)).Write(stream);
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
            return log;
        });
        return (port, task);
    }

    /// <summary>
    /// Starts the connection manager with the port query redirected to a fake server reporting
    /// the supplied dbserver port, and announces the test player.
    /// </summary>
    private static void StartConnectionManager(int dbPort, out FinderTestHelpers.FakeQueryServer queryServer)
    {
        queryServer = new FinderTestHelpers.FakeQueryServer(dbPort);
        DeviceFinder.Instance.PortOverride = 0;
        ConnectionManager.Instance.DbServerQueryPortOverride = queryServer.Port;
        ConnectionManager.Instance.Start();
        ServiceTestHelpers.SendToLoopback(DeviceFinder.Instance.LocalPort!.Value,
            ServiceTestHelpers.KeepAlivePacket("CDJ-3000", Player));
        FinderTestHelpers.WaitUntil(
            () => ConnectionManager.Instance.GetPlayerDBServerPort(Player) == dbPort,
            "the fake dbserver port to be cached");
    }

    private static List<Message> AwaitLog(Task<List<Message>> log)
    {
        Assert.True(log.Wait(ServerTimeout), "fake dbserver script did not complete");
        return log.Result;
    }

    /// <summary>
    /// The R:M:S:T value expected for requests posed by our (unassigned, number 0) virtual player
    /// against the USB slot of the test player for rekordbox tracks, addressed to the main menu.
    /// </summary>
    private const long MainMenuRmst = (0L << 24) | (1L << 16) | (3L << 8) | 1L;

    [Fact]
    public void RequestsRootMenuWithTypedRequest()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 2);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestRootMenuFrom(UsbSlot, sortOrder: 0);
        var messages = AwaitLog(log);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(Message.KnownType.MenuItem, item.RecognizedType));
        Assert.Equal("Item 0", ((StringField)items[0].Arguments[3]).Value);
        Assert.Equal("Item 1", ((StringField)items[1].Arguments[3]).Value);

        // The menu request named the root menu with the magic constant, defaulting the media type
        // to rekordbox since the MetadataFinder has no details for the slot.
        var menuRequest = messages[1];
        Assert.Equal(Message.KnownType.RootMenuReq, menuRequest.RecognizedType);
        Assert.Equal(MainMenuRmst, ((NumberField)menuRequest.Arguments[0]).Value);
        Assert.Equal(0, ((NumberField)menuRequest.Arguments[1]).Value);
        Assert.Equal(0xffffff, ((NumberField)menuRequest.Arguments[2]).Value);

        // The render request was correctly bounded.
        var render = messages[2];
        Assert.Equal(Message.KnownType.RenderMenuReq, render.RecognizedType);
        Assert.Equal(MainMenuRmst, ((NumberField)render.Arguments[0]).Value);
        Assert.Equal(0, ((NumberField)render.Arguments[1]).Value);   // Offset.
        Assert.Equal(2, ((NumberField)render.Arguments[2]).Value);   // Limit.
    }

    [Fact]
    public void RequestsPlaylistMenuAsRootFolder()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 1);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestPlaylistMenuFrom(UsbSlot, sortOrder: 0);
        var messages = AwaitLog(log);

        Assert.Single(items);

        // The playlist menu is the playlist request for folder 0, with the folder flag set.
        var menuRequest = messages[1];
        Assert.Equal(Message.KnownType.PlaylistReq, menuRequest.RecognizedType);
        Assert.Equal(MainMenuRmst, ((NumberField)menuRequest.Arguments[0]).Value);
        Assert.Equal(0, ((NumberField)menuRequest.Arguments[1]).Value);  // Sort order.
        Assert.Equal(0, ((NumberField)menuRequest.Arguments[2]).Value);  // Folder ID 0 (the root).
        Assert.Equal(1, ((NumberField)menuRequest.Arguments[3]).Value);  // Asking for a folder.
    }

    [Fact]
    public void RequestsPlaylistItemsWithIdentifiers()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 1);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MetadataFinder.Instance.RequestPlaylistItemsFrom(Player,
            CdjStatus.TrackSourceSlot.UsbSlot, sortOrder: 3, playlistOrFolderId: 25, folder: false);
        var messages = AwaitLog(log);

        Assert.Single(items);
        var menuRequest = messages[1];
        Assert.Equal(Message.KnownType.PlaylistReq, menuRequest.RecognizedType);
        Assert.Equal(3, ((NumberField)menuRequest.Arguments[1]).Value);   // Sort order.
        Assert.Equal(25, ((NumberField)menuRequest.Arguments[2]).Value);  // Playlist ID.
        Assert.Equal(0, ((NumberField)menuRequest.Arguments[3]).Value);   // Asking for a playlist.
    }

    [Fact]
    public void RequestsArtistMenuWithSortOrder()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 3);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestArtistMenuFrom(UsbSlot, sortOrder: 2);
        var messages = AwaitLog(log);

        Assert.Equal(3, items.Count);
        var menuRequest = messages[1];
        Assert.Equal(Message.KnownType.ArtistMenuReq, menuRequest.RecognizedType);
        Assert.Equal(MainMenuRmst, ((NumberField)menuRequest.Arguments[0]).Value);
        Assert.Equal(2, ((NumberField)menuRequest.Arguments[1]).Value);  // The requested sort order.
        Assert.Equal(2, menuRequest.Arguments.Count);
    }

    [Fact]
    public void RequestsGenreArtistAlbumTrackMenuWithAllIdentifiers()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 1);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestGenreArtistAlbumTrackMenuFrom(UsbSlot,
            sortOrder: 1, genreId: 5, artistId: -1, albumId: 7);
        var messages = AwaitLog(log);

        Assert.Single(items);
        var menuRequest = messages[1];
        Assert.Equal(Message.KnownType.TrackMenuForGenreArtistAndAlbum, menuRequest.RecognizedType);
        Assert.Equal(MainMenuRmst, ((NumberField)menuRequest.Arguments[0]).Value);
        Assert.Equal(1, ((NumberField)menuRequest.Arguments[1]).Value);
        Assert.Equal(5, ((NumberField)menuRequest.Arguments[2]).Value);
        Assert.Equal(0xffffffffL, ((NumberField)menuRequest.Arguments[3]).Value);  // -1: all artists.
        Assert.Equal(7, ((NumberField)menuRequest.Arguments[4]).Value);
    }

    [Fact]
    public void EmptyMenusReturnNoItemsWithoutRendering()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 0);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestKeyMenuFrom(UsbSlot, sortOrder: 0);
        var messages = AwaitLog(log);

        Assert.Empty(items);
        Assert.Equal(Message.KnownType.KeyMenuReq, messages[1].RecognizedType);
        // Only the setup, the menu request, and the teardown were seen: no render request.
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public void SearchUppercasesTextAndHonorsLimit()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 5);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestSearchResultsFrom(Player,
            CdjStatus.TrackSourceSlot.UsbSlot, sortOrder: 0, text: "beat", limit: 3, out var totalAvailable);
        var messages = AwaitLog(log);

        // Only the requested number of items came back, with the true total reported.
        Assert.Equal(3, items.Count);
        Assert.Equal(5, totalAvailable);

        // The search request carried the uppercased text with its byte size.
        var searchRequest = messages[1];
        Assert.Equal(Message.KnownType.SearchMenu, searchRequest.RecognizedType);
        Assert.Equal(MainMenuRmst, ((NumberField)searchRequest.Arguments[0]).Value);
        Assert.Equal(0, ((NumberField)searchRequest.Arguments[1]).Value);
        var textField = Assert.IsType<StringField>(searchRequest.Arguments[3]);
        Assert.Equal("BEAT", textField.Value);
        Assert.Equal(textField.Size, ((NumberField)searchRequest.Arguments[2]).Value);
        Assert.Equal(0, ((NumberField)searchRequest.Arguments[4]).Value);

        // The render request was bounded by the limit, not the total.
        var render = messages[2];
        Assert.Equal(Message.KnownType.RenderMenuReq, render.RecognizedType);
        Assert.Equal(0, ((NumberField)render.Arguments[1]).Value);
        Assert.Equal(3, ((NumberField)render.Arguments[2]).Value);
    }

    [Fact]
    public void MoreSearchResultsUseOffsetAndCount()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 5);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestMoreSearchResultsFrom(Player,
            CdjStatus.TrackSourceSlot.UsbSlot, sortOrder: 0, text: "beat", offset: 3, count: 2);
        var messages = AwaitLog(log);

        Assert.Equal(2, items.Count);
        Assert.Equal("Item 3", ((StringField)items[0].Arguments[3]).Value);
        Assert.Equal("Item 4", ((StringField)items[1].Arguments[3]).Value);

        var render = messages[2];
        Assert.Equal(Message.KnownType.RenderMenuReq, render.RecognizedType);
        Assert.Equal(3, ((NumberField)render.Arguments[1]).Value);  // Offset.
        Assert.Equal(2, ((NumberField)render.Arguments[2]).Value);  // Count.
    }

    [Fact]
    public void RequestsFolderMenuAsUnanalyzedTypedRequest()
    {
        var (dbPort, log) = StartMenuDbServer(itemCount: 1);
        StartConnectionManager(dbPort, out var queryServer);
        using var _ = queryServer;

        var items = MenuLoader.Instance.RequestFolderMenuFrom(UsbSlot, sortOrder: 0, folderId: -1);
        var messages = AwaitLog(log);

        Assert.Single(items);
        var menuRequest = messages[1];
        Assert.Equal(Message.KnownType.FolderMenuReq, menuRequest.RecognizedType);
        // The R:M:S:T names the unanalyzed track type (2) rather than rekordbox.
        Assert.Equal((0L << 24) | (1L << 16) | (3L << 8) | 2L, ((NumberField)menuRequest.Arguments[0]).Value);
        Assert.Equal(0, ((NumberField)menuRequest.Arguments[1]).Value);
        Assert.Equal(0xffffffffL, ((NumberField)menuRequest.Arguments[2]).Value);  // Folder -1: the root.
        Assert.Equal(0xffffff, ((NumberField)menuRequest.Arguments[3]).Value);
    }
}
