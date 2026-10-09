using SBridge.Sessions;
using Xunit;

namespace SBridge.Tests;

public class ProcessMatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactPathAndLineageBeatNameOnlyWithoutDependingOnEnumerationOrder()
    {
        var parent = new ProcessIdentity(1, Start);
        var lineage = new Dictionary<int, ProcessLifetime> { [1] = new(parent, null) };
        var real = P(2, "game", @"C:\Games\game.exe", parent: 1);
        var unknownPath = P(3, "game", null);
        var evidence = Evidence(path: @"C:\Games\game.exe");
        foreach (var input in new[] { new[] { real, unknownPath }, new[] { unknownPath, real } })
        {
            var result = ProcessMatcher.Select(evidence, input, lineage, new HashSet<ProcessIdentity>());
            Assert.Equal(2, result.Selected?.Process.Id);
            Assert.False(result.Ambiguous);
            Assert.Contains("descendant", result.Selected!.Reason);
        }
    }

    [Fact]
    public void EqualNamesAndPathsRemainAmbiguousRatherThanUsingPidOrNewestProcess()
    {
        var result = Match(Evidence(), P(2, "game"), P(3, "game", started: Start.AddSeconds(3)));
        Assert.True(result.Ambiguous);
        Assert.Null(result.Selected);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void AWindowAloneDoesNotResolveNearTieBetweenSameNamedProcesses()
    {
        Assert.True(Match(Evidence(), P(2, "game", window: true), P(3, "game")).Ambiguous);
    }

    [Fact]
    public void BaselineAndPrelaunchProcessesAreNotAdopted()
    {
        var baseline = P(2, "game");
        var evidence = Evidence(before: new[] { baseline });
        Assert.Null(Match(evidence, baseline).Selected);
        Assert.Null(Match(Evidence(), P(3, "game", started: Start.AddMilliseconds(-1))).Selected);
    }

    [Fact]
    public void ReusedPidWithNewCreationTimeIsDifferentFromBaselineIdentity()
    {
        var old = P(2, "game", started: Start.AddMinutes(-1));
        var result = Match(Evidence(before: new[] { old }), P(2, "game", started: Start.AddSeconds(3)));
        Assert.Equal(2, result.Selected?.Process.Id);
    }

    [Fact]
    public void UnknownCreationTimeCannotProveSessionMembershipButHiddenPathCanUseNameAndTime()
    {
        var unknown = new ProcessObservation(2, null, "game", null, null, true);
        Assert.Null(Match(Evidence(), unknown).Selected);
        Assert.NotNull(Match(Evidence(), P(3, "game", path: null)).Selected);
    }

    [Fact]
    public void ExplicitKnownPathRejectsUnrelatedSameNameAtDifferentLocation()
    {
        Assert.Null(Match(Evidence(path: @"C:\Games\game.exe"), P(2, "game", @"D:\Other\game.exe")).Selected);
    }

    [Theory]
    [InlineData("C:\\Games\\game.exe", true)]
    [InlineData("c:/games/sub/game.exe", true)]
    [InlineData("C:\\GamesOther\\game.exe", false)]
    [InlineData("C:\\Games", false)]
    public void InstallDirectoryRequiresRealPathBoundary(string path, bool expected)
    {
        Assert.Equal(expected, ProcessMatcher.IsWithinDirectory(path, @"C:\Games"));
    }

    [Fact]
    public void UnknownNameNeedsVisibleWindowForBroadInstallDirectoryFallback()
    {
        Assert.Null(Match(Evidence(), P(2, "worker", @"C:\Games\worker.exe")).Selected);
        Assert.NotNull(Match(Evidence(), P(2, "actual", @"C:\Games\actual.exe", window: true)).Selected);
    }

    [Theory]
    [InlineData("crashreportclient")]
    [InlineData("werfault")]
    [InlineData("updater")]
    [InlineData("sisr")]
    [InlineData("viiper")]
    [InlineData("conhost")]
    [InlineData("openconsole")]
    [InlineData("dllhost")]
    [InlineData("runtimebroker")]
    public void MaintenanceAndControllerResourcesCannotBecomeReplacementGames(string name)
    {
        Assert.Null(Match(Evidence(), P(2, name, @"C:\Games\helper.exe", window: true)).Selected);
    }

    [Fact]
    public void FastChildBornBeforeRetainedParentsExitRemainsAValidDescendant()
    {
        var identity = new ProcessIdentity(1, Start);
        var lineage = new Dictionary<int, ProcessLifetime> { [1] = new(identity, Start.AddSeconds(5)) };
        Assert.True(ProcessMatcher.IsDescendant(P(2, "bootstrap", parent: 1, started: Start.AddSeconds(4)), Array.Empty<ProcessObservation>(), lineage));
        Assert.False(ProcessMatcher.IsDescendant(P(2, "bootstrap", parent: 1, started: Start.AddSeconds(6)), Array.Empty<ProcessObservation>(), lineage));
    }

    [Fact]
    public void ReusedParentPidAndCyclicOrBackwardParentChainsDoNotProveLineage()
    {
        var known = new Dictionary<int, ProcessLifetime> { [1] = new(new ProcessIdentity(1, Start), null) };
        var child = P(2, "game", parent: 1, started: Start.AddSeconds(5));
        Assert.False(ProcessMatcher.IsDescendant(child, new[] { P(1, "unrelated", started: Start.AddSeconds(3)) }, known));
        var cycle = new[] { P(2, "a", parent: 3), P(3, "b", parent: 2) };
        Assert.False(ProcessMatcher.IsDescendant(cycle[0], cycle, new Dictionary<int, ProcessLifetime>()));
    }

    [Fact]
    public void OldBootstrapSurvivorIsExcludedAfterActualGameExit()
    {
        var parent = new ProcessIdentity(1, Start);
        var lineage = new Dictionary<int, ProcessLifetime> { [1] = new(parent, null) };
        var helper = P(2, "easyanticheat", parent: 1);
        Assert.NotNull(ProcessMatcher.Select(Evidence(), new[] { helper }, lineage, new HashSet<ProcessIdentity>()).Selected);
        Assert.Null(ProcessMatcher.Select(Evidence(), new[] { helper }, lineage, new HashSet<ProcessIdentity>(),
            bootstrapNotBefore: Start.AddMinutes(10)).Selected);
    }

    private static ProcessObservation P(int id, string name, string? path = null, int? parent = null,
        bool window = false, DateTimeOffset? started = null) => new(id, started ?? Start.AddSeconds(1), name, path, parent, window);
    private static GameLaunchEvidence Evidence(string? path = null, IReadOnlyList<ProcessObservation>? before = null) =>
        new(Guid.NewGuid(), Start, "target", "game.exe", path, @"C:\Games", before ?? Array.Empty<ProcessObservation>(), null);
    private static ProcessMatch Match(GameLaunchEvidence evidence, params ProcessObservation[] processes) =>
        ProcessMatcher.Select(evidence, processes, new Dictionary<int, ProcessLifetime>(), new HashSet<ProcessIdentity>());
}
