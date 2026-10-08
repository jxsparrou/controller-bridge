using SBridge.Steam;
using Xunit;

namespace SBridge.Tests;

public class SteamShortcutIdentityTests
{
    // Independently calculated with Python zlib.crc32 over UTF-8. These lock the
    // legacy quoting contract, not Steam's interpretation of new shortcut values.
    [Theory]
    [InlineData("Example Game", @"C:\Games\sBridge\sBridge.exe", 0x8B0F1B27u)]
    [InlineData("Example Game", @"C:\Program Files\sBridge\sBridge.exe", 0x94CFC93Eu)]
    [InlineData("遊戲 Café 🎮", @"C:\Program Files\sBridge\sBridge.exe", 0xE1334FDDu)]
    [InlineData("example game", @"C:\Program Files\sBridge\sBridge.exe", 0xACCCBDF8u)]
    public void LegacyQuotedPathMatchesIndependentVector(string name, string path, uint expected)
    {
        Assert.Equal(expected, SteamShortcutIdentity.CalculateAppId(name, path));
    }

    [Fact]
    public void QuotingIsPartOfIdentityAndIsNotNormalized()
    {
        // Existing callers supply unquoted paths. Characterize double quoting so
        // a future normalization change cannot silently change stored identities.
        uint appId = SteamShortcutIdentity.CalculateAppId(
            "Example Game", "\"C:\\Program Files\\sBridge\\sBridge.exe\"");

        Assert.Equal(0xA865339Bu, appId);
        Assert.NotEqual(0x94CFC93Eu, appId);
        // Independent unquoted-Exe alternative from the PR discussion.
        Assert.NotEqual(0xCD2CEBFCu,
            SteamShortcutIdentity.CalculateAppId("Example Game", @"C:\Games\sBridge\sBridge.exe"));
    }
}
