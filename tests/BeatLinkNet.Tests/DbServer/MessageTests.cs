using BeatLink.DbServer;

namespace BeatLink.Tests;

public class MessageTests
{
    private static byte[] Serialize(Message message)
    {
        using var stream = new MemoryStream();
        message.Write(stream);
        return stream.ToArray();
    }

    private static Message RoundTrip(Message message)
    {
        using var stream = new MemoryStream(Serialize(message));
        var result = Message.Read(stream);
        Assert.Equal(stream.Length, stream.Position);
        return result;
    }

    [Fact]
    public void MessageSerializationIsByteExact()
    {
        var message = new Message(5, Message.KnownType.CueListReq,
            new NumberField(0x05010301L), new NumberField(42));
        byte[] expected =
        [
            0x11, 0x87, 0x23, 0x49, 0xae,             // Magic message-start number field.
            0x11, 0x00, 0x00, 0x00, 0x05,             // Transaction, 4 bytes.
            0x10, 0x21, 0x04,                         // Message type, 2 bytes.
            0x0f, 0x02,                               // Argument count, 1 byte.
            0x14, 0x00, 0x00, 0x00, 0x0c,             // Argument-type blob, always 12 bytes...
            0x06, 0x06, 0x00, 0x00, 0x00, 0x00,       // ...two number tags, zero padded.
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x11, 0x05, 0x01, 0x03, 0x01,             // First argument.
            0x11, 0x00, 0x00, 0x00, 0x2a              // Second argument.
        ];
        Assert.Equal(expected, Serialize(message));
    }

    [Fact]
    public void MessageWithMixedArgumentsRoundTrips()
    {
        var message = new Message(0x1234, Message.KnownType.MenuItem.ProtocolValue,
            new NumberField(1), new NumberField(2), new NumberField(10),
            new StringField("Title"), NumberField.Word0, new StringField(""),
            new NumberField(0x0004), new NumberField(0), new BinaryField([]));
        var read = RoundTrip(message);
        Assert.Equal(0x1234, read.Transaction.Value);
        Assert.Equal(Message.KnownType.MenuItem, read.RecognizedType);
        Assert.Equal(9, read.ArgumentCount.Value);
        Assert.Equal(9, read.Arguments.Count);
        Assert.Equal("Title", Assert.IsType<StringField>(read.Arguments[3]).Value);
        Assert.Equal(Serialize(message), Serialize(read));
    }

    [Fact]
    public void EmptyBinaryArgumentAfterZeroNumberIsSynthesizedOnRead()
    {
        var message = new Message(7, Message.KnownType.BeatGrid,
            new NumberField(0x2204), NumberField.Word0, new BinaryField([]));
        var serialized = Serialize(message);

        // The empty binary field contributes no bytes at all: magic(5) + transaction(5) + type(3) +
        // arg count(2) + tag blob(17) + two 4-byte number arguments (5 each) and nothing more.
        Assert.Equal(42, serialized.Length);
        // But its argument tag still appears in the tag blob.
        Assert.Equal(0x06, serialized[20]);
        Assert.Equal(0x06, serialized[21]);
        Assert.Equal(0x03, serialized[22]);

        var read = RoundTrip(message);
        Assert.Equal(3, read.Arguments.Count);
        var blob = Assert.IsType<BinaryField>(read.Arguments[2]);
        Assert.Equal(0, blob.Size);
        Assert.Empty(blob.GetValueAsArray());
    }

    [Fact]
    public void NonEmptyBinaryArgumentAfterNonZeroNumberIsReadNormally()
    {
        var message = new Message(8, Message.KnownType.BeatGrid,
            new NumberField(0x2204), new NumberField(4), new BinaryField([0xca, 0xfe, 0xba, 0xbe]));
        var read = RoundTrip(message);
        var blob = Assert.IsType<BinaryField>(read.Arguments[2]);
        Assert.Equal(new byte[] { 0xca, 0xfe, 0xba, 0xbe }, blob.GetValueAsArray());
    }

    [Fact]
    public void MessagesRejectMoreThanTwelveArguments()
    {
        var arguments = Enumerable.Range(0, 13).Select(Field (i) => new NumberField(i)).ToArray();
        Assert.Throws<ArgumentException>(() => new Message(1, 0x1000, arguments));
    }

