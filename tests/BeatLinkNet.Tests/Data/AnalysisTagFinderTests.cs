using System.Collections.Concurrent;
using System.Net;
using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;
using BeatLink.DbServer;
using Kaitai;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the <see cref="AnalysisTagFinder"/>: the four-character-code protocol number
/// conversion (cross-checked against the constants in <see cref="Message"/>), keyed listener
/// registration, and end-to-end tag delivery from a fake metadata provider driven by synthetic
/// CDJ status injections. All network services run on loopback override seams.
/// </summary>
[Collection("Network services")]
public sealed class AnalysisTagFinderTests : IDisposable
{
    private static AnalysisTagFinder Finder => AnalysisTagFinder.Instance;

    private readonly FinderTestHelpers.FakeMetadataProvider _provider = new();

    public AnalysisTagFinderTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        MetadataFinder.Instance.RemoveMetadataProvider(_provider);
        ResetState();
    }

    private static void ResetState()
    {
        Finder.Stop();
        MetadataFinder.Instance.Stop();
        MetadataFinder.Instance.IsPassive = false;
        MetadataFinder.Instance.ClearMountsForTests();
        ConnectionManager.Instance.Stop();
        DeviceFinder.Instance.Stop();
        DeviceFinder.Instance.PortOverride = null;
        DeviceFinder.Instance.RemoveIgnoredAddress(IPAddress.Loopback);
        ConnectionManager.Instance.DbServerQueryPortOverride = null;
        VirtualCdj.Instance.ResetForTests();
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Builds a parsed PWV5 (color waveform scroll) tagged section whose entries are the supplied
    /// bytes, in the layout the ANLZ file format uses: the four-character code, the header and
    /// tag lengths, then the body (entry size, entry count, an unknown word, and the entries).
    /// </summary>
    internal static RekordboxAnlz.TaggedSection MakePwv5Section(byte[] entries)
    {
        var body = new byte[12 + entries.Length];
        Util.NumberToBytes(2, body, 0, 4);                    // Bytes per entry.
        Util.NumberToBytes(entries.Length / 2, body, 4, 4);   // Number of entries.
        Util.NumberToBytes(0, body, 8, 4);                    // Unknown.
        entries.CopyTo(body, 12);

        var bytes = new byte[12 + body.Length];
        "PWV5"u8.ToArray().CopyTo(bytes, 0);                  // Four-character code.
        Util.NumberToBytes(24, bytes, 4, 4);                  // Header length.
        Util.NumberToBytes(12 + body.Length, bytes, 8, 4);    // Total tag length.
        body.CopyTo(bytes, 12);
        return new RekordboxAnlz.TaggedSection(new KaitaiStream(bytes));
    }

    [Fact]
    public void StringToProtocolNumberMatchesKnownProtocolConstants()
    {
        // These byte-reversed values are the documented protocol constants, so they cross-check
        // the conversion algorithm.
        Assert.Equal(Message.AlnzFileTypeExt, Finder.StringToProtocolNumber("EXT").Value);
        Assert.Equal(Message.AlnzFileTypeDat, Finder.StringToProtocolNumber("DAT").Value);
        Assert.Equal(Message.AlnzFileType2Ex, Finder.StringToProtocolNumber("2EX").Value);
        Assert.Equal(Message.AnlzFileTagSongStructure, Finder.StringToProtocolNumber("PSSI").Value);
        Assert.Equal(Message.AnlzFileTagCueComment, Finder.StringToProtocolNumber("PCO2").Value);
    }

    [Fact]
    public void ListenerRegistrationIsKeyedByTagAndExtension()
    {
        EventHandler<AnalysisTagUpdate> handler = (_, _) => { };
        EventHandler<AnalysisTagUpdate> other = (_, _) => { };
        try
        {
            Finder.AddAnalysisTagListener(handler, ".EXT", "PSSI");
            Finder.AddAnalysisTagListener(handler, ".EXT", "PSSI");  // Duplicate is a no-op.
            Finder.AddAnalysisTagListener(other, ".DAT", "PCOB");

            var listeners = Finder.TagListeners;
            Assert.Equal(2, listeners.Count);
            Assert.Contains(handler, listeners["PSSI.EXT"]);
            Assert.Single(listeners["PSSI.EXT"]);
            Assert.Contains(other, listeners["PCOB.DAT"]);

            // Removing under the wrong key does nothing; the right key clears the entry.
            Finder.RemoveAnalysisTagListener(handler, ".DAT", "PSSI");
            Assert.Contains("PSSI.EXT", Finder.TagListeners.Keys);
            Finder.RemoveAnalysisTagListener(handler, ".EXT", "PSSI");
            Assert.DoesNotContain("PSSI.EXT", Finder.TagListeners.Keys);
        }
        finally
        {
            Finder.RemoveAnalysisTagListener(handler, ".EXT", "PSSI");
            Finder.RemoveAnalysisTagListener(other, ".DAT", "PCOB");
        }
    }

    [Fact]
    public void DeliversTagsFromRegisteredProviderAndReportsLossOnTrackUnload()
    {
        using var sink = new FinderTestHelpers.ConnectionCountingSink();
        using var query = new FinderTestHelpers.FakeQueryServer(sink.Port);

        // Start the whole chain on loopback overrides.
        DeviceFinder.Instance.PortOverride = 0;
        ConnectionManager.Instance.DbServerQueryPortOverride = query.Port;
        VirtualCdj.Instance.SetRunningStateForTests(true);
        Finder.Start();
        Assert.True(Finder.IsRunning);
        Assert.True(MetadataFinder.Instance.IsRunning);

        ServiceTestHelpers.SendToLoopback(DeviceFinder.Instance.LocalPort!.Value,
            ServiceTestHelpers.KeepAlivePacket("CDJ-3000", FinderTestHelpers.Player));
        FinderTestHelpers.WaitUntil(
            () => ConnectionManager.Instance.GetPlayerDBServerPort(FinderTestHelpers.Player) == sink.Port,
            "the fake dbserver port to be cached");

        // Mount the USB slot and record its media details.
        VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithoutTrack());
        FinderTestHelpers.WaitUntil(
            () => MetadataFinder.Instance.MountedMediaSlots.Contains(FinderTestHelpers.UsbSlot),
            "the USB slot to be recorded as mounted");
        var details = FinderTestHelpers.UsbMediaDetails();
        VirtualCdj.Instance.DeliverMediaDetailsUpdate(details);
        FinderTestHelpers.WaitUntil(
            () => MetadataFinder.Instance.GetMediaDetailsFor(FinderTestHelpers.UsbSlot) != null,
            "the media details to be recorded");

        // Register a provider offering both the metadata and the analysis section.
        var metadata = new TrackMetadata(FinderTestHelpers.TrackReference,
            CdjStatus.TrackType.Rekordbox, [], null);
        var section = MakePwv5Section([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        _provider.Media.Add(details);
        _provider.Metadata = metadata;
        _provider.AnalysisSection = section;
        MetadataFinder.Instance.AddMetadataProvider(_provider);

        var received = new ConcurrentQueue<AnalysisTagUpdate>();
        EventHandler<AnalysisTagUpdate> handler = (_, update) => received.Enqueue(update);
        Finder.AddAnalysisTagListener(handler, ".EXT", "PWV5");
        try
        {
            // Loading the track flows: status -> metadata (from provider) -> analysis tag
            // (from provider) -> keyed listener delivery.
            VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithTrack());
            FinderTestHelpers.WaitUntil(
                () => received.Any(u => u.TaggedSection != null),
                "the analysis tag to be delivered");

            var update = received.First(u => u.TaggedSection != null);
            Assert.Equal(FinderTestHelpers.Player, update.Player);
            Assert.Equal(".EXT", update.FileExtension);
            Assert.Equal("PWV5", update.TypeTag);
            Assert.Same(section, update.TaggedSection);

            // The provider was asked for exactly the registered tag type, and the section is now
            // in the hot cache.
            Assert.Contains(_provider.AnalysisRequests,
                r => r.Track.Equals(FinderTestHelpers.TrackReference) && r.Extension == ".EXT" && r.TypeTag == "PWV5");
            Assert.Same(section,
                Finder.GetLatestTrackAnalysisFor(FinderTestHelpers.Player, ".EXT", "PWV5"));
            Assert.Same(section,
                Finder.RequestAnalysisTagFrom(FinderTestHelpers.TrackReference, ".EXT", "PWV5"));

            // No dbserver conversation was needed for any of this.
            Assert.Equal(0, sink.ConnectionCount);

            // Unloading the track reports the loss of the tag.
            VirtualCdj.Instance.ProcessUpdate(FinderTestHelpers.StatusWithoutTrack());
            FinderTestHelpers.WaitUntil(
                () => received.Any(u => u.TaggedSection == null),
                "the tag loss to be delivered");
            Assert.Null(Finder.GetLatestTrackAnalysisFor(FinderTestHelpers.Player, ".EXT", "PWV5"));
        }
        finally
        {
            Finder.RemoveAnalysisTagListener(handler, ".EXT", "PWV5");
        }
    }
}
