using System.Net;
using System.Net.Sockets;
using BeatLink.Data;
using BeatLink.DbServer;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the dbserver request construction of the <see cref="WaveformFinder"/> against a
/// scripted fake dbserver on a loopback port: the style-specific ANLZ tag requests, the fallback
/// ladder down to the classic blue requests, and the parsing of the returned waveforms. Shares
/// the "Network services" collection because it mutates the <see cref="WaveformFinder"/>
/// singleton's preferred style.
/// </summary>
[Collection("Network services")]
public sealed class WaveformFinderTests : IDisposable
{
    private static WaveformFinder Finder => WaveformFinder.Instance;

    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(15);

    private const int RekordboxId = 42;

    private static SlotReference Slot => SlotReference.GetSlotReference(2, CdjStatus.TrackSourceSlot.UsbSlot);

    public WaveformFinderTests()
    {
        Finder.PreferredStyle = WaveformStyle.Rgb;
    }

    public void Dispose()
    {
        Finder.PreferredStyle = WaveformStyle.Rgb;
    }

    /// <summary>
    /// Starts a fake dbserver on an ephemeral loopback port which accepts a single connection and
    /// runs the supplied script against it, recording every message it reads.
    /// </summary>
    private static (int Port, Task<List<Message>> Log) StartFakeDbServer(Action<Stream, List<Message>> script)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(() =>
        {
            var log = new List<Message>();
            try
            {
                using var socket = listener.AcceptSocket();
                using var stream = new NetworkStream(socket, ownsSocket: false);

                // Serve the greeting and setup exchanges.
                var greeting = Assert.IsType<NumberField>(Field.Read(stream));
                Assert.Equal(1, greeting.Value);
                new NumberField(1, 4).Write(stream);
                var setup = Message.Read(stream);
                log.Add(setup);
                new Message(0xfffffffeL, Message.KnownType.MenuAvailable,
                    new NumberField(Message.KnownType.SetupReq.ProtocolValue),
                    new NumberField(2)).Write(stream);

                script(stream, log);
            }
            finally
            {
                listener.Stop();
            }
            return log;
        });
        return (port, task);
    }

    private static Message ReadAndLog(Stream stream, List<Message> log)
    {
        var message = Message.Read(stream);
        log.Add(message);
        return message;
    }

    /// <summary>
    /// Writes an ANLZ_TAG (or WAVE_PREVIEW/WAVE_DETAIL) style response whose fourth argument is
    /// the supplied binary payload.
    /// </summary>
    private static void WriteBinaryResponse(Stream stream, Message request, Message.KnownType type, byte[] payload) =>
        new Message(request.Transaction.Value, type,
            new NumberField(request.MessageType.Value), NumberField.Word0,
            new NumberField(payload.Length), new BinaryField(payload), NumberField.Word1).Write(stream);

    /// <summary>
    /// Writes an UNAVAILABLE response, which makes the finder fall back to the next request in
    /// its ladder.
    /// </summary>
    private static void WriteUnavailable(Stream stream, Message request) =>
        new Message(request.Transaction.Value, Message.KnownType.Unavailable,
            new NumberField(request.MessageType.Value)).Write(stream);

    private static Client ConnectClient(int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(IPAddress.Loopback, port);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new Client(socket, targetPlayer: 2, posingAsPlayer: 5);
    }

    private static List<Message> AwaitLog(Task<List<Message>> log)
    {
        Assert.True(log.Wait(ServerTimeout), "fake dbserver script did not complete");
        return log.Result;
    }

    /// <summary>
    /// A payload for a color (RGB) waveform: the junk bytes the dbserver prepends, followed by
    /// twelve data bytes.
    /// </summary>
    private static byte[] RgbPayload()
    {
        var payload = new byte[WaveformPreview.LeadingDbserverColorJunkBytes + 12];
        for (var i = 0; i < 12; i++)
        {
            payload[WaveformPreview.LeadingDbserverColorJunkBytes + i] = (byte)i;
        }
        return payload;
    }

    [Fact]
    public void RequestsRgbPreviewViaAnlzTagWhenPreferred()
    {
        Finder.PreferredStyle = WaveformStyle.Rgb;
        var (port, log) = StartFakeDbServer((stream, messages) =>
        {
            var request = ReadAndLog(stream, messages);
            WriteBinaryResponse(stream, request, Message.KnownType.AnlzTag, RgbPayload());
            ReadAndLog(stream, messages);  // Teardown.
        });

        WaveformPreview? preview;
        using (var client = ConnectClient(port))
        {
            preview = Finder.GetWaveformPreview(RekordboxId, Slot, CdjStatus.TrackType.Rekordbox, null, client);
        }
        var messages = AwaitLog(log);

        // The request asked for the color waveform preview tag from the EXT file.
        var tagRequest = messages[1];
        Assert.Equal(Message.KnownType.AnlzTagReq, tagRequest.RecognizedType);
        Assert.Equal(0x05010301L, ((NumberField)tagRequest.Arguments[0]).Value);  // R:M:S:T (main menu).
        Assert.Equal(RekordboxId, ((NumberField)tagRequest.Arguments[1]).Value);
        Assert.Equal(Message.AnlzFileTagColorWaveformPreview, ((NumberField)tagRequest.Arguments[2]).Value);
        Assert.Equal(Message.AlnzFileTypeExt, ((NumberField)tagRequest.Arguments[3]).Value);

        // The junk bytes were skipped and the RGB data interpreted (six bytes per segment).
        Assert.NotNull(preview);
        Assert.Equal(WaveformStyle.Rgb, preview.Style);
        Assert.Equal(2, preview.SegmentCount);
        Assert.True(preview.Data.Span.SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }));
    }

    [Fact]
    public void FallsBackToBluePreviewWhenColorUnavailable()
    {
        Finder.PreferredStyle = WaveformStyle.Rgb;
        var blueData = new byte[] { 0x1f, 0x07, 0x10, 0x03 };
        var (port, log) = StartFakeDbServer((stream, messages) =>
        {
            var colorRequest = ReadAndLog(stream, messages);
            WriteUnavailable(stream, colorRequest);
            var blueRequest = ReadAndLog(stream, messages);
            WriteBinaryResponse(stream, blueRequest, Message.KnownType.WavePreview, blueData);
            ReadAndLog(stream, messages);  // Teardown.
        });

        WaveformPreview? preview;
        using (var client = ConnectClient(port))
        {
            preview = Finder.GetWaveformPreview(RekordboxId, Slot, CdjStatus.TrackType.Rekordbox, null, client);
        }
        var messages = AwaitLog(log);

        // After the unavailable ANLZ tag, the classic blue request was sent to the DATA menu.
        Assert.Equal(Message.KnownType.AnlzTagReq, messages[1].RecognizedType);
        var blue = messages[2];
        Assert.Equal(Message.KnownType.WavePreviewReq, blue.RecognizedType);
        Assert.Equal(0x05080301L, ((NumberField)blue.Arguments[0]).Value);  // R:M:S:T (data menu).
        Assert.Equal(1, ((NumberField)blue.Arguments[1]).Value);
        Assert.Equal(RekordboxId, ((NumberField)blue.Arguments[2]).Value);
        Assert.Equal(0, ((NumberField)blue.Arguments[3]).Value);

        Assert.NotNull(preview);
        Assert.Equal(WaveformStyle.Blue, preview.Style);
        Assert.Equal(2, preview.SegmentCount);  // Two bytes per blue segment, no junk skipping.
        Assert.True(preview.Data.Span.SequenceEqual(blueData));
    }

    [Fact]
    public void RequestsThreeBandPreviewViaAnlzTagWhenPreferred()
    {
        Finder.PreferredStyle = WaveformStyle.ThreeBand;
        var (port, log) = StartFakeDbServer((stream, messages) =>
        {
            var request = ReadAndLog(stream, messages);
            WriteBinaryResponse(stream, request, Message.KnownType.AnlzTag, RgbPayload());
            ReadAndLog(stream, messages);  // Teardown.
        });

        WaveformPreview? preview;
        using (var client = ConnectClient(port))
        {
            preview = Finder.GetWaveformPreview(RekordboxId, Slot, CdjStatus.TrackType.Rekordbox, null, client);
        }
        var messages = AwaitLog(log);

        // The request asked for the 3-band waveform preview tag from the 2EX file.
        var tagRequest = messages[1];
        Assert.Equal(Message.KnownType.AnlzTagReq, tagRequest.RecognizedType);
        Assert.Equal(Message.AnlzFileTag3BandWaveformPreview, ((NumberField)tagRequest.Arguments[2]).Value);
        Assert.Equal(Message.AlnzFileType2Ex, ((NumberField)tagRequest.Arguments[3]).Value);

        Assert.NotNull(preview);
        Assert.Equal(WaveformStyle.ThreeBand, preview.Style);
        Assert.Equal(4, preview.SegmentCount);  // Three bytes per 3-band segment.
    }

    [Fact]
    public void RequestsRgbDetailAndFallsBackToBlueDetail()
    {
        Finder.PreferredStyle = WaveformStyle.Rgb;
        var (port, log) = StartFakeDbServer((stream, messages) =>
        {
            // First serve a successful color detail request.
            var colorRequest = ReadAndLog(stream, messages);
            WriteBinaryResponse(stream, colorRequest, Message.KnownType.AnlzTag, RgbPayload());

            // Then serve a second query where color is unavailable so the blue path is used.
            var secondColorRequest = ReadAndLog(stream, messages);
            WriteUnavailable(stream, secondColorRequest);
            var blueRequest = ReadAndLog(stream, messages);
            var bluePayload = new byte[WaveformDetail.LeadingDbserverJunkBytes + 4];
            bluePayload[WaveformDetail.LeadingDbserverJunkBytes] = 0x25;
            WriteBinaryResponse(stream, blueRequest, Message.KnownType.WaveDetail, bluePayload);

            ReadAndLog(stream, messages);  // Teardown.
        });

        WaveformDetail? colorDetail;
        WaveformDetail? blueDetail;
        using (var client = ConnectClient(port))
        {
            colorDetail = Finder.GetWaveformDetail(RekordboxId, Slot, CdjStatus.TrackType.Rekordbox, null, client);
            blueDetail = Finder.GetWaveformDetail(RekordboxId, Slot, CdjStatus.TrackType.Rekordbox, null, client);
        }
        var messages = AwaitLog(log);

        // The color detail request named the color waveform detail tag in the EXT file.
        var tagRequest = messages[1];
        Assert.Equal(Message.KnownType.AnlzTagReq, tagRequest.RecognizedType);
        Assert.Equal(0x05010301L, ((NumberField)tagRequest.Arguments[0]).Value);  // R:M:S:T (main menu).
        Assert.Equal(Message.AnlzFileTagColorWaveformDetail, ((NumberField)tagRequest.Arguments[2]).Value);
        Assert.Equal(Message.AlnzFileTypeExt, ((NumberField)tagRequest.Arguments[3]).Value);
        Assert.NotNull(colorDetail);
        Assert.Equal(WaveformStyle.Rgb, colorDetail.Style);
        Assert.Equal(6, colorDetail.FrameCount);  // Two bytes per RGB detail frame.

        // The fallback sent the classic WAVE_DETAIL_REQ to the main menu.
        var blue = messages[3];
        Assert.Equal(Message.KnownType.WaveDetailReq, blue.RecognizedType);
        Assert.Equal(0x05010301L, ((NumberField)blue.Arguments[0]).Value);
        Assert.Equal(RekordboxId, ((NumberField)blue.Arguments[1]).Value);
        Assert.Equal(0, ((NumberField)blue.Arguments[2]).Value);
        Assert.NotNull(blueDetail);
        Assert.Equal(WaveformStyle.Blue, blueDetail.Style);
        Assert.Equal(4, blueDetail.FrameCount);  // One byte per blue detail frame.
    }

    [Fact]
    public void PreferredStyleDefaultsToRgb()
    {
        Assert.Equal(WaveformStyle.Rgb, Finder.PreferredStyle);
        Assert.True(Finder.IsFindingDetails);
    }
}
