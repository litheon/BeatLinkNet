using BeatLink.DbServer;

namespace BeatLink.Tests;

public class FieldTests
{
    private static Field RoundTrip(Field field)
    {
        using var stream = new MemoryStream(field.GetBytes().ToArray());
        var result = Field.Read(stream);
        Assert.Equal(stream.Length, stream.Position);  // The entire representation was consumed.
        return result;
    }

    [Theory]
    [InlineData(1, 0L)]
    [InlineData(1, 1L)]
    [InlineData(1, 0xffL)]
    [InlineData(2, 0L)]
    [InlineData(2, 1L)]
    [InlineData(2, 0xffL)]
    [InlineData(2, 0xffffL)]
    [InlineData(4, 0L)]
    [InlineData(4, 1L)]
    [InlineData(4, 0xffL)]
    [InlineData(4, 0xffffL)]
    [InlineData(4, 0xffffffffL)]
    public void NumberFieldRoundTripsThroughStream(int size, long value)
    {
        var field = new NumberField(value, size);
        var read = Assert.IsType<NumberField>(RoundTrip(field));
        Assert.Equal(value, read.Value);
        Assert.Equal(size, read.Size);
        Assert.Equal(field.TypeTag, read.TypeTag);
        Assert.Equal(field.GetBytes().ToArray(), read.GetBytes().ToArray());
    }

    [Theory]
    [InlineData(1, 0x0f)]
    [InlineData(2, 0x10)]
    [InlineData(4, 0x11)]
    public void NumberFieldUsesCorrectTypeTagForSize(int size, byte expectedTag)
    {
        var field = new NumberField(1, size);
        Assert.Equal(expectedTag, field.TypeTag);
        Assert.Equal(expectedTag, field.GetBytes().Span[0]);
        Assert.Equal(size + 1, field.GetBytes().Length);
        Assert.Equal(0x06, field.ArgumentTag);
    }

    [Fact]
    public void NumberFieldSerializesBigEndian()
    {
        Assert.Equal(new byte[] { 0x10, 0x12, 0x34 }, new NumberField(0x1234, 2).GetBytes().ToArray());
        Assert.Equal(new byte[] { 0x11, 0xde, 0xad, 0xbe, 0xef }, new NumberField(0xdeadbeefL, 4).GetBytes().ToArray());
        Assert.Equal(new byte[] { 0x0f, 0x7f }, new NumberField(0x7f, 1).GetBytes().ToArray());
    }

