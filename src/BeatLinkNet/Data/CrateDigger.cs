using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BeatLink.CrateDigger.Pdb;
using Kaitai;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Database = BeatLink.CrateDigger.Database;
using FileFetcher = BeatLink.CrateDigger.FileFetcher;

namespace BeatLink.Data;

/// <summary>
/// <para>Uses the Crate Digger functionality (the NFS <see cref="FileFetcher"/> and the rekordbox
/// database export parser <see cref="Database"/>) to provide an even more reliable source of track
/// metadata, even when there are four players in use on the network and the
/// <see cref="VirtualCdj"/> is forced to use a non-standard player number.</para>
///
/// <para>To take advantage of these capabilities, simply call the <see cref="Start"/> method and
/// then use the <see cref="MetadataFinder"/> as you would have without this class.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>CrateDigger</c>, by James Elliott. Like the Java
/// original (where <c>org.deepsymmetry.beatlink.data.CrateDigger</c> coexists with
/// <c>org.deepsymmetry.cratedigger.Database</c>), this class shares its name with the
/// <c>BeatLink.CrateDigger</c> namespace.
/// </remarks>
public sealed partial class CrateDigger
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<CrateDigger>();

    /// <summary>
    /// The file name rekordbox uses to create legacy DeviceSQL export databases.
    /// </summary>
    public const string DeviceSqlFilename = "export.pdb";

    /// <summary>
    /// The file name rekordbox uses to create newer SQLite export databases.
    /// </summary>
    public const string SqliteFilename = "exportLibrary.db";

    /// <summary>
    /// Backs <see cref="RetryLimit"/>, volatile to match the Java <c>AtomicInteger</c>.
    /// </summary>
    private volatile int _retryLimit = 3;

    /// <summary>
    /// How many times we will try to download a file from a player before giving up.
    /// </summary>
    /// <exception cref="ArgumentException">if the value is not between 1 and 10, inclusive</exception>
    public int RetryLimit
    {
        get => _retryLimit;
        set
        {
            if (value is < 1 or > 10)
            {
                throw new ArgumentException("limit must be between 1 and 10", nameof(value));
            }
            _retryLimit = value;
        }
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running.
    /// </summary>
    /// <returns><c>true</c> if track metadata is being fetched using NFS for all active players</returns>
    public bool IsRunning => _running;

    /// <summary>
    /// Keeps track of the media that we have discovered name their database folder
    /// <c>.PIONEER</c> rather than <c>PIONEER</c> (which happens on HFS+ formatted media).
    /// </summary>
    private readonly ConcurrentDictionary<SlotReference, byte> _mediaWithHiddenPioneerFolder = new();

    /// <summary>
    /// Keeps track of the <see cref="DeviceAnnouncement"/> packets associated with media we hear
    /// about, so that if the device has already disappeared from the network when we learn about
    /// the unmount (caused by the loss of the device), we can still know what address it used to
    /// have, in order to properly clean up the <see cref="FileFetcher"/> cache.
    /// </summary>
    private readonly ConcurrentDictionary<int, DeviceAnnouncement> _addressBackup = new();

    /// <summary>
    /// Keep track of the slots we are currently trying to fetch local databases for, so we only do
    /// it once even if multiple media details responses arrive while we are working on the first.
    /// </summary>
    private readonly ConcurrentDictionary<SlotReference, byte> _activeRequests = new();

    /// <summary>
    /// Holds the local DeviceSQL databases we have fetched and parsed for mounted media, so we can
    /// use them to respond to metadata requests. A given slot will be present in at most one of
    /// this map and <see cref="_connections"/>, depending on whether the host device uses Device
    /// Library Plus.
    /// </summary>
    private readonly ConcurrentDictionary<SlotReference, Database> _databases = new();

    /// <summary>
    /// Holds the connections we have made to local SQLite databases we have fetched for mounted
    /// media, so we can use them to respond to metadata requests. A given slot will be present in
    /// at most one of this map and <see cref="_databases"/>, depending on whether the host device
    /// uses Device Library Plus.
    /// </summary>
    private readonly ConcurrentDictionary<SlotReference, SqliteConnection> _connections = new();

    /// <summary>
    /// Return the filesystem path needed to mount the NFS filesystem associated with a particular
    /// media slot.
    /// </summary>
    /// <param name="slot">the slot whose filesystem is desired</param>
    /// <returns>the path to use in the NFS mount request to access the files mounted in that slot</returns>
    /// <exception cref="ArgumentException">if it is a slot that we don't know how to handle</exception>
    private static string MountPath(CdjStatus.TrackSourceSlot slot)
    {
        if (slot == CdjStatus.TrackSourceSlot.SdSlot)
        {
            return "/B/";
        }
        if (slot == CdjStatus.TrackSourceSlot.UsbSlot)
        {
            return "/C/";
        }
        throw new ArgumentException("Don't know how to NFS mount filesystem for slot " + slot, nameof(slot));
    }

    /// <summary>
    /// How long we should back off, in milliseconds, before retrying after each failure to get a file.
    /// </summary>
    private const long RetryBackoff = 2000;

    /// <summary>
    /// The maximum amount of time we should wait between retry attempts, in milliseconds.
    /// </summary>
    private const long MaxRetryInterval = 6000;

    /// <summary>
    /// Helper method to call the <see cref="FileFetcher"/> with the right arguments to get a file
    /// for a particular slot, retrying up to the number of tries configured in
    /// <see cref="RetryLimit"/>.
    /// </summary>
    /// <param name="slot">the slot from which a file is desired</param>
    /// <param name="path">the path to the file within the slot's mounted filesystem</param>
    /// <param name="destination">where to write the file contents</param>
    /// <exception cref="IOException">if there is a problem fetching the file</exception>
    private void FetchFile(SlotReference slot, string path, FileInfo destination) =>
        FetchFile(slot, path, destination, RetryLimit);

    /// <summary>
    /// Helper method to call the <see cref="FileFetcher"/> with the right arguments to get a file
    /// for a particular slot, retrying up to the specified number of times. If a lookup of the
    /// <c>PIONEER</c> folder fails, retries with a <c>.PIONEER</c> prefix, because HFS+ formatted
    /// media hide the folder as a dot-file, and remembers that this is needed for the slot.
    /// </summary>
    /// <param name="slot">the slot from which a file is desired</param>
    /// <param name="path">the path to the file within the slot's mounted filesystem</param>
    /// <param name="destination">where to write the file contents</param>
    /// <param name="retryLimit">the maximum number of tries that will be made to read the file</param>
    /// <exception cref="IOException">if there is a problem fetching the file</exception>
    private void FetchFile(SlotReference slot, string path, FileInfo destination, int retryLimit)
    {
        var player = DeviceFinder.Instance.GetLatestAnnouncementFrom(slot.Player) ??
            throw new IOException("Cannot fetch file from player that is not found on the network; slot: " + slot);
        var triesMade = 0;
        if (path.StartsWith("PIONEER/", StringComparison.Ordinal) && _mediaWithHiddenPioneerFolder.ContainsKey(slot))
        {
            path = "." + path;  // We are dealing with HFS+ media, so skip the first, failed attempt to read it.
        }
        while (triesMade < retryLimit)
        {
            try
            {
                FileFetcher.Instance.Fetch(player.Address, MountPath(slot.Slot), path, destination);
                return;
            }
            catch (IOException e)
            {
                if (path.StartsWith("PIONEER/", StringComparison.Ordinal) &&
                    e.Message.Contains("lookup of element \"PIONEER\" returned status", StringComparison.Ordinal))
                {
                    // Workaround for the fact that HFS+ formatted devices hide their PIONEER directory as a dot-file.
                    _mediaWithHiddenPioneerFolder.TryAdd(slot, 0);  // Skip the initial failed attempt next time we access it.
                    FetchFile(slot, "." + path, destination);
                    return;
                }
                triesMade++;
                if (triesMade < retryLimit)
                {
                    Logger.LogWarning(e, "Attempt to fetch file {Path} from {Slot} to {Destination} failed, tries left: {TriesLeft}",
                        path, slot, destination, retryLimit - triesMade);
                    try
                    {
                        Thread.Sleep((int)Math.Min(MaxRetryInterval, triesMade * RetryBackoff));
                    }
                    catch (ThreadInterruptedException)
                    {
                        Logger.LogWarning("Interrupted while sleeping between file fetch attempts. Retrying immediately.");
                    }
                }
                else
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Format the filename prefix that will be used to store files downloaded from a particular
    /// player slot. This allows them all to be cleaned up when that slot is unmounted or the
    /// player goes away.
    /// </summary>
    /// <param name="slotReference">the slot from which files are being downloaded</param>
    /// <returns>the prefix with which the names of all files downloaded from that slot will start</returns>
    private static string SlotPrefix(SlotReference slotReference) =>
        "player-" + slotReference.Player + "-slot-" + slotReference.Slot.ProtocolValue + "-";

    /// <summary>
    /// Format a number of bytes in a human-centric format.
    /// From <a href="https://stackoverflow.com/a/3758880/802383">Stack Overflow</a>.
    /// </summary>
    /// <param name="bytes">the number of bytes</param>
    /// <param name="si"><c>true</c> if we should use SI interpretation where k=1000</param>
    /// <returns>the nicely readable summary string</returns>
    public static string HumanReadableByteCount(long bytes, bool si)
    {
        var unit = si ? 1000 : 1024;
        if (bytes < unit)
        {
            return bytes + " B";
        }
        var exp = (int)(Math.Log(bytes) / Math.Log(unit));
        var pre = (si ? "kMGTPE" : "KMGTPE")[exp - 1] + (si ? "" : "i");
        return $"{bytes / Math.Pow(unit, exp):F1} {pre}B";
    }

    /// <summary>
    /// Check whether we would be able to use a database read from the specified slot. Makes sure
    /// the device that owns the slot uses DeviceSQL track IDs, or that we know how to access
    /// SQLite databases if it doesn't. (Experimental, like the Java original.)
    /// </summary>
    /// <param name="slot">the slot from which a track was loaded</param>
    /// <returns><c>true</c> if we will be able to read track metadata from a database downloaded
    /// from that slot</returns>
    public bool CanUseDatabase(SlotReference slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var owner = DeviceFinder.Instance.GetLatestAnnouncementFrom(slot.Player);
        // (The Java implementation would throw a NullPointerException for an unknown player; we
        // simply report that we cannot use the database.)
        return owner != null && (!owner.IsUsingDeviceLibraryPlus || OpusProvider.Instance.UsingDeviceLibraryPlus);
    }

    /// <summary>
    /// Helper function to determine where a database file for a particular slot should be downloaded.
    /// </summary>
    /// <param name="slot">the media slot from which the database is being downloaded</param>
    /// <param name="databaseFileName">the name of the file being downloaded (varies between
    /// DeviceSQL and SQLite)</param>
    /// <returns>the file to which that database should be downloaded</returns>
    private FileInfo LocalDatabaseFile(SlotReference slot, string databaseFileName) =>
        new(Path.Combine(DownloadDirectory, SlotPrefix(slot) + databaseFileName));

    /// <summary>
    /// Whenever we learn media details about a newly-mounted media slot, if it is rekordbox media,
    /// start the process of fetching and parsing the database, so we can offer metadata for that slot.
    /// </summary>
    private void OnMediaDetailsAvailable(object? sender, MediaDetails details) => HandleMediaDetails(details);

    /// <summary>
    /// The body of the media details handler, separated out so it can also be used to process
    /// already-known media when we start.
    /// </summary>
    /// <param name="details">the details reported for a mounted media slot</param>
    private void HandleMediaDetails(MediaDetails details)
    {
        if (IsRunning && details.MediaType == CdjStatus.TrackType.Rekordbox &&
            !VirtualCdj.Instance.InOpusQuadCompatibilityMode &&  // If we are dealing with an Opus Quad, we can't download files.
            details.SlotReference.Slot != CdjStatus.TrackSourceSlot.Collection &&  // We always use dbserver to talk to rekordbox.
            !_databases.ContainsKey(details.SlotReference) &&    // We haven't already downloaded it, ...
            !_connections.ContainsKey(details.SlotReference) &&  // ... or downloaded and connected to the SQLite version.
            CanUseDatabase(details.SlotReference) &&  // The player uses a database format we can read.
            _activeRequests.TryAdd(details.SlotReference, 0))
        {
            var thread = new Thread(() => DownloadAndParseDatabase(details))
            {
                Name = "beat-link CrateDigger database fetch",
                IsBackground = true
            };
            thread.Start();
        }
    }

    /// <summary>
    /// Runs on a background thread to download the database export for a newly-mounted media
    /// slot, parse or connect to it, record it so we can answer metadata queries about the slot,
    /// and let listeners know it is available.
    /// </summary>
    /// <param name="details">the details reported for the mounted media slot</param>
    private void DownloadAndParseDatabase(MediaDetails details)
    {
        var slot = details.SlotReference;
        var owner = DeviceFinder.Instance.GetLatestAnnouncementFrom(slot.Player);
        var usingDeviceLibraryPlus = owner?.IsUsingDeviceLibraryPlus ?? false;
        var databaseFileName = usingDeviceLibraryPlus ? SqliteFilename : DeviceSqlFilename;
        var file = LocalDatabaseFile(slot, databaseFileName);
        try
        {
            if (owner == null)
            {
                throw new IOException("Cannot fetch database from player that is not found on the network; slot: " + slot);
            }
            Logger.LogInformation("Fetching rekordbox {File} from player {Player}, slot {Slot}",
                databaseFileName, slot.Player, slot.Slot);
            var started = Util.CurrentTimestampNanos();
            FetchFile(slot, "PIONEER/rekordbox/" + databaseFileName, file);
            var duration = Math.Max(1, (Util.CurrentTimestampNanos() - started) / 1_000_000);
            file.Refresh();
            Logger.LogInformation("Finished fetching {File} from player {Player}, slot {Slot}; received {Size} in {Duration}ms, {Rate}/s.",
                databaseFileName, slot.Player, slot.Slot, HumanReadableByteCount(file.Length, true),
                duration, HumanReadableByteCount(file.Length * 1000 / duration, true));
            if (usingDeviceLibraryPlus)
            {
                var connection = OpusProvider.OpenSQLiteConnection(file.FullName);
                Logger.LogInformation("Found a Device Library Plus database and opened a connection to it.");
                _connections[slot] = connection;
                DeliverConnectionUpdate(slot, connection, true);
            }
            else
            {
                // The device uses DeviceSQL databases; parse it and notify listeners about it.
                started = Util.CurrentTimestampNanos();
                var database = new Database(file.FullName);
                duration = Math.Max(1, (Util.CurrentTimestampNanos() - started) / 1_000_000);
                Logger.LogInformation("Parsing database took {Duration}ms, {Rate} tracks/s",
                    duration, database.TrackIndex.Count * 1000L / duration);
                _databases[slot] = database;
                DeliverDatabaseUpdate(slot, database, true);
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem fetching rekordbox database for media {Details}, will not offer metadata for it.", details);
            TryDeleteFile(file);
        }
        finally
        {
            _activeRequests.TryRemove(slot, out _);
        }
    }

    /// <summary>
    /// Find the database we have downloaded and parsed that can provide information about the
    /// supplied data reference, if any.
    /// </summary>
    /// <param name="reference">identifies the location from which data is desired</param>
    /// <returns>the appropriate rekordbox extract to start from in finding that data, if we have one</returns>
    public Database? FindDatabase(DataReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.TrackType == CdjStatus.TrackType.Rekordbox)
        {
            return FindDatabase(reference.GetSlotReference());
        }
        return null;  // We can only offer data about tracks in the rekordbox database.
    }

    /// <summary>
    /// Find the database we have downloaded and parsed that can provide information about the
    /// supplied slot reference, if any.
    /// </summary>
    /// <param name="slot">identifies the slot from which data is desired</param>
    /// <returns>the appropriate rekordbox extract to start from in finding that data, if we have one</returns>
    public Database? FindDatabase(SlotReference slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return _databases.GetValueOrDefault(slot);
    }

    /// <summary>
    /// Find the connection, if any, we have opened to the SQLite (Device Library Plus) database
    /// we downloaded that can provide information about the supplied data reference.
    /// (Corresponds to the Java <c>findJdbcConnection</c>; experimental, like its Java counterpart.)
    /// </summary>
    /// <param name="reference">identifies the location from which data is desired</param>
    /// <returns>the appropriate connection to start from in finding that data, if we have one</returns>
    public SqliteConnection? FindConnection(DataReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.TrackType == CdjStatus.TrackType.Rekordbox)
        {
            return FindConnection(reference.GetSlotReference());
        }
        return null;  // We can only offer data about tracks in the rekordbox database.
    }

    /// <summary>
    /// Find the connection, if any, we have opened to the SQLite (Device Library Plus) database
    /// we downloaded that can provide information about the supplied slot reference.
    /// (Corresponds to the Java <c>findJdbcConnection</c>; experimental, like its Java counterpart.)
    /// </summary>
    /// <param name="slot">identifies the location from which data is desired</param>
    /// <returns>the appropriate connection to start from in finding that data, if we have one</returns>
    public SqliteConnection? FindConnection(SlotReference slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return _connections.GetValueOrDefault(slot);
    }

    /// <summary>
    /// Matches the <c>.DAT</c> extension at the end of an analysis path, so it can be replaced to
    /// request other analysis file types.
    /// </summary>
    [GeneratedRegex(@"\.DAT$")]
    private static partial Regex DatExtensionRegex();

    /// <summary>
    /// Find the analysis file for the specified track, downloading it from the player if we have
    /// not already done so. Be sure to dispose the struct's <c>M_Io</c> stream when you are done
    /// using the returned struct.
    /// </summary>
    /// <param name="track">the track whose analysis file is desired</param>
    /// <param name="database">the parsed database export from which the analysis path can be determined</param>
    /// <returns>the parsed file containing the track analysis</returns>
    private RekordboxAnlz? FindTrackAnalysis(DataReference track, Database database) =>
        FindTrackAnalysis(track, database, ".DAT");

    /// <summary>
    /// Find the extended analysis file for the specified track, downloading it from the player if
    /// we have not already done so. Be sure to dispose the struct's <c>M_Io</c> stream when you
    /// are done using the returned struct.
    /// </summary>
    /// <param name="track">the track whose extended analysis file is desired</param>
    /// <param name="database">the parsed DeviceSQL database export from which the analysis path
    /// can be determined</param>
    /// <returns>the parsed file containing the track analysis</returns>
    private RekordboxAnlz? FindExtendedAnalysis(DataReference track, Database database) =>
        FindTrackAnalysis(track, database, ".EXT");

    /// <summary>
    /// Find an analysis file for the specified track, with the specified file extension,
    /// downloading it from the player if we have not already done so. Be sure to dispose the
    /// struct's <c>M_Io</c> stream when you are done using the returned struct.
    /// </summary>
    /// <param name="track">the track whose analysis file is desired</param>
    /// <param name="database">the parsed DeviceSQL database export from which the analysis path
    /// can be determined</param>
    /// <param name="extension">the file extension (such as ".DAT" or ".EXT") which identifies the
    /// type of file to be retrieved</param>
    /// <returns>the parsed file containing the track analysis</returns>
    private RekordboxAnlz? FindTrackAnalysis(DataReference track, Database database, string extension)
    {
        var file = TrackAnalysisFile(track, extension);
        try
        {
            if (!database.TrackIndex.TryGetValue(track.RekordboxId, out var trackRow))
            {
                return null;
            }
            var filePath = Path.GetFullPath(file.FullName);
            var analyzePath = Database.GetText(trackRow.AnalyzePath);
            var requestedPath = DatExtensionRegex().Replace(analyzePath, extension.ToUpperInvariant());
            try
            {
                lock (Util.AllocateNamedLock(filePath))
                {
                    return OpenOrFetchAnalysis(track, filePath, requestedPath, file);
                }
            }
            catch (Exception e)
            {
                // We can give a more specific error including the file path.
                Logger.LogError(e, "Problem parsing requested analysis file {Path} for track {Track} from database {Database}",
                    requestedPath, track, database);
                TryDeleteFile(file);
            }
            finally
            {
                Util.FreeNamedLock(filePath);
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem fetching analysis file with extension {Extension} for track {Track} from database {Database}",
                extension.ToUpperInvariant(), track, database);
            TryDeleteFile(file);
        }
        return null;
    }

    /// <summary>
    /// Once the analysis path is known and the named lock for the local file is held, returns the
    /// parsed local copy of the analysis file, downloading it first if we do not already have it.
    /// </summary>
    /// <param name="track">the track whose analysis file is desired</param>
    /// <param name="filePath">the full path of the local file which caches the analysis</param>
    /// <param name="requestedPath">the path of the analysis file within the slot's filesystem</param>
    /// <param name="file">the local file which caches the analysis</param>
    /// <returns>the parsed file containing the track analysis</returns>
    private RekordboxAnlz OpenOrFetchAnalysis(DataReference track, string filePath, string requestedPath, FileInfo file)
    {
        if (!File.Exists(filePath))
        {
            FetchFile(track.GetSlotReference(), requestedPath, file);
        }
        return new RekordboxAnlz(new KaitaiStream(filePath));
    }

    /// <summary>
    /// Find the analysis file for the specified track, downloading it from the player if we have
    /// not already done so. Be sure to dispose the struct's <c>M_Io</c> stream when you are done
    /// using the returned struct.
    /// </summary>
    /// <param name="track">the track whose analysis file is desired</param>
    /// <param name="connection">the connection to the SQLite Device Library Plus database export
    /// from which the analysis path can be determined</param>
    /// <returns>the parsed file containing the track analysis</returns>
    private RekordboxAnlz? FindTrackAnalysis(DataReference track, SqliteConnection connection) =>
        FindTrackAnalysis(track, connection, ".DAT");

    /// <summary>
    /// Find the extended analysis file for the specified track, downloading it from the player if
    /// we have not already done so. Be sure to dispose the struct's <c>M_Io</c> stream when you
    /// are done using the returned struct.
    /// </summary>
    /// <param name="track">the track whose extended analysis file is desired</param>
    /// <param name="connection">the connection to the SQLite Device Library Plus database export
    /// from which the analysis path can be determined</param>
    /// <returns>the parsed file containing the track analysis</returns>
    private RekordboxAnlz? FindExtendedAnalysis(DataReference track, SqliteConnection connection) =>
        FindTrackAnalysis(track, connection, ".EXT");

    /// <summary>
    /// Identifies the file that will hold analysis information for a particular track.
    /// </summary>
    /// <param name="track">the track whose analysis file is desired</param>
    /// <param name="extension">the file extension (such as ".DAT" or ".EXT") which identifies the
    /// type of file to be retrieved</param>
    /// <returns>the file that should be used to hold this analysis</returns>
    private FileInfo TrackAnalysisFile(DataReference track, string extension) =>
        new(Path.Combine(DownloadDirectory, SlotPrefix(track.GetSlotReference()) +
            "track-" + track.RekordboxId + "-anlz" + extension.ToLowerInvariant()));

    /// <summary>
    /// Find an analysis file for the specified track, with the specified file extension,
    /// downloading it from the player if we have not already done so. Be sure to dispose the
    /// struct's <c>M_Io</c> stream when you are done using the returned struct.
    /// </summary>
    /// <param name="track">the track whose analysis file is desired</param>
    /// <param name="connection">the connection to the SQLite Device Library Plus database export
    /// from which the analysis path can be determined</param>
    /// <param name="extension">the file extension (such as ".DAT" or ".EXT") which identifies the
    /// type of file to be retrieved</param>
    /// <returns>the parsed file containing the track analysis</returns>
    private RekordboxAnlz? FindTrackAnalysis(DataReference track, SqliteConnection connection, string extension)
    {
        var file = TrackAnalysisFile(track, extension);
        try
        {
            string? analyzePath = null;
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "select analysisDataFilePath from content where content_id = $id";
                command.Parameters.AddWithValue("$id", track.RekordboxId);
                analyzePath = command.ExecuteScalar() as string;
            }
            catch (SqliteException e)
            {
                Logger.LogError(e, "Problem reading track analysis file path from SQLite database");
            }

            if (analyzePath != null)
            {
                var filePath = Path.GetFullPath(file.FullName);
                var requestedPath = DatExtensionRegex().Replace(analyzePath, extension.ToUpperInvariant());
                try
                {
                    lock (Util.AllocateNamedLock(filePath))
                    {
                        return OpenOrFetchAnalysis(track, filePath, requestedPath, file);
                    }
                }
                catch (Exception e)
                {
                    // We can give a more specific error including the file path.
                    Logger.LogError(e, "Problem parsing requested analysis file {Path} for track {Track} from connection {Connection}",
                        requestedPath, track, connection);
                    TryDeleteFile(file);
                }
                finally
                {
                    Util.FreeNamedLock(filePath);
                }
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem fetching analysis file with extension {Extension} for track {Track} from connection {Connection}",
                extension.ToUpperInvariant(), track, connection);
            TryDeleteFile(file);
        }
        return null;
    }

    /// <summary>
    /// Capture a log copy of a track analysis for which a data problem has been identified, to
    /// assist in further research. (Experimental, like the Java original.)
    /// </summary>
    /// <param name="track">the track whose analysis file is to be logged</param>
    /// <param name="extension">the file extension (such as ".DAT" or ".EXT") which identifies the
    /// type of file to be logged</param>
    /// <param name="reason">a description of why the file is being logged, for the application log</param>
    /// <param name="logDir">the directory into which the file should be copied</param>
    /// <exception cref="IOException">if there is a problem copying the analysis file to the log directory</exception>
    /// <exception cref="ArgumentException">if the track or its analysis file can't be found</exception>
    public void LogTrackAnalysis(DataReference track, string extension, string reason, string logDir)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(logDir);
        var analysis = TrackAnalysisFile(track, extension);
        if (!analysis.Exists)
        {
            throw new ArgumentException("No analysis file exists for track " + track, nameof(track));
        }

        Logger.LogInformation("Copying {Name} to analysis problem log directory because {Reason}.", analysis.Name, reason);
        File.Copy(analysis.FullName, Path.Combine(logDir, analysis.Name), overwrite: true);
    }

    /// <summary>
    /// Attempt to delete a file, ignoring any failure (the Java implementation ignored the result
    /// of <c>File.delete()</c> the same way).
    /// </summary>
    /// <param name="file">the file to be deleted if possible</param>
    private static void TryDeleteFile(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "Unable to delete file {File}", file);
        }
    }

    /// <summary>
    /// This is the mechanism by which we offer metadata to the <see cref="MetadataFinder"/> while
    /// we are running.
    /// </summary>
    private readonly IMetadataProvider _metadataProvider;

    /// <summary>
    /// Test seam: allows the metadata provider we register with the <see cref="MetadataFinder"/>
    /// to be exercised directly.
    /// </summary>
    internal IMetadataProvider MetadataProviderForTests => _metadataProvider;

    /// <summary>
    /// The implementation of <see cref="IMetadataProvider"/> that answers queries from the local
    /// databases and analysis files we have downloaded.
    /// </summary>
    /// <param name="owner">gives access to the downloaded databases and files</param>
    private sealed class CrateDiggerMetadataProvider(CrateDigger owner) : IMetadataProvider
    {
        /// <inheritdoc/>
        public IReadOnlyList<MediaDetails> SupportedMedia() => [];  // We can potentially answer any query.

        /// <inheritdoc/>
        public TrackMetadata? GetTrackMetadata(MediaDetails sourceMedia, DataReference track)
        {
            var connection = owner.FindConnection(track);  // First try SQLite database if open.
            if (connection != null)
            {
                try
                {
                    return new TrackMetadata(track, connection, GetCueList(sourceMedia, track));
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching metadata for track {Track} from connection {Connection}", track, connection);
                }
            }

            var database = owner.FindDatabase(track);  // Otherwise try parsed DeviceSQL database if available.
            if (database != null)
            {
                try
                {
                    return new TrackMetadata(track, database, GetCueList(sourceMedia, track));
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching metadata for track {Track} from database {Database}", track, database);
                }
            }
            return null;
        }

        /// <inheritdoc/>
        public AlbumArt? GetAlbumArt(MediaDetails sourceMedia, DataReference art)
        {
            string? artPath = null;
            var connection = owner.FindConnection(art);  // First try SQLite database if open.
            if (connection != null)
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "select path from image where image_id = $id";
                    command.Parameters.AddWithValue("$id", art.RekordboxId);
                    artPath = command.ExecuteScalar() as string;
                }
                catch (SqliteException e)
                {
                    Logger.LogError(e, "Problem retrieving artwork path from SQLite database");
                }
            }
            else
            {
                var database = owner.FindDatabase(art);  // Otherwise try parsed DeviceSQL database if available.
                if (database != null)
                {
                    if (database.ArtworkIndex.TryGetValue(art.RekordboxId, out var artworkRow))
                    {
                        artPath = Database.GetText(artworkRow.Path);
                    }
                    else
                    {
                        Logger.LogWarning("Unable to find artwork {Art} in database {Database}", art, database);
                    }
                }
            }

            if (artPath != null)
            {
                var file = new FileInfo(Path.Combine(owner.DownloadDirectory,
                    SlotPrefix(art.GetSlotReference()) + "art-" + art.RekordboxId + ".jpg"));
                try
                {
                    if (file.Exists)
                    {
                        return new AlbumArt(art, file.FullName);
                    }
                    if (ArtFinder.Instance.RequestHighResolutionArt)
                    {
                        try
                        {
                            owner.FetchFile(art.GetSlotReference(), Util.HighResolutionPath(artPath), file, 1);
                        }
                        catch (IOException e)
                        {
                            if (e is not FileNotFoundException)
                            {
                                Logger.LogError(e, "Unexpected exception type trying to load high resolution album art");
                            }
                            // Fall back to looking for the normal resolution art.
                            owner.FetchFile(art.GetSlotReference(), artPath, file);
                        }
                    }
                    else
                    {
                        owner.FetchFile(art.GetSlotReference(), artPath, file);
                    }
                    return new AlbumArt(art, file.FullName);
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "Problem fetching artwork {Art}", art);
                    TryDeleteFile(file);
                }
            }

            return null;
        }

        /// <inheritdoc/>
        public BeatGrid? GetBeatGrid(MediaDetails sourceMedia, DataReference track)
        {
            RekordboxAnlz? analysisFile = null;
            var connection = owner.FindConnection(track);  // First try SQLite database if open.
            if (connection != null)
            {
                try
                {
                    analysisFile = owner.FindTrackAnalysis(track, connection);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching beat grid for track {Track} from connection {Connection}", track, connection);
                }
            }

            var database = owner.FindDatabase(track);  // Otherwise try parsed DeviceSQL database if available.
            if (database != null)
            {
                try
                {
                    analysisFile = owner.FindTrackAnalysis(track, database);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching beat grid for track {Track} from database {Database}", track, database);
                }
            }

            if (analysisFile != null)
            {
                try
                {
                    return new BeatGrid(track, analysisFile);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching beat grid for track {Track}", track);
                }
                finally
                {
                    CloseAnalysisFile(analysisFile, "fetching beat grid", track);
                }
            }

            return null;
        }

        /// <inheritdoc/>
        public CueList? GetCueList(MediaDetails sourceMedia, DataReference track)
        {
            RekordboxAnlz? analysisFile = null;

            var connection = owner.FindConnection(track);  // First try SQLite database if open.
            if (connection != null)
            {
                try
                {
                    // Try the extended file first, because it can contain both nxs2-style commented cues and basic cues.
                    analysisFile = owner.FindExtendedAnalysis(track, connection) ??
                        owner.FindTrackAnalysis(track, connection);  // No extended analysis found, fall back to the basic one.
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching cue list for track {Track} from connection {Connection}", track, connection);
                }
            }

            var database = owner.FindDatabase(track);  // Otherwise try parsed DeviceSQL database if available.
            if (database != null)
            {
                try
                {
                    // Try the extended file first, because it can contain both nxs2-style commented cues and basic cues.
                    analysisFile = owner.FindExtendedAnalysis(track, database) ??
                        owner.FindTrackAnalysis(track, database);  // No extended analysis found, fall back to the basic one.
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching cue list for track {Track} from database {Database}", track, database);
                }
            }

            if (analysisFile != null)
            {
                try
                {
                    return new CueList(analysisFile);
                }
                finally
                {
                    CloseAnalysisFile(analysisFile, "fetching cue list", track);
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to build a waveform preview of a specific style from a specific analysis file.
        /// </summary>
        /// <param name="track">the track whose waveform preview is desired</param>
        /// <param name="analysisFile">the analysis file which may contain it, or <c>null</c> if
        /// the file could not be obtained (in which case <c>null</c> is simply returned)</param>
        /// <param name="style">the style of waveform preview desired</param>
        /// <returns>the preview, or <c>null</c> if the file did not contain one</returns>
        /// <exception cref="InvalidOperationException">if the analysis file lacks the requested waveform</exception>
        private static WaveformPreview? BuildPreview(DataReference track, RekordboxAnlz? analysisFile, WaveformStyle style)
        {
            if (analysisFile == null)
            {
                return null;
            }
            try
            {
                return new WaveformPreview(track, analysisFile, style);
            }
            finally
            {
                CloseAnalysisFile(analysisFile, "fetching waveform preview", track);
            }
        }

        /// <inheritdoc/>
        public WaveformPreview? GetWaveformPreview(MediaDetails sourceMedia, DataReference track)
        {
            var connection = owner.FindConnection(track);  // First try SQLite database if open.
            if (connection != null)
            {
                // Attempt 3-band preview if so configured first.
                if (WaveformFinder.Instance.PreferredStyle == WaveformStyle.ThreeBand)
                {
                    try
                    {
                        var preview = BuildPreview(track, owner.FindTrackAnalysis(track, connection, ".2EX"), WaveformStyle.ThreeBand);
                        if (preview != null)
                        {
                            return preview;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Logger.LogInformation("No 3-band preview waveform found (using SQLite), falling back to color or blue version.");
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching 3-band waveform preview for track {Track} from connection {Connection}", track, connection);
                    }
                }

                // Attempt color preview if so configured next.
                if (WaveformFinder.Instance.PreferredStyle == WaveformStyle.Rgb)
                {
                    try
                    {
                        var preview = BuildPreview(track, owner.FindExtendedAnalysis(track, connection), WaveformStyle.Rgb);
                        if (preview != null)
                        {
                            return preview;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Logger.LogInformation("No color preview waveform found (using SQLite), checking for blue version.");
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching color waveform preview for track {Track} from connection {Connection}", track, connection);
                    }
                }

                // Final fallback option, the old blue-and-white waveforms.
                try
                {
                    var preview = BuildPreview(track, owner.FindTrackAnalysis(track, connection), WaveformStyle.Blue);
                    if (preview != null)
                    {
                        return preview;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching waveform preview for track {Track} from connection {Connection}", track, connection);
                }
            }

            var database = owner.FindDatabase(track);  // Otherwise try parsed DeviceSQL database if available.
            if (database != null)
            {
                // Attempt 3-band preview if so configured first.
                if (WaveformFinder.Instance.PreferredStyle == WaveformStyle.ThreeBand)
                {
                    try
                    {
                        var preview = BuildPreview(track, owner.FindTrackAnalysis(track, database, ".2EX"), WaveformStyle.ThreeBand);
                        if (preview != null)
                        {
                            return preview;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Logger.LogInformation("No 3-band preview waveform found, falling back to color or blue version.");
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching 3-band waveform preview for track {Track} from database {Database}", track, database);
                    }
                }

                // Attempt color preview if so configured next.
                if (WaveformFinder.Instance.PreferredStyle == WaveformStyle.Rgb)
                {
                    try
                    {
                        var preview = BuildPreview(track, owner.FindExtendedAnalysis(track, database), WaveformStyle.Rgb);
                        if (preview != null)
                        {
                            return preview;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Logger.LogInformation("No color preview waveform found, checking for blue version.");
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching color waveform preview for track {Track} from database {Database}", track, database);
                    }
                }

                // Final fallback option, the old blue-and-white waveforms.
                try
                {
                    var preview = BuildPreview(track, owner.FindTrackAnalysis(track, database), WaveformStyle.Blue);
                    if (preview != null)
                    {
                        return preview;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching waveform preview for track {Track} from database {Database}", track, database);
                }
            }
            return null;
        }

        /// <summary>
        /// Attempts to build a waveform detail of a specific style from a specific analysis file.
        /// </summary>
        /// <param name="track">the track whose waveform detail is desired</param>
        /// <param name="analysisFile">the analysis file which may contain it, or <c>null</c> if
        /// the file could not be obtained (in which case <c>null</c> is simply returned)</param>
        /// <param name="style">the style of waveform detail desired</param>
        /// <returns>the detail, or <c>null</c> if the file did not contain one</returns>
        /// <exception cref="InvalidOperationException">if the analysis file lacks the requested waveform</exception>
        private static WaveformDetail? BuildDetail(DataReference track, RekordboxAnlz? analysisFile, WaveformStyle style)
        {
            if (analysisFile == null)
            {
                return null;
            }
            try
            {
                return new WaveformDetail(track, analysisFile, style);
            }
            finally
            {
                CloseAnalysisFile(analysisFile, "fetching waveform detail", track);
            }
        }

        /// <inheritdoc/>
        public WaveformDetail? GetWaveformDetail(MediaDetails sourceMedia, DataReference track)
        {
            var connection = owner.FindConnection(track);  // First try SQLite database if open.
            if (connection != null)
            {
                // Attempt 3-band waveform detail if so configured first.
                if (WaveformFinder.Instance.PreferredStyle == WaveformStyle.ThreeBand)
                {
                    try
                    {
                        var detail = BuildDetail(track, owner.FindTrackAnalysis(track, connection, ".2EX"), WaveformStyle.ThreeBand);
                        if (detail != null)
                        {
                            return detail;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Logger.LogInformation("No 3-band detail waveform found (using SQLite), falling back to color or blue version.");
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching waveform detail for track {Track} from connection {Connection}", track, connection);
                    }
                }

                // Fall back to color or blue/white waveform detail.
                try
                {
                    var detail = BuildDetail(track, owner.FindExtendedAnalysis(track, connection), WaveformStyle.Rgb);
                    if (detail != null)
                    {
                        return detail;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching waveform detail for track {Track} from connection {Connection}", track, connection);
                }
            }

            var database = owner.FindDatabase(track);  // Otherwise try parsed DeviceSQL database if available.
            if (database != null)
            {
                // Attempt 3-band waveform detail if so configured first.
                if (WaveformFinder.Instance.PreferredStyle == WaveformStyle.ThreeBand)
                {
                    try
                    {
                        var detail = BuildDetail(track, owner.FindTrackAnalysis(track, database, ".2EX"), WaveformStyle.ThreeBand);
                        if (detail != null)
                        {
                            return detail;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Logger.LogInformation("No 3-band detail waveform found, falling back to color or blue version.");
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching waveform detail for track {Track} from database {Database}", track, database);
                    }
                }

                // Fall back to color or blue/white waveform detail.
                try
                {
                    var detail = BuildDetail(track, owner.FindExtendedAnalysis(track, database), WaveformStyle.Rgb);
                    if (detail != null)
                    {
                        return detail;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching waveform detail for track {Track} from database {Database}", track, database);
                }
            }
            return null;
        }

        /// <inheritdoc/>
        public RekordboxAnlz.TaggedSection? GetAnalysisSection(MediaDetails sourceMedia, DataReference track,
            string fileExtension, string typeTag)
        {
            RekordboxAnlz? analysisFile = null;
            var connection = owner.FindConnection(track);  // First try SQLite database if open.
            if (connection != null)
            {
                analysisFile = owner.FindTrackAnalysis(track, connection, fileExtension);  // Open the desired file to scan.
            }
            else
            {
                var database = owner.FindDatabase(track);  // Otherwise try parsed DeviceSQL database if available.
                if (database != null)
                {
                    analysisFile = owner.FindTrackAnalysis(track, database, fileExtension);  // Open the desired file to scan.
                }
            }

            if (analysisFile != null)
            {
                try
                {
                    if (typeTag.Length > 4)
                    {
                        throw new ArgumentException("typeTag cannot be longer than four characters", nameof(typeTag));
                    }

                    var fourcc = 0;  // Convert the type tag to an integer as found in the analysis file.
                    for (var i = 0; i < 4; i++)
                    {
                        fourcc *= 256;
                        if (i < typeTag.Length)
                        {
                            fourcc += typeTag[i];
                        }
                    }

                    try
                    {
                        // Scan for the requested tag type.
                        foreach (var section in analysisFile.Sections)
                        {
                            if ((int)section.Fourcc == fourcc)
                            {
                                return section;
                            }
                        }
                    }
                    finally
                    {
                        analysisFile.M_Io.Dispose();
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching analysis file {Extension} section {TypeTag} for track {Track}",
                        fileExtension, typeTag, track);
                }
            }
            return null;
        }

        /// <summary>
        /// Closes the file stream underlying a parsed analysis file, logging rather than
        /// propagating any failure.
        /// </summary>
        /// <param name="analysisFile">the parsed analysis file to close</param>
        /// <param name="activity">describes what we were doing, for the log message</param>
        /// <param name="track">the track we were working on, for the log message</param>
        private static void CloseAnalysisFile(RekordboxAnlz analysisFile, string activity, DataReference track)
        {
            try
            {
                analysisFile.M_Io.Dispose();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Problem closing analysis file {File} {Activity} for track {Track}",
                    analysisFile, activity, track);
            }
        }
    }

    /// <summary>
    /// Serializes lifecycle transitions, standing in for the Java <c>synchronized</c> start and
    /// stop methods.
    /// </summary>
    private readonly object _lifecycleLock = new();

    /// <summary>
    /// Start finding track metadata for all active players using the NFS server on the players to
    /// pull the exported database and track analysis files. Starts the <see cref="MetadataFinder"/>
    /// if it is not already running, because we build on its features. This will transitively
    /// start many of the other Beat Link subsystems, and stopping any of them will stop us as well.
    /// </summary>
    /// <exception cref="Exception">if there is a problem starting the required components</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                MetadataFinder.Instance.Start();
                _running = true;
                // Try fetching the databases of anything that was already mounted before we started.
                foreach (var details in MetadataFinder.Instance.MountedMediaDetails)
                {
                    HandleMediaDetails(details);
                }
                MetadataFinder.Instance.AddMetadataProvider(_metadataProvider);
            }
        }
    }

    /// <summary>
    /// Stop finding track metadata for all active players.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                _running = false;
                MetadataFinder.Instance.RemoveMetadataProvider(_metadataProvider);
                foreach (var database in _databases.Values)
                {
                    // (Unlike the Java implementation, we close each database before deleting its
                    // source file, since open files cannot be deleted on all platforms.)
                    var sourceFile = database.SourceFile;
                    try
                    {
                        database.Dispose();
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem closing parsed rekordbox database export {File}.", sourceFile);
                    }
                    TryDeleteFile(new FileInfo(sourceFile));
                }
                _databases.Clear();
                foreach (var entry in _connections)
                {
                    try
                    {
                        entry.Value.Dispose();
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem closing connection to SQLite database {Connection} for slot {Slot}.",
                            entry.Value, entry.Key);
                    }
                    TryDeleteFile(LocalDatabaseFile(entry.Key, SqliteFilename));
                }
                _connections.Clear();
            }
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static CrateDigger Instance { get; } = new();

    /// <summary>
    /// The folder into which database exports and track analysis files will be downloaded.
    /// </summary>
    public string DownloadDirectory { get; }

    /// <summary>
    /// The number of variations on the directory name we will attempt when creating our temporary
    /// directory.
    /// </summary>
    private const int TempDirAttempts = 1000;

    /// <summary>
    /// Create the directory into which we can download (and reuse) database exports and track
    /// analysis files: a uniquely-named directory inside a <c>beat-link-crate-digger</c> root in
    /// the system temporary directory.
    /// </summary>
    /// <returns>the path of the created temporary directory</returns>
    /// <exception cref="InvalidOperationException">if the directory cannot be created</exception>
    internal static string CreateDownloadDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "beat-link-crate-digger");
        Directory.CreateDirectory(baseDir);
        var baseName = "bl-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-";

        for (var counter = 0; counter < TempDirAttempts; counter++)
        {
            var tempDir = Path.Combine(baseDir, baseName + counter);
            if (!Directory.Exists(tempDir) && !File.Exists(tempDir))
            {
                try
                {
                    Directory.CreateDirectory(tempDir);
                    return tempDir;
                }
                catch (IOException)
                {
                    // Another process claimed the name first; try the next candidate.
                }
            }
        }
        throw new InvalidOperationException("Failed to create download directory within " + TempDirAttempts + " attempts.");
    }

    /// <summary>
    /// <para>Raised when a DeviceSQL database has been fetched and parsed for a media slot, so it
    /// is available for answering metadata queries.</para>
    ///
    /// <para>To reduce latency, this event is delivered synchronously on the thread that noticed
    /// the change (matching the Java listener semantics), so handlers must finish quickly and
    /// must not interact with user interface objects directly.</para>
    /// </summary>
    public event EventHandler<DatabaseUpdate>? DatabaseMounted;

    /// <summary>
    /// <para>Raised when the media underlying a previously-announced DeviceSQL database has been
    /// unmounted, so the database is no longer relevant (it is closed, and its downloaded file
    /// deleted, as soon as the handlers return).</para>
    ///
    /// <para>To reduce latency, this event is delivered synchronously on the thread that noticed
    /// the change (matching the Java listener semantics), so handlers must finish quickly and
    /// must not interact with user interface objects directly.</para>
    /// </summary>
    public event EventHandler<DatabaseUpdate>? DatabaseUnmounted;

    /// <summary>
    /// Send a database announcement to all registered listeners.
    /// </summary>
    /// <param name="slot">the media slot whose database availability has changed</param>
    /// <param name="database">the database whose relevance has changed</param>
    /// <param name="available">if <c>true</c>, the database is newly available, otherwise it is no
    /// longer relevant</param>
    private void DeliverDatabaseUpdate(SlotReference slot, Database database, bool available) =>
        EventDispatcher.DeliverNow(available ? DatabaseMounted : DatabaseUnmounted, this,
            new DatabaseUpdate(slot, database), Logger);

    /// <summary>
    /// <para>Raised when a connection has been opened to a downloaded SQLite Device Library Plus
    /// database for a media slot, so it is available for answering metadata queries.
    /// (Experimental, like the Java <c>SQLiteConnectionListener</c>.)</para>
    ///
    /// <para>To reduce latency, this event is delivered synchronously on the thread that noticed
    /// the change (matching the Java listener semantics), so handlers must finish quickly and
    /// must not interact with user interface objects directly.</para>
    /// </summary>
    public event EventHandler<ConnectionUpdate>? ConnectionMounted;

    /// <summary>
    /// <para>Raised when the media underlying a previously-announced SQLite Device Library Plus
    /// database has been unmounted, so the connection is no longer relevant (it is closed, and
    /// the downloaded file deleted, as soon as the handlers return). (Experimental, like the Java
    /// <c>SQLiteConnectionListener</c>.)</para>
    ///
    /// <para>To reduce latency, this event is delivered synchronously on the thread that noticed
    /// the change (matching the Java listener semantics), so handlers must finish quickly and
    /// must not interact with user interface objects directly.</para>
    /// </summary>
    public event EventHandler<ConnectionUpdate>? ConnectionUnmounted;

    /// <summary>
    /// Send a SQLite database connection announcement to all registered listeners.
    /// </summary>
    /// <param name="slot">the media slot whose database connection availability has changed</param>
    /// <param name="connection">the SQLite connection whose relevance has changed</param>
    /// <param name="available">if <c>true</c>, the connection is newly available, otherwise it is
    /// no longer relevant and about to be closed</param>
    private void DeliverConnectionUpdate(SlotReference slot, SqliteConnection connection, bool available) =>
        EventDispatcher.DeliverNow(available ? ConnectionMounted : ConnectionUnmounted, this,
            new ConnectionUpdate(slot, connection), Logger);

    /// <summary>
    /// Test seam: discards the caches which (as in the Java implementation) otherwise survive a
    /// stop/start cycle, so tests remain independent of each other.
    /// </summary>
    internal void ClearCachesForTests()
    {
        _mediaWithHiddenPioneerFolder.Clear();
        _addressBackup.Clear();
        _activeRequests.Clear();
    }

    /// <summary>
    /// Test seam: when set to <c>false</c>, prevents the <see cref="MetadataFinder"/> lifecycle
    /// wiring from automatically starting us whenever the finder starts (which would otherwise
    /// interfere with unrelated tests once this singleton has been created). Automatic stopping
    /// remains in effect, since it is harmless.
    /// </summary>
    internal volatile bool AutoStartOnMetadataFinderStart = true;

    /// <summary>
    /// Allows us to automatically start when the MetadataFinder, which we depend on, does.
    /// </summary>
    private void OnMetadataFinderStarted(object? sender, EventArgs e)
    {
        if (!AutoStartOnMetadataFinderStart)
        {
            return;
        }
        Task.Run(() =>
        {
            try
            {
                Logger.LogInformation("CrateDigger starting because {Sender} has.", sender);
                Start();
            }
            catch (Exception t)
            {
                Logger.LogError(t, "Problem starting the CrateDigger in response to a lifecycle event.");
            }
        });
    }

    /// <summary>
    /// Allows us to automatically shut down when the MetadataFinder, which we depend on, does.
    /// </summary>
    private void OnMetadataFinderStopped(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            Logger.LogInformation("CrateDigger stopping because {Sender} has.", sender);
            Stop();
        }
    }

    /// <summary>
    /// Records information about the device owning a newly-mounted slot, in case it disappears
    /// before we learn we need to unmount its media.
    /// </summary>
    private void OnMediaMounted(object? sender, SlotReference slot)
    {
        var player = DeviceFinder.Instance.GetLatestAnnouncementFrom(slot.Player);
        if (player != null)
        {
            _addressBackup[slot.Player] = player;
        }

        // Nothing else to do here yet, we need to wait until the media details are available.
        Logger.LogDebug("Media mounted, waiting for details.");
    }

    /// <summary>
    /// Clears the <see cref="FileFetcher"/> cache when media is unmounted, so it does not try to
    /// use stale filesystem handles. Also cleans up our own caches for the vanished media, and
    /// closes the files associated with the parsed database structures.
    /// </summary>
    private void OnMediaUnmounted(object? sender, SlotReference slot)
    {
        _mediaWithHiddenPioneerFolder.TryRemove(slot, out _);
        var player = DeviceFinder.Instance.GetLatestAnnouncementFrom(slot.Player);
        if (player == null)
        {
            Logger.LogInformation("Received an unmount for a player we can't find, it must have left network.");
            player = _addressBackup.GetValueOrDefault(slot.Player);
        }
        if (player != null)
        {
            FileFetcher.Instance.RemovePlayer(player.Address);
        }
        else
        {
            Logger.LogWarning("Unable to clear FileFetcher connections for player that we can't find backup address for!");
        }

        if (_databases.TryRemove(slot, out var database))
        {
            DeliverDatabaseUpdate(slot, database, false);
            var sourceFile = database.SourceFile;
            try
            {
                database.Dispose();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Problem closing parsed rekordbox database export for slot {Slot}.", slot);
            }
            TryDeleteFile(new FileInfo(sourceFile));
        }

        if (_connections.TryRemove(slot, out var connection))
        {
            DeliverConnectionUpdate(slot, connection, false);
            try
            {
                connection.Dispose();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Problem closing SQLite rekordbox database export connection for slot {Slot}.", slot);
            }
            TryDeleteFile(LocalDatabaseFile(slot, SqliteFilename));
        }

        var prefix = SlotPrefix(slot);
        try
        {
            foreach (var file in Directory.EnumerateFiles(DownloadDirectory))
            {
                if (Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
                {
                    TryDeleteFile(new FileInfo(file));
                }
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "Problem cleaning up files downloaded for slot {Slot}.", slot);
        }
    }

    /// <summary>
    /// Clears the <see cref="FileFetcher"/> cache when players disappear, so it does not try to
    /// use stale filesystem handles. Our own caches will be cleaned up by the unmount handler.
    /// </summary>
    private void OnDeviceLost(object? sender, DeviceAnnouncement announcement) =>
        FileFetcher.Instance.RemovePlayer(announcement.Address);

    /// <summary>
    /// Prevent direct instantiation, create a temporary directory for our file downloads, and
    /// register the handlers that hook us into the streams of information we need.
    /// </summary>
    private CrateDigger()
    {
        DownloadDirectory = CreateDownloadDirectory();
        _metadataProvider = new CrateDiggerMetadataProvider(this);

        // Allows us to automatically shut down when the MetadataFinder, which we depend on, does,
        // and start back up once it resumes.
        MetadataFinder.Instance.Started += OnMetadataFinderStarted;
        MetadataFinder.Instance.Stopped += OnMetadataFinderStopped;

        MetadataFinder.Instance.MediaMounted += OnMediaMounted;
        MetadataFinder.Instance.MediaUnmounted += OnMediaUnmounted;
        MetadataFinder.Instance.MediaDetailsAvailable += OnMediaDetailsAvailable;

        DeviceFinder.Instance.DeviceLost += OnDeviceLost;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("CrateDigger[running: ").Append(IsRunning);
        if (IsRunning)
        {
            sb.Append(", databases mounted: ").Append(_databases.Count)
                .Append(", SQLite connections open: ").Append(_connections.Count)
                .Append(", download directory: ").Append(DownloadDirectory)
                .Append(", media using hidden PIONEER folder: [")
                .Append(string.Join(", ", _mediaWithHiddenPioneerFolder.Keys)).Append(']');
        }
        return sb.Append(']').ToString();
    }
}
