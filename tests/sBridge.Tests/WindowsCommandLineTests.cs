using SBridge.Launching;
using Xunit;

namespace SBridge.Tests;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("simple", "simple")]
    [InlineData("Player One", "\"Player One\"")]
    [InlineData("a\tb", "\"a\tb\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("C:\\Game Folder\\", "\"C:\\Game Folder\\\\\"")]
    [InlineData("C:\\Game\\", "C:\\Game\\")]
    public void QuoteVectorsPreserveEmptyWhitespaceQuotesAndTrailingBackslashes(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(argument));
    }

    [Theory]
    [InlineData("\"C:\\Game Folder\\game.exe\" --profile \"Player One\"", "C:\\Game Folder\\game.exe")]
    [InlineData("  Example_123!Game\tGame.exe", "Example_123!Game")]
    [InlineData("C:\\Game\u00A0Folder\\game.exe --flag", "C:\\Game\u00A0Folder\\game.exe")]
    [InlineData("\"C:\\Game Folder\\\\\" --flag", "C:\\Game Folder\\")]
    [InlineData("\"\" --flag", "")]
    [InlineData("\"unclosed target with spaces", "unclosed target with spaces")]
    [InlineData("\"a\\\"b\" next", "a\"b")]
    [InlineData("\"a\"\"b\" next", "a\"b")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FirstArgumentMatchesWindowsTokenMeaning(string? options, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.FirstArgument(options));
    }

    [Fact]
    public void CustomShortcutKeepsQuotedTargetAndRawExpertArguments()
    {
        const string raw = "--profile \"Player One\" \"\" --path \"C:\\Trailing Space\\\\\"";
        Assert.Equal("\"C:\\Games\\game.exe\" " + raw, LegacyLaunchOptions.ForCustomGame(@"C:\Games\game.exe", raw));
        Assert.Equal("\"C:\\Games\\game.exe\"", LegacyLaunchOptions.ForCustomGame(@"C:\Games\game.exe", ""));
    }

    [Fact]
    public void PackagedShortcutQuotesHintAsOneTokenWithoutChangingOrdinaryOptions()
    {
        Assert.Equal("Example_123!Game Game.exe", LegacyLaunchOptions.ForPackagedGame("Example_123!Game", "Game.exe"));
        Assert.Equal("Example_123!Game \"Game Folder/Game.exe\"", LegacyLaunchOptions.ForPackagedGame("Example_123!Game", "Game Folder/Game.exe"));
        Assert.Equal("Example_123!Game", LegacyLaunchOptions.ForPackagedGame("Example_123!Game", null));
    }

    [Fact]
    public void EmbeddedNulCannotBeEncodedForWindows()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.QuoteArgument("bad\0argument"));
    }
}
