using BeatLink.Data;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Exercises construction of <see cref="BeatGrid"/> from synthetic dbserver responses and raw
/// buffers, along with the beat lookup math.
/// </summary>
public class BeatGridTests
{
    private static readonly DataReference Reference =
        new(2, CdjStatus.TrackSourceSlot.UsbSlot, 1234, CdjStatus.TrackType.Rekordbox);

    /// <summary>
    /// Builds the raw byte representation of a beat grid: a 20-byte header followed by 16 bytes
    /// per beat, holding little-endian beat-within-bar (2 bytes), BPM times 100 (2 bytes), and
    /// time within track in milliseconds (4 bytes).
    /// </summary>
    private static byte[] BuildGridBytes(int[] beatsWithinBar, int[] bpms, long[] times)
    {
        var bytes = new byte[20 + beatsWithinBar.Length * 16];
        for (var i = 0; i < beatsWithinBar.Length; i++)
        {
            var baseOffset = 20 + i * 16;
            bytes[baseOffset] = (byte)beatsWithinBar[i];
            bytes[baseOffset + 1] = (byte)(beatsWithinBar[i] >> 8);
            bytes[baseOffset + 2] = (byte)bpms[i];
            bytes[baseOffset + 3] = (byte)(bpms[i] >> 8);
            for (var b = 0; b < 4; b++)
            {
                bytes[baseOffset + 4 + b] = (byte)(times[i] >> (8 * b));
            }
        }
        return bytes;
    }

    private static readonly int[] BeatsWithinBar = [1, 2, 3, 4, 1, 2];
    private static readonly int[] Bpms = [12000, 12000, 12050, 12050, 12100, 12100];
    private static readonly long[] Times = [500L, 1000L, 1500L, 2000L, 2500L, 3000L];

    private static BeatGrid BuildFromMessage()
    {
        var gridBytes = BuildGridBytes(BeatsWithinBar, Bpms, Times);
        var message = new Message(11, Message.KnownType.BeatGrid,
            new NumberField(Message.KnownType.BeatGridReq.ProtocolValue),  // Request type.
            new NumberField(0),                                            // Unknown.
            new NumberField(gridBytes.Length),                             // Beat grid length.
            new BinaryField(gridBytes),                                    // Beat grid bytes.
            new NumberField(0));                                           // Unknown.
        return new BeatGrid(Reference, message);
    }

    [Fact]
    public void ParsesGridFromDbserverMessage()
    {
        var grid = BuildFromMessage();
        Assert.Equal(6, grid.BeatCount);
        Assert.Same(Reference, grid.DataReference);
        for (var beat = 1; beat <= 6; beat++)
        {
            Assert.Equal(BeatsWithinBar[beat - 1], grid.GetBeatWithinBar(beat));
            Assert.Equal(Bpms[beat - 1], grid.GetBpm(beat));
            Assert.Equal(Times[beat - 1], grid.GetTimeWithinTrack(beat));
        }
    }

    [Fact]
    public void RawBufferAndMessageConstructorsAgree()
    {
        var fromMessage = BuildFromMessage();
        var fromBuffer = new BeatGrid(Reference, BuildGridBytes(BeatsWithinBar, Bpms, Times));
        Assert.Equal(fromMessage.BeatCount, fromBuffer.BeatCount);
        for (var beat = 1; beat <= fromMessage.BeatCount; beat++)
        {
            Assert.Equal(fromMessage.GetTimeWithinTrack(beat), fromBuffer.GetTimeWithinTrack(beat));
            Assert.Equal(fromMessage.GetBeatWithinBar(beat), fromBuffer.GetBeatWithinBar(beat));
            Assert.Equal(fromMessage.GetBpm(beat), fromBuffer.GetBpm(beat));
        }
        Assert.NotNull(fromBuffer.RawData);
        Assert.Equal(BuildGridBytes(BeatsWithinBar, Bpms, Times), fromBuffer.RawData!.Value.ToArray());
    }

    [Fact]
    public void RawArraysConstructorCopiesValues()
    {
        var beats = new[] { 1, 2 };
        var bpms = new[] { 12000, 12000 };
        var times = new[] { 100L, 600L };
        var grid = new BeatGrid(Reference, beats, bpms, times);
        beats[0] = 4;  // Mutating the source arrays must not affect the grid.
        times[0] = 999L;
        Assert.Equal(1, grid.GetBeatWithinBar(1));
        Assert.Equal(100L, grid.GetTimeWithinTrack(1));
        Assert.Null(grid.RawData);
    }

    [Fact]
    public void RawArraysConstructorRejectsMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            new BeatGrid(Reference, [1, 2], [12000, 12000], [100L]));
    }

    [Fact]
    public void FindBeatAtTimeHandlesBoundaries()
    {
        var grid = BuildFromMessage();
        Assert.Equal(-1, grid.FindBeatAtTime(0));      // Before the first beat.
        Assert.Equal(-1, grid.FindBeatAtTime(499));    // Just before the first beat.
        Assert.Equal(1, grid.FindBeatAtTime(500));     // Exactly on the first beat.
        Assert.Equal(1, grid.FindBeatAtTime(750));     // Between beats 1 and 2.
        Assert.Equal(2, grid.FindBeatAtTime(1000));    // Exactly on the second beat.
        Assert.Equal(6, grid.FindBeatAtTime(3000));    // Exactly on the last beat.
        Assert.Equal(6, grid.FindBeatAtTime(99999));   // Beyond the last beat.
    }

    [Fact]
    public void OutOfRangeBeatsAreClampedByAccessors()
    {
        var grid = BuildFromMessage();
        Assert.Equal(0, grid.GetTimeWithinTrack(0));            // Beat 0 means "before the track".
        Assert.Equal(500L, grid.GetTimeWithinTrack(-5));        // Clamped to the first beat.
        Assert.Equal(3000L, grid.GetTimeWithinTrack(99));       // Clamped to the last beat.
        Assert.Equal(1, grid.GetBeatWithinBar(0));              // Clamped to the first beat.
        Assert.Equal(2, grid.GetBeatWithinBar(99));             // Clamped to the last beat.
    }

    [Fact]
    public void BarNumbersStartAtOneWhenTrackStartsOnDownbeat()
    {
        var grid = BuildFromMessage();
        Assert.Equal(1, grid.GetBarNumber(1));
        Assert.Equal(1, grid.GetBarNumber(4));
        Assert.Equal(2, grid.GetBarNumber(5));
    }

    [Fact]
    public void IncompleteFirstBarIsNumberedMinusOne()
    {
        // The track starts on beat 3 of a bar, so the first two beats form partial bar -1.
        var grid = new BeatGrid(Reference, [3, 4, 1, 2, 3, 4, 1],
            [12000, 12000, 12000, 12000, 12000, 12000, 12000],
            [500L, 1000L, 1500L, 2000L, 2500L, 3000L, 3500L]);
        Assert.Equal(-1, grid.GetBarNumber(1));
        Assert.Equal(-1, grid.GetBarNumber(2));
        Assert.Equal(1, grid.GetBarNumber(3));
        Assert.Equal(1, grid.GetBarNumber(6));
        Assert.Equal(2, grid.GetBarNumber(7));
    }

    [Fact]
    public void EmptyGridsReportNoBeatsAndRejectLookups()
    {
        var grid = new BeatGrid(Reference, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(0, grid.BeatCount);
        Assert.Throws<InvalidOperationException>(() => grid.GetBeatWithinBar(1));
        Assert.Equal(0, grid.GetTimeWithinTrack(0));
    }
}
