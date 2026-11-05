using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;
using Kaitai;

namespace BeatLink.Tests.CrateDigger;

/// <summary>
/// Exercises the phrase color, text color, and label lookups of <see cref="Util"/> against the
/// PSSI song structure data in the checked-in ANLZ fixture file.
/// </summary>
public class PhraseColorTests
{
    private static RekordboxAnlz.SongStructureTag LoadFixtureTag()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "anlz", "ANLZ0000.EXT");
        var anlz = new RekordboxAnlz(new KaitaiStream(File.ReadAllBytes(path)));
        return anlz.Sections.Select(section => section.Body).OfType<RekordboxAnlz.SongStructureTag>().Single();
    }

    [Fact]
    public void EveryFixturePhraseResolvesToKnownColorAndLabel()
    {
        var tag = LoadFixtureTag();
        Assert.NotEmpty(tag.Body.Entries);
        foreach (var entry in tag.Body.Entries)
        {
            var label = Util.PhraseLabel(entry);
            Assert.False(label.StartsWith("Unknown", StringComparison.Ordinal),
                $"Phrase {entry.Index} produced unrecognized label '{label}'.");

            var color = Util.PhraseColor(entry);
            Assert.NotEqual(Util.PhraseColor(entry), default);  // Fully opaque colors only.
            Assert.Equal(255, color.A);
            Assert.NotEqual(ArgbColor.White, color);  // White would mean the phrase was unrecognized.
        }
    }

    [Fact]
    public void PhraseTextColorMatchesMood()
    {
        var tag = LoadFixtureTag();
        var expected = tag.Body.Mood == RekordboxAnlz.TrackMood.High ? ArgbColor.White : ArgbColor.Black;
        foreach (var entry in tag.Body.Entries)
        {
            Assert.Equal(expected, Util.PhraseTextColor(entry));
        }
    }

    [Fact]
    public void PhraseColorsAreConsistentWithLabels()
    {
        // Two phrases with the same label must always be painted the same color.
        var tag = LoadFixtureTag();
        var byLabel = new Dictionary<string, ArgbColor>();
        foreach (var entry in tag.Body.Entries)
        {
            var label = Util.PhraseLabel(entry);
            var color = Util.PhraseColor(entry);
            if (byLabel.TryGetValue(label, out var existing))
            {
                Assert.Equal(existing, color);
            }
            else
            {
                byLabel[label] = color;
            }
        }
        Assert.NotEmpty(byLabel);
    }

    [Fact]
    public void LowMoodPhraseLookupsUseLowTables()
    {
        // Build a minimal synthetic song structure tag so we can validate a known mood/phrase
        // combination independent of what the fixture track happens to contain: one low-mood
        // chorus phrase starting at beat 16. The tag payload starts with len_entry_bytes (4 bytes)
        // and len_entries (2 bytes), followed by the 14-byte body header and one 24-byte entry.
        var tagBytes = new byte[6 + 14 + 24];
        tagBytes[3] = 24;      // len_entry_bytes.
        tagBytes[5] = 1;       // len_entries.
        tagBytes[7] = 3;       // mood = low (raw mood of 3 is under the masking threshold of 20).
        tagBytes[15] = 32;     // end beat.
        tagBytes[6 + 14 + 1] = 1;   // Entry index.
        tagBytes[6 + 14 + 3] = 16;  // Entry starting beat.
        tagBytes[6 + 14 + 5] = 9;   // Entry kind: low-mood chorus.
        var tag = new RekordboxAnlz.SongStructureTag(new KaitaiStream(tagBytes));
        var entry = tag.Body.Entries.Single();

        Assert.False(tag.IsMasked);
        Assert.Equal(RekordboxAnlz.TrackMood.Low, tag.Body.Mood);
        Assert.Equal(32, tag.Body.EndBeat);
        Assert.Equal(16, entry.Beat);
        Assert.Equal("Chorus", Util.PhraseLabel(entry));
        Assert.Equal(Util.LowChorusColor, Util.PhraseColor(entry));
        Assert.Equal(ArgbColor.Black, Util.PhraseTextColor(entry));
    }
}
