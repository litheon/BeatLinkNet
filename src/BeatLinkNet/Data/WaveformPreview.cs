using BeatLink.CrateDigger.Pdb;
using BeatLink.DbServer;

namespace BeatLink.Data;

/// <summary>
/// Gives a birds-eye view of the audio content of a track. The Swing rendering component offered
/// by the Java library is not ported; instead the segment accessors expose everything a .NET UI
/// framework needs to draw the preview itself.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>WaveformPreview</c>, by James Elliott. Where the Java
/// library exposes <c>java.awt.Color</c>, this port uses the platform-neutral <see cref="ArgbColor"/> struct.
/// </remarks>
public class WaveformPreview
{
    /// <summary>
    /// The number of bytes at the start of the color waveform data to be skipped when that was
    /// loaded using the nxs2 ANLZ tag request. We actually know what these mean, now that we know
    /// how to parse EXT files, but we can simply skip them anyway.
    /// </summary>
    public const int LeadingDbserverColorJunkBytes = 28;

    /// <summary>The unique identifier that was used to request this waveform preview.</summary>
    public DataReference DataReference { get; }

    /// <summary>
    /// The message holding the preview as it was read over the network, if it came from the
    /// dbserver. This can be used to analyze fields that have not yet been reliably understood,
    /// and is also used for storing the preview in a file. Will be <c>null</c> if the preview was
    /// not obtained from a dbserver response.
    /// </summary>
    public Message? RawMessage { get; }

    /// <summary>
    /// The waveform data in its most convenient form.
    /// </summary>
    private readonly ReadOnlyMemory<byte> _expandedData;

    /// <summary>Indicates the format of this waveform preview.</summary>
    public WaveformStyle Style { get; }

    /// <summary>
    /// Get the raw bytes of the waveform preview data, as described in the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/track_metadata.html#_waveform_previews">Packet Analysis document</a>.
    /// </summary>
    public ReadOnlyMemory<byte> Data => _expandedData;

    /// <summary>The pixel width (number of waveform preview columns) available.</summary>
    public int SegmentCount { get; }

    /// <summary>
    /// Holds the maximum height of any point along the waveform, so that it can be drawn in a
    /// normalized manner to fit its display area.
    /// </summary>
    public int MaxHeight { get; }

    // The Java createViewComponent method (Swing) is intentionally not ported.

    /// <summary>
    /// Figures out how many segments (columns) are in the preview.
    /// </summary>
    /// <returns>the width, in pixels, at which the preview should ideally be drawn</returns>
    private int ComputeSegmentCount()
    {
        var bytes = _expandedData.Length;
        return Style switch
        {
            WaveformStyle.Blue => bytes / 2,
            WaveformStyle.ThreeBand => bytes / 3,
            WaveformStyle.Rgb => bytes / 6,
            _ => throw new InvalidOperationException($"Unknown waveform style: {Style}")
        };
    }

    /// <summary>
    /// Scan the segments to find the largest height value present.
    /// </summary>
    /// <returns>the largest waveform height anywhere in the preview</returns>
    private int ComputeMaxHeight()
    {
        var result = 0;
        for (var i = 0; i < SegmentCount; i++)
        {
            if (Style == WaveformStyle.ThreeBand)
            {
                result = Math.Max(result, SegmentHeight(i, ThreeBandLayer.Low));
                result = Math.Max(result, SegmentHeight(i, ThreeBandLayer.Mid));
                result = Math.Max(result, SegmentHeight(i, ThreeBandLayer.High));
            }
            else
            {
                result = Math.Max(result, SegmentHeight(i, false));
            }
        }
        return result;
    }

