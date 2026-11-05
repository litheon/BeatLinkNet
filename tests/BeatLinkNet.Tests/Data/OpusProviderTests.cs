// These tests exercise the experimental Opus Quad API surface deliberately.
#pragma warning disable BLNET001

using System.IO.Compression;
using System.Net;
using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;
using BeatLink.DbServer;
using BeatLink.Tests.CrateDigger;
using Kaitai;
using Microsoft.Data.Sqlite;
using Digger = BeatLink.Data.CrateDigger;

namespace BeatLink.Tests;

/// <summary>
/// Shared helpers for building synthetic Opus Quad metadata archives (the zip layout produced by
/// the Crate Digger <c>Archivist</c>: <c>export.pdb</c> and/or <c>exportLibrary.db</c> at the
/// root, with analysis and artwork files under <c>PIONEER/</c>).
/// </summary>
internal static class OpusTestHelpers
{
    /// <summary>The directory holding the real ANLZ fixture files.</summary>
    internal static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "anlz");

    /// <summary>
    /// Creates a metadata archive zip file from the supplied entries, returning its path (the
    /// caller is responsible for deleting it).
    /// </summary>
    internal static string BuildArchive(params (string EntryName, byte[] Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"beat-link-net-opus-archive-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(content);
        }
        return path;
    }

    /// <summary>
    /// Builds the standard DeviceSQL archive used by most tests: the synthetic
    /// <c>export.pdb</c> (tracks 1 and 2 sharing one analysis path), the real ANLZ fixture
    /// files, and a small artwork file at the path named by the track rows.
    /// </summary>
    internal static string BuildStandardArchive(bool hiddenPioneerFolder = false)
    {
        var pioneer = hiddenPioneerFolder ? ".PIONEER" : "PIONEER";
        return BuildArchive(
            ("export.pdb", PdbBuilder.BuildExportFile(analyzePath: "/PIONEER/USBANLZ/ANLZ0000.DAT")),
            ($"{pioneer}/USBANLZ/ANLZ0000.DAT", File.ReadAllBytes(Path.Combine(FixtureDirectory, "ANLZ0000.DAT"))),
            ($"{pioneer}/USBANLZ/ANLZ0000.EXT", File.ReadAllBytes(Path.Combine(FixtureDirectory, "ANLZ0000.EXT"))),
            ($"{pioneer}/ARTWORK/00001/a.jpg", [0xff, 0xd8, 0xff, 0xdb, 0x42, 0x42]));
    }

    /// <summary>
    /// Reads the raw body of the SONG_STRUCTURE (PSSI) section of the real fixture extended
    /// analysis file, which is what the PSSI SHA-1 index is keyed by.
    /// </summary>
    internal static byte[] FixtureSongStructureRawBody()
    {
        var anlz = new RekordboxAnlz(new KaitaiStream(Path.Combine(FixtureDirectory, "ANLZ0000.EXT")));
        try
        {
            return anlz.Sections.First(s => s.Fourcc == RekordboxAnlz.SectionTags.SongStructure).M_RawBody;
        }
        finally
        {
            anlz.M_Io.Dispose();
        }
    }

    /// <summary>
    /// Builds a synthetic extended analysis (.EXT) file containing only a masked SONG_STRUCTURE
    /// section with zero phrase entries, and returns both the file bytes and the raw section body
    /// (the bytes that the PSSI SHA-1 index hashes). Unlike the fixture file's PSSI section, this
    /// one's raw body ends with a non-zero byte, which matters for the trailing-zero trimming the
    /// OPUS_METADATA reassembly performs.
    /// </summary>
    internal static (byte[] FileBytes, byte[] SectionRawBody) BuildSyntheticPssiExt()
    {
        // Plain song-structure payload with zero entries: mood(2) + 6 + endBeat(2) + 2 + bank(1) + 1.
        byte[] plain = [0x00, 0x02, 0, 0, 0, 0, 0, 0, 0x00, 0x40, 0, 0, 0x01, 0x00];
        // The XOR mask used when a song structure is masked, with C = number of entries = 0.
        int[] maskBase = [203, 225, 238, 250, 229, 238, 173, 238, 233, 210, 233, 235, 225, 233, 243, 232, 233, 244, 225];
        var masked = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            masked[i] = (byte)(plain[i] ^ (byte)maskBase[i % maskBase.Length]);
        }

        // Raw section body: len_entry_bytes(4) + len_entries(2) + masked payload.
        var rawBody = new byte[6 + masked.Length];
        rawBody[3] = 24;  // len_entry_bytes = 24, big-endian.
        masked.CopyTo(rawBody, 6);
        Assert.NotEqual(0, rawBody[^1]);  // Trailing-zero trimming must not eat into the body.

        // The tagged section: fourcc + len_header + len_tag + raw body.
        var section = new byte[12 + rawBody.Length];
        "PSSI"u8.CopyTo(section);
        section[7] = 24;                          // len_header (unused by the parser).
        section[11] = (byte)section.Length;       // len_tag.
        rawBody.CopyTo(section, 12);

        // The PMAI container: magic + len_header(28) + len_file + 16 filler bytes + the section.
        var file = new byte[28 + section.Length];
        "PMAI"u8.CopyTo(file);
        file[7] = 28;                             // len_header.
        file[11] = (byte)file.Length;             // len_file.
        section.CopyTo(file, 28);

        // Prove the synthetic file round-trips through the parser with the expected raw body.
        var parsed = new RekordboxAnlz(new KaitaiStream(file));
        var parsedBody = parsed.Sections.Single(s => s.Fourcc == RekordboxAnlz.SectionTags.SongStructure).M_RawBody;
        Assert.Equal(rawBody, parsedBody);

        return (file, rawBody);
    }
}

