using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Shared helpers for the finder tests which drive the <see cref="MetadataFinder"/> through the
/// <see cref="VirtualCdj"/>'s internal update-injection seam over loopback-safe services.
/// </summary>
internal static class FinderTestHelpers
{
    /// <summary>
    /// The player number used by the synthetic statuses (above 15, so ordinary device-number
    /// impersonation rules do not get in the way).
    /// </summary>
    internal const int Player = 17;

    /// <summary>
    /// The USB slot of the test player.
    /// </summary>
    internal static SlotReference UsbSlot =>
        SlotReference.GetSlotReference(Player, CdjStatus.TrackSourceSlot.UsbSlot);

    /// <summary>
    /// The track reference reported by <see cref="StatusWithTrack"/>.
    /// </summary>
    internal static DataReference TrackReference =>
        new(Player, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Rekordbox);

    /// <summary>
    /// Polls for a condition, failing the test if it does not become true in time.
    /// </summary>
    internal static void WaitUntil(Func<bool> condition, string description, int timeoutMillis = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMillis;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(25);
        }
        Assert.Fail($"Timed out waiting for {description}");
    }

    /// <summary>
    /// Builds a synthetic CDJ status for the test player with USB media loaded and the fixture
    /// track (rekordbox ID 42 in the USB slot) on deck.
    /// </summary>
    internal static CdjStatus StatusWithTrack()
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x21] = Player;
        bytes[0x28] = Player;  // Track source player.
        return new CdjStatus(bytes, VirtualCdjTestHelpers.AddressFor(Player));
    }

    /// <summary>
    /// Builds a synthetic CDJ status for the test player with USB media loaded but no track on
    /// deck, so mounts are recorded without triggering any metadata requests.
    /// </summary>
    internal static CdjStatus StatusWithoutTrack()
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x21] = Player;
        bytes[0x28] = 0;
        bytes[0x29] = 0;                            // Track source slot: no track.
        bytes[0x2a] = 0;                            // Track type: no track.
        Util.NumberToBytes(0, bytes, 0x2c, 4);      // Rekordbox ID: none.
        return new CdjStatus(bytes, VirtualCdjTestHelpers.AddressFor(Player));
    }

    /// <summary>
    /// Builds a synthetic CDJ status for the test player reporting that its USB slot is empty.
    /// </summary>
    internal static CdjStatus StatusWithUsbEjected()
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x21] = Player;
        bytes[0x28] = 0;
        bytes[0x29] = 0;
        bytes[0x2a] = 0;
        Util.NumberToBytes(0, bytes, 0x2c, 4);
        bytes[0x6f] = 4;                            // USB state: empty.
        return new CdjStatus(bytes, VirtualCdjTestHelpers.AddressFor(Player));
    }

    /// <summary>
    /// Builds the media details the test player would report for its USB slot.
    /// </summary>
    internal static MediaDetails UsbMediaDetails()
    {
        var bytes = PacketFixtures.NewPacket(0xc0, 0x06, "CDJ-3000", 0x0b);
        Util.NumberToBytes(0xc0 - 0x24, bytes, 0x22, 2);
        bytes[0x27] = Player;
        bytes[0x2b] = 3;  // USB slot.
        Encoding.BigEndianUnicode.GetBytes("BEAT LINK").CopyTo(bytes, 0x2c);
        Encoding.BigEndianUnicode.GetBytes("2024-01-15").CopyTo(bytes, 0x6c);
        Util.NumberToBytes(742, bytes, 0xa6, 2);
        bytes[0xaa] = 1;  // Media type: rekordbox.
        Util.NumberToBytes(25, bytes, 0xae, 2);
        return new MediaDetails(bytes);
    }

    /// <summary>
    /// A fake dbserver port-query service on an ephemeral loopback port which reports the given
    /// dbserver port for every connection.
    /// </summary>
    internal sealed class FakeQueryServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _response;

        public int Port { get; }

        public FakeQueryServer(int dbServerPort)
        {
            _response = [(byte)(dbServerPort >> 8), (byte)dbServerPort];
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;  // The listener was stopped; we are done.
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var connection = client;
                        var stream = connection.GetStream();
                        await stream.ReadExactlyAsync(new byte[19]);
                        await stream.WriteAsync(_response);
                    }
                    catch (Exception)
                    {
                        // The connection was torn down mid-script.
                    }
                });
            }
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>
    /// A TCP sink which accepts connections and immediately closes them, recording how many
    /// arrived, so tests can prove that no dbserver conversation was attempted.
    /// </summary>
    internal sealed class ConnectionCountingSink : IDisposable
    {
        private readonly TcpListener _listener;
        private int _connections;

        public int Port { get; }

        public int ConnectionCount => Volatile.Read(ref _connections);

        public ConnectionCountingSink()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        Interlocked.Increment(ref _connections);
                        client.Dispose();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>
    /// A configurable metadata provider for testing the provider-first retrieval paths.
    /// </summary>
    internal sealed class FakeMetadataProvider : IMetadataProvider
    {
        public List<MediaDetails> Media { get; } = [];

        public TrackMetadata? Metadata { get; set; }

        public BeatGrid? BeatGrid { get; set; }

        public RekordboxAnlz.TaggedSection? AnalysisSection { get; set; }

        public ConcurrentQueue<(DataReference Track, string Extension, string TypeTag)> AnalysisRequests { get; } = new();

        public IReadOnlyList<MediaDetails> SupportedMedia() => Media;

        public TrackMetadata? GetTrackMetadata(MediaDetails sourceMedia, DataReference track) =>
            Metadata != null && Metadata.TrackReference.Equals(track) ? Metadata : null;

        public AlbumArt? GetAlbumArt(MediaDetails sourceMedia, DataReference art) => null;

        public BeatGrid? GetBeatGrid(MediaDetails sourceMedia, DataReference track) =>
            BeatGrid != null && BeatGrid.DataReference.Equals(track) ? BeatGrid : null;

        public CueList? GetCueList(MediaDetails sourceMedia, DataReference track) => null;

        public WaveformPreview? GetWaveformPreview(MediaDetails sourceMedia, DataReference track) => null;

        public WaveformDetail? GetWaveformDetail(MediaDetails sourceMedia, DataReference track) => null;

        public RekordboxAnlz.TaggedSection? GetAnalysisSection(MediaDetails sourceMedia, DataReference track,
            string fileExtension, string typeTag)
        {
            AnalysisRequests.Enqueue((track, fileExtension, typeTag));
            return AnalysisSection;
        }
    }
}

