using System.Text;
using BeatLink.Data;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Exercises construction of <see cref="CueList"/> from synthetic dbserver cue list responses,
/// along with the rekordbox color table and entry search helpers.
/// </summary>
public class CueListTests
{
    private static void WriteLittleEndian(byte[] buffer, int offset, long value, int length)
    {
        for (var i = 0; i < length; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * i));
        }
    }

    /// <summary>
    /// Fills in one 36-byte entry of an original nexus style cue list blob.
    /// </summary>
    private static void WriteNexusEntry(byte[] blob, int index, bool isLoop, byte cueFlag,
        byte hotCueNumber, long position, long endPosition)
    {
        var offset = index * 36;
        blob[offset] = (byte)(isLoop ? 1 : 0);
        blob[offset + 1] = cueFlag;
        blob[offset + 2] = hotCueNumber;
        WriteLittleEndian(blob, offset + 12, position, 4);
        WriteLittleEndian(blob, offset + 16, endPosition, 4);
    }

    /// <summary>
    /// Builds a synthetic CUE_LIST response holding the supplied cue entry blob.
    /// </summary>
    private static Message MakeNexusMessage(byte[] blob, int hotCueCount, int memoryPointCount) =>
        new(7, Message.KnownType.CueList,
            new NumberField(Message.KnownType.CueListReq.ProtocolValue),  // Request type.
            new NumberField(0),                                           // Unknown.
            new NumberField(blob.Length),                                 // Blob 1 length.
            new BinaryField(blob),                                        // Blob 1.
            new NumberField(0x24),                                        // Unknown (0x24).
            new NumberField(hotCueCount),                                 // Hot cue count.
            new NumberField(memoryPointCount));                           // Memory point count.

    [Fact]
    public void ParsesNexusCueListMessage()
    {
        var blob = new byte[5 * 36];
        WriteNexusEntry(blob, 0, isLoop: false, cueFlag: 0, hotCueNumber: 1, position: 150, endPosition: 0);
        WriteNexusEntry(blob, 1, isLoop: true, cueFlag: 1, hotCueNumber: 0, position: 450, endPosition: 600);
        WriteNexusEntry(blob, 2, isLoop: false, cueFlag: 1, hotCueNumber: 0, position: 150, endPosition: 0);
        WriteNexusEntry(blob, 3, isLoop: false, cueFlag: 1, hotCueNumber: 0, position: 300, endPosition: 0);
        // Entry 4 is left empty (all zero) and must be skipped.

        var message = MakeNexusMessage(blob, 1, 3);
        var cueList = new CueList(message);

        Assert.Equal(4, cueList.Entries.Count);

        // Sorted by position, with the hot cue after the memory point at the same position.
        Assert.Equal(150, cueList.Entries[0].CuePosition);
        Assert.Equal(0, cueList.Entries[0].HotCueNumber);
        Assert.Equal(150, cueList.Entries[1].CuePosition);
        Assert.Equal(1, cueList.Entries[1].HotCueNumber);
        Assert.Equal(300, cueList.Entries[2].CuePosition);
        Assert.Equal(450, cueList.Entries[3].CuePosition);

        // Half-frame positions are converted to milliseconds (half-frames are 1/150 s).
        Assert.Equal(1000, cueList.Entries[0].CueTime);
        Assert.Equal(1000, cueList.Entries[1].CueTime);
        Assert.Equal(2000, cueList.Entries[2].CueTime);
        Assert.Equal(3000, cueList.Entries[3].CueTime);

        // Loop flags and loop end conversion.
        Assert.False(cueList.Entries[0].IsLoop);
        Assert.False(cueList.Entries[1].IsLoop);
        Assert.False(cueList.Entries[2].IsLoop);
        Assert.True(cueList.Entries[3].IsLoop);
        Assert.Equal(600, cueList.Entries[3].LoopPosition);
        Assert.Equal(4000, cueList.Entries[3].LoopTime);

        // The counts are reported from the raw message arguments.
        Assert.Equal(1, cueList.HotCueCount);
        Assert.Equal(3, cueList.MemoryPointCount);
        Assert.Same(message, cueList.RawMessage);
    }

    [Fact]
    public void FindsEntriesBeforeAndAfterTimes()
    {
        var blob = new byte[3 * 36];
        WriteNexusEntry(blob, 0, isLoop: false, cueFlag: 1, hotCueNumber: 0, position: 150, endPosition: 0);
        WriteNexusEntry(blob, 1, isLoop: false, cueFlag: 1, hotCueNumber: 0, position: 300, endPosition: 0);
        WriteNexusEntry(blob, 2, isLoop: true, cueFlag: 1, hotCueNumber: 0, position: 450, endPosition: 600);
        var cueList = new CueList(MakeNexusMessage(blob, 0, 3));

        Assert.Null(cueList.FindEntryBefore(500));               // Rounds to half-frame 75, before all cues.
        Assert.Equal(150, cueList.FindEntryBefore(1000)!.CuePosition);  // Exact match.
        Assert.Equal(150, cueList.FindEntryBefore(1500)!.CuePosition);
        Assert.Equal(450, cueList.FindEntryBefore(9999)!.CuePosition);

        Assert.Equal(150, cueList.FindEntryAfter(500)!.CuePosition);
        Assert.Equal(300, cueList.FindEntryAfter(2000)!.CuePosition);   // Exact match.
        Assert.Equal(450, cueList.FindEntryAfter(2500)!.CuePosition);
        Assert.Null(cueList.FindEntryAfter(4000));               // After the last cue.
    }

    /// <summary>
    /// Builds one extended nxs2 style cue entry, returning its bytes.
    /// </summary>
    private static byte[] MakeNxs2Entry(byte hotCueNumber, byte cueFlag, long timeMs, long loopTimeMs,
        byte colorId, string comment, byte colorCode, byte red, byte green, byte blue)
    {
        var commentBytes = Encoding.Unicode.GetBytes(comment);
        var commentSize = commentBytes.Length > 0 ? commentBytes.Length + 2 : 0;
        var size = commentSize + 0x52;  // Large enough to hold the trailing color bytes.
        var entry = new byte[size];
        WriteLittleEndian(entry, 0, size, 4);
        entry[4] = hotCueNumber;
        entry[6] = cueFlag;
        WriteLittleEndian(entry, 12, timeMs, 4);
        WriteLittleEndian(entry, 16, loopTimeMs, 4);
        entry[0x22] = colorId;
        WriteLittleEndian(entry, 0x48, commentSize, 2);
        Array.Copy(commentBytes, 0, entry, 0x4a, commentBytes.Length);
        entry[commentSize + 0x4e] = colorCode;
        entry[commentSize + 0x4f] = red;
        entry[commentSize + 0x50] = green;
        entry[commentSize + 0x51] = blue;
        return entry;
    }

    [Fact]
    public void ParsesExtendedNxs2CueListMessage()
    {
        // A memory point at 2 seconds with a comment and a color table id.
        var memory = MakeNxs2Entry(hotCueNumber: 0, cueFlag: 1, timeMs: 2000, loopTimeMs: 0,
            colorId: 3, comment: "Hi", colorCode: 0, red: 0, green: 0, blue: 0);
        // A hot cue loop from 1 to 3 seconds with rekordbox color code 1 and matching embedded color.
        var hotLoop = MakeNxs2Entry(hotCueNumber: 2, cueFlag: 2, timeMs: 1000, loopTimeMs: 3000,
            colorId: 0, comment: "", colorCode: 0x01, red: 0x30, green: 0x5a, blue: 0xff);

        var blob = new byte[memory.Length + hotLoop.Length];
        Array.Copy(memory, 0, blob, 0, memory.Length);
        Array.Copy(hotLoop, 0, blob, memory.Length, hotLoop.Length);

        var message = new Message(9, Message.KnownType.CueListExt,
            new NumberField(Message.KnownType.CueListExtReq.ProtocolValue),  // Request type.
            new NumberField(0),                                              // Unknown.
            new NumberField(blob.Length),                                    // Blob length.
            new BinaryField(blob),                                           // Blob.
            new NumberField(2));                                             // Entry count.

        var cueList = new CueList(message);
        Assert.Equal(2, cueList.Entries.Count);

        var loop = cueList.Entries[0];
        Assert.Equal(2, loop.HotCueNumber);
        Assert.True(loop.IsLoop);
        Assert.Equal(150, loop.CuePosition);   // 1000 ms is half-frame 150.
        Assert.Equal(1000, loop.CueTime);
        Assert.Equal(450, loop.LoopPosition);  // 3000 ms is half-frame 450.
        Assert.Equal(3000, loop.LoopTime);
        Assert.Equal("", loop.Comment);
        Assert.Equal(ArgbColor.FromRgb(0x30, 0x5a, 0xff), loop.RekordboxColor);
        Assert.Equal(ArgbColor.FromRgb(0x30, 0x5a, 0xff), loop.EmbeddedColor);
        Assert.Equal(ArgbColor.FromRgb(0x30, 0x5a, 0xff), loop.Color);

        var point = cueList.Entries[1];
        Assert.Equal(0, point.HotCueNumber);
        Assert.False(point.IsLoop);
        Assert.Equal(300, point.CuePosition);
        Assert.Equal(2000, point.CueTime);
        Assert.Equal("Hi", point.Comment);
        Assert.Equal(3, point.ColorId);
        Assert.Equal(ColorItem.ColorForId(3), point.Color);
    }

    [Fact]
    public void NexusColorsFollowEntryKind()
    {
        var memory = new CueList.Entry(300, "", 0);
        Assert.Equal(ArgbColor.FromRgb(255, 0, 0), memory.Color);      // Memory points show red.
        var loop = new CueList.Entry(300, 450, "", 0);
        Assert.Equal(ArgbColor.FromRgb(255, 200, 0), loop.Color);      // Loops show orange.
        var hotCue = new CueList.Entry(3, 300, "", null, null);
        Assert.Equal(ArgbColor.FromRgb(0, 255, 0), hotCue.Color);      // Hot cues show green.
    }

    [Theory]
    [InlineData(0x01, 0x30, 0x5a, 0xff)]
    [InlineData(0x09, 0x00, 0xe0, 0xff)]
    [InlineData(0x16, 0x28, 0xe2, 0x14)]
    [InlineData(0x21, 0xe1, 0xaa, 0x00)]
    [InlineData(0x2e, 0xf5, 0x1e, 0x8c)]
    [InlineData(0x3e, 0x64, 0x73, 0xff)]
    public void RekordboxColorTableContainsKnownColors(int code, int red, int green, int blue)
    {
        Assert.Equal(ArgbColor.FromRgb(red, green, blue), CueList.FindRekordboxColor(code));
    }

    [Fact]
    public void UnknownRekordboxColorCodesReturnNull()
    {
        Assert.Null(CueList.FindRekordboxColor(0));
        Assert.Null(CueList.FindRekordboxColor(0x3f));
        Assert.Null(CueList.FindRekordboxColor(255));
    }

    [Fact]
    public void EntryEqualityIgnoresColors()
    {
        var a = new CueList.Entry(300, "Comment", 3);
        var b = new CueList.Entry(300, "Comment", 5);
        Assert.Equal(a, b);  // Equality considers only position, kind, and times, like the Java library.
        Assert.NotEqual(a, new CueList.Entry(301, "Comment", 3));
    }

    [Fact]
    public void HotCueEntriesRequireNonZeroNumbers()
    {
        Assert.Throws<ArgumentException>(() => new CueList.Entry(0, 300, "", null, null));
        Assert.Throws<ArgumentException>(() => new CueList.Entry(0, 300, 450, "", null, null));
    }

    [Fact]
    public void DescriptionsSummarizeEntries()
    {
        Assert.Equal("Memory", new CueList.Entry(300, "", 0).Description);
        Assert.Equal("Loop: intro", new CueList.Entry(300, 450, "intro", 0).Description);
        Assert.Equal("Hot Cue A", new CueList.Entry(1, 300, "", null, null).Description);
        Assert.Equal("Hot Loop B: drop", new CueList.Entry(2, 300, 450, "drop", null, null).Description);
    }
}
