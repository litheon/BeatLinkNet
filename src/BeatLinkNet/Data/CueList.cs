using System.Text;
using BeatLink.CrateDigger.Pdb;
using BeatLink.DbServer;
using Kaitai;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// Provides information about each memory point, hot cue, and loop stored for a track.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>CueList</c>, by James Elliott. Where the Java library
/// exposes <c>java.awt.Color</c>, this port uses the platform-neutral <see cref="ArgbColor"/> struct.
/// </remarks>
public class CueList
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<CueList>();

    /// <summary>
    /// The message holding the cue list information as it was read over the network. This can be
    /// used to analyze fields that have not yet been reliably understood, and is also used for
    /// storing the cue list in a file. This will be <c>null</c> if the cue list was not obtained
    /// from a dbserver query.
    /// </summary>
    public Message? RawMessage { get; }

    /// <summary>
    /// The bytes from which the cue list information was parsed, when it was extracted from a
    /// rekordbox track analysis file (or restored from raw tag buffers saved earlier). Each buffer
    /// holds the payload of one ordinary cue tag (the tag body content following the twelve-byte
    /// section header). Will be <c>null</c> if the cue list was obtained from a dbserver query.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? RawTags { get; }

    /// <summary>
    /// The bytes from which any extended (nxs2-style commented) cue list information was parsed,
    /// when it was extracted from a rekordbox track analysis file (or restored from raw tag
    /// buffers saved earlier). Each buffer holds the payload of one extended cue tag (the tag body
    /// content following the twelve-byte section header). Will be <c>null</c> if the cue list was
    /// obtained from a dbserver query.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? RawExtendedTags { get; }

    /// <summary>
    /// Helper method to add cue list entries from a parsed ANLZ cue tag.
    /// </summary>
    /// <param name="entries">the list of entries being accumulated</param>
    /// <param name="tag">the tag whose entries are to be added</param>
    private static void AddEntriesFromTag(List<Entry> entries, RekordboxAnlz.CueTag tag)
    {
        foreach (var cueEntry in tag.Cues)
        {
            if (cueEntry.HotCue == 0)
            {
                // This is an ordinary memory point or loop.
                if (cueEntry.Type == RekordboxAnlz.CueEntryType.Loop)
                {
                    entries.Add(new Entry(Util.TimeToHalfFrame(cueEntry.Time),
                        Util.TimeToHalfFrame(cueEntry.LoopTime), "", 0));
                }
                else
                {
                    entries.Add(new Entry(Util.TimeToHalfFrame(cueEntry.Time), "", 0));
                }
            }
            else
            {
                // This is a hot cue or loop.
                if (cueEntry.Type == RekordboxAnlz.CueEntryType.Loop)
                {
                    entries.Add(new Entry((int)cueEntry.HotCue, Util.TimeToHalfFrame(cueEntry.Time),
                        Util.TimeToHalfFrame(cueEntry.LoopTime), "", null, null));
                }
                else
                {
                    entries.Add(new Entry((int)cueEntry.HotCue, Util.TimeToHalfFrame(cueEntry.Time), "", null, null));
                }
            }
        }
    }

    /// <summary>
    /// Decode the embedded color present in the cue entry, if there is one.
    /// </summary>
    /// <param name="entry">the parsed extended cue entry</param>
    /// <returns>the embedded color value, or <c>null</c> if there is none present</returns>
    private static ArgbColor? FindEmbeddedColor(RekordboxAnlz.CueExtendedEntry entry)
    {
        if (entry.ColorRed == null || entry.ColorGreen == null || entry.ColorBlue == null ||
            (entry.ColorRed == 0 && entry.ColorGreen == 0 && entry.ColorBlue == 0))
        {
            return null;
        }
        return ArgbColor.FromRgb(entry.ColorRed.Value, entry.ColorGreen.Value, entry.ColorBlue.Value);
    }

    /// <summary>
    /// Helper method to add cue list entries from a parsed extended ANLZ nxs2 comment cue tag.
    /// </summary>
    /// <param name="entries">the list of entries being accumulated</param>
    /// <param name="tag">the tag whose entries are to be added</param>
    private static void AddEntriesFromTag(List<Entry> entries, RekordboxAnlz.CueExtendedTag tag)
    {
        foreach (var cueEntry in tag.Cues)
        {
            // Normalize missing comments to empty strings.
            var comment = cueEntry.Comment ?? "";
            if (cueEntry.HotCue == 0)
            {
                // This is an ordinary memory point or loop.
                if (cueEntry.Type == RekordboxAnlz.CueEntryType.Loop)
                {
                    entries.Add(new Entry(Util.TimeToHalfFrame(cueEntry.Time),
                        Util.TimeToHalfFrame(cueEntry.LoopTime), comment, cueEntry.ColorId));
                }
                else
                {
                    entries.Add(new Entry(Util.TimeToHalfFrame(cueEntry.Time), comment, cueEntry.ColorId));
                }
            }
            else
            {
                // This is a hot cue or loop.
                var embeddedColor = FindEmbeddedColor(cueEntry);
                var colorCode = cueEntry.ColorCode ?? 0;
                var expectedColor = ExpectedEmbeddedColor(colorCode);
                var rekordboxColor = FindRekordboxColor(colorCode);
                if (((embeddedColor == null && expectedColor != null) ||
                        (embeddedColor != null && !embeddedColor.Equals(expectedColor))) &&
                    (colorCode != 0 || embeddedColor != null))
                {
                    Logger.LogWarning("Was expecting embedded color {ExpectedColor} for rekordbox color code {ColorCode}, but found color {EmbeddedColor}",
                        expectedColor, colorCode, embeddedColor);
                }
                if (cueEntry.Type == RekordboxAnlz.CueEntryType.Loop)
                {
                    entries.Add(new Entry((int)cueEntry.HotCue, Util.TimeToHalfFrame(cueEntry.Time),
                        Util.TimeToHalfFrame(cueEntry.LoopTime), comment, embeddedColor, rekordboxColor));
                }
                else
                {
                    entries.Add(new Entry((int)cueEntry.HotCue, Util.TimeToHalfFrame(cueEntry.Time),
                        comment, embeddedColor, rekordboxColor));
                }
            }
        }
    }

    /// <summary>
    /// Constructor for when reading from a rekordbox track analysis file. Finds the cues sections
    /// and translates them into the objects Beat Link uses to represent them.
    /// </summary>
    /// <param name="anlzFile">the rekordbox analysis file corresponding to the track</param>
    /// <exception cref="ArgumentNullException">if <paramref name="anlzFile"/> is <c>null</c></exception>
    public CueList(RekordboxAnlz anlzFile)
    {
        ArgumentNullException.ThrowIfNull(anlzFile);
        RawMessage = null;  // We did not create this from a dbserver response.
        var tagBuffers = new List<ReadOnlyMemory<byte>>(2);
        var extendedTagBuffers = new List<ReadOnlyMemory<byte>>(2);
        var mutableEntries = new List<Entry>();

        // First see if there are any nxs2-style cue comment tags available.
        foreach (var section in anlzFile.Sections)
        {
            if (section.Body is RekordboxAnlz.CueExtendedTag extendedTag)
            {
                extendedTagBuffers.Add(section.M_RawBody);
                AddEntriesFromTag(mutableEntries, extendedTag);
            }
        }

        // Then, collect any old style cue tags, but ignore their entries if we found nxs2-style ones.
        foreach (var section in anlzFile.Sections)
        {
            if (section.Body is RekordboxAnlz.CueTag tag)
            {
                tagBuffers.Add(section.M_RawBody);
                if (extendedTagBuffers.Count == 0)
                {
                    AddEntriesFromTag(mutableEntries, tag);
                }
            }
        }
        _entries = SortEntries(mutableEntries);
        RawTags = tagBuffers.AsReadOnly();
        RawExtendedTags = extendedTagBuffers.AsReadOnly();
    }

    /// <summary>
    /// Constructor for when recreating from files containing the raw tag bytes if there were no
    /// nxs2-style extended cue tags. Included for backwards compatibility.
    /// </summary>
    /// <param name="rawTags">the un-parsed ANLZ file tags holding the cue list entries</param>
    /// <exception cref="ArgumentNullException">if <paramref name="rawTags"/> is <c>null</c></exception>
    public CueList(IReadOnlyList<ReadOnlyMemory<byte>> rawTags) :
        this(rawTags, [])
    {
    }

    /// <summary>
    /// Constructor for when recreating from files containing the raw tag bytes and raw nxs2-style
    /// cue comment tag bytes.
    /// </summary>
    /// <param name="rawTags">the un-parsed ANLZ file tags holding the cue list entries</param>
    /// <param name="rawExtendedTags">the un-parsed extended ANLZ file tags holding the nxs2-style
    /// commented cue list entries</param>
    /// <exception cref="ArgumentNullException">if <paramref name="rawTags"/> or
    /// <paramref name="rawExtendedTags"/> is <c>null</c></exception>
    public CueList(IReadOnlyList<ReadOnlyMemory<byte>> rawTags, IReadOnlyList<ReadOnlyMemory<byte>> rawExtendedTags)
    {
        ArgumentNullException.ThrowIfNull(rawTags);
        ArgumentNullException.ThrowIfNull(rawExtendedTags);
        RawMessage = null;
        RawTags = rawTags;
        RawExtendedTags = rawExtendedTags;
        var mutableEntries = new List<Entry>();
        if (rawExtendedTags.Count == 0)
        {
            foreach (var buffer in rawTags)
            {
                var tag = new RekordboxAnlz.CueTag(new KaitaiStream(buffer.ToArray()));
                AddEntriesFromTag(mutableEntries, tag);
            }
        }
        else
        {
            foreach (var buffer in rawExtendedTags)
            {
                var tag = new RekordboxAnlz.CueExtendedTag(new KaitaiStream(buffer.ToArray()));
                AddEntriesFromTag(mutableEntries, tag);
            }
        }
        _entries = SortEntries(mutableEntries);
    }

    /// <summary>
    /// Return the number of entries in the cue list that represent hot cues.
    /// </summary>
    public int HotCueCount
    {
        get
        {
            if (RawMessage != null)
            {
                return (int)((NumberField)RawMessage.Arguments[5]).Value;
            }
            var total = 0;
            foreach (var entry in Entries)
            {
                if (entry.HotCueNumber > 0)
                {
                    ++total;
                }
            }
            return total;
        }
    }

    /// <summary>
    /// Return the number of entries in the cue list that represent ordinary memory points, rather
    /// than hot cues. The memory points can also be loops.
    /// </summary>
    public int MemoryPointCount
    {
        get
        {
            if (RawMessage != null)
            {
                return (int)((NumberField)RawMessage.Arguments[6]).Value;
            }
            var total = 0;
            foreach (var entry in Entries)
            {
                if (entry.HotCueNumber == 0)
                {
                    ++total;
                }
            }
            return total;
        }
    }

    /// <summary>
    /// <para>Returns the entry whose track position comes most closely before the specified number
    /// of milliseconds, if any. If there is a cue which falls exactly at the specified time, it
    /// will be returned (and it will also be returned by <see cref="FindEntryAfter"/>). If there
    /// is more than one entry at the exact time as the one that is returned, the one chosen will
    /// be unpredictable.</para>
    ///
    /// <para>All times are rounded to half frame units, because that is the resolution at which
    /// cues are stored.</para>
    /// </summary>
    /// <param name="milliseconds">the time of interest within the track</param>
    /// <returns>the cue whose start time is closest to the specified time but not after it, or
    /// <c>null</c> if there was no cue at or before the desired time</returns>
    public Entry? FindEntryBefore(long milliseconds)
    {
        var target = new Entry(Util.TimeToHalfFrameRounded(milliseconds), "", 0);
        var index = _entries.BinarySearch(target, TimeOnlyComparator);
        if (index >= 0)
        {
            return _entries[index];  // An exact match.
        }

        // The exact time was not found, so convert the result to the index where the time would be inserted.
        index = ~index;
        if (index > 0)
        {
            // If there is a value before where we should insert this time, that's what we should return.
            return _entries[index - 1];
        }

        // There was no cue at or before the desired time.
        return null;
    }

    /// <summary>
    /// <para>Returns the entry whose track position comes most closely after the specified number
    /// of milliseconds, if any. If there is a cue which falls exactly at the specified time, it
    /// will be returned (and it will also be returned by <see cref="FindEntryBefore"/>). If there
    /// is more than one entry at the exact time as the one that is returned, the one chosen will
    /// be unpredictable.</para>
    ///
    /// <para>All times are rounded to half frame units, because that is the resolution at which
    /// cues are stored.</para>
    /// </summary>
    /// <param name="milliseconds">the time of interest within the track</param>
    /// <returns>the cue whose start time is closest to the specified time but not before it, or
    /// <c>null</c> if there was no cue at or after the desired time</returns>
    public Entry? FindEntryAfter(long milliseconds)
    {
        var target = new Entry(Util.TimeToHalfFrameRounded(milliseconds), "", 0);
        var index = _entries.BinarySearch(target, TimeOnlyComparator);
        if (index >= 0)
        {
            return _entries[index];  // An exact match.
        }

        // The exact time was not found, so convert the result to the index where the time would be inserted.
        index = ~index;
        if (index < _entries.Count)
        {
            // If there is a value where we should insert this time, that's what we should return.
            return _entries[index];
        }

        // There was no cue at or after the desired time.
        return null;
    }

    /// <summary>
    /// Breaks out information about each entry in the cue list.
    /// </summary>
    public class Entry
    {
        /// <summary>If this has a non-zero value, this entry is a hot cue with that identifier.</summary>
        public int HotCueNumber { get; }

        /// <summary>Indicates whether this entry represents a loop, as opposed to a simple cue point.</summary>
        public bool IsLoop { get; }

        /// <summary>
        /// Indicates the location of the cue in half-frame units, which are 1/150 of a second. If
        /// the cue is a loop, this is the start of the loop.
        /// </summary>
        public long CuePosition { get; }

        /// <summary>
        /// Indicates the location of the cue in milliseconds. If the cue is a loop, this is the
        /// start of the loop.
        /// </summary>
        public long CueTime { get; }

        /// <summary>
        /// If the entry represents a loop, indicates the loop point in half-frame units, which
        /// are 1/150 of a second. The loop point is the end of the loop, at which point playback
        /// jumps back to <see cref="CuePosition"/>.
        /// </summary>
        public long LoopPosition { get; }

        /// <summary>
        /// If the entry represents a loop, indicates the loop point in milliseconds. The loop
        /// point is the end of the loop, at which point playback jumps back to <see cref="CueTime"/>.
        /// </summary>
        public long LoopTime { get; }

        /// <summary>
        /// If the entry was constructed from an extended nxs2-style commented cue tag, and the DJ
        /// assigned a comment to the cue, this will contain the comment text. Otherwise, it will
        /// be an empty string.
        /// </summary>
        public string Comment { get; }

        /// <summary>The color table ID identifying the color assigned to a memory point or loop.</summary>
        public int ColorId { get; }

        /// <summary>The explicit color embedded into the hot cue, or <c>null</c> if there was none.</summary>
        public ArgbColor? EmbeddedColor { get; }

        /// <summary>
        /// The color with which this hot cue will be displayed in rekordbox, if it is a hot cue
        /// with a recognized color code, or <c>null</c> if that does not apply.
        /// </summary>
        public ArgbColor? RekordboxColor { get; }

        /// <summary>
        /// Constructor for non-loop memory point entries.
        /// </summary>
        /// <param name="position">the position of this cue/memory point in half-frame units, which
        /// are 1/150 of a second</param>
        /// <param name="comment">the DJ-assigned comment, or an empty string if none was assigned</param>
        /// <param name="colorId">the row in the color table representing the color assigned this
        /// cue, or zero if none</param>
        /// <exception cref="ArgumentNullException">if <paramref name="comment"/> is <c>null</c></exception>
        public Entry(long position, string comment, int colorId)
        {
            ArgumentNullException.ThrowIfNull(comment);
            HotCueNumber = 0;
            CuePosition = position;
            CueTime = Util.HalfFrameToTime(position);
            IsLoop = false;
            LoopPosition = 0;
            LoopTime = 0;
            ColorId = colorId;
            Comment = comment.Trim();
            EmbeddedColor = null;
            RekordboxColor = null;
        }

        /// <summary>
        /// Constructor for loop memory point entries.
        /// </summary>
        /// <param name="startPosition">the position of the start of this loop in half-frame units,
        /// which are 1/150 of a second</param>
        /// <param name="endPosition">the position of the end of this loop in half-frame units</param>
        /// <param name="comment">the DJ-assigned comment, or an empty string if none was assigned</param>
        /// <param name="colorId">the row in the color table representing the color assigned this
        /// loop, or zero if none</param>
        /// <exception cref="ArgumentNullException">if <paramref name="comment"/> is <c>null</c></exception>
        public Entry(long startPosition, long endPosition, string comment, int colorId)
        {
            ArgumentNullException.ThrowIfNull(comment);
            HotCueNumber = 0;
            CuePosition = startPosition;
            CueTime = Util.HalfFrameToTime(startPosition);
            IsLoop = true;
            LoopPosition = endPosition;
            LoopTime = Util.HalfFrameToTime(endPosition);
            ColorId = colorId;
            Comment = comment.Trim();
            EmbeddedColor = null;
            RekordboxColor = null;
        }

        /// <summary>
        /// Constructor for non-loop hot cue entries.
        /// </summary>
        /// <param name="number">the non-zero hot cue identifier</param>
        /// <param name="position">the position of this cue/memory point in half-frame units, which
        /// are 1/150 of a second</param>
        /// <param name="comment">the DJ-assigned comment, or an empty string if none was assigned</param>
        /// <param name="embeddedColor">the explicit color embedded in the hot cue, if any</param>
        /// <param name="rekordboxColor">the color that rekordbox will display for this hot cue, if available</param>
        /// <exception cref="ArgumentException">if <paramref name="number"/> is zero</exception>
        /// <exception cref="ArgumentNullException">if <paramref name="comment"/> is <c>null</c></exception>
        public Entry(int number, long position, string comment, ArgbColor? embeddedColor, ArgbColor? rekordboxColor)
        {
            if (number == 0)
            {
                throw new ArgumentException("Hot cues must have non-zero numbers", nameof(number));
            }
            ArgumentNullException.ThrowIfNull(comment);
            HotCueNumber = number;
            CuePosition = position;
            CueTime = Util.HalfFrameToTime(position);
            IsLoop = false;
            LoopPosition = 0;
            LoopTime = 0;
            ColorId = 0;
            Comment = comment.Trim();
            EmbeddedColor = embeddedColor;
            RekordboxColor = rekordboxColor;
        }

        /// <summary>
        /// Constructor for loop hot cue entries.
        /// </summary>
        /// <param name="number">the non-zero hot cue identifier</param>
        /// <param name="startPosition">the position of the start of this loop in half-frame units,
        /// which are 1/150 of a second</param>
        /// <param name="endPosition">the position of the end of this loop in half-frame units</param>
        /// <param name="comment">the DJ-assigned comment, or an empty string if none was assigned</param>
        /// <param name="embeddedColor">the explicit color embedded in the cue, if any</param>
        /// <param name="rekordboxColor">the color that rekordbox will display for this cue, if available</param>
        /// <exception cref="ArgumentException">if <paramref name="number"/> is zero</exception>
        /// <exception cref="ArgumentNullException">if <paramref name="comment"/> is <c>null</c></exception>
        public Entry(int number, long startPosition, long endPosition, string comment,
            ArgbColor? embeddedColor, ArgbColor? rekordboxColor)
        {
            if (number == 0)
            {
                throw new ArgumentException("Hot cues must have non-zero numbers", nameof(number));
            }
            ArgumentNullException.ThrowIfNull(comment);
            HotCueNumber = number;
            CuePosition = startPosition;
            CueTime = Util.HalfFrameToTime(startPosition);
            IsLoop = true;
            LoopPosition = endPosition;
            LoopTime = Util.HalfFrameToTime(endPosition);
            ColorId = 0;
            Comment = comment.Trim();
            EmbeddedColor = embeddedColor;
            RekordboxColor = rekordboxColor;
        }

        /// <summary>
        /// Determine the color that an original Nexus series player would use to display this cue.
        /// Hot cues are green, loops are orange, and ordinary memory points are red.
        /// </summary>
        public ArgbColor NexusColor
        {
            get
            {
                if (HotCueNumber > 0)
                {
                    return ArgbColor.FromRgb(0, 255, 0);  // Green
                }
                if (IsLoop)
                {
                    return ArgbColor.FromRgb(255, 200, 0);  // Orange
                }
                return ArgbColor.FromRgb(255, 0, 0);  // Red
            }
        }

        /// <summary>
        /// <para>Determine the best color to be used to display this cue. If it is a memory point,
        /// its color will be a reference to the standard colors table, as implemented by
        /// <see cref="ColorItem.ColorForId"/>.</para>
        ///
        /// <para>If it is a hot cue, things are more complex: If there is an indexed rekordbox
        /// color in the cue, use that; otherwise, if there is an explicit color embedded, use
        /// that, and if neither of those is available, delegate to <see cref="NexusColor"/>.</para>
        /// </summary>
        public ArgbColor Color
        {
            get
            {
                // This is an ordinary memory point or loop.
                if (HotCueNumber == 0)
                {
                    var assigned = ColorItem.ColorForId(ColorId);
                    if (ColorItem.IsNoColor(assigned))
                    {
                        return NexusColor;
                    }
                    return assigned;
                }

                // This is a hot cue or loop.
                if (RekordboxColor.HasValue)
                {
                    return RekordboxColor.Value;
                }
                if (EmbeddedColor.HasValue)
                {
                    return EmbeddedColor.Value;
                }
                return NexusColor;
            }
        }

        /// <summary>
        /// Provides a brief description of the cue, suitable for a tool tip explaining its marker
        /// on a waveform preview.
        /// </summary>
        public string Description
        {
            get
            {
                string kind;
                if (HotCueNumber > 0)
                {
                    kind = "Hot " + (IsLoop ? "Loop " : "Cue ") + (char)(64 + HotCueNumber);
                }
                else
                {
                    kind = IsLoop ? "Loop" : "Memory";
                }
                if (Comment.Length == 0)
                {
                    return kind;
                }
                return kind + ": " + Comment;
            }
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(obj, this))
            {
                return true;
            }
            if (obj == null || obj.GetType() != GetType())
            {
                return false;
            }
            var other = (Entry)obj;
            return HotCueNumber == other.HotCueNumber && IsLoop == other.IsLoop &&
                CuePosition == other.CuePosition && CueTime == other.CueTime &&
                LoopPosition == other.LoopPosition && LoopTime == other.LoopTime;
        }

        /// <inheritdoc/>
        public override int GetHashCode() =>
            HashCode.Combine(HotCueNumber, IsLoop, CuePosition, CueTime, LoopPosition, LoopTime);

        /// <inheritdoc/>
        public override string ToString()
        {
            var sb = new StringBuilder();
            if (HotCueNumber == 0)
            {
                sb.Append(IsLoop ? "Loop[" : "Memory Point[");
            }
            else
            {
                sb.Append("Hot Cue ").Append((char)(HotCueNumber + '@')).Append('[');
            }
            sb.Append("time ").Append(CueTime).Append("ms");
            if (IsLoop)
            {
                sb.Append(", loop time ").Append(LoopTime).Append("ms");
            }
            if (Comment.Length > 0)
            {
                sb.Append(", comment ").Append(Comment);
            }
            sb.Append(", ").Append(Color);
            sb.Append(']');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Backing list for <see cref="Entries"/>, kept as a concrete list so that binary searches
    /// can be performed.
    /// </summary>
    private readonly List<Entry> _entries;

    /// <summary>
    /// The entries present in the cue list, sorted into order by increasing position, with hot
    /// cues coming after ordinary memory points if both are at the same position (as often seems
    /// to happen).
    /// </summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// A comparer for sorting or searching entries that considers only their position within the
    /// track, and not whether they are a hot cue.
    /// </summary>
    public static readonly IComparer<Entry> TimeOnlyComparator =
        Comparer<Entry>.Create((entry1, entry2) => entry1.CuePosition.CompareTo(entry2.CuePosition));

    /// <summary>
    /// The comparer used for sorting the cue list entries during construction, which orders them
    /// by position within the track, with hot cues coming after ordinary memory points if both
    /// exist at the same position. This often happens, and moving hot cues to the end ensures the
    /// waveform display components identify that position as a hot cue, which is important
    /// information.
    /// </summary>
    public static readonly IComparer<Entry> SortComparator = Comparer<Entry>.Create((entry1, entry2) =>
    {
        var result = entry1.CuePosition.CompareTo(entry2.CuePosition);
        if (result == 0)
        {
            var h1 = entry1.HotCueNumber != 0 ? 1 : 0;
            var h2 = entry2.HotCueNumber != 0 ? 1 : 0;
            result = h1 - h2;
        }
        return result;
    });

    /// <summary>
    /// Sorts the entries into the order we want to present them in, which is by position, with
    /// hot cues coming after ordinary memory points if both exist at the same position, which
    /// often happens.
    /// </summary>
    /// <param name="loadedEntries">the unsorted entries we have loaded from a dbserver message or
    /// rekordbox database export</param>
    /// <returns>the same list, sorted into the proper order</returns>
    private static List<Entry> SortEntries(List<Entry> loadedEntries)
    {
        loadedEntries.Sort(SortComparator);
        return loadedEntries;
    }

    /// <summary>
    /// Look up the embedded color that we expect to be paired with a given rekordbox color code,
    /// so we can warn if something else is found instead, which implies our understanding of cue
    /// colors is incorrect.
    /// </summary>
    /// <param name="colorCode">the first color byte</param>
    /// <returns>the color corresponding to the three bytes that are expected to follow it</returns>
    private static ArgbColor? ExpectedEmbeddedColor(int colorCode)
    {
        if (colorCode == 0)
        {
            return ArgbColor.FromRgb(0, 255, 0);  // The default green color used by older CDJs.
        }
        return FindRekordboxColor(colorCode);
    }

    /// <summary>
    /// Look up the color that rekordbox would use to display a cue. The colors in this table
    /// correspond to the 4x4 grids that are available inside the hot cue configuration interface.
    /// </summary>
    /// <param name="colorCode">the color index found in the cue</param>
    /// <returns>the corresponding color or <c>null</c> if the index is not recognized</returns>
    public static ArgbColor? FindRekordboxColor(int colorCode)
    {
        switch (colorCode)
        {
            case 0x01: return ArgbColor.FromRgb(0x30, 0x5a, 0xff);
            case 0x02: return ArgbColor.FromRgb(0x50, 0x73, 0xff);
            case 0x03: return ArgbColor.FromRgb(0x50, 0x8c, 0xff);
            case 0x04: return ArgbColor.FromRgb(0x50, 0xa0, 0xff);
            case 0x05: return ArgbColor.FromRgb(0x50, 0xb4, 0xff);
            case 0x06: return ArgbColor.FromRgb(0x50, 0xb0, 0xf2);
            case 0x07: return ArgbColor.FromRgb(0x50, 0xae, 0xe8);
            case 0x08: return ArgbColor.FromRgb(0x45, 0xac, 0xdb);
            case 0x09: return ArgbColor.FromRgb(0x00, 0xe0, 0xff);
            case 0x0a: return ArgbColor.FromRgb(0x19, 0xda, 0xf0);
            case 0x0b: return ArgbColor.FromRgb(0x32, 0xd2, 0xe6);
            case 0x0c: return ArgbColor.FromRgb(0x21, 0xb4, 0xb9);
            case 0x0d: return ArgbColor.FromRgb(0x20, 0xaa, 0xa0);
            case 0x0e: return ArgbColor.FromRgb(0x1f, 0xa3, 0x92);
            case 0x0f: return ArgbColor.FromRgb(0x19, 0xa0, 0x8c);
            case 0x10: return ArgbColor.FromRgb(0x14, 0xa5, 0x84);
            case 0x11: return ArgbColor.FromRgb(0x14, 0xaa, 0x7d);
            case 0x12: return ArgbColor.FromRgb(0x10, 0xb1, 0x76);
            case 0x13: return ArgbColor.FromRgb(0x30, 0xd2, 0x6e);
            case 0x14: return ArgbColor.FromRgb(0x37, 0xde, 0x5a);
            case 0x15: return ArgbColor.FromRgb(0x3c, 0xeb, 0x50);
            case 0x16: return ArgbColor.FromRgb(0x28, 0xe2, 0x14);
            case 0x17: return ArgbColor.FromRgb(0x7d, 0xc1, 0x3d);
            case 0x18: return ArgbColor.FromRgb(0x8c, 0xc8, 0x32);
            case 0x19: return ArgbColor.FromRgb(0x9b, 0xd7, 0x23);
            case 0x1a: return ArgbColor.FromRgb(0xa5, 0xe1, 0x16);
            case 0x1b: return ArgbColor.FromRgb(0xa5, 0xdc, 0x0a);
            case 0x1c: return ArgbColor.FromRgb(0xaa, 0xd2, 0x08);
            case 0x1d: return ArgbColor.FromRgb(0xb4, 0xc8, 0x05);
            case 0x1e: return ArgbColor.FromRgb(0xb4, 0xbe, 0x04);
            case 0x1f: return ArgbColor.FromRgb(0xba, 0xb4, 0x04);
            case 0x20: return ArgbColor.FromRgb(0xc3, 0xaf, 0x04);
            case 0x21: return ArgbColor.FromRgb(0xe1, 0xaa, 0x00);
            case 0x22: return ArgbColor.FromRgb(0xff, 0xa0, 0x00);
            case 0x23: return ArgbColor.FromRgb(0xff, 0x96, 0x00);
            case 0x24: return ArgbColor.FromRgb(0xff, 0x8c, 0x00);
            case 0x25: return ArgbColor.FromRgb(0xff, 0x75, 0x00);
            case 0x26: return ArgbColor.FromRgb(0xe0, 0x64, 0x1b);
            case 0x27: return ArgbColor.FromRgb(0xe0, 0x46, 0x1e);
            case 0x28: return ArgbColor.FromRgb(0xe0, 0x30, 0x1e);
            case 0x29: return ArgbColor.FromRgb(0xe0, 0x28, 0x23);
            case 0x2a: return ArgbColor.FromRgb(0xe6, 0x28, 0x28);
            case 0x2b: return ArgbColor.FromRgb(0xff, 0x37, 0x6f);
            case 0x2c: return ArgbColor.FromRgb(0xff, 0x2d, 0x6f);
            case 0x2d: return ArgbColor.FromRgb(0xff, 0x12, 0x7b);
            case 0x2e: return ArgbColor.FromRgb(0xf5, 0x1e, 0x8c);
            case 0x2f: return ArgbColor.FromRgb(0xeb, 0x2d, 0xa0);
            case 0x30: return ArgbColor.FromRgb(0xe6, 0x37, 0xb4);
            case 0x31: return ArgbColor.FromRgb(0xde, 0x44, 0xcf);
            case 0x32: return ArgbColor.FromRgb(0xde, 0x44, 0x8d);
            case 0x33: return ArgbColor.FromRgb(0xe6, 0x30, 0xb4);
            case 0x34: return ArgbColor.FromRgb(0xe6, 0x19, 0xdc);
            case 0x35: return ArgbColor.FromRgb(0xe6, 0x00, 0xff);
            case 0x36: return ArgbColor.FromRgb(0xdc, 0x00, 0xff);
            case 0x37: return ArgbColor.FromRgb(0xcc, 0x00, 0xff);
            case 0x38: return ArgbColor.FromRgb(0xb4, 0x32, 0xff);
            case 0x39: return ArgbColor.FromRgb(0xb9, 0x3c, 0xff);
            case 0x3a: return ArgbColor.FromRgb(0xc5, 0x42, 0xff);
            case 0x3b: return ArgbColor.FromRgb(0xaa, 0x5a, 0xff);
            case 0x3c: return ArgbColor.FromRgb(0xaa, 0x72, 0xff);
            case 0x3d: return ArgbColor.FromRgb(0x82, 0x72, 0xff);
            case 0x3e: return ArgbColor.FromRgb(0x64, 0x73, 0xff);

            case 0x00:  // None, use default color.
                return null;

            default:
                Logger.LogWarning("Unrecognized rekordbox color code, {ColorCode}, returning null.", colorCode);
                return null;
        }
    }

    /// <summary>
    /// Constructor when reading from the network or a file.
    /// </summary>
    /// <param name="message">the response holding the cue list information, in either original
    /// nexus (<see cref="Message.KnownType.CueList"/>) or extended nxs2
    /// (<see cref="Message.KnownType.CueListExt"/>) format</param>
    /// <exception cref="ArgumentNullException">if <paramref name="message"/> is <c>null</c></exception>
    public CueList(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RawMessage = message;
        _entries = message.RecognizedType == Message.KnownType.CueList ?
            ParseNexusEntries(message) : ParseNxs2Entries(message);
    }

    /// <summary>
    /// Parse the memory points, loops, and hot cues from an original nexus style cue list response.
    /// </summary>
    /// <param name="message">the response holding the cue list information</param>
    /// <returns>the parsed entries, sorted properly for searching and display</returns>
    private static List<Entry> ParseNexusEntries(Message message)
    {
        var entryBytes = ((BinaryField)message.Arguments[3]).GetValueAsArray();
        var entryCount = entryBytes.Length / 36;
        var mutableEntries = new List<Entry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var offset = i * 36;
            int cueFlag = entryBytes[offset + 1];
            int hotCueNumber = entryBytes[offset + 2];
            if (cueFlag != 0 || hotCueNumber != 0)
            {
                // This entry is not empty, so represent it.
                var position = Util.BytesToNumberLittleEndian(entryBytes, offset + 12, 4);
                if (entryBytes[offset] != 0)
                {
                    // This is a loop.
                    var endPosition = Util.BytesToNumberLittleEndian(entryBytes, offset + 16, 4);
                    mutableEntries.Add(hotCueNumber == 0 ?
                        new Entry(position, endPosition, "", 0) :
                        new Entry(hotCueNumber, position, endPosition, "", null, null));
                }
                else
                {
                    mutableEntries.Add(hotCueNumber == 0 ?
                        new Entry(position, "", 0) :
                        new Entry(hotCueNumber, position, "", null, null));
                }
            }
        }
        return SortEntries(mutableEntries);
    }

    /// <summary>
    /// Attempt to load a color byte from the end of an extended cue entry. Since these are
    /// sometimes missing, will return 0 when asked for a value past the end of the entry.
    /// </summary>
    /// <param name="entryBytes">the bytes of the extended cue entry</param>
    /// <param name="address">the index of the byte that might hold color information</param>
    /// <returns>the unsigned byte value found at the specified location, or 0 if it was past the
    /// end of the entry</returns>
    private static int SafelyFetchColorByte(byte[] entryBytes, int address)
    {
        if (address < entryBytes.Length)
        {
            return entryBytes[address];
        }
        return 0;
    }

    /// <summary>
    /// Parse the memory points, loops, and hot cues from an extended nxs2 style cue list response.
    /// </summary>
    /// <param name="message">the response holding the cue list information</param>
    /// <returns>the parsed entries, sorted properly for searching and display</returns>
    private static List<Entry> ParseNxs2Entries(Message message)
    {
        var entryBytes = ((BinaryField)message.Arguments[3]).GetValueAsArray();
        var entryCount = (int)((NumberField)message.Arguments[4]).Value;
        var mutableEntries = new List<Entry>(entryCount);
        var offset = 0;
        for (var i = 0; i < entryCount; i++)
        {
            var entrySize = (int)Util.BytesToNumberLittleEndian(entryBytes, offset, 4);
            int cueFlag = entryBytes[offset + 6];
            int hotCueNumber = entryBytes[offset + 4];
            if (cueFlag != 0 || hotCueNumber != 0)
            {
                // This entry is not empty, so represent it.
                var position = Util.TimeToHalfFrame(Util.BytesToNumberLittleEndian(entryBytes, offset + 12, 4));

                // See if there is a comment.
                var comment = "";
                var commentSize = 0;
                if (entrySize > 0x49)
                {
                    // This entry is large enough to have a comment.
                    commentSize = (int)Util.BytesToNumberLittleEndian(entryBytes, offset + 0x48, 2);
                }
                if (commentSize > 0)
                {
                    comment = Encoding.Unicode.GetString(entryBytes, offset + 0x4a, commentSize - 2);
                }

                if (hotCueNumber == 0)
                {
                    // This is an ordinary memory point or loop.
                    int colorId = entryBytes[offset + 0x22];
                    if (cueFlag == 2)
                    {
                        // This is a loop.
                        var endPosition = Util.TimeToHalfFrame(Util.BytesToNumberLittleEndian(entryBytes, offset + 16, 4));
                        mutableEntries.Add(new Entry(position, endPosition, comment, colorId));
                    }
                    else
                    {
                        mutableEntries.Add(new Entry(position, comment, colorId));
                    }
                }
                else
                {
                    // This is a hot cue or loop. See if there is a color.
                    var colorCode = SafelyFetchColorByte(entryBytes, offset + commentSize + 0x4e);
                    var red = SafelyFetchColorByte(entryBytes, offset + commentSize + 0x4f);
                    var green = SafelyFetchColorByte(entryBytes, offset + commentSize + 0x50);
                    var blue = SafelyFetchColorByte(entryBytes, offset + commentSize + 0x51);
                    var rekordboxColor = FindRekordboxColor(colorCode);
                    var expectedColor = ExpectedEmbeddedColor(colorCode);
                    var embeddedColor = red == 0 && green == 0 && blue == 0 ?
                        (ArgbColor?)null : ArgbColor.FromRgb(red, green, blue);
                    if (((embeddedColor == null && expectedColor != null) ||
                            (embeddedColor != null && !embeddedColor.Equals(expectedColor))) &&
                        (colorCode != 0 || embeddedColor != null))
                    {
                        Logger.LogWarning("Was expecting embedded color {ExpectedColor} for rekordbox color code {ColorCode}, but found color {EmbeddedColor}",
                            expectedColor, colorCode, embeddedColor);
                    }

                    if (cueFlag == 2)
                    {
                        // This is a loop.
                        var endPosition = Util.TimeToHalfFrame(Util.BytesToNumberLittleEndian(entryBytes, offset + 16, 4));
                        mutableEntries.Add(new Entry(hotCueNumber, position, endPosition, comment, embeddedColor, rekordboxColor));
                    }
                    else
                    {
                        mutableEntries.Add(new Entry(hotCueNumber, position, comment, embeddedColor, rekordboxColor));
                    }
                }
            }
            offset += entrySize;  // Move on to the next entry.
        }
        return SortEntries(mutableEntries);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(obj, this))
        {
            return true;
        }
        if (obj == null || obj.GetType() != GetType())
        {
            return false;
        }
        var other = (CueList)obj;
        return _entries.SequenceEqual(other._entries);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in _entries)
        {
            hash.Add(entry);
        }
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => $"Cue List[entries: [{string.Join(", ", _entries)}]]";
}
