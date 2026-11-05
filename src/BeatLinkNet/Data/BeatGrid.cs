using BeatLink.CrateDigger.Pdb;
using BeatLink.DbServer;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// Provides information about each beat in a track: the number of milliseconds after the start of
/// the track that the beat occurs, and where the beat falls within a measure.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>BeatGrid</c>, by James Elliott.
/// </remarks>
public class BeatGrid
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<BeatGrid>();

    /// <summary>The unique identifier that was used to request this beat grid.</summary>
    public DataReference DataReference { get; }

    /// <summary>
    /// The raw bytes of the beat grid as it was read over the network. This can be used to analyze
    /// fields that have not yet been reliably understood, and is also used for storing the beat
    /// grid in a file. This is not available (<c>null</c>) when the beat grid was loaded by Crate
    /// Digger or built from raw arrays.
    /// </summary>
    private readonly byte[]? _rawData;

    /// <summary>
    /// Get the raw bytes of the beat grid as it was read over the network, or <c>null</c> if the
    /// beat grid was not obtained from a dbserver response or raw byte buffer.
    /// </summary>
    public ReadOnlyMemory<byte>? RawData => _rawData is null ? null : (ReadOnlyMemory<byte>?)_rawData;

    /// <summary>The number of beats in the track.</summary>
    public int BeatCount { get; }

    /// <summary>Holds the reported musical count of each beat.</summary>
    private readonly int[] _beatWithinBarValues;

    /// <summary>Holds the reported tempo of each beat.</summary>
    private readonly int[] _bpmValues;

    /// <summary>Holds the reported start time of each beat in milliseconds.</summary>
    private readonly long[] _timeWithinTrackValues;

    /// <summary>
    /// Constructor for when reading from the network.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this beat grid</param>
    /// <param name="message">the response that contained the beat grid data</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="message"/> is <c>null</c></exception>
    public BeatGrid(DataReference reference, Message message) :
        this(reference, ((BinaryField)NonNull(message).Arguments[3]).Value)
    {
    }

    /// <summary>
    /// Helper allowing constructor delegation to validate the message before dereferencing it.
    /// </summary>
    private static Message NonNull(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message;
    }

    /// <summary>
    /// Constructor for reading from a raw byte buffer (for example loaded from a file).
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this beat grid</param>
    /// <param name="buffer">the raw bytes representing the beat grid</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> is <c>null</c></exception>
    public BeatGrid(DataReference reference, ReadOnlyMemory<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(reference);
        DataReference = reference;
        var gridBytes = buffer.ToArray();
        _rawData = gridBytes;
        BeatCount = Math.Max(0, (gridBytes.Length - 20) / 16);  // Handle the case of an empty beat grid.
        _beatWithinBarValues = new int[BeatCount];
        _bpmValues = new int[BeatCount];
        _timeWithinTrackValues = new long[BeatCount];
        for (var beatNumber = 0; beatNumber < BeatCount; beatNumber++)
        {
            var baseOffset = 20 + beatNumber * 16;  // Data for the current beat starts here.
            // For some reason, unlike nearly every other number in the protocol, beat timings are little-endian.
            _beatWithinBarValues[beatNumber] = (int)Util.BytesToNumberLittleEndian(gridBytes, baseOffset, 2);
            _bpmValues[beatNumber] = (int)Util.BytesToNumberLittleEndian(gridBytes, baseOffset + 2, 2);
            _timeWithinTrackValues[beatNumber] = Util.BytesToNumberLittleEndian(gridBytes, baseOffset + 4, 4);
        }
    }

    /// <summary>
    /// Helper function to find the beat grid section in a rekordbox track analysis file.
    /// </summary>
    /// <param name="anlzFile">the file that was downloaded from the player</param>
    /// <returns>the tag containing the beat grid</returns>
    /// <exception cref="ArgumentException">if no beat grid could be found in the file</exception>
    private static RekordboxAnlz.BeatGridTag FindTag(RekordboxAnlz anlzFile)
    {
        foreach (var section in anlzFile.Sections)
        {
            if (section.Body is RekordboxAnlz.BeatGridTag tag)
            {
                return tag;
            }
        }
        throw new ArgumentException("No beat grid found inside analysis file " + anlzFile);
    }

    /// <summary>
    /// Constructor for when fetched from Crate Digger.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this beat grid</param>
    /// <param name="anlzFile">the parsed rekordbox track analysis file containing the beat grid</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="anlzFile"/> is <c>null</c></exception>
    /// <exception cref="ArgumentException">if no beat grid could be found in the file</exception>
    public BeatGrid(DataReference reference, RekordboxAnlz anlzFile)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(anlzFile);
        DataReference = reference;
        _rawData = null;
        var tag = FindTag(anlzFile);
        BeatCount = (int)tag.NumBeats;
        _beatWithinBarValues = new int[BeatCount];
        _bpmValues = new int[BeatCount];
        _timeWithinTrackValues = new long[BeatCount];
        for (var beatNumber = 0; beatNumber < BeatCount; beatNumber++)
        {
            var beat = tag.Beats[beatNumber];
            _beatWithinBarValues[beatNumber] = beat.BeatNumber;
            _bpmValues[beatNumber] = beat.Tempo;
            _timeWithinTrackValues[beatNumber] = beat.Time;
        }
    }

    /// <summary>
    /// Constructor for use by an external storage system.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this beat grid</param>
    /// <param name="beatWithinBarValues">the musical time on which each beat in the grid falls</param>
    /// <param name="bpmValues">the tempo of the track at each beat, as beats per minute multiplied by 100</param>
    /// <param name="timeWithinTrackValues">the time, in milliseconds, at which each beat occurs in the track</param>
    /// <exception cref="ArgumentNullException">if any argument is <c>null</c></exception>
    /// <exception cref="ArgumentException">if the arrays do not contain the same number of beats</exception>
    public BeatGrid(DataReference reference, int[] beatWithinBarValues, int[] bpmValues, long[] timeWithinTrackValues)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(beatWithinBarValues);
        ArgumentNullException.ThrowIfNull(bpmValues);
        ArgumentNullException.ThrowIfNull(timeWithinTrackValues);
        DataReference = reference;
        _rawData = null;
        BeatCount = beatWithinBarValues.Length;
        if (BeatCount != timeWithinTrackValues.Length)
        {
            throw new ArgumentException("Arrays must contain the same number of beats.");
        }
        _beatWithinBarValues = (int[])beatWithinBarValues.Clone();
        _bpmValues = (int[])bpmValues.Clone();
        _timeWithinTrackValues = (long[])timeWithinTrackValues.Clone();
    }

    /// <summary>
    /// Calculate where within the beat grid arrays the information for the specified beat can be
    /// found. Yes, this is a super simple calculation; the main point of the method is to provide
    /// a nice exception when the beat is out of bounds.
    /// </summary>
    /// <param name="beatNumber">the beat desired</param>
    /// <returns>the offset of the start of our cache arrays for information about that beat</returns>
    /// <exception cref="InvalidOperationException">if there are no beats in the grid</exception>
    private int BeatOffset(int beatNumber)
    {
        if (BeatCount == 0)
        {
            throw new InvalidOperationException("There are no beats in this beat grid.");
        }
        if (beatNumber < 1)
        {
            Logger.LogDebug("Offset for nonexistent beat, {BeatNumber} requested; returning offset for beat 1", beatNumber);
            return 0;
        }
        if (beatNumber > BeatCount)
        {
            Logger.LogDebug("Offset for nonexistent beat, {BeatNumber} requested; returning offset for last beat, {BeatCount}",
                beatNumber, BeatCount);
            return BeatCount - 1;
        }
        return beatNumber - 1;
    }

    /// <summary>
    /// Returns the time at which the specified beat falls within the track. Beat 0 means we are
    /// before the first beat (e.g. ready to play the track), so we return 0.
    /// </summary>
    /// <param name="beatNumber">the beat number desired, must fall within the range 1..BeatCount</param>
    /// <returns>the number of milliseconds into the track at which the specified beat occurs</returns>
    public long GetTimeWithinTrack(int beatNumber)
    {
        if (beatNumber == 0)
        {
            return 0;
        }
        return _timeWithinTrackValues[BeatOffset(beatNumber)];
    }

    /// <summary>
    /// Returns the musical count of the specified beat, represented by <i>B<sub>b</sub></i> in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/vcdj.html#cdj-status-packets">Packet Analysis document</a>.
    /// A number from 1 to 4, where 1 is the downbeat, or the start of a new measure.
    /// </summary>
    /// <param name="beatNumber">the number of the beat of interest, must fall within the range 1..BeatCount</param>
    /// <returns>where that beat falls in a bar of music</returns>
    public int GetBeatWithinBar(int beatNumber) => _beatWithinBarValues[BeatOffset(beatNumber)];

    /// <summary>
    /// Get the track BPM at the time of a beat. This is an integer representing the BPM times 100,
    /// so a track running at 120.5 BPM would be represented by the value 12050.
    /// </summary>
    /// <param name="beatNumber">the beat number desired, must fall within the range 1..BeatCount</param>
    /// <returns>the track BPM at the specified beat number to two decimal places multiplied by 100</returns>
    public int GetBpm(int beatNumber) => _bpmValues[BeatOffset(beatNumber)];

    /// <summary>
    /// Return the bar number within which the specified beat falls. Like beats, bars are numbered
    /// starting at 1. Handles the case where the first bar is incomplete (the first beat is not a
    /// downbeat); like rekordbox, in such cases bar 1 is the first complete bar, and the preceding
    /// bar is bar -1.
    /// </summary>
    /// <param name="beatNumber">the beat number whose bar number is desired</param>
    /// <returns>the bar within which the specified beat falls</returns>
    public int GetBarNumber(int beatNumber)
    {
        var offset = GetBeatWithinBar(1) - 1;
        var bar = (offset + BeatOffset(beatNumber)) / 4;
        if (offset == 0)
        {
            return bar + 1;
        }
        if (bar == 0)
        {
            return -1;
        }
        return bar;
    }

    /// <summary>
    /// Finds the beat in which the specified track position falls.
    /// </summary>
    /// <param name="milliseconds">how long the track has been playing</param>
    /// <returns>the beat number represented by that time, or -1 if the time is before the first beat</returns>
    public int FindBeatAtTime(long milliseconds)
    {
        var found = Array.BinarySearch(_timeWithinTrackValues, milliseconds);
        if (found >= 0)
        {
            return found + 1;  // An exact match, just change 0-based array index to 1-based beat number.
        }
        if (found == -1)
        {
            return found;  // We are before the first beat.
        }
        return -(found + 1);  // We are after some beat, report its beat number.
    }

    /// <inheritdoc/>
    public override string ToString() => $"BeatGrid[dataReference:{DataReference}, beats:{BeatCount}]";
}