/// <summary>
/// Exercises the <see cref="OpusProvider"/> singleton: attaching and detaching metadata archives
/// built in-test (synthetic <c>export.pdb</c> plus real ANLZ fixtures, laid out the way the Crate
/// Digger <c>Archivist</c> creates them), the PSSI SHA-1 track-matching index, the metadata
/// provider that serves all data kinds from the archives, the synthesized media-details queue,
/// and the persisted database key. No real DJ Link port is ever touched.
/// </summary>
[Collection("Network services")]
public sealed class OpusProviderTests : IDisposable
{
    private static OpusProvider Opus => OpusProvider.Instance;

    private readonly List<string> _tempFiles = [];

    public OpusProviderTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
        foreach (var file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Returns all services to a known stopped state, so the tests remain independent of each
    /// other and of the other network-service tests.
    /// </summary>
    private static void ResetState()
    {
        Digger.Instance.AutoStartOnMetadataFinderStart = false;
        Digger.Instance.Stop();
        Opus.ResetForTests();
        OpusProvider.DatabaseKey = null;
        VirtualRekordbox.Instance.ResetForTests();
        MetadataFinder.Instance.Stop();
        MetadataFinder.Instance.ClearMountsForTests();
        ConnectionManager.Instance.Stop();
        DeviceFinder.Instance.Stop();
        DeviceFinder.Instance.PortOverride = null;
        DeviceFinder.Instance.RemoveIgnoredAddress(IPAddress.Loopback);
        VirtualCdj.Instance.ResetForTests();
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Puts the <see cref="VirtualCdj"/> into (test-forced) Opus Quad compatibility mode and
    /// starts the provider, which is a prerequisite for attaching archives (the synthesized
    /// <see cref="MediaDetails"/> require a running provider).
    /// </summary>
    private static void StartProvider()
    {
        VirtualCdj.Instance.SetOpusQuadCompatibilityModeForTests(true);
        Opus.Start();
        Assert.True(Opus.IsRunning);
    }

    /// <summary>
    /// Creates the standard archive, remembering it for cleanup.
    /// </summary>
    private string StandardArchive(bool hiddenPioneerFolder = false)
    {
        var path = OpusTestHelpers.BuildStandardArchive(hiddenPioneerFolder);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void StartRequiresOpusQuadCompatibilityMode()
    {
        Assert.False(Opus.IsRunning);
        var e = Assert.Throws<InvalidOperationException>(Opus.Start);
        Assert.Contains("Opus Quad compatibility mode", e.Message);

        StartProvider();
        Opus.Start();  // Starting again has no effect.
        Assert.True(Opus.IsRunning);
        Opus.Stop();
        Assert.False(Opus.IsRunning);
    }

    [Fact]
    public void AttachValidatesSlotNumber()
    {
        StartProvider();
        Assert.Throws<ArgumentException>(() => Opus.AttachMetadataArchive(null, 0));
        Assert.Throws<ArgumentException>(() => Opus.AttachMetadataArchive(null, 4));
    }

    [Fact]
    public void AttachRejectsInvalidArchive()
    {
        StartProvider();
        var bogus = OpusTestHelpers.BuildArchive(("README.txt", "not a metadata archive"u8.ToArray()));
        _tempFiles.Add(bogus);
        var e = Assert.Throws<IOException>(() => Opus.AttachMetadataArchive(bogus, 1));
        Assert.Contains("export.pdb", e.Message);
        Assert.Null(Opus.FindArchive(1));
    }

    [Fact]
    public void AttachedArchiveServesAllMetadataKindsAndDetaches()
    {
        StartProvider();
        var archivePath = StandardArchive();
        Opus.AttachMetadataArchive(archivePath, 2);

        var archive = Opus.FindArchive(2);
        Assert.NotNull(archive);
        Assert.Equal(2, archive.UsbSlot);
        Assert.NotNull(archive.Database);
        Assert.Null(archive.Connection);
        Assert.Null(Opus.FindArchive(1));

        // The media-details queue holds the detach notification (always enqueued first) followed
        // by the details describing the newly attached archive; they are delivered through the
        // VirtualCdj one poll at a time.
        var delivered = new List<MediaDetails>();
        EventHandler<MediaDetails> onDetails = (_, details) => delivered.Add(details);
        VirtualCdj.Instance.MediaDetailsReceived += onDetails;
        try
        {
            Opus.PollAndSendMediaDetails(2);
            Opus.PollAndSendMediaDetails(2);
            Opus.PollAndSendMediaDetails(2);  // The queue is now empty, so nothing further arrives.
            Opus.PollAndSendMediaDetails(5);  // Out-of-range slots are ignored.
        }
        finally
        {
            VirtualCdj.Instance.MediaDetailsReceived -= onDetails;
        }
        Assert.Equal(2, delivered.Count);
        Assert.Equal(0, delivered[0].TrackCount);  // The "archive closed" notification.
        var details = delivered[1];
        Assert.Same(SlotReference.GetSlotReference(2, CdjStatus.TrackSourceSlot.UsbSlot), details.SlotReference);
        Assert.Equal(CdjStatus.TrackType.Rekordbox, details.MediaType);
        Assert.Equal(archivePath, details.Name);
        Assert.Equal(2, details.TrackCount);     // Tracks 1 and 2 (track 3 is deleted in the fixture pdb).
        Assert.Equal(1, details.PlaylistCount);

        // The provider serves every data kind from the archive. The player number in these
        // references is the USB slot to which the archive was attached.
        var provider = Opus.MetadataProvider;
        Assert.Empty(provider.SupportedMedia());
        var track = new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 1, CdjStatus.TrackType.Rekordbox);

        var metadata = provider.GetTrackMetadata(details, track);
        Assert.NotNull(metadata);
        Assert.Equal("alpha song", metadata.Title);
        Assert.Equal(new SearchableItem(7, "Björk"), metadata.Artist);

        var beatGrid = provider.GetBeatGrid(details, track);
        Assert.NotNull(beatGrid);
        Assert.True(beatGrid.BeatCount > 0);

        var preview = provider.GetWaveformPreview(details, track);
        Assert.NotNull(preview);
        Assert.Equal(WaveformStyle.Rgb, preview.Style);  // The default preferred style.

        var waveformDetail = provider.GetWaveformDetail(details, track);
        Assert.NotNull(waveformDetail);
        Assert.Equal(WaveformStyle.Rgb, waveformDetail.Style);

        var cueList = provider.GetCueList(details, track);
        Assert.NotNull(cueList);
        Assert.Equal(2, cueList.RawExtendedTags!.Count);

        var section = provider.GetAnalysisSection(details, track, ".EXT", "PSSI");
        Assert.NotNull(section);
        Assert.Equal(RekordboxAnlz.SectionTags.SongStructure, section.Fourcc);
        Assert.Null(provider.GetAnalysisSection(details, track, ".DAT", "PSSI"));  // Not present in .DAT files.

        // Album art is extracted from the archive (the high-resolution variant is missing, which
        // exercises the fallback to the normal path).
        var art = new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 8, CdjStatus.TrackType.Rekordbox);
        var albumArt = provider.GetAlbumArt(details, art);
        Assert.NotNull(albumArt);
        Assert.Equal(6, albumArt.RawBytes.Length);

        // Data for tracks (or slots) we don't know about is simply not served.
        Assert.Null(provider.GetTrackMetadata(details,
            new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 999, CdjStatus.TrackType.Rekordbox)));
        Assert.Null(provider.GetBeatGrid(details,
            new DataReference(1, CdjStatus.TrackSourceSlot.UsbSlot, 1, CdjStatus.TrackType.Rekordbox)));
        Assert.NotEmpty(ExtractedSlotFiles(2));

        // Detaching closes everything, forgets the PSSI mappings, and cleans up extracted files.
        Opus.AttachMetadataArchive(null, 2);
        Assert.Null(Opus.FindArchive(2));
        Assert.Empty(ExtractedSlotFiles(2));
        var rawBody = OpusTestHelpers.FixtureSongStructureRawBody();
        var pssi = new byte[12 + rawBody.Length];
        rawBody.CopyTo(pssi, 12);
        Assert.Null(Opus.GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(pssi, 1));

        // The detach queued another "archive closed" notification.
        VirtualCdj.Instance.MediaDetailsReceived += onDetails;
        try
        {
            Opus.PollAndSendMediaDetails(2);
        }
        finally
        {
            VirtualCdj.Instance.MediaDetailsReceived -= onDetails;
        }
        Assert.Equal(3, delivered.Count);
        Assert.Equal(0, delivered[2].TrackCount);
    }