    [Fact]
    public void NumberFieldMasksValueToFourBytes()
    {
        Assert.Equal(0xffffffffL, new NumberField(-1).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void NumberFieldRejectsIllegalSizes(int size)
    {
        Assert.Throws<ArgumentException>(() => new NumberField(1, size));
    }

    [Fact]
    public void NumberFieldRejectsIllegalReadTag()
    {
        using var stream = new MemoryStream([0, 0, 0, 0]);
        Assert.Throws<ArgumentException>(() => new NumberField(0x12, stream));
    }

    [Fact]
    public void SharedWordConstantsHaveExpectedValues()
    {
        Assert.Equal(0, NumberField.Word0.Value);
        Assert.Equal(4, NumberField.Word0.Size);
        Assert.Equal(1, NumberField.Word1.Value);
        Assert.Equal(4, NumberField.Word1.Size);
    }

    [Fact]
    public void ConvenienceNumberFieldConstructorDefaultsToFourBytes()
    {
        Assert.Equal(4, new NumberField(17).Size);
    }

    [Fact]
    public void EmptyStringFieldIsByteExact()
    {
        var field = new StringField("");
        // Type tag, character count of 1 (just the trailing NUL), and the UTF-16BE NUL itself.
        Assert.Equal(new byte[] { 0x26, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 }, field.GetBytes().ToArray());
        Assert.Equal(2, field.Size);
        var read = Assert.IsType<StringField>(RoundTrip(field));
        Assert.Equal("", read.Value);
    }

    [Fact]
    public void AsciiStringFieldIsByteExact()
    {
        var field = new StringField("AB");
        // The length header counts characters (including the trailing NUL), not bytes.
        Assert.Equal(new byte[] { 0x26, 0x00, 0x00, 0x00, 0x03, 0x00, 0x41, 0x00, 0x42, 0x00, 0x00 },
            field.GetBytes().ToArray());
        Assert.Equal(6, field.Size);
        Assert.Equal(0x02, field.ArgumentTag);
        var read = Assert.IsType<StringField>(RoundTrip(field));
        Assert.Equal("AB", read.Value);
        Assert.Equal(field.GetBytes().ToArray(), read.GetBytes().ToArray());
    }

    [Fact]
    public void NonAsciiStringFieldIsByteExact()
    {
        var field = new StringField("é♫");
        Assert.Equal(new byte[] { 0x26, 0x00, 0x00, 0x00, 0x03, 0x00, 0xe9, 0x26, 0x6b, 0x00, 0x00 },
            field.GetBytes().ToArray());
        var read = Assert.IsType<StringField>(RoundTrip(field));
        Assert.Equal("é♫", read.Value);
        Assert.Equal(field.GetBytes().ToArray(), read.GetBytes().ToArray());
    }

    [Fact]
    public void LongerStringFieldRoundTrips()
    {
        const string text = "Never Gonna Give You Up";
        var field = new StringField(text);
        Assert.Equal((text.Length + 1) * 2, field.Size);
        Assert.Equal(text.Length + 1, (int)Util.BytesToNumber(field.GetBytes().Span, 1, 4));
        var read = Assert.IsType<StringField>(RoundTrip(field));
        Assert.Equal(text, read.Value);
    }

    [Fact]
    public void BinaryFieldIsByteExact()
    {
        var field = new BinaryField([0x01, 0x02, 0x03]);
        Assert.Equal(new byte[] { 0x14, 0x00, 0x00, 0x00, 0x03, 0x01, 0x02, 0x03 }, field.GetBytes().ToArray());
        Assert.Equal(3, field.Size);
        Assert.Equal(0x03, field.ArgumentTag);
        var read = Assert.IsType<BinaryField>(RoundTrip(field));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, read.GetValueAsArray());
        Assert.Equal(field.GetBytes().ToArray(), read.GetBytes().ToArray());
    }

    [Fact]
    public void ZeroLengthBinaryFieldSendsNothingAtAll()
    {
        var field = new BinaryField([]);
        Assert.Equal(0, field.GetBytes().Length);  // Not even the tag and length header!
        Assert.Equal(0, field.Size);
        Assert.Empty(field.GetValueAsArray());
        Assert.True(field.Value.IsEmpty);
    }

    [Fact]
    public void ZeroLengthBinaryFieldCanBeReadWhenHeaderIsPresent()
    {
        // Some players do send an explicit zero length header even though we never write one.
        using var stream = new MemoryStream([0x14, 0x00, 0x00, 0x00, 0x00]);
        var read = Assert.IsType<BinaryField>(Field.Read(stream));
        Assert.Equal(0, read.Size);
        Assert.Empty(read.GetValueAsArray());
    }

    [Fact]
    public void UnknownTagThrowsIOException()
    {
        using var stream = new MemoryStream([0x42, 0x00, 0x00]);
        var e = Assert.Throws<IOException>(() => Field.Read(stream));
        Assert.Contains("type tag", e.Message);
    }

    [Fact]
    public void TruncatedFieldThrowsIOException()
    {
        using var stream = new MemoryStream([0x11, 0x00]);
        Assert.IsAssignableFrom<IOException>(Record.Exception(() => Field.Read(stream)));
    }

    [Fact]
    public void EmptyStreamThrowsIOException()
    {
        using var stream = new MemoryStream([]);
        Assert.IsAssignableFrom<IOException>(Record.Exception(() => Field.Read(stream)));
    }
}
