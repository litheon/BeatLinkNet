using BeatLink.CrateDigger.Pdb;
using BeatLink.DbServer;

namespace BeatLink.Data;

/// <summary>
/// Gives a detail view of the audio content of a track. The Swing rendering component offered by
/// the Java library is not ported; instead the segment accessors expose everything a .NET UI
/// framework needs to draw the waveform itself.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>WaveformDetail</c>, by James Elliott. Where the Java
/// library exposes <c>java.awt.Color</c>, this port uses the platform-neutral <see cref="ArgbColor"/> struct.
/// </remarks>
public class WaveformDetail
{
    /// <summary>
    /// The number of bytes at the start of the waveform data which do not seem to be valid or used
    /// when it is served by the dbserver protocol. They are not present when the ANLZ.EXT file is
    /// loaded directly by Crate Digger.
    /// </summary>
    public const int LeadingDbserverJunkBytes = 19;

    /// <summary>
    /// The number of bytes at the start of the color waveform data to be skipped when that was
    /// loaded using the nxs2 ANLZ tag request. We actually know what these mean, now that we know
    /// how to parse EXT files, but we can simply skip them anyway.
    /// </summary>
    public const int LeadingDbserverColorJunkBytes = 28;

    /// <summary>The unique identifier that was used to request this waveform detail.</summary>
    public DataReference DataReference { get; }

    /// <summary>
    /// The message holding the detail as it was read over the network. This can be used to analyze
    /// fields that have not yet been reliably understood, and is also used for storing the detail
    /// in a file. This will be <c>null</c> if the data was not obtained from a dbserver response.
    /// </summary>
    public Message? RawMessage { get; }

    /// <summary>
    /// The waveform detail data, regardless of how it was obtained.
    /// </summary>
    private readonly ReadOnlyMemory<byte> _detailBuffer;

    /// <summary>Indicates the format of this waveform detail.</summary>
    public WaveformStyle Style { get; }

    /// <summary>
    /// Get the raw bytes of the waveform detail data, as described in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/track_metadata.html#_detailed_waveforms">Packet Analysis document</a>.
    /// </summary>
    public ReadOnlyMemory<byte> Data => _detailBuffer;

    /// <summary>
    /// Count the half-frames of waveform available (the number of half-frames, or pixel columns,
    /// that make up the track).
    /// </summary>
    public int FrameCount => Style switch
    {
        WaveformStyle.ThreeBand => _detailBuffer.Length / 3,
        WaveformStyle.Rgb => _detailBuffer.Length / 2,
        WaveformStyle.Blue => _detailBuffer.Length,
        _ => throw new InvalidOperationException($"Unrecognized waveform style: {Style}")
    };

    /// <summary>
    /// Determine how long the track plays, in milliseconds. This provides a more accurate value
    /// than the track metadata, which is accurate only to the second, because we know how many
    /// half-frames (1/150 of a second) the track is composed of.
    /// </summary>
    public long TotalTime => Util.HalfFrameToTime(FrameCount);

    // The Java createViewComponent method (Swing) is intentionally not ported.