    /// <summary>
    /// Constructor when reading from the network or a file.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform preview</param>
    /// <param name="message">the response that contains the preview</param>
    /// <param name="style">the style of waveform that was requested, since this cannot be
    /// determined from the message alone</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="message"/> is <c>null</c></exception>
    public WaveformPreview(DataReference reference, Message message, WaveformStyle style)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(message);
        Style = style;
        DataReference = reference;
        RawMessage = message;
        var data = ((BinaryField)message.Arguments[3]).Value;
        _expandedData = style != WaveformStyle.Blue ? data[LeadingDbserverColorJunkBytes..] : data;
        SegmentCount = ComputeSegmentCount();
        MaxHeight = ComputeMaxHeight();
    }

    /// <summary>
    /// Constructor for use when the preview was received from Crate Digger, in whichever style the
    /// <see cref="WaveformFinder"/> is configured to prefer (see
    /// <see cref="WaveformFinder.PreferredStyle"/>). (Deprecated in the Java library in favor of
    /// the overload taking an explicit <see cref="WaveformStyle"/>.)
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform preview</param>
    /// <param name="anlzFile">the parsed rekordbox track analysis file containing the waveform preview</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="anlzFile"/> is <c>null</c></exception>
    /// <exception cref="InvalidOperationException">if no suitable waveform preview could be found
    /// in the analysis file</exception>
    public WaveformPreview(DataReference reference, RekordboxAnlz anlzFile) :
        this(reference, anlzFile, WaveformFinder.Instance.PreferredStyle)
    {
    }

    /// <summary>
    /// Constructor for use when the preview was received from Crate Digger.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform preview</param>
    /// <param name="anlzFile">the parsed rekordbox track analysis file containing the waveform
    /// preview (we rely on the correct file for the requested style being passed: ANLZ for blue,
    /// EXT for RGB, and 2EX for 3-band)</param>
    /// <param name="style">the waveform style being loaded</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> or
    /// <paramref name="anlzFile"/> is <c>null</c></exception>
    /// <exception cref="InvalidOperationException">if no suitable waveform preview could be found
    /// in the analysis file</exception>
    public WaveformPreview(DataReference reference, RekordboxAnlz anlzFile, WaveformStyle style)
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
            if (style == WaveformStyle.ThreeBand && section.Body is RekordboxAnlz.Wave3bandPreviewTag threeBandTag)
            {
                found = threeBandTag.Entries;
                threeBandFound = true;
                break;
            }

            // Next see if a requested color waveform is present.
            if (style == WaveformStyle.Rgb && section.Body is RekordboxAnlz.WaveColorPreviewTag colorTag)
            {
                found = colorTag.Entries;
                colorFound = true;
                break;
            }

            // Finally fall back to the blue/white waveform.
            if (section.Body is RekordboxAnlz.WavePreviewTag wavePreviewTag)
            {
                if (wavePreviewTag.LenData < 400 || wavePreviewTag.Data == null)
                {
                    continue;  // We want to ignore the tiny previews, and not crash for vestigial tags without data.
                }
                var tagBytes = wavePreviewTag.Data;
                var bytes = new byte[tagBytes.Length * 2];
                for (var i = 0; i < tagBytes.Length; i++)
                {
                    bytes[i * 2] = (byte)(tagBytes[i] & 0x1f);
                    bytes[i * 2 + 1] = (byte)((tagBytes[i] >> 5) & 7);
                }
                found = bytes;
                // Keep on going in case we also find the color version, which is better.
            }
        }

        if (found == null)
        {
            throw new InvalidOperationException("Could not construct WaveformPreview, missing from ANLZ file " + anlzFile);
        }
        _expandedData = found;
        Style = threeBandFound ? WaveformStyle.ThreeBand : (colorFound ? WaveformStyle.Rgb : WaveformStyle.Blue);
        SegmentCount = ComputeSegmentCount();
        MaxHeight = ComputeMaxHeight();
    }

    /// <summary>
    /// Constructor for use with external caching mechanisms.
    /// </summary>
    /// <param name="reference">the unique database reference that was used to request this waveform preview</param>
    /// <param name="data">the waveform data as will be returned by <see cref="Data"/></param>
    /// <param name="style">indicates the style of the waveform</param>
    /// <exception cref="ArgumentNullException">if <paramref name="reference"/> is <c>null</c></exception>
    public WaveformPreview(DataReference reference, ReadOnlyMemory<byte> data, WaveformStyle style)
    {
        ArgumentNullException.ThrowIfNull(reference);
        DataReference = reference;
        RawMessage = null;
        Style = style;
        _expandedData = data.ToArray();
        SegmentCount = ComputeSegmentCount();
        MaxHeight = ComputeMaxHeight();
    }

    /// <summary>
    /// Rounds like Java's <c>Math.round(float)</c> (floor of the value plus one half), which
    /// differs from <see cref="MathF.Round(float)"/> for exact midpoints.
    /// </summary>
    private static int JavaRound(float value) => (int)MathF.Floor(value + 0.5f);

    /// <summary>The color at which segments of the blue waveform marked most intense are drawn.</summary>
    public static readonly ArgbColor IntenseColor = ArgbColor.FromRgb(116, 246, 244);

    /// <summary>The color at which non-intense blue waveform segments are drawn.</summary>
    public static readonly ArgbColor NormalColor = ArgbColor.FromRgb(43, 89, 255);

    /// <summary>
    /// Determine the height of the preview given an index into it.
    /// </summary>
    /// <param name="segment">the index of the waveform preview segment to examine</param>
    /// <param name="front">if <c>true</c> the height of the front (brighter) segment of a color
    /// waveform preview is returned, otherwise the height of the back (dimmer) segment is
    /// returned. Has no effect for blue previews.</param>
    /// <returns>a value from 0 to 31 representing the height of the waveform at that segment</returns>
    /// <exception cref="NotSupportedException">for three-band waveforms since each band has a
    /// different height</exception>
    public int SegmentHeight(int segment, bool front)
    {
        var bytes = _expandedData.Span;

        switch (Style)
        {
            case WaveformStyle.ThreeBand:
                throw new NotSupportedException();

            case WaveformStyle.Rgb:
                var baseOffset = segment * 6;
                int frontHeight = bytes[baseOffset + 5];
                if (front)
                {
                    return frontHeight;
                }
                return Math.Max(frontHeight, Math.Max(bytes[baseOffset + 3], bytes[baseOffset + 4]));

            case WaveformStyle.Blue:
                return bytes[segment * 2] & 0x1f;

            default:
                throw new InvalidOperationException($"Unknown waveform style: {Style}");
        }
    }

    /// <summary>
    /// Determine the height of a three-band preview given an index into it.
    /// </summary>
    /// <param name="segment">the index of the waveform preview segment to examine</param>
    /// <param name="band">the band whose height is wanted</param>
    /// <returns>a value from 0 to 31 representing the height of the waveform at that segment</returns>
    /// <exception cref="NotSupportedException">if called on a non three-band waveform</exception>
    /// <exception cref="ArgumentNullException">if <paramref name="band"/> is <c>null</c></exception>
    public int SegmentHeight(int segment, ThreeBandLayer band)
    {
        ArgumentNullException.ThrowIfNull(band);
        if (Style != WaveformStyle.ThreeBand)
        {
            throw new NotSupportedException();
        }

        var bytes = _expandedData.Span;
        var baseOffset = segment * 3;

        var lowScaled = bytes[baseOffset + 2] * 0.49f;
        if (band == ThreeBandLayer.Low)
        {
            return JavaRound(lowScaled);
        }

        var midScaled = bytes[baseOffset] * 0.32f;
        if (band == ThreeBandLayer.Mid)
        {
            return JavaRound(lowScaled + midScaled);
        }

        var highScaled = bytes[baseOffset + 1] * 0.25f;
        if (band == ThreeBandLayer.High)
        {
            return JavaRound(lowScaled + midScaled + highScaled);
        }

        throw new InvalidOperationException($"Unrecognized three-band waveform band: {band}");
    }

    /// <summary>
    /// Determine the color of the waveform given an index into it.
    /// </summary>
    /// <param name="segment">the index of the first waveform byte to examine</param>
    /// <param name="front">if <c>true</c> the front (brighter) segment of a color waveform
    /// preview is returned, otherwise the back (dimmer) segment is returned. Has no effect for
    /// blue previews.</param>
    /// <returns>the color of the waveform at that segment</returns>
    /// <exception cref="NotSupportedException">if called on a three-band waveform, colors are
    /// fixed for each band</exception>
    public ArgbColor SegmentColor(int segment, bool front)
    {
        var bytes = _expandedData.Span;

        switch (Style)
        {
            case WaveformStyle.ThreeBand:
                throw new NotSupportedException();

            case WaveformStyle.Rgb:
                var baseOffset = segment * 6;
                var backHeight = SegmentHeight(segment, false);
                if (backHeight == 0)
                {
                    return ArgbColor.Black;
                }
                var maxLevel = front ? 255 : 191;
                var red = bytes[baseOffset + 3] * maxLevel / backHeight;
                var green = bytes[baseOffset + 4] * maxLevel / backHeight;
                var blue = bytes[baseOffset + 5] * maxLevel / backHeight;
                return ArgbColor.FromRgb(red, green, blue);

            case WaveformStyle.Blue:
                var intensity = bytes[segment * 2 + 1] & 0x07;
                return intensity >= 5 ? IntenseColor : NormalColor;

            default:
                throw new InvalidOperationException($"Unknown waveform style: {Style}");
        }
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is WaveformPreview other && _expandedData.Span.SequenceEqual(other._expandedData.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_expandedData.Span);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"WaveformPreview[dataReference={DataReference}, style: {Style}, size:{_expandedData.Length}, segments:{SegmentCount}]";
}