/// <summary>
/// Exercises the <see cref="MetadataFinder"/> singleton: provider precedence over the dbserver,
/// the hot-cache lifecycle driven by synthetic CDJ status injections through the
/// <see cref="VirtualCdj"/>'s internal seam, mount/unmount events, and passive mode. All network
/// services run on loopback override seams so no real DJ Link port is ever touched.
/// </summary>
[Collection("Network services")]
public sealed class MetadataFinderTests : IDisposable
{
    private static MetadataFinder Finder => MetadataFinder.Instance;

    private readonly FinderTestHelpers.FakeMetadataProvider _provider = new();

    public MetadataFinderTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        Finder.RemoveMetadataProvider(_provider);
        ResetState();
    }

    private static void ResetState()
    {
        Finder.Stop();
        Finder.IsPassive = false;
        Finder.ClearMountsForTests();
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
    /// Starts the finder with all its dependencies on loopback overrides, announces the test
    /// player, and waits until its (fake) dbserver port has been cached so automatic metadata
    /// requests are enabled.
    /// </summary>
    private static void StartFinderWithFakeDbServerPort(int dbServerPort, int queryPort)
    {
        DeviceFinder.Instance.PortOverride = 0;
        ConnectionManager.Instance.DbServerQueryPortOverride = queryPort;
        VirtualCdj.Instance.SetRunningStateForTests(true);
        Finder.Start();
        Assert.True(Finder.IsRunning);
        Assert.True(ConnectionManager.Instance.IsRunning);

        ServiceTestHelpers.SendToLoopback(DeviceFinder.Instance.LocalPort!.Value,
            ServiceTestHelpers.KeepAlivePacket("CDJ-3000", FinderTestHelpers.Player));
        FinderTestHelpers.WaitUntil(
            () => DeviceFinder.Instance.GetLatestAnnouncementFrom(FinderTestHelpers.Player) != null,
            "the test player to be announced");
        FinderTestHelpers.WaitUntil(
            () => ConnectionManager.Instance.GetPlayerDBServerPort(FinderTestHelpers.Player) == dbServerPort,
            "the fake dbserver port to be cached");
    }

    /// <summary>
    /// Mounts the USB slot (via a no-track status) and delivers its media details, waiting until
    /// the finder has recorded them.
    /// </summary>
    private static MediaDetails MountUsbSlotWithDetails()
    {
        VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithoutTrack());
        FinderTestHelpers.WaitUntil(
            () => Finder.MountedMediaSlots.Contains(FinderTestHelpers.UsbSlot),
            "the USB slot to be recorded as mounted");
        var details = FinderTestHelpers.UsbMediaDetails();
        VirtualCdj.Instance.DeliverMediaDetailsUpdate(details);
        FinderTestHelpers.WaitUntil(
            () => Finder.GetMediaDetailsFor(FinderTestHelpers.UsbSlot) != null,
            "the media details to be recorded");
        return details;
    }

    [Fact]
    public void MountAndUnmountEventsFollowSlotFlags()
    {
        using var sink = new FinderTestHelpers.ConnectionCountingSink();
        using var query = new FinderTestHelpers.FakeQueryServer(sink.Port);

        var mounted = new ConcurrentQueue<SlotReference>();
        var unmounted = new ConcurrentQueue<SlotReference>();
        var detailsSeen = new ConcurrentQueue<MediaDetails>();
        EventHandler<SlotReference> onMounted = (_, slot) => mounted.Enqueue(slot);
        EventHandler<SlotReference> onUnmounted = (_, slot) => unmounted.Enqueue(slot);
        EventHandler<MediaDetails> onDetails = (_, details) => detailsSeen.Enqueue(details);
        Finder.MediaMounted += onMounted;
        Finder.MediaUnmounted += onUnmounted;
        Finder.MediaDetailsAvailable += onDetails;
        try
        {
            StartFinderWithFakeDbServerPort(sink.Port, query.Port);

            var details = MountUsbSlotWithDetails();
            FinderTestHelpers.WaitUntil(() => mounted.Contains(FinderTestHelpers.UsbSlot),
                "the mount event to be delivered");
            FinderTestHelpers.WaitUntil(() => detailsSeen.Contains(details),
                "the media details event to be delivered");
            Assert.Contains(details, Finder.MountedMediaDetails);

            // Ejecting the USB media unmounts the slot and discards its details.
            VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithUsbEjected());
            FinderTestHelpers.WaitUntil(() => unmounted.Contains(FinderTestHelpers.UsbSlot),
                "the unmount event to be delivered");
            Assert.DoesNotContain(FinderTestHelpers.UsbSlot, Finder.MountedMediaSlots);
            Assert.Null(Finder.GetMediaDetailsFor(FinderTestHelpers.UsbSlot));
        }
        finally
        {
            Finder.MediaMounted -= onMounted;
            Finder.MediaUnmounted -= onUnmounted;
            Finder.MediaDetailsAvailable -= onDetails;
        }
    }

    [Fact]
    public void MediaDetailsForUnmountedSlotsAreDiscarded()
    {
        using var sink = new FinderTestHelpers.ConnectionCountingSink();
        using var query = new FinderTestHelpers.FakeQueryServer(sink.Port);
        StartFinderWithFakeDbServerPort(sink.Port, query.Port);

        // Details delivered before the slot has been mounted are ignored.
        VirtualCdj.Instance.DeliverMediaDetailsUpdate(FinderTestHelpers.UsbMediaDetails());
        Thread.Sleep(200);
        Assert.Null(Finder.GetMediaDetailsFor(FinderTestHelpers.UsbSlot));
    }

    [Fact]
    public void RegisteredProviderWinsOverDbServerForAutomaticRequests()
    {
        using var sink = new FinderTestHelpers.ConnectionCountingSink();
        using var query = new FinderTestHelpers.FakeQueryServer(sink.Port);
        StartFinderWithFakeDbServerPort(sink.Port, query.Port);
        var details = MountUsbSlotWithDetails();

        // Register a provider keyed to the mounted media which can offer the fixture track.
        var metadata = new TrackMetadata(FinderTestHelpers.TrackReference,
            CdjStatus.TrackType.Rekordbox, [], null);
        _provider.Media.Add(details);
        _provider.Metadata = metadata;
        Finder.AddMetadataProvider(_provider);
        Assert.Contains(_provider, Finder.GetMetadataProviders(details));

        var updates = new ConcurrentQueue<TrackMetadataUpdate>();
        EventHandler<TrackMetadataUpdate> onMetadata = (_, update) => updates.Enqueue(update);
        Finder.TrackMetadataChanged += onMetadata;
        try
        {
            // Loading the track triggers an automatic request which the provider satisfies.
            VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithTrack());
            FinderTestHelpers.WaitUntil(
                () => Finder.GetLatestMetadataFor(FinderTestHelpers.Player) != null,
                "the metadata to be provided");
            Assert.Same(metadata, Finder.GetLatestMetadataFor(FinderTestHelpers.Player));
            Assert.Same(metadata, Finder.LoadedTracks[DeckReference.GetDeckReference(FinderTestHelpers.Player, 0)]);
            FinderTestHelpers.WaitUntil(
                () => updates.Any(u => ReferenceEquals(u.Metadata, metadata)),
                "the metadata update event to be delivered");

            // The dbserver was never contacted; the provider preempted it.
            Assert.Equal(0, sink.ConnectionCount);

            // An explicit request is served from the provider as well.
            Assert.Same(metadata, Finder.RequestMetadataFrom(FinderTestHelpers.TrackReference));
            Assert.Equal(0, sink.ConnectionCount);
        }
        finally
        {
            Finder.TrackMetadataChanged -= onMetadata;
        }
    }

    [Fact]
    public void EjectingMediaEvictsHotCachedMetadata()
    {
        using var sink = new FinderTestHelpers.ConnectionCountingSink();
        using var query = new FinderTestHelpers.FakeQueryServer(sink.Port);
        StartFinderWithFakeDbServerPort(sink.Port, query.Port);
        var details = MountUsbSlotWithDetails();

        var metadata = new TrackMetadata(FinderTestHelpers.TrackReference,
            CdjStatus.TrackType.Rekordbox, [], null);
        _provider.Media.Add(details);
        _provider.Metadata = metadata;
        Finder.AddMetadataProvider(_provider);

        VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithTrack());
        FinderTestHelpers.WaitUntil(
            () => Finder.GetLatestMetadataFor(FinderTestHelpers.Player) != null,
            "the metadata to be provided");

        // Ejecting the media flushes the hot cache entries loaded from that slot.
        VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithUsbEjected());
        FinderTestHelpers.WaitUntil(
            () => Finder.GetLatestMetadataFor(FinderTestHelpers.Player) == null,
            "the hot-cached metadata to be evicted");
        Assert.Empty(Finder.LoadedTracks);
    }

    [Fact]
    public void PassiveModeSuppressesAutomaticDbServerQueries()
    {
        using var sink = new FinderTestHelpers.ConnectionCountingSink();
        using var query = new FinderTestHelpers.FakeQueryServer(sink.Port);
        StartFinderWithFakeDbServerPort(sink.Port, query.Port);

        Finder.IsPassive = true;
        Assert.True(Finder.IsPassive);

        // With no provider registered and passive mode on, loading a track produces no metadata
        // and, crucially, no dbserver connection.
        VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithTrack());
        Thread.Sleep(400);
        Assert.Null(Finder.GetLatestMetadataFor(FinderTestHelpers.Player));
        Assert.Equal(0, sink.ConnectionCount);

        // An explicit request is still allowed to try the dbserver (and fails against our sink).
        Assert.Null(Finder.RequestMetadataFrom(FinderTestHelpers.TrackReference));
        FinderTestHelpers.WaitUntil(() => sink.ConnectionCount > 0,
            "the explicit request to attempt a dbserver connection");
    }

    [Fact]
    public void RequestMetadataFromStatusWithoutTrackReturnsNull()
    {
        Assert.Null(Finder.RequestMetadataFrom(FinderTestHelpers.StatusWithoutTrack()));
    }
}