    [Fact]
    public void PssiMatchingPrefersIdReportedByOpus()
    {
        StartProvider();
        _tempFiles.Add(OpusTestHelpers.BuildStandardArchive());
        Opus.AttachMetadataArchive(_tempFiles[^1], 3);

        // Both tracks in the synthetic pdb share the same analysis path, so they hash to the same
        // song structure and produce multiple matches for a single PSSI.
        var rawBody = OpusTestHelpers.FixtureSongStructureRawBody();
        var pssi = new byte[12 + rawBody.Length];
        rawBody.CopyTo(pssi, 12);

        var preferred = Opus.GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(pssi, 2);
        Assert.NotNull(preferred);
        Assert.Equal(2, preferred.RekordboxId);  // The ID the Opus reported wins.
        Assert.Equal(3, preferred.UsbSlot);

        var alternate = Opus.GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(pssi, 1);
        Assert.NotNull(alternate);
        Assert.Equal(1, alternate.RekordboxId);

        // An ID we have no match for falls back to the first match found.
        var fallback = Opus.GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(pssi, 99);
        Assert.NotNull(fallback);
        Assert.Contains(fallback.RekordboxId, new[] { 1, 2 });

        // A PSSI we have never seen produces no match at all.
        Assert.Null(Opus.GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(new byte[64], 1));
        Assert.Null(Opus.GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(new byte[4], 1));  // Too short.

        // Full-track matching scans the attached archives via the analysis files themselves.
        Assert.Equal(3, Opus.FindMatchingUsbSlotForTrack(1, 3, pssi));
        Assert.Equal(0, Opus.FindMatchingUsbSlotForTrack(1, 3, new byte[64]));
        Assert.True(Opus.HasArchive(3));
        Assert.False(Opus.HasArchive(1));
    }