    [Fact]
    public void MessagesEnforceHeaderFieldSizes()
    {
        Assert.Throws<ArgumentException>(() =>
            new Message(new NumberField(1, 2), new NumberField(0x1000, 2)));
        Assert.Throws<ArgumentException>(() =>
            new Message(new NumberField(1, 4), new NumberField(0x1000, 4)));
    }

    [Fact]
    public void ReadRejectsWrongMagicNumber()
    {
        using var stream = new MemoryStream([0x11, 0x87, 0x23, 0x49, 0xaf]);
        var e = Assert.Throws<IOException>(() => Message.Read(stream));
        Assert.Contains("wrong value to start message", e.Message);
    }

    [Fact]
    public void ReadRejectsWrongStartFieldType()
    {
        using var stream = new MemoryStream([0x10, 0x87, 0x23]);
        var e = Assert.Throws<IOException>(() => Message.Read(stream));
        Assert.Contains("must be of size 4", e.Message);
    }

    [Fact]
    public void GetMenuResultsCountReportsAvailableItems()
    {
        var response = new Message(3, Message.KnownType.MenuAvailable,
            new NumberField(0x1004), new NumberField(250));
        Assert.Equal(250, response.GetMenuResultsCount());
    }

    [Fact]
    public void GetMenuResultsCountReportsNoResultsSentinel()
    {
        var response = new Message(3, Message.KnownType.MenuAvailable,
            new NumberField(0x1004), new NumberField(0xffffffffL));
        Assert.Equal(Message.NoMenuResultsAvailable, response.GetMenuResultsCount());
    }

    [Fact]
    public void GetMenuResultsCountRejectsOtherMessageTypes()
    {
        var response = new Message(3, Message.KnownType.MenuHeader);
        Assert.Throws<InvalidOperationException>(() => response.GetMenuResultsCount());
    }

    private static Message BuildMenuItem(long itemTypeValue) =>
        new(9, Message.KnownType.MenuItem,
            new NumberField(0), new NumberField(1), new NumberField(10), new StringField("Label 1"),
            new NumberField(0), new StringField(""), new NumberField(itemTypeValue));

    [Fact]
    public void GetMenuItemTypeMasksHighBytes()
    {
        // Real players sometimes send junk in the high bytes of the menu item type argument.
        Assert.Equal(Message.MenuItemType.TrackTitle, BuildMenuItem(0xbeef0004L).GetMenuItemType());
        Assert.Equal(Message.MenuItemType.AlbumTitle, BuildMenuItem(0x00000002L).GetMenuItemType());
        Assert.Equal(Message.MenuItemType.All, BuildMenuItem(0xdead00a0L).GetMenuItemType());
    }

    [Fact]
    public void GetMenuItemTypeReturnsUnknownForUnrecognizedValues()
    {
        Assert.Equal(Message.MenuItemType.Unknown, BuildMenuItem(0x7777).GetMenuItemType());
    }

    [Fact]
    public void GetMenuItemTypeRejectsOtherMessageTypes()
    {
        var response = new Message(3, Message.KnownType.MenuFooter);
        Assert.Throws<InvalidOperationException>(() => response.GetMenuItemType());
    }

    [Theory]
    [InlineData(0x0000L, "SETUP_REQ")]
    [InlineData(0x0100L, "TEARDOWN_REQ")]
    [InlineData(0x2c04L, "ANLZ_TAG_REQ")]
    [InlineData(0x3000L, "RENDER_MENU_REQ")]
    [InlineData(0x4000L, "MENU_AVAILABLE")]
    [InlineData(0x4101L, "MENU_ITEM")]
    [InlineData(0x4f02L, "ANLZ_TAG")]
    public void KnownTypesCanBeLookedUpByProtocolValue(long protocolValue, string expectedName)
    {
        var type = Message.KnownType.ProtocolValueMap[protocolValue];
        Assert.Equal(expectedName, type.Name);
        Assert.Equal(protocolValue, type.ProtocolValue);
    }

    [Fact]
    public void UnknownMessageTypeYieldsNullRecognizedType()
    {
        var message = new Message(1, 0x7654);
        Assert.Null(message.RecognizedType);
        Assert.Equal(0x7654, message.MessageType.Value);
    }

