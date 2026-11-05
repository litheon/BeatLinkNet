using System.Collections.Concurrent;
using System.Net;
using BeatLink.CrateDigger.Nfs;
using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;
using BeatLink.DbServer;
using BeatLink.Tests.CrateDigger;
using BeatLink.Tests.Nfs;
using Microsoft.Data.Sqlite;
using Digger = BeatLink.Data.CrateDigger;
using FileFetcher = BeatLink.CrateDigger.FileFetcher;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the <see cref="Digger"/> singleton end to end: databases are downloaded over a fake
/// NFS server (a synthetic <c>export.pdb</c>, real ANLZ fixture files, and an in-test-generated
/// encrypted SQLite Device Library Plus export), parsed or connected to, registered as a metadata
/// provider with the <see cref="MetadataFinder"/>, and cleaned up again on unmount. All network
/// services run on loopback override seams so no real DJ Link port is ever touched.
/// </summary>
[Collection("Network services")]
public sealed class CrateDiggerTests : IDisposable
{
    private static Digger CrateDigger => Digger.Instance;

    private static MetadataFinder Finder => MetadataFinder.Instance;

    private readonly FakeRpcServer _server = new();
    private readonly FinderTestHelpers.ConnectionCountingSink _sink = new();
    private readonly FinderTestHelpers.FakeQueryServer _query;
    private readonly List<string> _tempFiles = [];

    public CrateDiggerTests()
    {
        _query = new FinderTestHelpers.FakeQueryServer(_sink.Port);
        ResetState();
        FileFetcher.Instance.PortmapperPort = _server.Port;
        FileFetcher.Instance.RetransmitTimeout = 100;
    }