    [Fact]
    public void ArchivesFromHfsMediaHideThePioneerFolder()
    {
        StartProvider();
        // The archive was created from HFS+ media, so its PIONEER folder is hidden with a leading
        // period while the database's analysis paths still name the visible folder; every
        // extraction must exercise the leading-dot fallback.
        Opus.AttachMetadataArchive(StandardArchive(hiddenPioneerFolder: true), 1);
        var details = new MediaDetails(SlotReference.GetSlotReference(1, CdjStatus.TrackSourceSlot.UsbSlot),
            CdjStatus.TrackType.Rekordbox, "test", 2, 1, 0);
        var track = new DataReference(1, CdjStatus.TrackSourceSlot.UsbSlot, 1, CdjStatus.TrackType.Rekordbox);
        var beatGrid = Opus.MetadataProvider.GetBeatGrid(details, track);
        Assert.NotNull(beatGrid);
        Assert.True(beatGrid.BeatCount > 0);
    }

    [Fact]
    public void SqliteArchivesUseDeviceLibraryPlus()
    {
        const string key = "beat-link-opus-test-key";
        SQLitePCL.Batteries_V2.Init();  // The SQLCipher bundle is referenced by the test project.
        OpusProvider.DatabaseKey = key;
        Assert.True(Opus.UsingDeviceLibraryPlus);
        StartProvider();

        var archivePath = OpusTestHelpers.BuildArchive(
            ("exportLibrary.db", BuildEncryptedLibrary(key)),
            ("PIONEER/USBANLZ/ANLZ0000.DAT", File.ReadAllBytes(Path.Combine(OpusTestHelpers.FixtureDirectory, "ANLZ0000.DAT"))),
            ("PIONEER/USBANLZ/ANLZ0000.EXT", File.ReadAllBytes(Path.Combine(OpusTestHelpers.FixtureDirectory, "ANLZ0000.EXT"))));
        _tempFiles.Add(archivePath);
        Opus.AttachMetadataArchive(archivePath, 1);

        var archive = Opus.FindArchive(1);
        Assert.NotNull(archive);
        Assert.Null(archive.Database);
        Assert.NotNull(archive.Connection);

        // The queued details report the row counts from the SQLite database.
        var delivered = new List<MediaDetails>();
        EventHandler<MediaDetails> onDetails = (_, details) => delivered.Add(details);
        VirtualCdj.Instance.MediaDetailsReceived += onDetails;
        try
        {
            Opus.PollAndSendMediaDetails(1);
            Opus.PollAndSendMediaDetails(1);
        }
        finally
        {
            VirtualCdj.Instance.MediaDetailsReceived -= onDetails;
        }
        Assert.Equal(2, delivered.Count);
        Assert.Equal(1, delivered[1].TrackCount);
        Assert.Equal(0, delivered[1].PlaylistCount);

        // Metadata and analysis-file data are served through the SQLite connection.
        var track = new DataReference(1, CdjStatus.TrackSourceSlot.UsbSlot, 77, CdjStatus.TrackType.Rekordbox);
        var metadata = Opus.MetadataProvider.GetTrackMetadata(delivered[1], track);
        Assert.NotNull(metadata);
        Assert.Equal("Library Plus Song", metadata.Title);
        var beatGrid = Opus.MetadataProvider.GetBeatGrid(delivered[1], track);
        Assert.NotNull(beatGrid);
        Assert.True(beatGrid.BeatCount > 0);

        Opus.AttachMetadataArchive(null, 1);
        Assert.Null(Opus.FindArchive(1));
    }

