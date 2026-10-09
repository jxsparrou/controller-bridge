using SBridge.Launching;
using SBridge.Core;
using Xunit;

namespace SBridge.Tests;

public class LegacyLaunchRequestTests
{
    [Fact]
    public void NoArgumentsSelectsSettingsWithoutProbingFiles()
    {
        Assert.Null(LegacyLaunchRequest.Parse(Array.Empty<string>(), _ => throw new InvalidOperationException("Settings must not probe a launch target")));
    }

    [Theory]
    [InlineData("C:\\Games With Spaces\\game.exe")]
    [InlineData("game.EXE")]
    [InlineData("relative\\launcher")]
    [InlineData("\\\\server\\games\\game.exe")]
    public void LegacyExeAndPathHeuristicsRemainCustom(string target)
    {
        var request = Parse(target, "--profile", "Player One", "");
        Assert.Equal(LegacyLaunchKind.Win32, request.Kind);
        Assert.Equal(target, request.Target);
        Assert.Equal(target, request.ResolveProcessHint(null));
        Assert.Equal(new[] { "--profile", "Player One", "" }, request.Arguments);
    }

    [Fact]
    public void ExistingFileWithoutExeExtensionRetainsLegacyCustomClassification()
    {
        var request = LegacyLaunchRequest.Parse(new[] { "game.com", "argument" }, path => path == "game.com")!;
        Assert.Equal(LegacyLaunchKind.Win32, request.Kind);
        Assert.Equal(new[] { "argument" }, request.Arguments);
    }

    [Fact]
    public void ForwardSlashTargetIsNormalizedBeforeClassificationAndPreferenceLookup()
    {
        string? probed = null;
        var request = LegacyLaunchRequest.Parse(new[] { "D:/Game Folder/game.exe", "https://example.test/a/b" }, path => { probed = path; return false; })!;
        Assert.Equal(@"D:\Game Folder\game.exe", request.Target);
        Assert.Equal(request.Target, probed);
        Assert.Equal(new[] { "https://example.test/a/b" }, request.Arguments);
    }

    [Fact]
    public void PackagedHintIsNotForwardedEvenWithAWatchOverride()
    {
        var request = Parse("Example_123!Game", "Game Folder/Game.exe", "--profile", "Player One", "");
        Assert.Equal(LegacyLaunchKind.Packaged, request.Kind);
        Assert.Equal(@"Game Folder\Game.exe", request.ResolveProcessHint(null));
        Assert.Equal("Override/actual.exe", request.ResolveProcessHint("Override/actual.exe"));
        Assert.Equal(new[] { "--profile", "Player One", "" }, request.Arguments);
    }

    [Fact]
    public void EmptyPackagedHintAllowsFollowingArgumentsWithoutShiftingPositions()
    {
        var request = Parse("Example_123!Game", "", "--fullscreen");
        Assert.Equal("", request.ResolveProcessHint(""));
        Assert.Equal(new[] { "--fullscreen" }, request.Arguments);
        Assert.Empty(Parse("Example_123!Game").Arguments);
        // Without a placeholder the second argument is still a hint, not a flag.
        Assert.Empty(Parse("Example_123!Game", "--fullscreen").Arguments);
    }

    [Fact]
    public void RequestDefensivelyCopiesDecodedArgumentTokens()
    {
        string[] input = { @"C:\Games\game.exe", "Original Value" };
        var request = LegacyLaunchRequest.Parse(input, _ => false)!;
        input[1] = "Changed";
        Assert.Equal(new[] { "Original Value" }, request.Arguments);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)request.Arguments)[0] = "Mutated");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaseInsensitiveOverrideRemovalRestoresGlobalRatherThanStoringFalse(bool globalDefault)
    {
        var request = Parse(@"C:\Games\game.exe");
        var settings = new AppSettings { SisrEnabled = globalDefault };
        Assert.Equal(globalDefault, settings.IsSisrEnabledFor(request.Target));
        settings.SetSteamInputMode(@"c:\games\GAME.EXE", globalDefault ? SteamInputMode.Disabled : SteamInputMode.Enabled);
        Assert.Equal(!globalDefault, settings.IsSisrEnabledFor(request.Target));
        settings.SetSteamInputMode(@"C:\GAMES\GAME.EXE", SteamInputMode.Disabled);
        Assert.False(settings.IsSisrEnabledFor(request.Target));
        settings.SetSteamInputMode(request.Target, SteamInputMode.Automatic);
        Assert.Equal(globalDefault, settings.IsSisrEnabledFor(request.Target));
    }

    [Fact]
    public void LegacyAdapterDoesNotOwnModernVerbDispatch()
    {
        var request = Parse("launch", "some-id");
        Assert.Equal(LegacyLaunchKind.Packaged, request.Kind);
        Assert.Equal("launch", request.Target);
        Assert.Equal("some-id", request.ProcessHint);
    }

    [Fact]
    public void InvalidEmptyOrNulTargetIsRejectedBeforeResourceStartup()
    {
        Assert.Throws<ArgumentException>(() => Parse(""));
        Assert.Throws<ArgumentException>(() => Parse("bad\0target"));
        Assert.Throws<ArgumentException>(() => Parse("game.exe", "bad\0argument"));
    }

    private static LegacyLaunchRequest Parse(params string[] arguments) => LegacyLaunchRequest.Parse(arguments, _ => false)!;
}
