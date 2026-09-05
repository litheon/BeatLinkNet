using BeatLink.Data;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the segment height and color math of <see cref="WaveformPreview"/> and
/// <see cref="WaveformDetail"/> for all three waveform styles, using values computed from the
/// formulas in the Java library.
/// </summary>
public class WaveformTests
{
    private static readonly DataReference Reference =
        new(2, CdjStatus.TrackSourceSlot.UsbSlot, 1234, CdjStatus.TrackType.Rekordbox);

    private static Message MakePreviewMessage(byte[] payload) =>
        new(13, Message.KnownType.WavePreview,
            new NumberField(Message.KnownType.WavePreviewReq.ProtocolValue),  // Request type.
            new NumberField(0),                                               // Unknown.
            new NumberField(payload.Length),                                  // Waveform length.
            new BinaryField(payload));                                        // Waveform bytes.

    private static Message MakeDetailMessage(byte[] payload) =>
        new(14, Message.KnownType.WaveDetail,
            new NumberField(Message.KnownType.WaveDetailReq.ProtocolValue),   // Request type.
            new NumberField(0),                                               // Unknown.
            new NumberField(payload.Length),                                  // Waveform length.
            new BinaryField(payload));                                        // Waveform bytes.

    // --- WaveformPreview -------------------------------------------------------------------

    private static readonly byte[] BluePreviewData = [0x05, 0x02, 0x1f, 0x07, 0x0a, 0x05];

    [Fact]
    public void BluePreviewHeightsAndColors()
    {
        var preview = new WaveformPreview(Reference, BluePreviewData, WaveformStyle.Blue);
        Assert.Equal(3, preview.SegmentCount);
        Assert.Equal(31, preview.MaxHeight);
        Assert.Equal(5, preview.SegmentHeight(0, front: true));
        Assert.Equal(31, preview.SegmentHeight(1, front: false));
        Assert.Equal(10, preview.SegmentHeight(2, front: true));
        Assert.Equal(WaveformPreview.NormalColor, preview.SegmentColor(0, front: true));   // Intensity 2.
        Assert.Equal(WaveformPreview.IntenseColor, preview.SegmentColor(1, front: true));  // Intensity 7.
        Assert.Equal(WaveformPreview.IntenseColor, preview.SegmentColor(2, front: true));  // Intensity 5.
    }

    [Fact]
    public void BluePreviewFromDbserverMessageUsesAllBytes()
    {
        var preview = new WaveformPreview(Reference, MakePreviewMessage(BluePreviewData), WaveformStyle.Blue);
        Assert.Equal(3, preview.SegmentCount);
        Assert.Equal(BluePreviewData, preview.Data.ToArray());
        Assert.NotNull(preview.RawMessage);
    }

    private static readonly byte[] RgbPreviewSegment = [0, 0, 0, 100, 50, 80];

    [Fact]
    public void RgbPreviewHeightsAndColors()
    {
        var data = new byte[12];
        Array.Copy(RgbPreviewSegment, 0, data, 0, 6);  // Segment 0; segment 1 stays all zero.
        var preview = new WaveformPreview(Reference, data, WaveformStyle.Rgb);
        Assert.Equal(2, preview.SegmentCount);

        // Front height is the blue byte; back is the maximum of all three channel bytes.
        Assert.Equal(80, preview.SegmentHeight(0, front: true));
        Assert.Equal(100, preview.SegmentHeight(0, front: false));
        Assert.Equal(100, preview.MaxHeight);

        // Colors are the channel bytes scaled by maxLevel / backHeight.
        Assert.Equal(ArgbColor.FromRgb(255, 127, 204), preview.SegmentColor(0, front: true));
        Assert.Equal(ArgbColor.FromRgb(191, 95, 152), preview.SegmentColor(0, front: false));

        // A silent segment is drawn black.
        Assert.Equal(ArgbColor.Black, preview.SegmentColor(1, front: true));
    }