    [Fact]
    public void MetadataFinderMountsPollTheDetailsQueue()
    {
        StartProvider();
        _tempFiles.Add(OpusTestHelpers.BuildStandardArchive());
        Opus.AttachMetadataArchive(_tempFiles[^1], 2);

        // Start the MetadataFinder (on loopback-safe overrides); when a mount is recorded for
        // player 2 while we are in Opus Quad compatibility mode, it skips the media query and
        // instead polls our queued details, one per status update.
        DeviceFinder.Instance.PortOverride = 0;
        VirtualCdj.Instance.SetRunningStateForTests(true);
        MetadataFinder.Instance.Start();
        Assert.True(MetadataFinder.Instance.IsRunning);

        var usbSlot = SlotReference.GetSlotReference(2, CdjStatus.TrackSourceSlot.UsbSlot);
        FinderTestHelpers.WaitUntil(() =>
        {
            var bytes = PacketFixtures.NexusCdjStatusPacket();
            bytes[0x21] = 2;
            bytes[0x28] = 0;
            bytes[0x29] = 0;
            bytes[0x2a] = 0;
            Util.NumberToBytes(0, bytes, 0x2c, 4);
            VirtualCdj.Instance.ProcessUpdate(new CdjStatus(bytes, VirtualCdjTestHelpers.AddressFor(2)));
            return MetadataFinder.Instance.GetMediaDetailsFor(usbSlot) != null;
        }, "the queued media details to be delivered through the mount report");

        var details = MetadataFinder.Instance.GetMediaDetailsFor(usbSlot)!;
        Assert.Equal(2, details.TrackCount);
        Assert.Equal(CdjStatus.TrackType.Rekordbox, details.MediaType);
    }

