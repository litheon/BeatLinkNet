// Experimental Opus Quad support: this file both defines and consumes the experimental surface,
// so the experimental-API diagnostic is disabled for the whole file.
#pragma warning disable BLNET001

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BeatLink.CrateDigger.Pdb;
using Kaitai;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Database = BeatLink.CrateDigger.Database;

namespace BeatLink.Data;

/// <summary>
/// <para>Allows users to attach metadata archives created by the Crate Digger
/// <c>Archivist</c> to provide metadata when the corresponding USB is mounted in an Opus Quad,
/// which is not capable of providing most metadata on its own.</para>
///
/// <para>To take advantage of these capabilities, simply call the <see cref="Start"/> method and
/// then use the <see cref="MetadataFinder"/> as you would have without this class.</para>
///
/// <para>Ported from the beat-link <c>OpusProvider</c> class by James Elliott. Like all Opus Quad
/// support, this class is experimental.</para>
/// </summary>
public sealed partial class OpusProvider : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<OpusProvider>();

    /// <summary>
    /// The device name reported by the Opus Quad, which uses a significantly different protocol
    /// than true Pro DJ Link devices.
    /// </summary>
    public const string OpusName = "OPUS-QUAD";

    /// <summary>
    /// The device name reported by the XDJ-AZ, which (like the Opus Quad) can be in a strange,
    /// non-Pro DJ Link mode.
    /// </summary>
    public const string XdjAzName = "XDJ-AZ";

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running: <c>true</c> if track metadata is being proxied
    /// from mounted media archives for Opus Quad players.
    /// </summary>
    public override bool IsRunning => _running;

    /// <summary>
    /// Serializes archive attachment and lifecycle transitions, standing in for the Java
    /// <c>synchronized</c> methods.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Check whether we are using Device Library Plus (SQLite) databases rather than DeviceSQL,
    /// which happens when the key needed to open them has been supplied through
    /// <see cref="DatabaseKey"/>. (In other words, whether we are posing as a Virtual CDJ and
    /// relying on our ability to read Device Library Plus records to match track information,
    /// rather than using PSSI matching with rekordbox lighting emulation.)
    /// </summary>
    public bool UsingDeviceLibraryPlus => DatabaseKey != null;

    /// <summary>
    /// Guards the lazily-loaded, file-backed <see cref="DatabaseKey"/> value.
    /// </summary>
    private static readonly object DatabaseKeyLock = new();

    /// <summary>
    /// Backs <see cref="DatabaseKey"/> once it has been loaded from (or stored to) the backing file.
    /// </summary>
    private static string? _databaseKey;

    /// <summary>
    /// Tracks whether we have loaded the persisted database key yet.
    /// </summary>
    private static bool _databaseKeyLoaded;

    /// <summary>
    /// The file in which the database key is persisted between runs, standing in for the Java
    /// implementation's use of the platform preferences store.
    /// </summary>
    private static string DatabaseKeyFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatLinkNet", "opus-database-key");

    /// <summary>
    /// The key used to decrypt SQLite Device Library Plus (<c>exportLibrary.db</c>) database
    /// exports, or <c>null</c> if none has been configured, in which case such databases cannot
    /// be used. The value is persisted in a file under the user's application-data folder
    /// (standing in for the Java implementation's platform preferences store), so once supplied
    /// it survives across runs; assigning <c>null</c> removes the persisted value.
    /// </summary>
    public static string? DatabaseKey
    {
        get
        {
            lock (DatabaseKeyLock)
            {
                if (!_databaseKeyLoaded)
                {
                    _databaseKeyLoaded = true;
                    try
                    {
                        if (File.Exists(DatabaseKeyFile))
                        {
                            _databaseKey = File.ReadAllText(DatabaseKeyFile);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem reading stored database key");
                    }
                }
                return _databaseKey;
            }
        }
        set
        {
            lock (DatabaseKeyLock)
            {
                _databaseKey = value;
                _databaseKeyLoaded = true;
                try
                {
                    if (value != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseKeyFile)!);
                        File.WriteAllText(DatabaseKeyFile, value);
                    }
                    else if (File.Exists(DatabaseKeyFile))
                    {
                        File.Delete(DatabaseKeyFile);
                    }
                }
                catch (Exception e)
                {
                    // The Java implementation logs (rather than propagates) preference store
                    // failures the same way.
                    Logger.LogError(e, "Problem updating stored database key");
                }
            }
        }
    }

    /// <summary>
    /// Test seam: discards the cached <see cref="DatabaseKey"/> value so the next read reloads it
    /// from the backing file, allowing the persistence behavior itself to be verified.
    /// </summary>
    internal static void ReloadDatabaseKeyForTests()
    {
        lock (DatabaseKeyLock)
        {
            _databaseKey = null;
            _databaseKeyLoaded = false;
        }
    }

    /// <summary>
    /// Container for the USB slot number, database or SQLite connection, and archive of any
    /// particular rekordbox USB metadata archive. Experimental, like all Opus Quad support.
    /// </summary>
    [Experimental("BLNET001")]
    public sealed class RekordboxUsbArchive
    {
        /// <summary>
        /// The USB slot on the Opus Quad to which the user has reported this metadata belongs.
        /// </summary>
        public int UsbSlot { get; }

        /// <summary>
        /// The parsed DeviceSQL database which contains information about tracks, artwork, etc.
        /// Will be <c>null</c> if <see cref="Connection"/> is not <c>null</c>.
        /// </summary>
        public Database? Database { get; }

        /// <summary>
        /// The connection used to communicate with the SQLite database found in this archive.
        /// Will be <c>null</c> if the archive lacked an <c>exportLibrary.db</c> file or if
        /// <see cref="DatabaseKey"/> is <c>null</c> or incorrect.
        /// </summary>
        public SqliteConnection? Connection { get; }

        /// <summary>
        /// The opened ZIP archive which contains the database and other metadata (this stands in
        /// for the ZIP filesystem the Java implementation mounts).
        /// </summary>
        public ZipArchive FileSystem { get; }

        /// <summary>
        /// The path of the archive file, used for logging and to name the media in the
        /// synthesized <see cref="MediaDetails"/>.
        /// </summary>
        internal string ArchivePath { get; }

        internal RekordboxUsbArchive(int usbSlot, Database? database, SqliteConnection? connection,
            ZipArchive fileSystem, string archivePath)
        {
            UsbSlot = usbSlot;
            Database = database;
            Connection = connection;
            FileSystem = fileSystem;
            ArchivePath = archivePath;
        }

        /// <inheritdoc/>
        public override string ToString() => $"RekordboxUsbArchive[usbSlot:{UsbSlot}, archive:{ArchivePath}]";
    }

    /// <summary>
    /// Holds the archive information (USB slot number, parsed database or SQLite connection, and
    /// opened ZIP archive) of media exports mounted for the Opus USB slots.
    /// </summary>
    private readonly ConcurrentDictionary<int, RekordboxUsbArchive> _usbArchiveMap = new();

    /// <summary>
    /// Contains a queue per slot number which allows us to slow down delivering media-details
    /// updates when we attach metadata archives until the application is actually ready to
    /// receive them (they are polled by the <see cref="MetadataFinder"/> as mounts are reported).
    /// Queues are initialized in the constructor and will never be missing for slots 1 through 3.
    /// </summary>
    private readonly ConcurrentDictionary<int, ConcurrentQueue<MediaDetails>> _archiveAttachQueueMap = new();

    /// <summary>
    /// Holds a rekordbox DeviceSQL ID and USB slot number together; a value type safe to use as a
    /// dictionary key or set member. Experimental, like all Opus Quad support.
    /// </summary>
    /// <param name="RekordboxId">the DeviceSQL rekordbox ID of a track</param>
    /// <param name="UsbSlot">the USB slot whose attached archive contains the track</param>
    [Experimental("BLNET001")]
    public sealed record DeviceSqlRekordboxIdAndSlot(int RekordboxId, int UsbSlot);

    /// <summary>
    /// A map from the SHA-1 hash of the song structure of a track to any corresponding DeviceSQL
    /// rekordbox ID and USB slot matches (the value dictionaries are used as concurrent sets).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DeviceSqlRekordboxIdAndSlot, byte>> _pssiToDeviceSqlRekordboxIds = new();

    /// <summary>
    /// Compute the SHA-1 hash of the given data, formatted as a lowercase hexadecimal string,
    /// similar to the <see cref="SignatureFinder"/>'s track signatures.
    /// </summary>
    /// <param name="data">the data to hash</param>
    /// <returns>the SHA-1 hash of the data</returns>
    private static string ComputeSha1(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));

    /// <summary>
    /// Attach a metadata archive to supply information for the media mounted in a USB slot of the
    /// Opus Quad. This must be a file created by the Crate Digger <c>Archivist</c> from that
    /// media. Experimental, like all Opus Quad support.
    /// </summary>
    /// <param name="archiveFile">the metadata archive that can provide metadata for tracks playing
    /// from the specified USB slot, or <c>null</c> to stop providing metadata for that slot</param>
    /// <param name="usbSlotNumber">which USB slot we are to provide metadata for; acceptable
    /// values are 1, 2 and 3</param>
    /// <exception cref="IOException">if there is a problem attaching the archive</exception>
    /// <exception cref="ArgumentException">if a slot other than 1, 2 or 3 is specified</exception>
    /// <exception cref="InvalidOperationException">if the <see cref="OpusProvider"/> is not
    /// running, since synthesized <see cref="MediaDetails"/> cannot be created without it</exception>
    [Experimental("BLNET001")]
    public void AttachMetadataArchive(string? archiveFile, int usbSlotNumber)
    {
        lock (_lock)
        {
            if (usbSlotNumber is < 1 or > 3)
            {
                throw new ArgumentException("Unsupported usbSlotNumber, can only use 1, 2 or 3.", nameof(usbSlotNumber));
            }

            // First close and remove any archive we had previously attached for this slot.
            _usbArchiveMap.TryRemove(usbSlotNumber, out var formerArchive);

            // Report archive closed. (The Java implementation uses a null slot type here, which
            // our interned SlotReference does not permit, so NoTrack stands in for it.)
            var emptySlotReference = SlotReference.GetSlotReference(usbSlotNumber, CdjStatus.TrackSourceSlot.NoTrack);
            var emptyDetails = new MediaDetails(emptySlotReference, CdjStatus.TrackType.Rekordbox, "", 0, 0, 0);
            _archiveAttachQueueMap[usbSlotNumber].Enqueue(emptyDetails);

            if (formerArchive != null)
            {
                try
                {
                    Logger.LogInformation("Detached metadata archive {Archive} from slot {Slot}",
                        formerArchive, formerArchive.UsbSlot);
                    formerArchive.Database?.Dispose();
                    if (formerArchive.Connection != null)
                    {
                        try
                        {
                            formerArchive.Connection.Dispose();
                        }
                        catch (Exception e)
                        {
                            Logger.LogError(e, "Problem closing metadata archive SQLite connection for slot {Slot}", usbSlotNumber);
                        }
                    }
                    formerArchive.FileSystem.Dispose();
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem closing database or archive for slot {Slot}", usbSlotNumber);
                }

                // Clear player caches as matching data is not applicable anymore.
                if (VirtualRekordbox.Instance.IsRunning)
                {
                    VirtualRekordbox.Instance.ClearPlayerCaches(usbSlotNumber);
                }

                // Clean up PSSI mappings for this USB slot.
                foreach (var matches in _pssiToDeviceSqlRekordboxIds.Values)
                {
                    foreach (var match in matches.Keys)
                    {
                        if (match.UsbSlot == usbSlotNumber)
                        {
                            matches.TryRemove(match, out _);
                        }
                    }
                }

                // Further clean up by removing any SHA-1 hashes that no longer have matches.
                foreach (var entry in _pssiToDeviceSqlRekordboxIds)
                {
                    if (entry.Value.IsEmpty)
                    {
                        _pssiToDeviceSqlRekordboxIds.TryRemove(entry.Key, out _);
                    }
                }

                Logger.LogInformation("Removed PSSI mappings for slot {Slot}, pssiToDeviceSqlRekordboxIds now has {Count} entries",
                    usbSlotNumber, _pssiToDeviceSqlRekordboxIds.Count);

                // Clean up any extracted files associated with this archive.
                DeleteExtractedFilesForSlot(usbSlotNumber);
            }

            if (archiveFile == null)
            {
                return;  // Detaching is all we were asked to do, so we are done.
            }

            var slotReference = SlotReference.GetSlotReference(usbSlotNumber, CdjStatus.TrackSourceSlot.UsbSlot);
            RekordboxUsbArchive? openedArchive = null;
            MediaDetails? newDetails = null;

            // First see if we can decrypt and use a SQLite Device Library Plus database from the archive.
            if (DatabaseKey != null)
            {
                // Open the new archive.
                var filesystem = ZipFile.OpenRead(archiveFile);
                try
                {
                    var databaseFile = new FileInfo(Path.Combine(ExtractDirectory, SlotPrefix(usbSlotNumber) + "exportLibrary.db"));
                    ExtractFile(filesystem, "exportLibrary.db", databaseFile);
                    var connection = OpenSQLiteConnection(databaseFile.FullName);

                    // If we got here, this is a valid metadata archive, we found a valid SQLite
                    // Device Library Plus export inside it, and we have the correct key to
                    // decrypt and use the database.
                    openedArchive = new RekordboxUsbArchive(usbSlotNumber, null, connection, filesystem, archiveFile);
                    databaseFile.Refresh();
                    newDetails = new MediaDetails(slotReference, CdjStatus.TrackType.Rekordbox, archiveFile,
                        GetRowCount(connection, "content"), GetRowCount(connection, "playlist"),
                        new DateTimeOffset(databaseFile.LastWriteTimeUtc).ToUnixTimeMilliseconds());
                    Logger.LogInformation("Attached SQLite metadata archive {Archive} for slot {Slot}.", archiveFile, usbSlotNumber);
                }
                catch (Exception e)
                {
                    filesystem.Dispose();
                    Logger.LogError(e, "Problem reading exportLibrary.db from metadata archive {Archive}, is database key correct?", archiveFile);
                }
            }

            // Failing that, fall back to parsing and using the DeviceSQL export database. (Unlike
            // the Java implementation, which reused the ZIP filesystem it had already closed after
            // a SQLite failure, we open the archive freshly for this attempt.)
            if (openedArchive == null)
            {
                var filesystem = ZipFile.OpenRead(archiveFile);
                try
                {
                    var databaseFile = new FileInfo(Path.Combine(ExtractDirectory, SlotPrefix(usbSlotNumber) + "export.pdb"));
                    ExtractFile(filesystem, "export.pdb", databaseFile);
                    var database = new Database(databaseFile.FullName);

                    // If we got here, this looks like a valid metadata archive because we found a
                    // valid DeviceSQL database export inside it.
                    openedArchive = new RekordboxUsbArchive(usbSlotNumber, database, null, filesystem, archiveFile);
                    databaseFile.Refresh();
                    newDetails = new MediaDetails(slotReference, CdjStatus.TrackType.Rekordbox, archiveFile,
                        database.TrackIndex.Count, database.PlaylistIndex.Count,
                        new DateTimeOffset(databaseFile.LastWriteTimeUtc).ToUnixTimeMilliseconds());

                    // Populate the PSSI SHA-1 index used to match tracks reported by the Opus.
                    foreach (var key in database.TrackIndex.Keys)
                    {
                        var trackId = checked((int)key);
                        var dataRef = new DataReference(slotReference, trackId, CdjStatus.TrackType.Rekordbox);
                        var anlz = FindTrackAnalysis(usbSlotNumber, dataRef, database, null, filesystem, ".EXT");
                        if (anlz != null)
                        {
                            try
                            {
                                var songStructure = GetSongStructureRawBody(anlz);
                                if (songStructure != null)
                                {
                                    var sha1 = ComputeSha1(songStructure);
                                    var shaSet = _pssiToDeviceSqlRekordboxIds.GetOrAdd(sha1,
                                        static _ => new ConcurrentDictionary<DeviceSqlRekordboxIdAndSlot, byte>());
                                    shaSet.TryAdd(new DeviceSqlRekordboxIdAndSlot(trackId, usbSlotNumber), 0);
                                }
                                else
                                {
                                    Logger.LogWarning("No SONG_STRUCTURE found for track {TrackId}", trackId);
                                }
                            }
                            finally
                            {
                                anlz.M_Io.Dispose();
                            }
                        }
                        else
                        {
                            Logger.LogWarning("No extended analysis found for track {TrackId}", trackId);
                        }
                    }

                    Logger.LogInformation("pssiToDeviceSqlRekordboxIds now contains {Count} entries", _pssiToDeviceSqlRekordboxIds.Count);
                    Logger.LogInformation("Attached DeviceSQL metadata archive {Archive} for slot {Slot}.", archiveFile, usbSlotNumber);

                    // Request initial PSSIs for track matching. After this we will request PSSI
                    // data on song change.
                    VirtualRekordbox.Instance.RequestPssi();
                }
                catch (Exception e)
                {
                    filesystem.Dispose();
                    throw new IOException("Problem reading export.pdb from metadata archive " + archiveFile, e);
                }
            }

            // We successfully opened the archive with one of the two database formats.
            _usbArchiveMap[usbSlotNumber] = openedArchive;

            // Enqueue a media update so clients know this media is mounted.
            _archiveAttachQueueMap[usbSlotNumber].Enqueue(newDetails!);
        }
    }

    /// <summary>
    /// Deletes any files extracted into <see cref="ExtractDirectory"/> for the specified USB slot.
    /// </summary>
    /// <param name="usbSlotNumber">the slot whose extracted files should be removed</param>
    private void DeleteExtractedFilesForSlot(int usbSlotNumber)
    {
        var prefix = SlotPrefix(usbSlotNumber);
        try
        {
            foreach (var file in Directory.EnumerateFiles(ExtractDirectory))
            {
                if (Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception e)
                    {
                        Logger.LogDebug(e, "Unable to delete extracted file {File}", file);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "Problem cleaning up files extracted for slot {Slot}.", usbSlotNumber);
        }
    }

    /// <summary>
    /// Opens a connection to a downloaded SQLite Device Library Plus database export, decrypting
    /// it with the configured <see cref="DatabaseKey"/>. The database uses SQLCipher (version 4
    /// defaults), so the host application must have initialized a SQLCipher-capable
    /// <c>SQLitePCLRaw</c> provider before this can succeed. (Corresponds to the Java
    /// <c>openSQLiteConnection</c> instance method, which delegates to
    /// <c>SQLiteMCSqlCipherConfig.getV4Defaults()</c>; since the key is held statically in this
    /// port, the method is static as well.)
    /// </summary>
    /// <param name="databaseFile">the path of the downloaded <c>exportLibrary.db</c> file</param>
    /// <returns>an open connection to the decrypted database</returns>
    /// <exception cref="InvalidOperationException">if no <see cref="DatabaseKey"/> has been
    /// configured, or if no SQLCipher-capable SQLite provider has been initialized for
    /// <c>Microsoft.Data.Sqlite</c> (in which case the exception message explains how to set one
    /// up)</exception>
    /// <exception cref="SqliteException">if the database cannot be opened or decrypted (for
    /// example because the key is incorrect)</exception>
    public static SqliteConnection OpenSQLiteConnection(string databaseFile)
    {
        ArgumentNullException.ThrowIfNull(databaseFile);
        var key = DatabaseKey ?? throw new InvalidOperationException("No database key has been configured.");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWrite,
            Password = key,
            // Disable pooling so closing the connection releases the downloaded file right away,
            // allowing it to be deleted when its media is unmounted (Java JDBC connections are
            // similarly unpooled).
            Pooling = false
        }.ConnectionString;
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();

            // Run a trivial query to force validation of the decryption key right away, so
            // callers learn about a bad key here rather than on some later query.
            using var command = connection.CreateCommand();
            command.CommandText = "select count(*) from sqlite_master";
            command.ExecuteScalar();
            return connection;
        }
        catch (Exception e)
        {
            connection.Dispose();
            if (e.Message.Contains("SetProvider", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "No SQLite implementation has been initialized, so encrypted Device Library Plus " +
                    "databases cannot be opened. The host application must reference a SQLCipher-capable " +
                    "SQLitePCLRaw bundle (such as SQLitePCLRaw.bundle_e_sqlcipher) and call " +
                    "SQLitePCL.Batteries_V2.Init() during startup before using this feature.", e);
            }
            throw;
        }
    }

    /// <summary>
    /// Helper method to count the rows in a table. (The Java implementation's query accidentally
    /// omitted the table name, so it always reported 1; this port counts the actual rows.)
    /// </summary>
    /// <param name="connection">provides access to the database</param>
    /// <param name="table">the name of the table whose row count is desired (an internal constant,
    /// never user input)</param>
    /// <returns>the number of rows found in the table, or 0 if something goes wrong</returns>
    private static int GetRowCount(SqliteConnection connection, string table)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select count(*) from " + table;
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem counting rows in SQLite database from table {Table}", table);
        }
        return 0;
    }

    /// <summary>
    /// Find the metadata archive we have been provided that can provide metadata for the supplied
    /// USB slot, if any. Experimental, like all Opus Quad support.
    /// </summary>
    /// <param name="usbSlotNumber">identifies the Opus Quad USB slot for which metadata is desired</param>
    /// <returns>the archive holding that metadata, if we have one, or <c>null</c></returns>
    [Experimental("BLNET001")]
    public RekordboxUsbArchive? FindArchive(int usbSlotNumber) => _usbArchiveMap.GetValueOrDefault(usbSlotNumber);

    /// <summary>
    /// Check whether a metadata archive has been attached for the specified USB slot.
    /// </summary>
    /// <param name="usbSlotNumber">the USB slot number being checked</param>
    /// <returns><c>true</c> if an archive is attached for that slot</returns>
    internal bool HasArchive(int usbSlotNumber) => _usbArchiveMap.ContainsKey(usbSlotNumber);

    /// <summary>
    /// Grab any <see cref="MediaDetails"/> queued for a slot when a metadata archive was attached
    /// or detached, and deliver it to <see cref="VirtualCdj"/> listeners. Called by the
    /// <see cref="MetadataFinder"/> as media mounts are reported, so the update is not delivered
    /// before the application is ready for it.
    /// </summary>
    /// <param name="usbSlotNumber">the USB slot whose queue should be checked</param>
    internal void PollAndSendMediaDetails(int usbSlotNumber)
    {
        if (usbSlotNumber is > 0 and < 4 &&
            _archiveAttachQueueMap.TryGetValue(usbSlotNumber, out var queue) &&
            queue.TryDequeue(out var mediaDetails))
        {
            VirtualCdj.Instance.DeliverMediaDetailsUpdate(mediaDetails);
        }
    }

    /// <summary>
    /// Format the filename prefix that will be used to store files extracted for a particular USB
    /// slot. This allows them all to be cleaned up when that slot's media is detached.
    /// </summary>
    /// <param name="slot">the slot from which files are being extracted</param>
    /// <returns>the prefix with which the names of all files extracted for that slot will start</returns>
    private static string SlotPrefix(int slot) => "slot-" + slot + "-";

    /// <summary>
    /// Get the raw body of the SONG_STRUCTURE section of the extended analysis file.
    /// </summary>
    /// <param name="anlz">the extended analysis file to search</param>
    /// <returns>the raw body of the SONG_STRUCTURE section, or <c>null</c> if none is found</returns>
    private static byte[]? GetSongStructureRawBody(RekordboxAnlz anlz)
    {
        foreach (var section in anlz.Sections)
        {
            if (section.Fourcc == RekordboxAnlz.SectionTags.SongStructure)
            {
                return section.M_RawBody;
            }
        }
        return null;
    }

    /// <summary>
    /// Helper method to extract a file from the metadata archive. Handles the fact that archives
    /// created from HFS+ media hide the PIONEER folder with a leading period.
    /// </summary>
    /// <param name="archive">the ZIP archive opened from the metadata file</param>
    /// <param name="sourcePath">identifies the desired file within the archive</param>
    /// <param name="destination">the file on the host filesystem to which the file should be extracted</param>
    /// <exception cref="FileNotFoundException">if the file cannot be found in the archive</exception>
    /// <exception cref="IOException">if there is a problem extracting the file</exception>
    private static void ExtractFile(ZipArchive archive, string sourcePath, FileInfo destination)
    {
        var entryPath = sourcePath.TrimStart('/');
        lock (archive)  // ZipArchive reads are not thread-safe, and provider calls can be concurrent.
        {
            var entry = archive.GetEntry(entryPath);
            if (entry == null && entryPath.StartsWith("PIONEER/", StringComparison.Ordinal))
            {
                entry = archive.GetEntry("." + entryPath);  // We must be dealing with HFS+ media.
            }
            if (entry == null)
            {
                throw new FileNotFoundException("Unable to find " + sourcePath + " in metadata archive.", sourcePath);
            }
            entry.ExtractToFile(destination.FullName, overwrite: true);
        }
    }

    /// <summary>
    /// Matches the <c>.DAT</c> extension at the end of an analysis path, so it can be replaced to
    /// request other analysis file types.
    /// </summary>
    [GeneratedRegex(@"\.DAT$")]
    private static partial Regex DatExtensionRegex();

    /// <summary>
    /// Find an analysis file for the specified track, with the specified file extension. Be sure
    /// to dispose the struct's <c>M_Io</c> stream when you are done using the returned struct.
    /// </summary>
    /// <param name="usbSlotNumber">the Opus Quad USB slot whose archive is being consulted</param>
    /// <param name="track">the track whose analysis file is desired</param>
    /// <param name="database">the parsed DeviceSQL database export from which the analysis path
    /// can be determined, when no SQLite connection is available</param>
    /// <param name="connection">the connection to the more-useful SQLite database, if one is available</param>
    /// <param name="filesystem">the open ZIP archive in which metadata can be found</param>
    /// <param name="extension">the file extension (such as ".DAT" or ".EXT") which identifies the
    /// type of file to be retrieved</param>
    /// <returns>the parsed file containing the track analysis, or <c>null</c> if it cannot be found</returns>
    private RekordboxAnlz? FindTrackAnalysis(int usbSlotNumber, DataReference track, Database? database,
        SqliteConnection? connection, ZipArchive filesystem, string extension)
    {
        FileInfo? file = null;
        string? analyzePath = null;
        if (connection != null)
        {
            // We can use the nice SQLite Device Library Plus database.
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
        }
        else if (database != null)
        {
            // We have to fall back to the legacy DeviceSQL database.
            if (database.TrackIndex.TryGetValue(track.RekordboxId, out var trackRow))
            {
                analyzePath = Database.GetText(trackRow.AnalyzePath);
            }
        }

        try
        {
            if (analyzePath != null)
            {
                file = new FileInfo(Path.Combine(ExtractDirectory, SlotPrefix(usbSlotNumber) +
                    "track-" + track.RekordboxId + "-anlz" + extension.ToLowerInvariant()));
                var filePath = Path.GetFullPath(file.FullName);
                var requestedPath = DatExtensionRegex().Replace(analyzePath, extension.ToUpperInvariant());
                try
                {
                    lock (Util.AllocateNamedLock(filePath))
                    {
                        if (!File.Exists(filePath))
                        {
                            ExtractFile(filesystem, requestedPath, file);
                        }
                        return new RekordboxAnlz(new KaitaiStream(filePath));
                    }
                }
                catch (Exception e)
                {
                    // We can give a more specific error including the file path.
                    Logger.LogError(e, "Problem parsing requested analysis file {Path} for track {Track} from archive slot {Slot}",
                        requestedPath, track, usbSlotNumber);
                    TryDeleteFile(file);
                }
                finally
                {
                    Util.FreeNamedLock(filePath);
                }
            }
            else
            {
                Logger.LogWarning("Unable to find track {Track} in database for slot {Slot}", track, usbSlotNumber);
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem extracting analysis file with extension {Extension} for track {Track} from archive slot {Slot}",
                extension.ToUpperInvariant(), track, usbSlotNumber);
            if (file != null)
            {
                TryDeleteFile(file);
            }
        }
        return null;
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
    /// we are running. Exposed publicly so that it can be used for testing and as a utility if
    /// desired. Experimental, like all Opus Quad support.
    /// </summary>
    [Experimental("BLNET001")]
    public IMetadataProvider MetadataProvider { get; }

    /// <summary>
    /// The implementation of <see cref="IMetadataProvider"/> that answers queries from the
    /// attached metadata archives.
    /// </summary>
    /// <param name="owner">gives access to the attached archives and extracted files</param>
    private sealed class OpusMetadataProvider(OpusProvider owner) : IMetadataProvider
    {
        /// <inheritdoc/>
        public IReadOnlyList<MediaDetails> SupportedMedia() => [];  // We can answer queries about any media.

        /// <inheritdoc/>
        public TrackMetadata? GetTrackMetadata(MediaDetails sourceMedia, DataReference track)
        {
            var archive = owner.FindArchive(track.Player);
            if (archive != null && track.TrackType == CdjStatus.TrackType.Rekordbox)
            {
                var connection = archive.Connection;
                if (connection != null)
                {
                    // We have a usable SQLite Device Library Plus database connection we can use.
                    try
                    {
                        return new TrackMetadata(track, connection, GetCueList(sourceMedia, track));
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching metadata for track {Track} from SQLite connection", track);
                    }
                }
                else
                {
                    // We have to fall back to the legacy DeviceSQL database and hope the IDs match.
                    var database = archive.Database!;
                    try
                    {
                        return new TrackMetadata(track, database, GetCueList(sourceMedia, track));
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Problem fetching metadata for track {Track} from DeviceSQL database {Database}", track, database);
                    }
                }
            }
            return null;
        }

        /// <inheritdoc/>
        public AlbumArt? GetAlbumArt(MediaDetails sourceMedia, DataReference art)
        {
            FileInfo? file = null;
            var archive = owner.FindArchive(art.Player);

            if (archive != null && art.TrackType == CdjStatus.TrackType.Rekordbox)
            {
                var fileSystem = archive.FileSystem;
                var connection = archive.Connection;
                var database = archive.Database;

                try
                {
                    string? artPath = null;
                    if (connection != null)
                    {
                        // We have a connection to the SQLite Device Library Plus export database.
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
                    else if (database != null)
                    {
                        // We have to use the legacy DeviceSQL database.
                        if (database.ArtworkIndex.TryGetValue(art.RekordboxId, out var artworkRow))
                        {
                            artPath = Database.GetText(artworkRow.Path);
                        }
                    }

                    if (artPath != null)
                    {
                        file = new FileInfo(Path.Combine(owner.ExtractDirectory,
                            SlotPrefix(archive.UsbSlot) + "art-" + art.RekordboxId + ".jpg"));
                        if (file.Exists)
                        {
                            return new AlbumArt(art, file.FullName);
                        }
                        if (ArtFinder.Instance.RequestHighResolutionArt)
                        {
                            try
                            {
                                ExtractFile(fileSystem, Util.HighResolutionPath(artPath), file);
                            }
                            catch (IOException e)
                            {
                                if (e is not FileNotFoundException)
                                {
                                    Logger.LogError(e, "Unexpected exception type trying to load high resolution album art");
                                }
                                // Fall back to looking for the normal resolution art.
                                ExtractFile(fileSystem, artPath, file);
                            }
                        }
                        else
                        {
                            ExtractFile(fileSystem, artPath, file);
                        }
                        return new AlbumArt(art, file.FullName);
                    }
                    Logger.LogWarning("Unable to find artwork {Art} in archive {Archive}", art, archive);
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "Problem fetching artwork {Art} from archive {Archive}", art, archive);
                    if (file != null)
                    {
                        TryDeleteFile(file);
                    }
                }
            }
            return null;
        }

        /// <inheritdoc/>
        public BeatGrid? GetBeatGrid(MediaDetails sourceMedia, DataReference track)
        {
            var archive = owner.FindArchive(track.Player);
            if (archive != null && track.TrackType == CdjStatus.TrackType.Rekordbox)
            {
                try
                {
                    var file = owner.FindTrackAnalysis(archive.UsbSlot, track, archive.Database,
                        archive.Connection, archive.FileSystem, ".DAT");
                    if (file != null)
                    {
                        try
                        {
                            return new BeatGrid(track, file);
                        }
                        finally
                        {
                            file.M_Io.Dispose();
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching beat grid for track {Track} from archive {Archive}", track, archive);
                }
            }
            return null;
        }

        /// <inheritdoc/>
        public CueList? GetCueList(MediaDetails sourceMedia, DataReference track)
        {
            var archive = owner.FindArchive(track.Player);
            if (archive != null && track.TrackType == CdjStatus.TrackType.Rekordbox)
            {
                try
                {
                    // Try the extended file first, because it can contain both nxs2-style commented
                    // cues and basic cues.
                    var file = owner.FindTrackAnalysis(archive.UsbSlot, track, archive.Database,
                            archive.Connection, archive.FileSystem, ".EXT") ??
                        owner.FindTrackAnalysis(archive.UsbSlot, track, archive.Database,
                            archive.Connection, archive.FileSystem, ".DAT");
                    if (file != null)
                    {
                        try
                        {
                            return new CueList(file);
                        }
                        finally
                        {
                            file.M_Io.Dispose();
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching cue list for track {Track} from archive {Archive}", track, archive);
                }
            }
            return null;
        }

        /// <summary>
        /// Helper function to try finding a waveform preview of a particular style.
        /// </summary>
        /// <param name="archive">the metadata archive in which to look</param>
        /// <param name="track">the track whose waveform is being sought</param>
        /// <param name="style">the type of preview desired</param>
        /// <returns>the waveform preview, if found, or <c>null</c></returns>
        private WaveformPreview? FindPreview(RekordboxUsbArchive archive, DataReference track, WaveformStyle style)
        {
            var fileExtension = style == WaveformStyle.Blue ? ".DAT" :
                style == WaveformStyle.Rgb ? ".EXT" : ".2EX";
            try
            {
                var file = owner.FindTrackAnalysis(archive.UsbSlot, track, archive.Database,
                    archive.Connection, archive.FileSystem, fileExtension);
                if (file != null)
                {
                    try
                    {
                        return new WaveformPreview(track, file, style);
                    }
                    finally
                    {
                        file.M_Io.Dispose();
                    }
                }
            }
            catch (InvalidOperationException)
            {
                Logger.LogInformation("No {Style} waveform preview found.", style);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Problem fetching {Style} waveform preview for track {Track} from archive {Archive}",
                    style, track, archive);
            }
            return null;
        }

        /// <inheritdoc/>
        public WaveformPreview? GetWaveformPreview(MediaDetails sourceMedia, DataReference track)
        {
            var archive = owner.FindArchive(track.Player);
            // Return early if no archive exists for this player/slot, or it is not a rekordbox track.
            if (archive == null || track.TrackType != CdjStatus.TrackType.Rekordbox)
            {
                return null;
            }

            var style = WaveformFinder.Instance.PreferredStyle;
            var result = FindPreview(archive, track, style);  // Try preferred style first.
            if (result != null)
            {
                return result;
            }

            // Preferred style not found, attempt fallbacks based on the preferred style.
            switch (style)
            {
                case WaveformStyle.Blue:
                    return FindPreview(archive, track, WaveformStyle.Rgb) ??
                        FindPreview(archive, track, WaveformStyle.ThreeBand);
                case WaveformStyle.Rgb:
                    return FindPreview(archive, track, WaveformStyle.ThreeBand) ??
                        FindPreview(archive, track, WaveformStyle.Blue);
                case WaveformStyle.ThreeBand:
                    return FindPreview(archive, track, WaveformStyle.Rgb) ??
                        FindPreview(archive, track, WaveformStyle.Blue);
                default:
                    Logger.LogError("Unrecognized waveform style: {Style}", style);
                    return null;
            }
        }

        /// <summary>
        /// Helper function to try finding a waveform detail of a particular style.
        /// </summary>
        /// <param name="archive">the metadata archive in which to look</param>
        /// <param name="track">the track whose waveform is being sought</param>
        /// <param name="style">the type of detail desired</param>
        /// <returns>the waveform detail, if found, or <c>null</c></returns>
        private WaveformDetail? FindDetail(RekordboxUsbArchive archive, DataReference track, WaveformStyle style)
        {
            var fileExtension = style == WaveformStyle.ThreeBand ? ".2EX" : ".EXT";
            try
            {
                var file = owner.FindTrackAnalysis(archive.UsbSlot, track, archive.Database,
                    archive.Connection, archive.FileSystem, fileExtension);
                if (file != null)
                {
                    try
                    {
                        return new WaveformDetail(track, file, style);
                    }
                    finally
                    {
                        file.M_Io.Dispose();
                    }
                }
            }
            catch (InvalidOperationException)
            {
                Logger.LogInformation("No {Style} waveform detail found.", style);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Problem fetching {Style} waveform detail for track {Track} from archive {Archive}",
                    style, track, archive);
            }
            return null;
        }

        /// <inheritdoc/>
        public WaveformDetail? GetWaveformDetail(MediaDetails sourceMedia, DataReference track)
        {
            var archive = owner.FindArchive(track.Player);
            // Return early if no archive exists for this player/slot, or it is not a rekordbox track.
            if (archive == null || track.TrackType != CdjStatus.TrackType.Rekordbox)
            {
                return null;
            }

            var style = WaveformFinder.Instance.PreferredStyle;
            var result = FindDetail(archive, track, style);  // Try preferred style first.
            if (result != null)
            {
                return result;
            }

            // Preferred style not found, attempt fallbacks based on the preferred style.
            switch (style)
            {
                case WaveformStyle.Blue:
                    // Note: BLUE style does not have a distinct detailed waveform, so fallbacks are
                    // the only option.
                    return FindDetail(archive, track, WaveformStyle.Rgb) ??
                        FindDetail(archive, track, WaveformStyle.ThreeBand);
                case WaveformStyle.Rgb:
                    return FindDetail(archive, track, WaveformStyle.ThreeBand);
                case WaveformStyle.ThreeBand:
                    return FindDetail(archive, track, WaveformStyle.Rgb);
                default:
                    Logger.LogError("Unrecognized waveform style: {Style}", style);
                    return null;
            }
        }

        /// <inheritdoc/>
        public RekordboxAnlz.TaggedSection? GetAnalysisSection(MediaDetails sourceMedia, DataReference track,
            string fileExtension, string typeTag)
        {
            var archive = owner.FindArchive(track.Player);
            if (archive != null && track.TrackType == CdjStatus.TrackType.Rekordbox)
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

                    var file = owner.FindTrackAnalysis(archive.UsbSlot, track, archive.Database,
                        archive.Connection, archive.FileSystem, fileExtension);  // Open the desired file to scan.
                    if (file != null)
                    {
                        try
                        {
                            // Scan for the requested tag type.
                            foreach (var section in file.Sections)
                            {
                                if ((int)section.Fourcc == fourcc)
                                {
                                    return section;
                                }
                            }
                        }
                        finally
                        {
                            file.M_Io.Dispose();
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Problem fetching analysis file {Extension} section {TypeTag} for track {Track} from archive {Archive}",
                        fileExtension, typeTag, track, archive);
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Given a PSSI message and the ID sent from the Opus, return the DeviceSQL rekordbox ID and
    /// USB slot number that matches the PSSI. When there are multiple matches, prefers the match
    /// with the same ID sent from the Opus, otherwise returns the first match found.
    /// Experimental, like all Opus Quad support.
    /// </summary>
    /// <param name="pssi">the PSSI message (whose first twelve bytes are a header preceding the
    /// song structure data)</param>
    /// <param name="idSentFromOpus">the rekordbox ID reported by the Opus</param>
    /// <returns>the DeviceSQL rekordbox ID and USB slot number that matches the PSSI, or
    /// <c>null</c> if no match is found</returns>
    [Experimental("BLNET001")]
    public DeviceSqlRekordboxIdAndSlot? GetDeviceSqlRekordboxIdAndSlotNumberFromPssi(byte[] pssi, int idSentFromOpus)
    {
        ArgumentNullException.ThrowIfNull(pssi);
        if (pssi.Length < 12)
        {
            Logger.LogWarning("PSSI message is too short to contain song structure data");
            return null;
        }

        // From observation, the actual part we need to check from the body sent from the Opus
        // starts at index 12 (that's where the SONG_STRUCTURE starts).
        var pssiBodySha1 = ComputeSha1(pssi.AsSpan(12));

        if (!_pssiToDeviceSqlRekordboxIds.TryGetValue(pssiBodySha1, out var matches) || matches.IsEmpty)
        {
            Logger.LogWarning("No PSSI matches found");
            return null;
        }

        var allMatches = matches.Keys.ToArray();
        if (allMatches.Length == 1)
        {
            return allMatches[0];
        }

        // Multiple matches found: prefer the match with the same ID sent from the Opus.
        foreach (var match in allMatches)
        {
            if (match.RekordboxId == idSentFromOpus)
            {
                Logger.LogInformation("Multiple PSSI matches found, preferring ID sent from Opus: {Id}", idSentFromOpus);
                return match;
            }
        }

        // If the Opus sent back an ID that we don't have a match for, we'll just return the first
        // match we found.
        var firstMatch = allMatches[0];
        Logger.LogInformation("Multiple PSSI matches found, but none match ID sent from Opus: {Id}. Returning the first match: {Match}",
            idSentFromOpus, firstMatch);
        return firstMatch;
    }

    /// <summary>
    /// Method that will use PSSI plus a rekordbox ID to confirm that an archive matches the song:
    /// looks up the PSSI in the chosen archive for the specific rekordbox ID and then sees if it
    /// matches what the Opus is sending us.
    /// </summary>
    /// <param name="dataRef">the track/slot data</param>
    /// <param name="pssiFromOpus">the PSSI sent from the Opus</param>
    /// <param name="archive">the archive we are comparing the PSSI and rekordbox ID against</param>
    /// <returns><c>true</c> if matched</returns>
    private bool TrackMatchesArchive(DataReference dataRef, ReadOnlySpan<byte> pssiFromOpus, RekordboxUsbArchive archive)
    {
        var anlz = FindTrackAnalysis(archive.UsbSlot, dataRef, archive.Database, archive.Connection,
            archive.FileSystem, ".EXT");
        if (anlz != null)
        {
            try
            {
                foreach (var taggedSection in anlz.Sections)
                {
                    if (taggedSection.Fourcc == RekordboxAnlz.SectionTags.SongStructure)
                    {
                        return Util.IndexOfBytes(pssiFromOpus, taggedSection.M_RawBody) > -1;
                    }
                }
            }
            finally
            {
                anlz.M_Io.Dispose();
            }
        }
        return false;
    }

    /// <summary>
    /// Find which USB slot contains a track that matches the specified ID, given the latest song
    /// structure report we have from that player. Experimental, like all Opus Quad support.
    /// </summary>
    /// <param name="rekordboxId">the reported track ID</param>
    /// <param name="player">the device playing the specified track</param>
    /// <param name="songStructureBytes">the PSSI data we have received for the player</param>
    /// <returns>the USB slot number in which a match was found, or zero if none was found</returns>
    [Experimental("BLNET001")]
    public int FindMatchingUsbSlotForTrack(int rekordboxId, int player, ReadOnlySpan<byte> songStructureBytes)
    {
        var slotRef = SlotReference.GetSlotReference(player, CdjStatus.TrackSourceSlot.UsbSlot);
        var dataRef = new DataReference(slotRef, rekordboxId, CdjStatus.TrackType.Rekordbox);

        foreach (var archive in _usbArchiveMap.Values)
        {
            if (TrackMatchesArchive(dataRef, songStructureBytes, archive))
            {
                return archive.UsbSlot;
            }
        }
        return 0;
    }

    /// <summary>
    /// Start proxying track metadata from mounted archives for the Opus Quad decks; should only
    /// be called by <see cref="VirtualCdj"/> and can only be called when that is in Opus Quad
    /// compatibility mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the <see cref="VirtualCdj"/> is not in Opus
    /// Quad compatibility mode</exception>
    public void Start()
    {
        lock (_lock)
        {
            if (!IsRunning)
            {
                if (!VirtualCdj.Instance.InOpusQuadCompatibilityMode)
                {
                    throw new InvalidOperationException("Can only be started when VirtualCdj is in Opus Quad compatibility mode.");
                }
                _running = true;
                MetadataFinder.Instance.AddMetadataProvider(MetadataProvider);
                DeliverLifecycleAnnouncement(Logger, true);
            }
        }
    }

    /// <summary>
    /// Stop proxying track metadata for Opus Quad decks.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                _running = false;
                MetadataFinder.Instance.RemoveMetadataProvider(MetadataProvider);
                DeliverLifecycleAnnouncement(Logger, false);
            }
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static OpusProvider Instance { get; } = new();

    /// <summary>
    /// The folder into which database exports and track analysis files will be extracted.
    /// Experimental, like all Opus Quad support.
    /// </summary>
    [Experimental("BLNET001")]
    public string ExtractDirectory { get; }

    /// <summary>
    /// Prevent direct instantiation, create the extraction directory, and set up the media
    /// details queues.
    /// </summary>
    private OpusProvider()
    {
        ExtractDirectory = CrateDigger.CreateDownloadDirectory();
        MetadataProvider = new OpusMetadataProvider(this);
        // Create MediaDetails queues, one per USB slot 1-3.
        for (var i = 1; i <= 3; i++)
        {
            _archiveAttachQueueMap[i] = new ConcurrentQueue<MediaDetails>();
        }
    }

    /// <summary>
    /// Test seam: stops the service and discards all attached archives, PSSI mappings, and queued
    /// media details (closing databases, connections, and archives, and deleting extracted
    /// files), so tests remain independent of each other.
    /// </summary>
    internal void ResetForTests()
    {
        lock (_lock)
        {
            Stop();
            foreach (var entry in _usbArchiveMap)
            {
                var archive = entry.Value;
                try
                {
                    archive.Database?.Dispose();
                    archive.Connection?.Dispose();
                    archive.FileSystem.Dispose();
                }
                catch (Exception e)
                {
                    Logger.LogDebug(e, "Problem closing archive for slot {Slot} during test reset", entry.Key);
                }
                DeleteExtractedFilesForSlot(entry.Key);
            }
            _usbArchiveMap.Clear();
            _pssiToDeviceSqlRekordboxIds.Clear();
            foreach (var queue in _archiveAttachQueueMap.Values)
            {
                queue.Clear();
            }
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("OpusProvider[running: ").Append(IsRunning);
        if (IsRunning)
        {
            foreach (var archive in _usbArchiveMap.Values)
            {
                sb.Append(", USB ").Append(archive.UsbSlot).Append(" media: ").Append(archive.ArchivePath);
            }
        }
        return sb.Append(']').ToString();
    }
}
