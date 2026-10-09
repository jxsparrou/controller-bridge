using System.Text;
using Xunit;

namespace SBridge.Tests;

public class VdfParserTests
{
    [Fact]
    public void IndependentFixtureDecodesNestedMapsUtf8AndUnsignedAppIds()
    {
        var root = Parse(LoadFixture());
        Assert.Equal("shortcuts", root.Name);
        Assert.Equal(new[] { "0", "7" }, root.Children.Select(child => child.Name));

        var bridge = root.Children[0];
        Assert.Equal(unchecked((int)0x94CFC93E), Field(bridge, "appid").IntValue);
        Assert.Equal("Example Game", Field(bridge, "AppName").StringValue);
        Assert.Equal("\"C:\\Program Files\\sBridge\\sBridge.exe\"", Field(bridge, "Exe").StringValue);
        Assert.Equal("Example_123!Game Game.exe --fullscreen", Field(bridge, "LaunchOptions").StringValue);
        Assert.Equal("Keep me", Field(bridge, "CustomField").StringValue);
        Assert.Equal("Favorites", Field(Field(bridge, "tags"), "0").StringValue);

        var unrelated = root.Children[1];
        Assert.Equal(-1, Field(unrelated, "appid").IntValue);
        Assert.Equal("遊戲 Café 🎮", Field(unrelated, "AppName").StringValue);
        Assert.Empty(Field(unrelated, "tags").Children);
    }

    [Fact]
    public void IndependentFixtureRoundTripsByteForByteIncludingRootTrailer()
    {
        byte[] fixture = LoadFixture();
        Assert.Equal(fixture, Program.SerializeShortcuts(Parse(fixture)));
    }

    [Fact]
    public void EditingLaunchOptionsPreservesUnrelatedShortcutAndCustomFields()
    {
        var root = Parse(LoadFixture());
        byte[] originalUnrelated = SerializeSubtree(root.Children[1]);
        const string options = "\"D:\\Games With Spaces\\game.exe\" --profile \"Player One\"";
        Field(root.Children[0], "LaunchOptions").StringValue = options;

        var reparsed = Parse(Program.SerializeShortcuts(root));
        Assert.Equal(options, Field(reparsed.Children[0], "LaunchOptions").StringValue);
        Assert.Equal("Keep me", Field(reparsed.Children[0], "CustomField").StringValue);
        Assert.Equal("Favorites", Field(Field(reparsed.Children[0], "tags"), "0").StringValue);
        Assert.Equal(originalUnrelated, SerializeSubtree(reparsed.Children[1]));
        Assert.Equal(unchecked((int)0x94CFC93E), Field(reparsed.Children[0], "appid").IntValue);
    }

    [Fact]
    public void EmptyRootHasExactBinaryEnvelope()
    {
        byte[] expected = Convert.FromHexString("0073686F727463757473000808");
        Assert.Equal(expected, Program.SerializeShortcuts(new Program.VdfElement
        {
            Type = 0x00,
            Name = "shortcuts"
        }));
        Assert.Empty(Parse(expected).Children);
    }

    [Fact]
    public void EveryTruncationBeforeRootCloseThrowsEndOfStream()
    {
        byte[] fixture = LoadFixture();
        // A complete root with no extra marker is accepted for compatibility.
        for (int length = 0; length < fixture.Length - 1; length++)
        {
            byte[] truncated = fixture[..length];
            Assert.Throws<EndOfStreamException>(() => Parse(truncated));
        }
    }

