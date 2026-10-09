using SBridge.Sessions;
using Xunit;

namespace SBridge.Tests;

public class GameSessionMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static GameSessionOptions Options => new()
    {
        PollInterval = TimeSpan.FromSeconds(1), LaunchSearchTimeout = TimeSpan.FromSeconds(12),
        ExitGrace = TimeSpan.FromSeconds(3), MaxProvisionalLifetime = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public async Task LauncherBootstrapGameSupportsMultipleHandoffsAndPromptFinalExit()
    {
        var launcher = P(1, "launcher", 0);
        var bootstrap = P(2, "bootstrap", 1, parent: 1);
        var game = P(3, "game", 2, parent: 2);
        var fixture = new Fixture(new[] { launcher }, new[] { bootstrap }, new[] { bootstrap, game },
            new[] { game }, Array.Empty<ProcessObservation>());
        var result = await fixture.Run(launcher);
        Assert.Equal(GameSessionOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Handoffs);
        Assert.Equal(game.Identity, result.FinalProcess);
        Assert.True(fixture.Clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.All(fixture.Handles, handle => Assert.Equal(1, handle.Disposed));
    }

    [Fact]
    public async Task LaterSlowBootstrapHandoffUsesStartupBudgetNotFirstExitCounter()
    {
        var launcher = P(1, "launcher", 0);
        var bootstrap = P(2, "bootstrap", 1, parent: 1);
        var game = P(3, "game", 7, parent: 2);
        var frames = new List<ProcessObservation[]> { new[] { launcher }, new[] { bootstrap }, Array.Empty<ProcessObservation>() };
        for (int i = 0; i < 5; i++) frames.Add(Array.Empty<ProcessObservation>());
        // Name+session time identifies a service-launched game even without a live parent.
        frames.Add(new[] { game }); frames.Add(Array.Empty<ProcessObservation>());
        var fixture = new Fixture(frames.ToArray());
        var result = await fixture.Run(launcher);
        Assert.Equal(2, result.Handoffs);
        Assert.Equal(game.Identity, result.FinalProcess);
    }

    [Fact]
    public async Task RealGameCanReplaceLauncherThatStaysAliveAndLauncherIsNeverReadopted()
    {
        var launcher = P(1, "launcher", 0);
        var game = P(2, "game", 1, parent: 1);
        var fixture = new Fixture(new[] { launcher }, new[] { launcher, game }, new[] { launcher, game }, new[] { launcher });
        var result = await fixture.Run(launcher);
        Assert.Equal(1, result.Handoffs);
        Assert.Equal(game.Identity, result.FinalProcess);
        Assert.Equal(GameSessionOutcome.Completed, result.Outcome);
        Assert.All(fixture.Handles, handle => Assert.Equal(1, handle.Disposed));
    }

    [Fact]
    public async Task QuickDirectLaunchHasShortExitGraceWithoutStartupRetryPenalty()
    {
        var game = P(1, "game", 0);
        var fixture = new Fixture(new[] { game }, Array.Empty<ProcessObservation>());
        Assert.Equal(GameSessionOutcome.Completed, (await fixture.Run(game)).Outcome);
        Assert.Equal(TimeSpan.FromSeconds(4), fixture.Clock.Elapsed);
    }

    [Fact]
    public async Task PidZeroUsesEvidenceDiscoveryInsteadOfImmediatelyEndingSession()
    {
        var game = P(2, "game", 1);
        var fixture = new Fixture(Array.Empty<ProcessObservation>(), new[] { game }, Array.Empty<ProcessObservation>());
        var result = await fixture.Run(null);
        Assert.Equal(GameSessionOutcome.Completed, result.Outcome);
        Assert.Equal(game.Identity, result.FinalProcess);
    }

    [Fact]
    public async Task AmbiguityTimesOutExplicitlyWithoutAdoptingEitherCandidate()
    {
        var fixture = new Fixture(new[] { P(1, "game", 1), P(2, "game", 1) });
        Assert.Equal(GameSessionOutcome.NotFound, (await fixture.Run(null)).Outcome);
        Assert.Empty(fixture.Handles);
        Assert.Contains(fixture.Log, line => line.Contains("Ambiguous"));
    }

    [Fact]
    public async Task LauncherExitWithoutAnyConfirmedGameIsNotReportedAsSuccess()
    {
        var launcher = P(1, "launcher", 0);
        var fixture = new Fixture(new[] { launcher }, Array.Empty<ProcessObservation>());
        Assert.Equal(GameSessionOutcome.NotFound, (await fixture.Run(launcher)).Outcome);
    }

    [Fact]
    public async Task NeverEndingProvisionalProcessAndRepeatedHandoffsAreBounded()
    {
        var launcher = P(1, "launcher", 0);
        var fixture = new Fixture(new[] { launcher });
        Assert.Equal(GameSessionOutcome.NotFound, (await fixture.Run(launcher)).Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Clock.Elapsed);

        var bootstrap = P(2, "bootstrap", 1, parent: 1);
        var game = P(3, "game", 2, parent: 2);
        var limited = new Fixture(new[] { launcher }, new[] { bootstrap }, new[] { bootstrap, game });
        Assert.Equal(GameSessionOutcome.HandoffLimit, (await limited.Run(launcher, Options with { MaxHandoffs = 1 })).Outcome);
    }

    [Fact]
    public async Task CancellationAndObservationFailureReleaseAllHandlesWithoutKillingGames()
    {
        var launcher = P(1, "launcher", 0);
        var fixture = new Fixture(new[] { launcher });
        using var cancel = new CancellationTokenSource();
        fixture.Clock.OnDelay = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(launcher, cancellationToken: cancel.Token));
        Assert.All(fixture.Handles, handle => Assert.Equal(1, handle.Disposed));

        var failed = new Fixture(new[] { launcher }) { FailCapture = true };
        await Assert.ThrowsAsync<IOException>(() => failed.Run(launcher));
        Assert.All(failed.Handles, handle => Assert.Equal(1, handle.Disposed));
    }

    private static ProcessObservation P(int id, string name, int seconds, int? parent = null) =>
        new(id, Start.AddSeconds(seconds), name, null, parent, false);

    private sealed class FakeClock : ISessionClock
    {
        public DateTimeOffset UtcNow => Start + Elapsed;
        public TimeSpan Elapsed { get; private set; }
        public Action? Advance { get; set; }
        public Action? OnDelay { get; set; }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Elapsed += delay;
            Advance?.Invoke(); OnDelay?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IProcessObserver
    {
        private readonly ProcessObservation[][] frames;
        private int frame;
        public FakeClock Clock { get; } = new();
        public List<Handle> Handles { get; } = new();
        public List<string> Log { get; } = new();
        public bool FailCapture { get; init; }
        public Fixture(params ProcessObservation[][] frames)
        {
            this.frames = frames;
            Clock.Advance = () =>
            {
                frame = Math.Min(frame + 1, frames.Length - 1);
                foreach (var handle in Handles)
                    if (!frames[frame].Any(observation => observation.Identity == handle.Identity)) handle.Exit(Clock.UtcNow);
            };
        }
        public IReadOnlyList<ProcessObservation> Capture() => FailCapture ? throw new IOException("Observation failed") : frames[frame];
        public ITrackedGameProcess? TryTrack(ProcessIdentity identity)
        {
            var handle = new Handle(identity);
            Handles.Add(handle);
            return handle;
        }
        public Task<GameSessionResult> Run(ProcessObservation? initial, GameSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            ITrackedGameProcess? handle = initial?.Identity is { } identity ? TryTrack(identity) : null;
            var evidence = new GameLaunchEvidence(Guid.NewGuid(), Start, "target", "game.exe", null, null,
                Array.Empty<ProcessObservation>(), handle, initial);
            return new GameSessionMonitor(this, options ?? Options, Clock).MonitorAsync(evidence, Log.Add, cancellationToken);
        }
    }

    private sealed class Handle(ProcessIdentity identity) : ITrackedGameProcess
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProcessIdentity Identity => identity;
        public bool HasExited { get; private set; }
        public DateTimeOffset? ExitedAtUtc { get; private set; }
        public int Disposed { get; private set; }
        public void Exit(DateTimeOffset time) { HasExited = true; ExitedAtUtc = time; exit.TrySetResult(); }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => exit.Task.WaitAsync(cancellationToken);
        public void Dispose() => Disposed++;
    }
}
