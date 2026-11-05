using System.Text;
using BeatLink.CrateDigger.Pdb;

namespace BeatLink.Tests.CrateDigger;

/// <summary>
/// Builds a minimal but structurally faithful <c>export.pdb</c> file: a file header listing
/// one table for each of the twelve types <see cref="BeatLink.CrateDigger.Database"/> indexes,
/// and one data page per table, with the row index built backwards from the end of each page as
/// documented in <c>rekordbox_pdb.ksy</c>. Shared by the <c>Database</c> parser tests and the
/// <c>CrateDigger</c> end-to-end tests.
/// </summary>
internal static class PdbBuilder
{
    private const int PageSize = 4096;
    private const int PageHeaderSize = 40;  // The heap begins here within each page.

    /// <summary>The table types, in the order their single pages appear in the file.</summary>
    private static readonly RekordboxPdb.PageType[] TableTypes =
    [
        RekordboxPdb.PageType.Tracks,
        RekordboxPdb.PageType.Genres,
        RekordboxPdb.PageType.Artists,
        RekordboxPdb.PageType.Albums,
        RekordboxPdb.PageType.Labels,
        RekordboxPdb.PageType.Keys,
        RekordboxPdb.PageType.Colors,
        RekordboxPdb.PageType.PlaylistTree,
        RekordboxPdb.PageType.PlaylistEntries,
        RekordboxPdb.PageType.HistoryPlaylists,
        RekordboxPdb.PageType.HistoryEntries,
        RekordboxPdb.PageType.Artwork,
    ];

    /// <summary>
    /// Builds the complete synthetic export file.
    /// </summary>
    /// <param name="analyzePath">the analysis file path to store in the track rows, allowing the
    /// CrateDigger tests to point the tracks at ANLZ files served over the fake NFS tree</param>
    public static byte[] BuildExportFile(string analyzePath = "")
    {
        var file = new byte[PageSize * (TableTypes.Length + 1)];

        // File header: unnamed, len_page, num_tables, next_unused_page, unnamed, sequence, gap.
        WriteU4(file, 4, PageSize);
        WriteU4(file, 8, (uint)TableTypes.Length);
        WriteU4(file, 12, (uint)(TableTypes.Length + 1));
        WriteU4(file, 20, 1);
        // Table entries: type, empty_candidate, first_page, last_page (16 bytes each).
        for (var i = 0; i < TableTypes.Length; i++)
        {
            var offset = 28 + i * 16;
            WriteU4(file, offset, (uint)TableTypes[i]);
            WriteU4(file, offset + 8, (uint)(i + 1));   // first_page
            WriteU4(file, offset + 12, (uint)(i + 1));  // last_page
        }

        for (var i = 0; i < TableTypes.Length; i++)
        {
            var page = BuildPage((uint)(i + 1), TableTypes[i], RowsFor(TableTypes[i], analyzePath),
                PresentFlagsFor(TableTypes[i], analyzePath));
            page.CopyTo(file, (i + 1) * PageSize);
        }
        return file;
    }