    [Fact]
    public void RgbPreviewFromDbserverMessageSkipsJunkBytes()
    {
        var payload = new byte[WaveformPreview.LeadingDbserverColorJunkBytes + 6];
        Array.Copy(RgbPreviewSegment, 0, payload, WaveformPreview.LeadingDbserverColorJunkBytes, 6);
        var preview = new WaveformPreview(Reference, MakePreviewMessage(payload), WaveformStyle.Rgb);
        Assert.Equal(1, preview.SegmentCount);
        Assert.Equal(80, preview.SegmentHeight(0, front: true));
    }

    [Fact]
    public void ThreeBandPreviewHeights()
    {
        // Each segment holds three bytes: mid, high, low.
        var preview = new WaveformPreview(Reference, new byte[] { 10, 20, 30 }, WaveformStyle.ThreeBand);
        Assert.Equal(1, preview.SegmentCount);
        Assert.Equal(15, preview.SegmentHeight(0, ThreeBandLayer.Low));   // round(30 * 0.49)
        Assert.Equal(18, preview.SegmentHeight(0, ThreeBandLayer.Mid));   // round(14.7 + 10 * 0.32)
        Assert.Equal(23, preview.SegmentHeight(0, ThreeBandLayer.High));  // round(17.9 + 20 * 0.25)
        Assert.Equal(23, preview.MaxHeight);
        Assert.Throws<NotSupportedException>(() => preview.SegmentHeight(0, front: true));
        Assert.Throws<NotSupportedException>(() => preview.SegmentColor(0, front: true));
    }

    [Fact]
    public void NonThreeBandPreviewsRejectBandAccessor()
    {
        var preview = new WaveformPreview(Reference, BluePreviewData, WaveformStyle.Blue);
        Assert.Throws<NotSupportedException>(() => preview.SegmentHeight(0, ThreeBandLayer.Low));
    }

    // --- WaveformDetail --------------------------------------------------------------------

    [Fact]
    public void BlueDetailHeightsAndColors()
    {
        // Each byte holds a height in the low five bits and a color index in the high three.
        var detail = new WaveformDetail(Reference, new byte[] { 0x25, 0xff }, WaveformStyle.Blue);
        Assert.Equal(2, detail.FrameCount);
        Assert.Equal(5, detail.SegmentHeight(0, 1));
        Assert.Equal(31, detail.SegmentHeight(1, 1));
        Assert.Equal(18, detail.SegmentHeight(0, 2));  // Average of 5 and 31.
        Assert.Equal(WaveformDetail.ColorMap[1], detail.SegmentColor(0, 1));
        Assert.Equal(WaveformDetail.ColorMap[7], detail.SegmentColor(1, 1));
        Assert.Equal(WaveformDetail.ColorMap[4], detail.SegmentColor(0, 2));  // Average of 1 and 7.
    }

    [Fact]
    public void RgbDetailHeightsAndColors()
    {
        // The sixteen-bit value 0xa534 encodes red 5, green 1, blue 2 and height 13:
        // red = (bits >> 13) & 7, green = (bits >> 10) & 7, blue = (bits >> 7) & 7,
        // height = (bits >> 2) & 0x1f. The Java library then swaps the green and blue channel
        // positions when building the color, which is ported faithfully.
        var detail = new WaveformDetail(Reference, new byte[] { 0xa5, 0x34 }, WaveformStyle.Rgb);
        Assert.Equal(1, detail.FrameCount);
        Assert.Equal(13, detail.SegmentHeight(0, 1));
        Assert.Equal(ArgbColor.FromRgb(5 * 255 / 7, 2 * 255 / 7, 1 * 255 / 7), detail.SegmentColor(0, 1));
    }

