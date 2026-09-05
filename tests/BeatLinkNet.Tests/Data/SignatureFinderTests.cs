using BeatLink.Data;

namespace BeatLink.Tests;

/// <summary>
/// Golden-vector tests for <see cref="SignatureFinder.ComputeTrackSignature"/>. The expected
/// hashes were computed independently (with Python's hashlib) from the digest layout of the Java
/// implementation: UTF-8 title bytes, a zero byte, UTF-8 artist label (or "[no artist]" when
/// there is no artist), a zero byte, the duration as a 4-byte big-endian integer, the raw RGB
/// waveform detail bytes, then for each beat from 1 through the beat count the beat-within-bar
/// and the time within the track (in milliseconds, truncated to an int), each as 4-byte
/// big-endian integers. The tempo at each beat is deliberately not included, and the resulting
/// SHA-1 digest is rendered as lowercase hex.
/// </summary>
public class SignatureFinderTests
{
    private static readonly DataReference Reference =
        new(2, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Rekordbox);

    /// <summary>
    /// Twelve sequential bytes representing a (tiny) RGB waveform detail.
    /// </summary>
    private static byte[] WaveformBytes => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    /// <summary>
    /// A six-beat grid: beats at half-second intervals starting at zero, cycling through the bar.
    /// </summary>
    private static BeatGrid MakeBeatGrid() =>
        new(Reference,
            beatWithinBarValues: [1, 2, 3, 4, 1, 2],
            bpmValues: [12000, 12000, 12000, 12000, 12000, 12000],
            timeWithinTrackValues: [0, 500, 1000, 1500, 2000, 2500]);

    private static WaveformDetail MakeRgbDetail() =>
        new(Reference, WaveformBytes, WaveformStyle.Rgb);

    [Fact]
    public void ComputesExpectedSignatureWithArtist()
    {
        var signature = SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Buns", new SearchableItem(7, "Some Artist"), 300, MakeRgbDetail(), MakeBeatGrid());
        Assert.Equal("6e348b05b835561f5dd88c20e28ef08197676d14", signature);
    }

    [Fact]
    public void ComputesExpectedSignatureWithoutArtistOrTitle()
    {
        // A null artist digests as "[no artist]", and a null title as the empty string.
        var withNulls = SignatureFinder.Instance.ComputeTrackSignature(
            null, null, 0, MakeRgbDetail(), MakeBeatGrid());
        Assert.Equal("359331019688e00e4984349c2893d33230bafbd4", withNulls);

        // An empty title is identical to a null one.
        var withEmptyTitle = SignatureFinder.Instance.ComputeTrackSignature(
            "", null, 0, MakeRgbDetail(), MakeBeatGrid());
        Assert.Equal(withNulls, withEmptyTitle);
    }

    [Fact]
    public void ReturnsNullWhenRequiredInputsAreMissing()
    {
        Assert.Null(SignatureFinder.Instance.ComputeTrackSignature(
            "Title", null, 100, null, MakeBeatGrid()));
        Assert.Null(SignatureFinder.Instance.ComputeTrackSignature(
            "Title", null, 100, MakeRgbDetail(), null));
    }

    [Fact]
    public void RejectsNonRgbWaveforms()
    {
        var blueDetail = new WaveformDetail(Reference, WaveformBytes, WaveformStyle.Blue);
        Assert.Throws<ArgumentException>(() => SignatureFinder.Instance.ComputeTrackSignature(
            "Title", null, 100, blueDetail, MakeBeatGrid()));
    }

    [Fact]
    public void SignatureIsSensitiveToEveryInput()
    {
        var baseline = SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Buns", new SearchableItem(7, "Some Artist"), 300, MakeRgbDetail(), MakeBeatGrid());

        Assert.NotEqual(baseline, SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Bun", new SearchableItem(7, "Some Artist"), 300, MakeRgbDetail(), MakeBeatGrid()));
        Assert.NotEqual(baseline, SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Buns", new SearchableItem(7, "Other Artist"), 300, MakeRgbDetail(), MakeBeatGrid()));
        Assert.NotEqual(baseline, SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Buns", new SearchableItem(7, "Some Artist"), 301, MakeRgbDetail(), MakeBeatGrid()));

        var otherGrid = new BeatGrid(Reference,
            beatWithinBarValues: [1, 2, 3, 4, 1, 2],
            bpmValues: [12000, 12000, 12000, 12000, 12000, 12000],
            timeWithinTrackValues: [0, 500, 1000, 1500, 2000, 2501]);
        Assert.NotEqual(baseline, SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Buns", new SearchableItem(7, "Some Artist"), 300, MakeRgbDetail(), otherGrid));
    }

    [Fact]
    public void ArtistIdDoesNotAffectSignature()
    {
        // Only the artist's label participates in the digest, not its database ID.
        var one = SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Buns", new SearchableItem(7, "Some Artist"), 300, MakeRgbDetail(), MakeBeatGrid());
        var two = SignatureFinder.Instance.ComputeTrackSignature(
            "Hot Cross Buns", new SearchableItem(99, "Some Artist"), 300, MakeRgbDetail(), MakeBeatGrid());
        Assert.Equal(one, two);
    }
}