    /// <summary>
    /// Constructor when reading from the network or a file.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform detail</param>
    /// <param name="message">the response that contains the detail</param>
    /// <param name="style">the style of waveform that was requested, since this can no longer be
    /// determined from the message alone</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="message"/> is <c>null</c></exception>
    public WaveformDetail(DataReference reference, Message message, WaveformStyle style)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(message);
        Style = style;
        DataReference = reference;
        RawMessage = message;
        // Load the bytes we were sent, and skip over the proper number of leading junk bytes.
        var rawBuffer = ((BinaryField)message.Arguments[3]).Value;
        _detailBuffer = rawBuffer[(style != WaveformStyle.Blue ? LeadingDbserverColorJunkBytes : LeadingDbserverJunkBytes)..];
    }

    /// <summary>
    /// Constructor for use when the detail was received from Crate Digger, in whichever style the
    /// <see cref="WaveformFinder"/> is configured to prefer (see
    /// <see cref="WaveformFinder.PreferredStyle"/>). (Deprecated in the Java library in favor of
    /// the overload taking an explicit <see cref="WaveformStyle"/>.)
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform detail</param>
    /// <param name="anlzFile">the parsed rekordbox track analysis file containing the waveform detail</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="anlzFile"/> is <c>null</c></exception>
    /// <exception cref="InvalidOperationException">if no suitable waveform detail could be found
    /// in the analysis file</exception>
    public WaveformDetail(DataReference reference, RekordboxAnlz anlzFile) :
        this(reference, anlzFile, WaveformFinder.Instance.PreferredStyle)
    {
    }

    /// <summary>
    /// Constructor from a raw analysis file tagged section, independent of the current preferred style.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform detail</param>
    /// <param name="section">the parsed rekordbox track analysis file section containing the waveform detail</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="section"/> is <c>null</c></exception>
    /// <exception cref="InvalidOperationException">if the section does not hold a waveform detail tag</exception>
    public WaveformDetail(DataReference reference, RekordboxAnlz.TaggedSection section)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(section);
        DataReference = reference;
        RawMessage = null;
        // Note: the Java implementation mistakenly tests for Wave3bandPreviewTag here before
        // casting to Wave3bandScrollTag, which would crash on a real 3-band scroll section; this
        // port tests for the type it actually uses.
        if (section.Body is RekordboxAnlz.Wave3bandScrollTag threeBandTag)
        {
            _detailBuffer = threeBandTag.Entries;
            Style = WaveformStyle.ThreeBand;
        }
        else if (section.Body is RekordboxAnlz.WaveColorScrollTag colorTag)
        {
            _detailBuffer = colorTag.Entries;
            Style = WaveformStyle.Rgb;
        }
        else if (section.Body is RekordboxAnlz.WaveScrollTag blueTag)
        {
            _detailBuffer = blueTag.Entries;
            Style = WaveformStyle.Blue;
        }
        else
        {
            throw new InvalidOperationException("Cannot create WaveformDetail from section: " + section);
        }
    }

    /// <summary>
    /// Constructor for use when the detail was received from Crate Digger.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform detail</param>
    /// <param name="anlzFile">the parsed rekordbox track analysis file containing the waveform
    /// detail (we rely on the correct file for the requested style being passed: ANLZ for blue,
    /// EXT for RGB, and 2EX for 3-band)</param>
    /// <param name="style">indicates the style of the waveform desired</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="anlzFile"/> is <c>null</c></exception>
    /// <exception cref="InvalidOperationException">if no suitable waveform detail could be found
    /// in the analysis file</exception>
    public WaveformDetail(DataReference reference, RekordboxAnlz anlzFile, WaveformStyle style)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(anlzFile);
        DataReference = reference;
        RawMessage = null;
        byte[]? found = null;
        var threeBandFound = false;
        var colorFound = false;

        foreach (var section in anlzFile.Sections)
        {
            // Check for a requested 3-band waveform first.
            if (style == WaveformStyle.ThreeBand && section.Body is RekordboxAnlz.Wave3bandScrollTag threeBandTag)
            {
                found = threeBandTag.Entries;
                threeBandFound = true;
                break;
            }

            // Next see if a requested color waveform is present.
            if (style == WaveformStyle.Rgb && section.Body is RekordboxAnlz.WaveColorScrollTag colorTag)
            {
                found = colorTag.Entries;
                colorFound = true;
                break;
            }

            // Finally fall back to the blue/white waveform.
            if (section.Body is RekordboxAnlz.WaveScrollTag blueTag)
            {
                found = blueTag.Entries;
                // Keep going in case we also find a requested color version, which is better.
            }
        }

        if (found == null)
        {
            throw new InvalidOperationException("Could not construct WaveformDetail of style " + style +
                ", missing from analysis file " + anlzFile);
        }
        _detailBuffer = found;
        Style = threeBandFound ? WaveformStyle.ThreeBand : (colorFound ? WaveformStyle.Rgb : WaveformStyle.Blue);
    }

    /// <summary>
    /// Constructor for use with external caching mechanisms.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform detail</param>
    /// <param name="data">the waveform data as will be returned by <see cref="Data"/></param>
    /// <param name="style">indicates the style of the waveform</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> is <c>null</c></exception>
    public WaveformDetail(DataReference reference, ReadOnlyMemory<byte> data, WaveformStyle style)
    {
        ArgumentNullException.ThrowIfNull(reference);
        DataReference = reference;
        RawMessage = null;
        _detailBuffer = data.ToArray();
        Style = style;
    }

    /// <summary>
    /// Rounds like Java's <c>Math.round(float)</c> (floor of the value plus one half), which
    /// differs from <see cref="MathF.Round(float)"/> for exact midpoints.
    /// </summary>
    private static int JavaRound(float value) => (int)MathF.Floor(value + 0.5f);

    /// <summary>
    /// The different colors the monochrome (blue) waveform can be drawn in, based on its intensity.
    /// </summary>
    public static readonly IReadOnlyList<ArgbColor> ColorMap =
    [
        ArgbColor.FromRgb(0, 104, 144),
        ArgbColor.FromRgb(0, 136, 176),
        ArgbColor.FromRgb(0, 168, 232),
        ArgbColor.FromRgb(0, 184, 216),
        ArgbColor.FromRgb(120, 184, 216),
        ArgbColor.FromRgb(136, 192, 232),
        ArgbColor.FromRgb(136, 192, 232),
        ArgbColor.FromRgb(200, 224, 232)
    ];

    /// <summary>
    /// Color waveforms are represented by a series of sixteen bit integers into which color and
    /// height information are packed. This function returns the integer corresponding to a
    /// particular half-frame in the waveform.
    /// </summary>
    /// <param name="waveBytes">the raw data making up the waveform</param>
    /// <param name="segment">the index of the half-frame of interest</param>
    /// <returns>the sixteen-bit number encoding the height and RGB values of that segment</returns>
    private static int GetColorWaveformBits(ReadOnlySpan<byte> waveBytes, int segment)
    {
        var baseOffset = segment * 2;
        int big = waveBytes[baseOffset];
        int small = waveBytes[baseOffset + 1];
        return big * 256 + small;
    }

    /// <summary>
    /// Determine the height of the waveform given an index into it. If <paramref name="scale"/>
    /// is larger than 1 we are zoomed out, so we determine an average height of
    /// <paramref name="scale"/> segments starting with the specified one.
    /// </summary>
    /// <param name="segment">the index of the first waveform byte to examine</param>
    /// <param name="scale">the number of wave segments being drawn as a single pixel column</param>
    /// <returns>a value from 0 to 31 representing the height of the waveform at that segment,
    /// which may be an average of a number of values starting there, determined by the scale</returns>
    /// <exception cref="NotSupportedException">for three-band waveforms since each band has a
    /// different height</exception>
    public int SegmentHeight(int segment, int scale)
    {
        var waveBytes = _detailBuffer.Span;
        var limit = FrameCount;
        var sum = 0;
        for (var i = segment; i < segment + scale && i < limit; i++)
        {
            switch (Style)
            {
                case WaveformStyle.ThreeBand:
                    throw new NotSupportedException();

                case WaveformStyle.Rgb:
                    // Note: the Java implementation reads the bits for the starting segment on
                    // every loop iteration (rather than for segment i); that behavior is ported faithfully.
                    sum += (GetColorWaveformBits(waveBytes, segment) >> 2) & 0x1f;
                    break;

                case WaveformStyle.Blue:
                    sum += waveBytes[i] & 0x1f;
                    break;

                default:
                    throw new InvalidOperationException($"Unrecognized waveform style: {Style}");
            }
        }
        return sum / scale;
    }

    /// <summary>
    /// Determine the height of a three-band waveform given an index into it.
    /// </summary>
    /// <param name="segment">the index of the waveform segment to examine</param>
    /// <param name="scale">the number of wave segments being drawn as a single pixel column</param>
    /// <param name="band">the band whose height is wanted</param>
    /// <returns>a value from 0 to 31 representing the height of the waveform at that segment,
    /// which may be an average of a number of values starting there, determined by the scale</returns>
    /// <exception cref="NotSupportedException">if called on a non three-band waveform</exception>
    /// <exception cref="ArgumentNullException">if <paramref name="band"/> is <c>null</c></exception>
    public int SegmentHeight(int segment, int scale, ThreeBandLayer band)
    {
        ArgumentNullException.ThrowIfNull(band);
        if (Style != WaveformStyle.ThreeBand)
        {
            throw new NotSupportedException();
        }

        var bytes = _detailBuffer.Span;
        var limit = FrameCount;
        var sum = 0;

        for (var i = segment; i < segment + scale && i < limit; i++)
        {
            var baseOffset = i * 3;

            if (band == ThreeBandLayer.Low)
            {
                sum += JavaRound(bytes[baseOffset + 2] * 0.4f);
            }
            else if (band == ThreeBandLayer.Mid)
            {
                sum += JavaRound(bytes[baseOffset] * 0.3f);
            }
            else if (band == ThreeBandLayer.High)
            {
                sum += JavaRound(bytes[baseOffset + 1] * 0.06f);
            }
            else
            {
                throw new InvalidOperationException($"Unrecognized three-band waveform band: {band}");
            }
        }

        return sum / scale;
    }

    /// <summary>
    /// Determine the color of the waveform given an index into it. If <paramref name="scale"/> is
    /// larger than 1 we are zoomed out, so we determine an average color of
    /// <paramref name="scale"/> segments starting with the specified one.
    /// </summary>
    /// <param name="segment">the index of the first waveform byte to examine</param>
    /// <param name="scale">the number of wave segments being drawn as a single pixel column</param>
    /// <returns>the color of the waveform at that segment, which may be based on an average of a
    /// number of values starting there, determined by the scale</returns>
    /// <exception cref="NotSupportedException">if called on a three-band waveform, colors are
    /// fixed for each band</exception>
    public ArgbColor SegmentColor(int segment, int scale)
    {
        var waveBytes = _detailBuffer.Span;
        var limit = FrameCount;

        switch (Style)
        {
            case WaveformStyle.ThreeBand:
                throw new NotSupportedException();

            case WaveformStyle.Rgb:
                var red = 0;
                var green = 0;
                var blue = 0;
                for (var i = segment; i < segment + scale && i < limit; i++)
                {
                    // Note: the Java implementation reads the bits for the starting segment on
                    // every loop iteration (rather than for segment i); that behavior is ported faithfully.
                    var bits = GetColorWaveformBits(waveBytes, segment);
                    red += (bits >> 13) & 7;
                    green += (bits >> 10) & 7;
                    blue += (bits >> 7) & 7;
                }
                // Note: the Java implementation passes the accumulated blue value as the green
                // component and vice versa; that channel swap is ported faithfully.
                return ArgbColor.FromRgb(red * 255 / (scale * 7), blue * 255 / (scale * 7), green * 255 / (scale * 7));

            case WaveformStyle.Blue:
                var sum = 0;
                for (var i = segment; i < segment + scale && i < limit; i++)
                {
                    sum += (waveBytes[i] & 0xe0) >> 5;
                }
                return ColorMap[sum / scale];

            default:
                throw new InvalidOperationException($"Unrecognized waveform style: {Style}");
        }
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is WaveformDetail other && _detailBuffer.Span.SequenceEqual(other._detailBuffer.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_detailBuffer.Span);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"WaveformDetail[dataReference={DataReference}, size:{_detailBuffer.Length}]";
}