    [Theory]
    [InlineData("6B6579")] // Unterminated key/string
    [InlineData("E9818AE688B2")] // Complete UTF-8 bytes but no NUL
    public void UnterminatedStringThrowsEndOfStream(string hex)
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        using var reader = new BinaryReader(stream);
        Assert.Throws<EndOfStreamException>(() => Program.ReadNullTerminatedString(reader));
    }

    [Fact]
    public void Utf8StringWriterUsesNulRatherThanBinaryWriterLengthPrefix()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Program.WriteNullTerminatedString(writer, "Café");
        writer.Flush();
        Assert.Equal(Convert.FromHexString("436166C3A900"), stream.ToArray());
    }

    private static byte[] LoadFixture()
    {
        string hex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcuts-supported.hex"));
        return Convert.FromHexString(string.Concat(hex.Where(character => !char.IsWhiteSpace(character))));
    }

    private static Program.VdfElement Parse(byte[] bytes) => Program.ParseShortcuts(bytes);

    private static byte[] SerializeSubtree(Program.VdfElement subtree) => Program.SerializeShortcuts(
        new Program.VdfElement { Type = 0x00, Name = "shortcuts", Children = new() { subtree } });

    [Theory]
    [InlineData("0173686F727463757473000808")] // Wrong root type
    [InlineData("006F74686572000808")] // Wrong root name
    [InlineData("0073686F7274637574730008FF")] // Bad trailer
    [InlineData("0073686F72746375747300080808")] // Surplus end marker
    [InlineData("0073686F7274637574730008080100")] // Trailing payload
    [InlineData("0073686F72746375747300037800000000000808")] // Unsupported float tag
    [InlineData("0073686F72746375747300017800FF000808")] // Invalid UTF-8 value
    [InlineData("0073686F7274637574730001FF00000808")] // Invalid UTF-8 key
    public void MalformedEnvelopeTypesAndUtf8AreRejected(string hex)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Convert.FromHexString(hex)));
    }

    [Fact]
    public void RootCloseWithOrWithoutExtraMarkerIsAccepted()
    {
        Assert.Empty(Parse(Convert.FromHexString("0073686F7274637574730008")).Children);
        Assert.Empty(Parse(Convert.FromHexString("0073686F727463757473000808")).Children);
    }

    [Fact]
    public void ExcessiveDepthIsRejectedInReaderAndWriter()
    {
        var root = new Program.VdfElement { Type = 0x00, Name = "shortcuts" };
        var current = root;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0x00);
        Program.WriteNullTerminatedString(writer, "shortcuts");
        for (int depth = 0; depth <= Program.MaxVdfDepth; depth++)
        {
            var child = new Program.VdfElement { Type = 0x00, Name = "nested" };
            current.Children.Add(child);
            current = child;
            writer.Write((byte)0x00);
            Program.WriteNullTerminatedString(writer, "nested");
        }
        writer.Write(Enumerable.Repeat((byte)0x08, Program.MaxVdfDepth + 3).ToArray());
        writer.Flush();
        Assert.Throws<InvalidDataException>(() => Parse(stream.ToArray()));
        Assert.Throws<InvalidDataException>(() => Program.SerializeShortcuts(root));
    }

    [Fact]
    public void OversizeStringsAreRejectedInReaderAndWriter()
    {
        byte[] bytes = Enumerable.Repeat((byte)'a', Program.MaxVdfStringBytes + 1).Append((byte)0).ToArray();
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        Assert.Throws<InvalidDataException>(() => Program.ReadNullTerminatedString(reader));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        Assert.Throws<InvalidDataException>(() => Program.WriteNullTerminatedString(writer, new string('a', Program.MaxVdfStringBytes + 1)));
    }

    [Fact]
    public void ExcessiveElementCountIsRejectedInReaderAndWriter()
    {
        var root = new Program.VdfElement { Type = 0x00, Name = "shortcuts" };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0);
        Program.WriteNullTerminatedString(writer, "shortcuts");
        for (int i = 0; i <= Program.MaxVdfElements; i++)
        {
            root.Children.Add(new Program.VdfElement { Type = 0x02, Name = "value", IntValue = i });
            writer.Write((byte)0x02);
            Program.WriteNullTerminatedString(writer, "value");
            writer.Write(i);
        }
        writer.Write(new byte[] { 0x08, 0x08 });
        writer.Flush();
        Assert.Throws<InvalidDataException>(() => Parse(stream.ToArray()));
        Assert.Throws<InvalidDataException>(() => Program.SerializeShortcuts(root));
    }

    [Fact]
    public void OversizeFileIsRejectedBeforeParsing()
    {
        Assert.Throws<InvalidDataException>(() => Parse(new byte[Program.MaxVdfFileBytes + 1]));
    }

    [Fact]
    public void InvalidOutputStringsAreRejected()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Assert.Throws<InvalidDataException>(() => Program.WriteNullTerminatedString(writer, "has\0nul"));
        // Attribute metadata cannot reliably transport an unpaired surrogate.
        string invalidUnicode = new string((char)0xD800, 1);
        Assert.Throws<InvalidDataException>(() => Program.WriteNullTerminatedString(writer, invalidUnicode));
    }

    [Fact]
    public void OversizeSerializedFileIsRejected()
    {
        var root = new Program.VdfElement { Type = 0x00, Name = "shortcuts" };
        string value = new string('a', Program.MaxVdfStringBytes);
        for (int i = 0; i < 33; i++)
            root.Children.Add(new Program.VdfElement { Type = 0x01, Name = i.ToString(), StringValue = value });
        Assert.Throws<InvalidDataException>(() => Program.SerializeShortcuts(root));
    }

    [Fact]
    public void CyclicAndUnsupportedOutputIsRejected()
    {
        var root = new Program.VdfElement { Type = 0x00, Name = "shortcuts" };
        root.Children.Add(root);
        Assert.Throws<InvalidDataException>(() => Program.SerializeShortcuts(root));
        root.Children.Clear();
        root.Children.Add(new Program.VdfElement { Type = 0x03, Name = "unsupported" });
        Assert.Throws<InvalidDataException>(() => Program.SerializeShortcuts(root));
    }

    private static Program.VdfElement Field(Program.VdfElement map, string name)
    {
        return Assert.Single(map.Children, child => child.Name == name);
    }
}
