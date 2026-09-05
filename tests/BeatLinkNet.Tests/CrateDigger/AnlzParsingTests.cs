using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;
using Kaitai;

namespace BeatLink.Tests.CrateDigger;

/// <summary>
/// Exercises the generated <see cref="RekordboxAnlz"/> Kaitai Struct parser and the ANLZ-based
/// constructors of the data model classes against a real pair of rekordbox track analysis files
/// (checked in as fixtures from the repository owner's own library).
/// </summary>
public class AnlzParsingTests
{
    private static readonly DataReference Reference =
        new(2, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Rekordbox);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "anlz", name);

    private static RekordboxAnlz ParseFixture(string name) =>
        new(new KaitaiStream(File.ReadAllBytes(FixturePath(name))));

    [Fact]
    public void DatFileParsesWithExpectedSectionInventory()
    {
        var anlz = ParseFixture("ANLZ0000.DAT");
        Assert.Equal("PMAI"u8.ToArray(), anlz.Magic);
        Assert.Equal((uint)new FileInfo(FixturePath("ANLZ0000.DAT")).Length, anlz.LenFile);
        var fourccs = anlz.Sections.Select(section => section.Fourcc).ToList();
        var expected = new List<RekordboxAnlz.SectionTags>
        {
            RekordboxAnlz.SectionTags.Path,
            RekordboxAnlz.SectionTags.Vbr,
            RekordboxAnlz.SectionTags.BeatGrid,
            RekordboxAnlz.SectionTags.WavePreview,
            RekordboxAnlz.SectionTags.WaveTiny,
            RekordboxAnlz.SectionTags.Cues,
            RekordboxAnlz.SectionTags.Cues,
        };
        Assert.Equal(expected, fourccs);
    }

    [Fact]
    public void ExtFileParsesWithExpectedSectionInventory()
    {
        var anlz = ParseFixture("ANLZ0000.EXT");
        var fourccs = anlz.Sections.Select(section => section.Fourcc).ToList();
        var expected = new List<RekordboxAnlz.SectionTags>
        {
            RekordboxAnlz.SectionTags.Path,
            RekordboxAnlz.SectionTags.WaveScroll,
            RekordboxAnlz.SectionTags.Cues,
            RekordboxAnlz.SectionTags.Cues,
            RekordboxAnlz.SectionTags.Cues2,
            RekordboxAnlz.SectionTags.Cues2,
            (RekordboxAnlz.SectionTags)0x50515432,  // PQT2, an extended beat grid we do not model.
            RekordboxAnlz.SectionTags.WaveColorScroll,
            RekordboxAnlz.SectionTags.WaveColorPreview,
            RekordboxAnlz.SectionTags.SongStructure,
        };
        Assert.Equal(expected, fourccs);
    }

    [Fact]
    public void PathTagIdentifiesAudioFile()
    {
        var anlz = ParseFixture("ANLZ0000.DAT");
        var path = anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.PathTag>().Single();
        Assert.True(path.LenPath > 2);
        // The UTF-16BE path is (len_path - 2) bytes long, so it holds (len_path - 2) / 2 characters.
        Assert.Equal((int)(path.LenPath - 2) / 2, path.Path.Length);
        Assert.EndsWith(".mp3", path.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BeatGridBuildsFromDatFile()
    {
        var anlz = ParseFixture("ANLZ0000.DAT");
        var tag = anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.BeatGridTag>().Single();
        var grid = new BeatGrid(Reference, anlz);

        Assert.True(grid.BeatCount > 0);
        Assert.Equal((int)tag.NumBeats, grid.BeatCount);
        Assert.Null(grid.RawData);

        long previousTime = -1;
        for (var beat = 1; beat <= grid.BeatCount; beat++)
        {
            var time = grid.GetTimeWithinTrack(beat);
            Assert.True(time > previousTime, $"Beat times must be strictly ascending (beat {beat}).");
            previousTime = time;
            Assert.InRange(grid.GetBeatWithinBar(beat), 1, 4);
            Assert.True(grid.GetBpm(beat) > 0);
        }

        // The time lookup should be consistent with the beat lookup.
        Assert.Equal(1, grid.FindBeatAtTime(grid.GetTimeWithinTrack(1)));
        Assert.Equal(-1, grid.FindBeatAtTime(grid.GetTimeWithinTrack(1) - 1));
    }

    [Fact]
    public void CueListBuildsFromDatFileAndRoundTripsThroughRawTags()
    {
        var anlz = ParseFixture("ANLZ0000.DAT");
        var cueList = new CueList(anlz);

        Assert.Null(cueList.RawMessage);
        Assert.NotNull(cueList.RawTags);
        Assert.NotNull(cueList.RawExtendedTags);
        Assert.Equal(2, cueList.RawTags.Count);  // One memory cue tag and one hot cue tag.
        Assert.Empty(cueList.RawExtendedTags);   // Old-style DAT files carry no extended cue tags.
        Assert.Empty(cueList.Entries);           // This fixture track has no cues stored.
        Assert.Equal(0, cueList.HotCueCount);
        Assert.Equal(0, cueList.MemoryPointCount);

        // Recreating the cue list from the raw tag buffers must produce an equal cue list.
        var recreated = new CueList(cueList.RawTags);
        Assert.Equal(cueList, recreated);
    }

    [Fact]
    public void CueListBuildsFromExtFilePreferringExtendedTags()
    {
        var anlz = ParseFixture("ANLZ0000.EXT");
        var cueList = new CueList(anlz);

        Assert.Equal(2, cueList.RawTags!.Count);
        Assert.Equal(2, cueList.RawExtendedTags!.Count);
        Assert.Empty(cueList.Entries);

        var recreated = new CueList(cueList.RawTags, cueList.RawExtendedTags);
        Assert.Equal(cueList, recreated);
    }

    [Fact]
    public void WaveformPreviewBlueBuildsFromDatFile()
    {
        var anlz = ParseFixture("ANLZ0000.DAT");
        var preview = new WaveformPreview(Reference, anlz, WaveformStyle.Blue);

        Assert.Equal(WaveformStyle.Blue, preview.Style);
        Assert.Null(preview.RawMessage);

        // The PWAV tag holds 400 bytes, which are expanded to two bytes (height and intensity) each.
        var tag = anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.WavePreviewTag>()
            .First(wavePreviewTag => wavePreviewTag.LenData >= 400);
        Assert.Equal((int)tag.LenData * 2, preview.Data.Length);
        Assert.Equal((int)tag.LenData, preview.SegmentCount);
        Assert.InRange(preview.MaxHeight, 1, 31);
        for (var segment = 0; segment < preview.SegmentCount; segment++)
        {
            Assert.InRange(preview.SegmentHeight(segment, false), 0, 31);
        }
    }

    [Fact]
    public void WaveformPreviewRgbBuildsFromExtFile()
    {
        var anlz = ParseFixture("ANLZ0000.EXT");
        var preview = new WaveformPreview(Reference, anlz, WaveformStyle.Rgb);

        Assert.Equal(WaveformStyle.Rgb, preview.Style);
        var tag = anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.WaveColorPreviewTag>().Single();
        Assert.Equal(tag.Entries.Length, preview.Data.Length);
        Assert.Equal((int)tag.LenEntries, preview.SegmentCount);
        Assert.InRange(preview.MaxHeight, 1, 127);
    }

    [Fact]
    public void WaveformDetailBlueBuildsFromExtFile()
    {
        var anlz = ParseFixture("ANLZ0000.EXT");
        var detail = new WaveformDetail(Reference, anlz, WaveformStyle.Blue);

        Assert.Equal(WaveformStyle.Blue, detail.Style);
        var tag = anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.WaveScrollTag>().Single();
        Assert.Equal(tag.Entries.Length, detail.Data.Length);
        Assert.Equal((int)tag.LenEntries, detail.FrameCount);
        Assert.Equal(Util.HalfFrameToTime(detail.FrameCount), detail.TotalTime);
    }

    [Fact]
    public void WaveformDetailRgbBuildsFromExtFile()
    {
        var anlz = ParseFixture("ANLZ0000.EXT");
        var detail = new WaveformDetail(Reference, anlz, WaveformStyle.Rgb);

        Assert.Equal(WaveformStyle.Rgb, detail.Style);
        var tag = anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.WaveColorScrollTag>().Single();
        Assert.Equal(tag.Entries.Length, detail.Data.Length);
        Assert.Equal((int)tag.LenEntries, detail.FrameCount);

        // Both scroll waveforms describe the same track, so they must agree on its length.
        var blueDetail = new WaveformDetail(Reference, anlz, WaveformStyle.Blue);
        Assert.Equal(blueDetail.FrameCount, detail.FrameCount);
    }

    [Fact]
    public void WaveformDetailBuildsFromTaggedSection()
    {
        var anlz = ParseFixture("ANLZ0000.EXT");
        var section = anlz.Sections.Single(candidate => candidate.Body is RekordboxAnlz.WaveScrollTag);
        var detail = new WaveformDetail(Reference, section);

        Assert.Equal(WaveformStyle.Blue, detail.Style);
        Assert.Equal(((RekordboxAnlz.WaveScrollTag)section.Body).Entries.Length, detail.Data.Length);

        var colorSection = anlz.Sections.Single(candidate => candidate.Body is RekordboxAnlz.WaveColorScrollTag);
        var colorDetail = new WaveformDetail(Reference, colorSection);
        Assert.Equal(WaveformStyle.Rgb, colorDetail.Style);
    }

    [Fact]
    public void WaveformDetailThrowsWhenRequestedStyleMissing()
    {
        var anlz = ParseFixture("ANLZ0000.DAT");  // The DAT file has no 3-band preview.
        Assert.Throws<InvalidOperationException>(() =>
            _ = new WaveformDetail(Reference, anlz, WaveformStyle.ThreeBand));
    }

    [Fact]
    public void SongStructureTagParsesFromExtFile()
    {
        var anlz = ParseFixture("ANLZ0000.EXT");
        var tag = anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.SongStructureTag>().Single();

        Assert.Equal(24u, tag.LenEntryBytes);
        Assert.True(tag.LenEntries > 0);
        Assert.Equal(tag.LenEntries, (ushort)tag.Body.Entries.Count);
        Assert.True(Enum.IsDefined(tag.Body.Mood));

        // Phrases are numbered from one and start on ascending beats.
        var previousBeat = 0;
        for (var i = 0; i < tag.Body.Entries.Count; i++)
        {
            var entry = tag.Body.Entries[i];
            Assert.Equal(i + 1, entry.Index);
            Assert.True(entry.Beat >= previousBeat);
            previousBeat = entry.Beat;
        }
        Assert.True(tag.Body.EndBeat >= previousBeat);
    }
}