    [Fact]
    public void ThreeBandDetailHeights()
    {
        var detail = new WaveformDetail(Reference, new byte[] { 10, 20, 30, 10, 20, 30 }, WaveformStyle.ThreeBand);
        Assert.Equal(2, detail.FrameCount);
        Assert.Equal(12, detail.SegmentHeight(0, 1, ThreeBandLayer.Low));   // round(30 * 0.4)
        Assert.Equal(3, detail.SegmentHeight(0, 1, ThreeBandLayer.Mid));    // round(10 * 0.3)
        Assert.Equal(1, detail.SegmentHeight(0, 1, ThreeBandLayer.High));   // round(20 * 0.06)
        Assert.Equal(12, detail.SegmentHeight(0, 2, ThreeBandLayer.Low));   // Average of two identical frames.
        Assert.Throws<NotSupportedException>(() => detail.SegmentHeight(0, 1));
        Assert.Throws<NotSupportedException>(() => detail.SegmentColor(0, 1));
    }

    [Fact]
    public void DetailFrameCountsAndTotalTimeVaryByStyle()
    {
        var bytes = new byte[300];
        Assert.Equal(300, new WaveformDetail(Reference, bytes, WaveformStyle.Blue).FrameCount);
        Assert.Equal(150, new WaveformDetail(Reference, bytes, WaveformStyle.Rgb).FrameCount);
        Assert.Equal(100, new WaveformDetail(Reference, bytes, WaveformStyle.ThreeBand).FrameCount);
        // 300 half-frames at 1/150 of a second each is two seconds of audio.
        Assert.Equal(2000, new WaveformDetail(Reference, bytes, WaveformStyle.Blue).TotalTime);
    }

    [Fact]
    public void BlueDetailFromDbserverMessageSkipsJunkBytes()
    {
        var payload = new byte[WaveformDetail.LeadingDbserverJunkBytes + 2];
        payload[WaveformDetail.LeadingDbserverJunkBytes] = 0x25;
        payload[WaveformDetail.LeadingDbserverJunkBytes + 1] = 0xff;
        var detail = new WaveformDetail(Reference, MakeDetailMessage(payload), WaveformStyle.Blue);
        Assert.Equal(2, detail.FrameCount);
        Assert.Equal(5, detail.SegmentHeight(0, 1));
        Assert.NotNull(detail.RawMessage);
    }

    [Fact]
    public void RgbDetailFromDbserverMessageSkipsJunkBytes()
    {
        var payload = new byte[WaveformDetail.LeadingDbserverColorJunkBytes + 2];
        payload[WaveformDetail.LeadingDbserverColorJunkBytes] = 0xa5;
        payload[WaveformDetail.LeadingDbserverColorJunkBytes + 1] = 0x34;
        var detail = new WaveformDetail(Reference, MakeDetailMessage(payload), WaveformStyle.Rgb);
        Assert.Equal(1, detail.FrameCount);
        Assert.Equal(13, detail.SegmentHeight(0, 1));
    }

    [Fact]
    public void WaveformEqualityIsBasedOnData()
    {
        var a = new WaveformPreview(Reference, BluePreviewData, WaveformStyle.Blue);
        var b = new WaveformPreview(
            new DataReference(3, CdjStatus.TrackSourceSlot.SdSlot, 9, CdjStatus.TrackType.Rekordbox),
            BluePreviewData, WaveformStyle.Blue);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var detailA = new WaveformDetail(Reference, new byte[] { 0x25, 0xff }, WaveformStyle.Blue);
        var detailB = new WaveformDetail(Reference, new byte[] { 0x25, 0xff }, WaveformStyle.Blue);
        Assert.Equal(detailA, detailB);
        Assert.NotEqual(detailA, new WaveformDetail(Reference, new byte[] { 0x25 }, WaveformStyle.Blue));
    }

    [Fact]
    public void ThreeBandLayersCarryTheirColors()
    {
        Assert.Equal(ArgbColor.FromRgb(32, 83, 217), ThreeBandLayer.Low.Color);
        Assert.Equal(ArgbColor.FromRgb(169, 107, 39), ThreeBandLayer.LowAndMid.Color);
        Assert.Equal(ArgbColor.FromRgb(242, 170, 60), ThreeBandLayer.Mid.Color);
        Assert.Equal(ArgbColor.FromRgb(255, 255, 255), ThreeBandLayer.High.Color);
        Assert.Equal(4, ThreeBandLayer.Values.Count);
    }
}
