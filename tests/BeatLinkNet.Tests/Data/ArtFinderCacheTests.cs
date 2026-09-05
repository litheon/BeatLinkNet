using BeatLink.Data;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the second-level "clock" (second-chance) artwork cache of the <see cref="ArtFinder"/>
/// through its internal test seams, without any network activity.
/// </summary>
public sealed class ArtFinderCacheTests : IDisposable
{
    private static ArtFinder Finder => ArtFinder.Instance;

    public ArtFinderCacheTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    private static void ResetState()
    {
        Finder.ClearArtCacheForTests();
        Finder.ArtCacheSize = ArtFinder.DefaultArtCacheSize;
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    private static DataReference ArtReference(int id) =>
        new(2, CdjStatus.TrackSourceSlot.UsbSlot, id, CdjStatus.TrackType.Rekordbox);

    private static AlbumArt MakeArt(int id) =>
        new(ArtReference(id), new byte[] { (byte)id, 0x42 });

    [Fact]
    public void HasExpectedDefaultsAndValidatesSize()
    {
        Assert.Equal(100, ArtFinder.DefaultArtCacheSize);
        Assert.Equal(ArtFinder.DefaultArtCacheSize, Finder.ArtCacheSize);
        Assert.True(Finder.RequestHighResolutionArt);
        Assert.Throws<ArgumentOutOfRangeException>(() => Finder.ArtCacheSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Finder.ArtCacheSize = -3);
    }

    [Fact]
    public void EvictsOldestUnusedArtWhenFull()
    {
        Finder.ArtCacheSize = 3;
        Finder.AddArtToCacheForTests(ArtReference(1), MakeArt(1));
        Finder.AddArtToCacheForTests(ArtReference(2), MakeArt(2));
        Finder.AddArtToCacheForTests(ArtReference(3), MakeArt(3));
        Assert.Equal(3, Finder.ArtCacheCountForTests);

        // Nothing has been used, so adding a fourth image evicts the oldest (1).
        Finder.AddArtToCacheForTests(ArtReference(4), MakeArt(4));
        Assert.Equal(3, Finder.ArtCacheCountForTests);
        Assert.False(Finder.IsArtInCacheForTests(ArtReference(1)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(2)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(3)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(4)));
    }

    [Fact]
    public void RecentlyUsedArtGetsSecondChance()
    {
        Finder.ArtCacheSize = 3;
        Finder.AddArtToCacheForTests(ArtReference(1), MakeArt(1));
        Finder.AddArtToCacheForTests(ArtReference(2), MakeArt(2));
        Finder.AddArtToCacheForTests(ArtReference(3), MakeArt(3));

        // Look up the oldest image, marking it recently used.
        Assert.NotNull(Finder.FindArtInMemoryCachesForTests(ArtReference(1)));

        // Adding a fourth image must skip over the used image (moving it to the back of the
        // eviction queue and clearing its used mark) and evict the oldest unused one instead.
        Finder.AddArtToCacheForTests(ArtReference(4), MakeArt(4));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(1)));
        Assert.False(Finder.IsArtInCacheForTests(ArtReference(2)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(3)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(4)));

        // Image 1 was moved to the back of the queue (behind 3 and 4), so the next addition
        // evicts the oldest remaining unused entry, 3.
        Finder.AddArtToCacheForTests(ArtReference(5), MakeArt(5));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(1)));
        Assert.False(Finder.IsArtInCacheForTests(ArtReference(3)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(4)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(5)));

        // The second chance was spent when it was granted (the used mark is cleared), so image 1,
        // now at the front of the queue again, is evicted by the next addition.
        Finder.AddArtToCacheForTests(ArtReference(6), MakeArt(6));
        Assert.False(Finder.IsArtInCacheForTests(ArtReference(1)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(4)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(5)));
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(6)));
    }

    [Fact]
    public void ShrinkingCacheSizeEvictsImmediately()
    {
        Finder.ArtCacheSize = 4;
        for (var id = 1; id <= 4; id++)
        {
            Finder.AddArtToCacheForTests(ArtReference(id), MakeArt(id));
        }

        // Shrinking the limit discards the oldest unused entries right away.
        Finder.ArtCacheSize = 1;
        Assert.Equal(1, Finder.ArtCacheCountForTests);
        Assert.True(Finder.IsArtInCacheForTests(ArtReference(4)));
    }

    [Fact]
    public void CacheHitsReturnTheCachedInstance()
    {
        var art = MakeArt(9);
        Finder.AddArtToCacheForTests(ArtReference(9), art);
        Assert.Same(art, Finder.FindArtInMemoryCachesForTests(ArtReference(9)));
        Assert.Null(Finder.FindArtInMemoryCachesForTests(ArtReference(10)));
    }
}
