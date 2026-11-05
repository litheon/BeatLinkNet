using BeatLink;
using BeatLink.Data;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the equality, interning, and derived-value behavior of the simple data value classes.
/// </summary>
public class DataValueClassTests
{
    [Fact]
    public void DataReferenceImplementsValueEquality()
    {
        var a = new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Rekordbox);
        var b = new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Rekordbox);
        Assert.NotSame(a, b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DataReferenceDistinguishesEachComponent()
    {
        var reference = new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Rekordbox);
        Assert.NotEqual(reference, new DataReference(3, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Rekordbox));
        Assert.NotEqual(reference, new DataReference(2, CdjStatus.TrackSourceSlot.SdSlot, 42, CdjStatus.TrackType.Rekordbox));
        Assert.NotEqual(reference, new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 43, CdjStatus.TrackType.Rekordbox));
        Assert.NotEqual(reference, new DataReference(2, CdjStatus.TrackSourceSlot.UsbSlot, 42, CdjStatus.TrackType.Unanalyzed));
    }

    [Fact]
    public void DataReferenceConvenienceConstructorsAssumeRekordbox()
    {
        var slot = SlotReference.GetSlotReference(4, CdjStatus.TrackSourceSlot.SdSlot);
        var fromPlayerAndSlot = new DataReference(4, CdjStatus.TrackSourceSlot.SdSlot, 7);
        var fromSlotReference = new DataReference(slot, 7);
        Assert.Equal(fromPlayerAndSlot, fromSlotReference);
        Assert.Same(CdjStatus.TrackType.Rekordbox, fromSlotReference.TrackType);
        Assert.Same(slot, fromSlotReference.GetSlotReference());
    }

    [Fact]
    public void SlotReferenceCanBeExtractedFromDataReference()
    {
        var reference = new DataReference(3, CdjStatus.TrackSourceSlot.UsbSlot, 12, CdjStatus.TrackType.Unanalyzed);
        var slot = SlotReference.GetSlotReference(reference);
        Assert.Same(SlotReference.GetSlotReference(3, CdjStatus.TrackSourceSlot.UsbSlot), slot);
    }

    [Fact]
    public void DeckReferenceIsInterned()
    {
        var a = DeckReference.GetDeckReference(2, 0);
        var b = DeckReference.GetDeckReference(2, 0);
        Assert.Same(a, b);
        Assert.Equal(2, a.Player);
        Assert.Equal(0, a.HotCue);
    }

    [Fact]
    public void DeckReferenceDistinguishesPlayerAndHotCue()
    {
        var deck = DeckReference.GetDeckReference(2, 1);
        Assert.NotSame(deck, DeckReference.GetDeckReference(2, 2));
        Assert.NotSame(deck, DeckReference.GetDeckReference(3, 1));
        Assert.NotEqual(deck, DeckReference.GetDeckReference(2, 2));
        Assert.NotEqual(deck, DeckReference.GetDeckReference(3, 1));
        Assert.Equal(deck, DeckReference.GetDeckReference(2, 1));
    }

    [Fact]
    public void SearchableItemImplementsValueEquality()
    {
        var a = new SearchableItem(12, "Deep Symmetry");
        var b = new SearchableItem(12, "Deep Symmetry");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new SearchableItem(13, "Deep Symmetry"));
        Assert.NotEqual(a, new SearchableItem(12, "Other"));
        Assert.Equal("SearchableItem[id:12, label:Deep Symmetry]", a.ToString());
    }

    [Fact]
    public void ColorItemDerivesColorAndNameFromId()
    {
        var item = new ColorItem(5, "Green");
        Assert.Equal(5, item.Id);
        Assert.Equal("Green", item.Label);
        Assert.Equal("Green", item.ColorName);
        Assert.Equal(ArgbColor.FromRgb(0, 255, 0), item.Color);
        Assert.False(ColorItem.IsNoColor(item.Color));

        var noColor = new ColorItem(0, "");
        Assert.Equal("No Color", noColor.ColorName);
        Assert.True(ColorItem.IsNoColor(noColor.Color));
    }

    [Fact]
    public void ColorItemAndSearchableItemAreNeverEqual()
    {
        // Java uses getClass() equality, so a ColorItem is never equal to a plain SearchableItem.
        var searchable = new SearchableItem(5, "Green");
        var color = new ColorItem(5, "Green");
        Assert.False(searchable.Equals(color));
        Assert.False(color.Equals(searchable));
        Assert.Equal(color, new ColorItem(5, "Green"));
    }

    [Fact]
    public void PlaybackStateImplementsValueEquality()
    {
        var a = new PlaybackState(3, 12345L, true);
        var b = new PlaybackState(3, 12345L, true);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new PlaybackState(4, 12345L, true));
        Assert.NotEqual(a, new PlaybackState(3, 12346L, true));
        Assert.NotEqual(a, new PlaybackState(3, 12345L, false));
    }

    [Fact]
    public void TrackPositionUpdateReportsBeatWithinBar()
    {
        var reference = new DataReference(1, CdjStatus.TrackSourceSlot.UsbSlot, 1, CdjStatus.TrackType.Rekordbox);
        var grid = new BeatGrid(reference, [1, 2, 3, 4], [12000, 12000, 12000, 12000], [500L, 1000L, 1500L, 2000L]);
        var update = new TrackPositionUpdate(123L, 1100L, 2, true, true, 1.0, false, grid);
        Assert.Equal(2, update.BeatWithinBar);
        Assert.False(update.Precise);
        Assert.False(update.FromBeat);

        var noGrid = new TrackPositionUpdate(123L, 1100L, 2, true, true, 1.0, false, null, true, true);
        Assert.Equal(0, noGrid.BeatWithinBar);
        Assert.True(noGrid.Precise);
        Assert.True(noGrid.FromBeat);
    }
}