    /// <summary>Returns the raw row payloads that belong on the single page of a table.</summary>
    private static List<byte[]> RowsFor(RekordboxPdb.PageType type, string analyzePath) => type switch
    {
        RekordboxPdb.PageType.Tracks =>
        [
            TrackRow(id: 1, title: "alpha song", artistId: 7, albumId: 5, genreId: 3, keyId: 4,
                labelId: 6, colorId: 1, artworkId: 8, rating: 4, duration: 300, tempo: 12800,
                year: 1997, bitrate: 320, comment: "Nice track", dateAdded: "2024-01-15",
                analyzePath: analyzePath),
            TrackRow(id: 3, title: "Deleted Song", artistId: 0, albumId: 0, genreId: 0, keyId: 0,
                labelId: 0, colorId: 0, artworkId: 0, rating: 0, duration: 100, tempo: 12000,
                year: 2000, bitrate: 128, comment: "", dateAdded: "", analyzePath: analyzePath),
            TrackRow(id: 2, title: "Beta Song", artistId: 9, albumId: 0, genreId: 3, keyId: 0,
                labelId: 0, colorId: 0, artworkId: 0, rating: 0, duration: 200, tempo: 17000,
                year: 2020, bitrate: 0, comment: "", dateAdded: "", analyzePath: analyzePath),
        ],
        RekordboxPdb.PageType.Genres => [IdAndName(3, ShortAsciiString("IDM"))],
        RekordboxPdb.PageType.Artists =>
        [
            ArtistRow(7, LongUtf16String("Björk")),
            ArtistRow(9, ShortAsciiString("Aphex Twin")),
        ],
        RekordboxPdb.PageType.Albums => [AlbumRow(5, 7, ShortAsciiString("Homogenic"))],
        RekordboxPdb.PageType.Labels => [IdAndName(6, LongAsciiString("Warp Records"))],
        RekordboxPdb.PageType.Keys => [KeyRow(4, ShortAsciiString("Am"))],
        RekordboxPdb.PageType.Colors => [ColorRow(1, ShortAsciiString("Pink"))],
        RekordboxPdb.PageType.PlaylistTree => [PlaylistTreeRow(10, 0, 0, false, "My List")],
        RekordboxPdb.PageType.PlaylistEntries =>
        [
            PlaylistEntryRow(entryIndex: 1, trackId: 1, playlistId: 10),
            PlaylistEntryRow(entryIndex: 0, trackId: 2, playlistId: 10),
        ],
        RekordboxPdb.PageType.HistoryPlaylists => [IdAndName(11, ShortAsciiString("HISTORY 001"))],
        RekordboxPdb.PageType.HistoryEntries => [HistoryEntryRow(trackId: 1, playlistId: 11, entryIndex: 0)],
        RekordboxPdb.PageType.Artwork => [IdAndName(8, ShortAsciiString("/PIONEER/ARTWORK/00001/a.jpg"))],
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>
    /// Returns the row-present bitmask for a table's page. The tracks page exercises the
    /// deleted-row logic: its second row offset (track ID 3) is marked not present.
    /// </summary>
    private static ushort PresentFlagsFor(RekordboxPdb.PageType type, string analyzePath) =>
        type == RekordboxPdb.PageType.Tracks
            ? (ushort)0b101
            : (ushort)((1 << RowsFor(type, analyzePath).Count) - 1);

    /// <summary>
    /// Assembles one table page: 40-byte header, row heap, and the row index growing
    /// backwards from the end of the page (offsets at base - 6 - 2*row, present flags at
    /// base - 4, where base for the first row group is the page size).
    /// </summary>
    private static byte[] BuildPage(uint index, RekordboxPdb.PageType type, List<byte[]> rows, ushort presentFlags)
    {
        if (rows.Count > 16)
        {
            throw new ArgumentException("This builder only supports a single row group (16 rows).");
        }
        var page = new byte[PageSize];
        WriteU4(page, 4, index);
        WriteU4(page, 8, (uint)type);
        WriteU4(page, 16, 1);  // Sequence.

        // num_row_offsets (13 bits) and num_rows (11 bits), packed little-endian.
        var presentCount = System.Numerics.BitOperations.PopCount(presentFlags);
        var bits = (uint)(rows.Count | (presentCount << 13));
        page[24] = (byte)bits;
        page[25] = (byte)(bits >> 8);
        page[26] = (byte)(bits >> 16);
        page[27] = 0x24;  // page_flags: an ordinary data page.

        // Write the rows into the heap, recording their offsets in the row index.
        ushort heapOffset = 0;
        for (var row = 0; row < rows.Count; row++)
        {
            rows[row].CopyTo(page, PageHeaderSize + heapOffset);
            WriteU2(page, PageSize - 6 - 2 * row, heapOffset);
            heapOffset += (ushort)rows[row].Length;
        }
        WriteU2(page, PageSize - 4, presentFlags);
        WriteU2(page, 28, (ushort)(PageSize - PageHeaderSize - heapOffset));  // free_size
        WriteU2(page, 30, heapOffset);  // used_size
        return page;
    }

    /// <summary>Encodes a short-ASCII DeviceSQL string (mangled odd length-and-kind byte).</summary>
    public static byte[] ShortAsciiString(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var result = new byte[1 + bytes.Length];
        result[0] = (byte)(((bytes.Length + 1) << 1) | 1);
        bytes.CopyTo(result, 1);
        return result;
    }

    /// <summary>Encodes a long-ASCII DeviceSQL string (kind byte 0x40).</summary>
    public static byte[] LongAsciiString(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var result = new byte[4 + bytes.Length];
        result[0] = 0x40;
        WriteU2(result, 1, (ushort)(bytes.Length + 4));
        bytes.CopyTo(result, 4);
        return result;
    }

    /// <summary>Encodes a long UTF-16LE DeviceSQL string (kind byte 0x90).</summary>
    public static byte[] LongUtf16String(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text);
        var result = new byte[4 + bytes.Length];
        result[0] = 0x90;
        WriteU2(result, 1, (ushort)(bytes.Length + 4));
        bytes.CopyTo(result, 4);
        return result;
    }

    /// <summary>Builds a row consisting of just a four-byte ID and an inline name/path string.</summary>
    private static byte[] IdAndName(uint id, byte[] name)
    {
        var row = new byte[4 + name.Length];
        WriteU4(row, 0, id);
        name.CopyTo(row, 4);
        return row;
    }

    private static byte[] KeyRow(uint id, byte[] name)
    {
        var row = new byte[8 + name.Length];
        WriteU4(row, 0, id);
        WriteU4(row, 4, id);  // id2 appears to be a second copy of the ID.
        name.CopyTo(row, 8);
        return row;
    }