    [Fact]
    public void KnownTypeDescribesArguments()
    {
        Assert.Equal("requesting player", Message.KnownType.SetupReq.DescribeArgument(0));
        Assert.Equal("unknown", Message.KnownType.SetupReq.DescribeArgument(1));
        Assert.Equal("unknown", Message.KnownType.SetupReq.DescribeArgument(-1));
        Assert.Equal(["request type", "# items available"], Message.KnownType.MenuAvailable.Arguments);
    }

    [Theory]
    [InlineData(0x0001L, "FOLDER")]
    [InlineData(0x0704L, "TRACK_TITLE_AND_ARTIST")]
    [InlineData(0x2e04L, "TRACK_TITLE_AND_DATE_ADDED")]
    [InlineData(-1L, "UNKNOWN")]
    public void MenuItemTypesCanBeLookedUpByProtocolValue(long protocolValue, string expectedName)
    {
        Assert.Equal(expectedName, Message.MenuItemType.ProtocolValueMap[protocolValue].Name);
    }

    [Fact]
    public void EnumRegistriesAreComplete()
    {
        Assert.Equal(69, Message.KnownType.Values.Count);
        Assert.Equal(69, Message.KnownType.ProtocolValueMap.Count);
        Assert.Equal(66, Message.MenuItemType.Values.Count);
        Assert.Equal(66, Message.MenuItemType.ProtocolValueMap.Count);
        Assert.Equal(5, Message.MenuIdentifier.Values.Count);
    }

    [Fact]
    public void MenuIdentifiersHaveExpectedProtocolValues()
    {
        Assert.Equal(1, Message.MenuIdentifier.MainMenu.ProtocolValue);
        Assert.Equal(2, Message.MenuIdentifier.SubMenu.ProtocolValue);
        Assert.Equal(3, Message.MenuIdentifier.TrackInfo.ProtocolValue);
        Assert.Equal(5, Message.MenuIdentifier.SortMenu.ProtocolValue);
        Assert.Equal(8, Message.MenuIdentifier.Data.ProtocolValue);
        Assert.Equal(Message.MenuIdentifier.Data, Message.MenuIdentifier.ProtocolValueMap[8]);
    }

    [Fact]
    public void AnlzConstantsMatchTheJavaLibrary()
    {
        Assert.Equal(0x00544144, Message.AlnzFileTypeDat);
        Assert.Equal(0x00545845, Message.AlnzFileTypeExt);
        Assert.Equal(0x00584532, Message.AlnzFileType2Ex);
        Assert.Equal(0x34565750, Message.AnlzFileTagColorWaveformPreview);
        Assert.Equal(0x35565750, Message.AnlzFileTagColorWaveformDetail);
        Assert.Equal(0x36565750, Message.AnlzFileTag3BandWaveformPreview);
        Assert.Equal(0x37565750, Message.AnlzFileTag3BandWaveformDetail);
        Assert.Equal(0x49535350, Message.AnlzFileTagSongStructure);
        Assert.Equal(0x324f4350, Message.AnlzFileTagCueComment);
    }

    [Fact]
    public void BuildRmstPacksAllFourBytes()
    {
        var field = Client.BuildRmst(0x09, Message.MenuIdentifier.MainMenu,
            CdjStatus.TrackSourceSlot.UsbSlot, CdjStatus.TrackType.Rekordbox);
        Assert.Equal(0x09010301L, field.Value);
        Assert.Equal(4, field.Size);
        Assert.Equal(new byte[] { 0x11, 0x09, 0x01, 0x03, 0x01 }, field.GetBytes().ToArray());
    }

    [Fact]
    public void BuildRmstDefaultsTrackTypeToRekordbox()
    {
        var field = Client.BuildRmst(2, Message.MenuIdentifier.Data, CdjStatus.TrackSourceSlot.SdSlot);
        Assert.Equal(0x02080201L, field.Value);
    }

    [Fact]
    public void BuildRmstMasksRequestingPlayerToOneByte()
    {
        var field = Client.BuildRmst(0x1fe, Message.MenuIdentifier.SubMenu,
            CdjStatus.TrackSourceSlot.Collection, CdjStatus.TrackType.CdDigitalAudio);
        Assert.Equal(0xfe020405L, field.Value);
    }

    [Fact]
    public void MessageStartHasProtocolMagicValue()
    {
        Assert.Equal(0x872349aeL, Message.MessageStart.Value);
        Assert.Equal(4, Message.MessageStart.Size);
    }
}