    [Fact]
    public void DatabaseKeyIsPersistedToDisk()
    {
        OpusProvider.DatabaseKey = "persisted-opus-key";
        OpusProvider.ReloadDatabaseKeyForTests();  // Force the next read to come from the file.
        Assert.Equal("persisted-opus-key", OpusProvider.DatabaseKey);
        Assert.True(Opus.UsingDeviceLibraryPlus);

        OpusProvider.DatabaseKey = null;
        OpusProvider.ReloadDatabaseKeyForTests();
        Assert.Null(OpusProvider.DatabaseKey);
        Assert.False(Opus.UsingDeviceLibraryPlus);
    }

    [Fact]
    public void OpenSQLiteConnectionRequiresConfiguredKey()
    {
        OpusProvider.DatabaseKey = null;
        var e = Assert.Throws<InvalidOperationException>(() =>
            OpusProvider.OpenSQLiteConnection(Path.Combine(Path.GetTempPath(), "missing.db")));
        Assert.Contains("No database key", e.Message);
    }

    /// <summary>
    /// Lists the files currently extracted for the given USB slot.
    /// </summary>
    private static string[] ExtractedSlotFiles(int slot) =>
        Directory.GetFiles(Opus.ExtractDirectory)
            .Where(file => Path.GetFileName(file).StartsWith($"slot-{slot}-", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Creates an encrypted (SQLCipher) Device Library Plus export with the minimal schema the
    /// SQLite metadata paths query, and returns its raw bytes so it can be placed in an archive.
    /// </summary>
    private byte[] BuildEncryptedLibrary(string key)
    {
        var path = Path.Combine(Path.GetTempPath(), $"beat-link-net-opus-library-{Guid.NewGuid():N}.db");
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
                create table playlist (playlist_id integer primary key);
                insert into content values (77, 'Library Plus Song', 245, 12345,
                    'From Device Library Plus', '2025-02-03', NULL, 2021, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, '/PIONEER/USBANLZ/ANLZ0000.DAT');
                """;
            command.ExecuteNonQuery();
        }
        return File.ReadAllBytes(path);
    }
}