    private static byte[] ColorRow(ushort id, byte[] name)
    {
        var row = new byte[8 + name.Length];
        WriteU2(row, 5, id);
        name.CopyTo(row, 8);
        return row;
    }

    private static byte[] ArtistRow(uint id, byte[] name)
    {
        var row = new byte[10 + name.Length];
        WriteU2(row, 0, 0x60);  // subtype: nearby name offset.
        WriteU4(row, 4, id);
        row[8] = 0x03;
        row[9] = 10;  // ofs_name_near: the name immediately follows the fixed fields.
        name.CopyTo(row, 10);
        return row;
    }

    private static byte[] AlbumRow(uint id, uint artistId, byte[] name)
    {
        var row = new byte[22 + name.Length];
        WriteU2(row, 0, 0x80);  // subtype: nearby name offset.
        WriteU4(row, 8, artistId);
        WriteU4(row, 12, id);
        row[20] = 0x03;
        row[21] = 22;  // ofs_name_near.
        name.CopyTo(row, 22);
        return row;
    }

    private static byte[] PlaylistTreeRow(uint id, uint parentId, uint sortOrder, bool isFolder, string name)
    {
        var nameBytes = ShortAsciiString(name);
        var row = new byte[20 + nameBytes.Length];
        WriteU4(row, 0, parentId);
        WriteU4(row, 8, sortOrder);
        WriteU4(row, 12, id);
        WriteU4(row, 16, isFolder ? 1u : 0u);
        nameBytes.CopyTo(row, 20);
        return row;
    }

    private static byte[] PlaylistEntryRow(uint entryIndex, uint trackId, uint playlistId)
    {
        var row = new byte[12];
        WriteU4(row, 0, entryIndex);
        WriteU4(row, 4, trackId);
        WriteU4(row, 8, playlistId);
        return row;
    }

    private static byte[] HistoryEntryRow(uint trackId, uint playlistId, uint entryIndex)
    {
        var row = new byte[12];
        WriteU4(row, 0, trackId);
        WriteU4(row, 4, playlistId);
        WriteU4(row, 8, entryIndex);
        return row;
    }

    /// <summary>
    /// Builds a track row: the 94 bytes of fixed fields, the 21 sixteen-bit string offsets,
    /// and then the variable-length strings themselves. Strings that are not interesting for
    /// the tests all share a single empty-string encoding.
    /// </summary>
    private static byte[] TrackRow(uint id, string title, uint artistId, uint albumId,
        uint genreId, uint keyId, uint labelId, byte colorId, uint artworkId, byte rating,
        ushort duration, uint tempo, ushort year, uint bitrate, string comment, string dateAdded,
        string analyzePath)
    {
        const int fixedSize = 94 + 21 * 2;  // Fixed fields plus the string offset table.
        var offsets = new ushort[21];

        // Everything defaults to a shared empty string appended first.
        var stringData = new List<byte>();
        var emptyOffset = (ushort)fixedSize;
        stringData.AddRange(ShortAsciiString(""));
        for (var i = 0; i < 21; i++)
        {
            offsets[i] = emptyOffset;
        }

        ushort AppendString(byte[] encoded)
        {
            var offset = (ushort)(fixedSize + stringData.Count);
            stringData.AddRange(encoded);
            return offset;
        }

        offsets[17] = AppendString(ShortAsciiString(title));       // title
        offsets[16] = AppendString(ShortAsciiString(comment));     // comment
        offsets[10] = AppendString(ShortAsciiString(dateAdded));   // date_added
        if (analyzePath.Length > 0)
        {
            offsets[14] = AppendString(ShortAsciiString(analyzePath));  // analyze_path
        }

        var row = new byte[fixedSize + stringData.Count];
        WriteU2(row, 0, 0x24);        // subtype
        WriteU4(row, 8, 44100);       // sample_rate
        WriteU4(row, 16, 1_000_000);  // file_size
        WriteU4(row, 28, artworkId);
        WriteU4(row, 32, keyId);
        WriteU4(row, 40, labelId);
        WriteU4(row, 48, bitrate);
        WriteU4(row, 52, 1);          // track_number
        WriteU4(row, 56, tempo);
        WriteU4(row, 60, genreId);
        WriteU4(row, 64, albumId);
        WriteU4(row, 68, artistId);
        WriteU4(row, 72, id);
        WriteU2(row, 80, year);
        WriteU2(row, 82, 16);         // sample_depth
        WriteU2(row, 84, duration);
        row[88] = colorId;
        row[89] = rating;
        for (var i = 0; i < 21; i++)
        {
            WriteU2(row, 94 + i * 2, offsets[i]);
        }
        stringData.CopyTo(row, fixedSize);
        return row;
    }

    private static void WriteU2(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU4(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