    public void Dispose()
    {
        ResetState();
        FileFetcher.Instance.PortmapperPort = PortmapperClient.DefaultPort;
        FileFetcher.Instance.RetransmitTimeout = FileFetcher.DefaultRpcRetransmitTimeout;
        _query.Dispose();
        _sink.Dispose();
        _server.Dispose();
        foreach (var file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Returns all services (and the CrateDigger's own caches) to a known stopped state, so the
    /// tests remain independent of each other and of the other network-service tests.
    /// </summary>
    private static void ResetState()
    {
        CrateDigger.AutoStartOnMetadataFinderStart = false;  // Keep other tests' MetadataFinder use from starting us.
        CrateDigger.Stop();
        CrateDigger.ClearCachesForTests();
        Finder.Stop();
        Finder.IsPassive = false;
        Finder.ClearMountsForTests();
        ConnectionManager.Instance.Stop();
        BeatFinder.Instance.Stop();  // May have been auto-started by an XDJ-AZ announcement.
        BeatFinder.Instance.PortOverride = null;
        DeviceFinder.Instance.Stop();
        DeviceFinder.Instance.PortOverride = null;
        DeviceFinder.Instance.RemoveIgnoredAddress(IPAddress.Loopback);
        ConnectionManager.Instance.DbServerQueryPortOverride = null;
        VirtualCdj.Instance.ResetForTests();
        OpusProvider.DatabaseKey = null;
        FileFetcher.Instance.RemovePlayer(IPAddress.Loopback);
    }

    /// <summary>
    /// Starts the CrateDigger (which transitively starts the MetadataFinder and its dependencies,
    /// all on loopback overrides) and announces the test player so its address is known.
    /// </summary>
    /// <param name="deviceName">the name the test player reports, which controls whether it is
    /// treated as using Device Library Plus</param>
    private static void StartCrateDiggerWithAnnouncedPlayer(int queryPort, string deviceName = "CDJ-3000")
    {
        DeviceFinder.Instance.PortOverride = 0;
        BeatFinder.Instance.PortOverride = 0;  // An XDJ-AZ announcement auto-starts the BeatFinder.
        ConnectionManager.Instance.DbServerQueryPortOverride = queryPort;
        VirtualCdj.Instance.SetRunningStateForTests(true);
        CrateDigger.Start();
        Assert.True(CrateDigger.IsRunning);
        Assert.True(Finder.IsRunning);

        ServiceTestHelpers.SendToLoopback(DeviceFinder.Instance.LocalPort!.Value,
            ServiceTestHelpers.KeepAlivePacket(deviceName, FinderTestHelpers.Player));
        FinderTestHelpers.WaitUntil(
            () => DeviceFinder.Instance.GetLatestAnnouncementFrom(FinderTestHelpers.Player) != null,
            "the test player to be announced");
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

    /// <summary>
    /// Builds the standard fake NFS tree for the USB slot: a <c>/C/</c> mount containing a
    /// <c>PIONEER/rekordbox</c> folder (optionally hidden as <c>.PIONEER</c>, the way HFS+
    /// formatted media serve it) holding the supplied database export.
    /// </summary>
    private FakeNode ServeDatabase(string databaseFileName, byte[] content, bool hiddenPioneerFolder = false)
    {
        var root = _server.AddMount("/C/");
        var pioneer = _server.AddDirectory(root, hiddenPioneerFolder ? ".PIONEER" : "PIONEER");
        var rekordbox = _server.AddDirectory(pioneer, "rekordbox");
        _server.AddFile(rekordbox, databaseFileName, content);
        return pioneer;
    }

    /// <summary>
    /// Lists the files currently downloaded for the test player's USB slot.
    /// </summary>
    private static string[] DownloadedSlotFiles() =>
        Directory.GetFiles(CrateDigger.DownloadDirectory)
            .Where(file => Path.GetFileName(file).StartsWith(
                $"player-{FinderTestHelpers.Player}-slot-3-", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void PdbDatabaseDownloadServesMetadataAndCleansUpOnUnmount()
    {
        ServeDatabase(Digger.DeviceSqlFilename, PdbBuilder.BuildExportFile());
        var mounted = new ConcurrentQueue<DatabaseUpdate>();
        var unmounted = new ConcurrentQueue<DatabaseUpdate>();
        EventHandler<DatabaseUpdate> onMounted = (_, update) => mounted.Enqueue(update);
        EventHandler<DatabaseUpdate> onUnmounted = (_, update) => unmounted.Enqueue(update);
        CrateDigger.DatabaseMounted += onMounted;
        CrateDigger.DatabaseUnmounted += onUnmounted;
        try
        {
            StartCrateDiggerWithAnnouncedPlayer(_query.Port);
            MountUsbSlotWithDetails();

            // The database is downloaded, parsed, and announced in the background.
            FinderTestHelpers.WaitUntil(() => CrateDigger.FindDatabase(FinderTestHelpers.UsbSlot) != null,
                "the rekordbox database to be downloaded and parsed");
            var database = CrateDigger.FindDatabase(FinderTestHelpers.UsbSlot)!;
            var mountEvent = Assert.Single(mounted);
            Assert.Same(FinderTestHelpers.UsbSlot, mountEvent.Slot);
            Assert.Same(database, mountEvent.Database);
            Assert.Equal(2, database.TrackIndex.Count);

            // Metadata requests through the MetadataFinder are now served from the parsed
            // database (provider-first), without ever contacting the (fake) dbserver.
            var track = new DataReference(FinderTestHelpers.Player, CdjStatus.TrackSourceSlot.UsbSlot,
                1, CdjStatus.TrackType.Rekordbox);
            var metadata = Finder.RequestMetadataFrom(track);
            Assert.NotNull(metadata);
            Assert.Equal("alpha song", metadata.Title);
            Assert.Equal(new SearchableItem(7, "Björk"), metadata.Artist);
            Assert.Equal(new SearchableItem(5, "Homogenic"), metadata.Album);
            Assert.Equal(12800, metadata.Tempo);
            Assert.Equal(300, metadata.Duration);
            Assert.Same(database.TrackIndex[1], metadata.RawRow);
            Assert.Equal(0, _sink.ConnectionCount);

            // Unknown tracks are simply not served.
            Assert.Null(CrateDigger.MetadataProviderForTests.GetTrackMetadata(
                FinderTestHelpers.UsbMediaDetails(), new DataReference(FinderTestHelpers.Player,
                    CdjStatus.TrackSourceSlot.UsbSlot, 999, CdjStatus.TrackType.Rekordbox)));

            // Ejecting the media unmounts the slot: the database is announced as gone, closed,
            // and its downloaded files are deleted.
            Assert.NotEmpty(DownloadedSlotFiles());
            VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithUsbEjected());
            FinderTestHelpers.WaitUntil(() => CrateDigger.FindDatabase(FinderTestHelpers.UsbSlot) == null,
                "the database to be discarded on unmount");
            FinderTestHelpers.WaitUntil(() => !unmounted.IsEmpty, "the database unmount event to be delivered");
            var unmountEvent = Assert.Single(unmounted);
            Assert.Same(database, unmountEvent.Database);
            FinderTestHelpers.WaitUntil(() => DownloadedSlotFiles().Length == 0,
                "the downloaded files to be deleted");
        }
        finally
        {
            CrateDigger.DatabaseMounted -= onMounted;
            CrateDigger.DatabaseUnmounted -= onUnmounted;
        }
    }

    [Fact]
    public void AnalysisFilesAreFetchedWithLeadingDotFallback()
    {
        // The media hides its PIONEER folder as ".PIONEER" (the way HFS+ formatted devices do),
        // while the database's analysis paths still name the visible "PIONEER" folder, so every
        // fetch must exercise the leading-dot fallback.
        var pioneer = ServeDatabase(Digger.DeviceSqlFilename,
            PdbBuilder.BuildExportFile(analyzePath: "PIONEER/USBANLZ/ANLZ0000.DAT"),
            hiddenPioneerFolder: true);
        var anlzDirectory = _server.AddDirectory(pioneer, "USBANLZ");
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "anlz");
        _server.AddFile(anlzDirectory, "ANLZ0000.DAT", File.ReadAllBytes(Path.Combine(fixtures, "ANLZ0000.DAT")));
        _server.AddFile(anlzDirectory, "ANLZ0000.EXT", File.ReadAllBytes(Path.Combine(fixtures, "ANLZ0000.EXT")));

        StartCrateDiggerWithAnnouncedPlayer(_query.Port);
        var details = MountUsbSlotWithDetails();
        FinderTestHelpers.WaitUntil(() => CrateDigger.FindDatabase(FinderTestHelpers.UsbSlot) != null,
            "the rekordbox database to be downloaded through the hidden-folder fallback");

        var provider = CrateDigger.MetadataProviderForTests;
        var track = new DataReference(FinderTestHelpers.Player, CdjStatus.TrackSourceSlot.UsbSlot,
            1, CdjStatus.TrackType.Rekordbox);

        // The beat grid comes from the .DAT file, downloaded through the dotted path.
        var beatGrid = provider.GetBeatGrid(details, track);
        Assert.NotNull(beatGrid);
        Assert.True(beatGrid.BeatCount > 0);
        Assert.True(File.Exists(Path.Combine(CrateDigger.DownloadDirectory,
            $"player-{FinderTestHelpers.Player}-slot-3-track-1-anlz.dat")));

        // Waveforms and the cue list come from the extended (.EXT) analysis file.
        var preview = provider.GetWaveformPreview(details, track);
        Assert.NotNull(preview);
        Assert.Equal(WaveformStyle.Rgb, preview.Style);  // The default preferred style.
        var waveformDetail = provider.GetWaveformDetail(details, track);
        Assert.NotNull(waveformDetail);
        Assert.Equal(WaveformStyle.Rgb, waveformDetail.Style);
        var cueList = provider.GetCueList(details, track);
        Assert.NotNull(cueList);
        Assert.Equal(2, cueList.RawExtendedTags!.Count);

        // Arbitrary tagged sections can be pulled from a specific analysis file as well.
        var section = provider.GetAnalysisSection(details, track, ".EXT", "PSSI");
        Assert.NotNull(section);
        Assert.Equal(RekordboxAnlz.SectionTags.SongStructure, section.Fourcc);
        Assert.Null(provider.GetAnalysisSection(details, track, ".DAT", "PSSI"));  // Not present in .DAT files.
    }

    [Fact]
    public void SqliteDeviceLibraryPlusDatabaseServesMetadata()
    {
        const string key = "beat-link-test-key";
        SQLitePCL.Batteries_V2.Init();  // The SQLCipher bundle is referenced by the test project.
        ServeDatabase(Digger.SqliteFilename, BuildEncryptedLibrary(key));
        OpusProvider.DatabaseKey = key;

        var mounted = new ConcurrentQueue<ConnectionUpdate>();
        var unmounted = new ConcurrentQueue<ConnectionUpdate>();
        EventHandler<ConnectionUpdate> onMounted = (_, update) => mounted.Enqueue(update);
        EventHandler<ConnectionUpdate> onUnmounted = (_, update) => unmounted.Enqueue(update);
        CrateDigger.ConnectionMounted += onMounted;
        CrateDigger.ConnectionUnmounted += onUnmounted;
        try
        {
            // The XDJ-AZ reports itself as a Device Library Plus device, so the SQLite database
            // is downloaded instead of the DeviceSQL one.
            StartCrateDiggerWithAnnouncedPlayer(_query.Port, OpusProvider.XdjAzName);
            MountUsbSlotWithDetails();
            FinderTestHelpers.WaitUntil(() => CrateDigger.FindConnection(FinderTestHelpers.UsbSlot) != null,
                "the Device Library Plus database to be downloaded and connected");
            var connection = CrateDigger.FindConnection(FinderTestHelpers.UsbSlot)!;
            var mountEvent = Assert.Single(mounted);
            Assert.Same(connection, mountEvent.Connection);
            Assert.Null(CrateDigger.FindDatabase(FinderTestHelpers.UsbSlot));

            // Metadata requests are served through the connection-first path, complete with the
            // related rows and JDBC-compatible NULL handling.
            var track = new DataReference(FinderTestHelpers.Player, CdjStatus.TrackSourceSlot.UsbSlot,
                77, CdjStatus.TrackType.Rekordbox);
            var metadata = Finder.RequestMetadataFrom(track);
            Assert.NotNull(metadata);
            Assert.Null(metadata.RawRow);
            Assert.Null(metadata.RawItems);
            Assert.Equal("Library Plus Song", metadata.Title);
            Assert.Equal(245, metadata.Duration);
            Assert.Equal(12345, metadata.Tempo);
            Assert.Equal("From Device Library Plus", metadata.Comment);
            Assert.Equal("2025-02-03", metadata.DateAdded);
            Assert.Equal(2021, metadata.Year);
            Assert.Equal(0, metadata.BitRate);  // Stored as NULL, read as zero like JDBC getInt.
            Assert.Equal(new SearchableItem(21, "DLP Artist"), metadata.Artist);
            Assert.Null(metadata.OriginalArtist);  // ID zero means no related row is loaded.
            Assert.Equal(new SearchableItem(21, "DLP Artist"), metadata.Remixer);
            Assert.Equal(new SearchableItem(31, "DLP Album"), metadata.Album);
            Assert.Equal(new SearchableItem(41, "DLP Label"), metadata.Label);
            Assert.Equal(new SearchableItem(51, "7A"), metadata.Key);
            Assert.Equal(new SearchableItem(61, "House"), metadata.Genre);
            Assert.Equal(new ColorItem(71, "Blue"), metadata.Color);
            Assert.Equal(0, _sink.ConnectionCount);

            // The constructor reports missing tracks the same way the PDB one does.
            Assert.Throws<KeyNotFoundException>(() => _ = new TrackMetadata(
                new DataReference(FinderTestHelpers.Player, CdjStatus.TrackSourceSlot.UsbSlot, 999,
                    CdjStatus.TrackType.Rekordbox), connection, null));

            // Ejecting the media closes the connection and deletes the downloaded database.
            VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithUsbEjected());
            FinderTestHelpers.WaitUntil(() => CrateDigger.FindConnection(FinderTestHelpers.UsbSlot) == null,
                "the connection to be discarded on unmount");
            FinderTestHelpers.WaitUntil(() => !unmounted.IsEmpty, "the connection unmount event to be delivered");
            Assert.Same(connection, Assert.Single(unmounted).Connection);
            FinderTestHelpers.WaitUntil(() => DownloadedSlotFiles().Length == 0,
                "the downloaded database to be deleted");
        }
        finally
        {
            CrateDigger.ConnectionMounted -= onMounted;
            CrateDigger.ConnectionUnmounted -= onUnmounted;
        }
    }

    [Fact]
    public void AutoStartFollowsMetadataFinderLifecycle()
    {
        ServeDatabase(Digger.DeviceSqlFilename, PdbBuilder.BuildExportFile());
        CrateDigger.AutoStartOnMetadataFinderStart = true;
        try
        {
            DeviceFinder.Instance.PortOverride = 0;
            ConnectionManager.Instance.DbServerQueryPortOverride = _query.Port;
            VirtualCdj.Instance.SetRunningStateForTests(true);
            Finder.Start();
            FinderTestHelpers.WaitUntil(() => CrateDigger.IsRunning,
                "the CrateDigger to start along with the MetadataFinder");

            Finder.Stop();
            FinderTestHelpers.WaitUntil(() => !CrateDigger.IsRunning,
                "the CrateDigger to stop along with the MetadataFinder");
        }
        finally
        {
            CrateDigger.AutoStartOnMetadataFinderStart = false;
        }
    }

    [Fact]
    public void RetryLimitIsValidated()
    {
        Assert.Throws<ArgumentException>(() => CrateDigger.RetryLimit = 0);
        Assert.Throws<ArgumentException>(() => CrateDigger.RetryLimit = 11);
        CrateDigger.RetryLimit = 5;
        Assert.Equal(5, CrateDigger.RetryLimit);
        CrateDigger.RetryLimit = 3;
    }

    [Fact]
    public void HumanReadableByteCountFormatsBothInterpretations()
    {
        Assert.Equal("999 B", Digger.HumanReadableByteCount(999, si: true));
        Assert.Equal("1.5 kB", Digger.HumanReadableByteCount(1500, si: true));
        Assert.Equal("1.5 KiB", Digger.HumanReadableByteCount(1536, si: false));
        Assert.Equal("1.0 MB", Digger.HumanReadableByteCount(1_000_000, si: true));
    }

    [Fact]
    public void OpenSQLiteConnectionRequiresConfiguredKey()
    {
        OpusProvider.DatabaseKey = null;
        Assert.False(OpusProvider.Instance.UsingDeviceLibraryPlus);
        var e = Assert.Throws<InvalidOperationException>(() =>
            OpusProvider.OpenSQLiteConnection(Path.Combine(Path.GetTempPath(), "missing.db")));
        Assert.Contains("No database key", e.Message);

        OpusProvider.DatabaseKey = "some-key";
        Assert.True(OpusProvider.Instance.UsingDeviceLibraryPlus);
        OpusProvider.DatabaseKey = null;
    }

    /// <summary>
    /// Creates an encrypted (SQLCipher) Device Library Plus export with the minimal schema the
    /// <see cref="TrackMetadata"/> SQLite constructor queries, and returns its raw bytes so it can
    /// be served over the fake NFS tree.
    /// </summary>
    private byte[] BuildEncryptedLibrary(string key)
    {
        var path = Path.Combine(Path.GetTempPath(), $"beat-link-net-crate-digger-{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = key,
            Pooling = false
        }.ConnectionString;
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                create table content (
                    content_id integer primary key, title text, length integer, bpmx100 integer,
                    djComment text, dateAdded text, bitrate integer, releaseYear integer,
                    image_id integer, artist_id_artist integer, artist_id_originalArtist integer,
                    artist_id_remixer integer, album_id integer, label_id integer, key_id integer,
                    genre_id integer, color_id integer, analysisDataFilePath text);
                create table artist (artist_id integer primary key, name text);
                create table album (album_id integer primary key, name text);
                create table label (label_id integer primary key, name text);
                create table "key" (key_id integer primary key, name text);
                create table genre (genre_id integer primary key, name text);
                create table color (color_id integer primary key, name text);
                create table image (image_id integer primary key, path text);
                insert into content values (77, 'Library Plus Song', 245, 12345,
                    'From Device Library Plus', '2025-02-03', NULL, 2021, 0, 21, 0, 21, 31, 41, 51,
                    61, 71, NULL);
                insert into artist values (21, 'DLP Artist');
                insert into album values (31, 'DLP Album');
                insert into label values (41, 'DLP Label');
                insert into "key" values (51, '7A');
                insert into genre values (61, 'House');
                insert into color values (71, 'Blue');
                """;
            command.ExecuteNonQuery();
        }
        return File.ReadAllBytes(path);
    }
}
