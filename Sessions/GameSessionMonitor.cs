using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Sessions;

internal sealed record GameSessionOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LaunchSearchTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(3);
    public int MaxHandoffs { get; init; } = 32;
    public TimeSpan MaxProvisionalLifetime { get; init; } = TimeSpan.FromMinutes(10);
}

internal enum GameSessionOutcome { Completed, NotFound, HandoffLimit }
internal sealed record GameSessionResult(GameSessionOutcome Outcome, int Handoffs, ProcessIdentity? FinalProcess);

internal sealed class GameSessionMonitor
{
    private readonly IProcessObserver observer;
    private readonly ISessionClock clock;
    private readonly GameSessionOptions options;

    public GameSessionMonitor(IProcessObserver observer, GameSessionOptions? options = null, ISessionClock? clock = null)
    {
        this.observer = observer;
        this.clock = clock ?? new SessionClock();
        this.options = options ?? new GameSessionOptions();
        if (this.options.PollInterval <= TimeSpan.Zero || this.options.LaunchSearchTimeout <= TimeSpan.Zero ||
            this.options.ExitGrace < TimeSpan.Zero || this.options.MaxHandoffs < 1 || this.options.MaxProvisionalLifetime <= TimeSpan.Zero)
            throw new ArgumentException("Session polling/search budgets must be positive and handoffs bounded.", nameof(options));
    }

    public async Task<GameSessionResult> MonitorAsync(GameLaunchEvidence evidence, Action<string> log, CancellationToken cancellationToken)
    {
        ITrackedGameProcess? current = evidence.InitialProcess;
        CancellationTokenSource? waitCancellation = null;
        Task? exitTask = null;
        var lineage = new Dictionary<int, ProcessLifetime>();
        var exited = new HashSet<ProcessIdentity>();
        var ancestors = new Dictionary<ProcessIdentity, ITrackedGameProcess>();
        ProcessIdentity? final = current?.Identity;
        int handoffs = 0;
        bool activeGame = false;
        bool everActiveGame = false;
        bool everTracked = current != null;
        TimeSpan? searchingSince = current == null ? clock.Elapsed : null;
        TimeSpan searchBudget = options.LaunchSearchTimeout;
        string? lastDecision = null;
        TimeSpan trackedSince = clock.Elapsed;
        DateTimeOffset? bootstrapNotBefore = null;

        void Report(string message) => log("Session " + evidence.SessionId.ToString("N") + ": " + message);
        void BeginWait()
        {
            if (current == null) return;
            waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exitTask = current.WaitForExitAsync(waitCancellation.Token);
            lineage[current.Identity.Id] = new ProcessLifetime(current.Identity, null);
        }
        async Task EndWaitAsync()
        {
            if (waitCancellation != null)
            {
                waitCancellation.Cancel();
                try { if (exitTask != null) await exitTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* The observation wait was explicitly cancelled. */ }
                finally { waitCancellation.Dispose(); waitCancellation = null; exitTask = null; }
            }
        }

        try
        {
            Report("Monitoring target=" + evidence.Target + ", hint=" + evidence.ExpectedName);
            if (current == null && string.IsNullOrEmpty(evidence.ExpectedName) && string.IsNullOrEmpty(evidence.InstallDirectory))
                return new GameSessionResult(GameSessionOutcome.NotFound, 0, null);
            BeginWait();
            // Authoritative direct launches can complete quickly without a window;
            // they should not incur a 90-second search on every ordinary quit.
            if (current != null)
            {
                var initial = evidence.InitialObservation ?? observer.Capture().FirstOrDefault(process => process.Identity == current.Identity);
                activeGame = initial != null && ProcessMatcher.Role(initial.Name) == GameProcessRole.Game &&
                    (string.IsNullOrEmpty(evidence.ExpectedName) || ProcessMatcher.IsExpected(initial, evidence));
                everActiveGame = activeGame;
                Report("Initial PID=" + current.Identity.Id + (activeGame ? " (game)" : " (provisional launcher)"));
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var ancestor in ancestors.ToArray())
                {
                    if (!ancestor.Value.HasExited) continue;
                    lineage[ancestor.Key.Id] = new ProcessLifetime(ancestor.Key, ancestor.Value.ExitedAtUtc ?? clock.UtcNow);
                    ancestor.Value.Dispose();
                    ancestors.Remove(ancestor.Key);
                }
                if (current != null && !activeGame && clock.Elapsed - trackedSince >= options.MaxProvisionalLifetime)
                {
                    Report("Provisional process exceeded the bounded startup lifetime.");
                    return new GameSessionResult(GameSessionOutcome.NotFound, handoffs, final);
                }
                if (current != null && current.HasExited)
                {
                    var identity = current.Identity;
                    exited.Add(identity);
                    var exitTime = current.ExitedAtUtc ?? clock.UtcNow;
                    lineage[identity.Id] = new ProcessLifetime(identity, exitTime);
                    bootstrapNotBefore = activeGame ? exitTime.AddSeconds(-10) : null;
                    Report("Exited PID=" + identity.Id + "; checking for handoff.");
                    await EndWaitAsync().ConfigureAwait(false);
                    current.Dispose();
                    current = null;
                    searchingSince = clock.Elapsed;
                    searchBudget = activeGame ? options.ExitGrace : options.LaunchSearchTimeout;
                }

                var observations = observer.Capture();
                var match = ProcessMatcher.Select(evidence, observations, lineage, exited, current?.Identity, bootstrapNotBefore);
                bool canSwitch = current == null || !activeGame;
                if (canSwitch && match.Selected is { } candidate && candidate.Process.Identity is { } selected)
                {
                    if (everTracked && handoffs >= options.MaxHandoffs)
                        return new GameSessionResult(GameSessionOutcome.HandoffLimit, handoffs, final);
                    var next = observer.TryTrack(selected);
                    if (next != null)
                    {
                        // Keep live retired ancestors until their exact exit time is
                        // known; cancel their waits and never re-adopt them as primary.
                        if (current != null)
                        {
                            try
                            {
                                lineage[current.Identity.Id] = new ProcessLifetime(current.Identity, current.ExitedAtUtc);
                                exited.Add(current.Identity); // Never re-adopt a retired live launcher.
                                await EndWaitAsync().ConfigureAwait(false);
                                ancestors.Add(current.Identity, current);
                            }
                            catch { next.Dispose(); throw; }
                        }
                        if (everTracked) handoffs++;
                        current = next;
                        final = next.Identity;
                        everTracked = true;
                        searchingSince = null;
                        trackedSince = clock.Elapsed;
                        bootstrapNotBefore = null;
                        activeGame = candidate.Role == GameProcessRole.Game &&
                            (ProcessMatcher.IsExpected(candidate.Process, evidence) || candidate.Process.HasVisibleWindow);
                        everActiveGame |= activeGame;
                        BeginWait();
                        Report("Matched PID=" + selected.Id + ", score=" + candidate.Score + ", " + candidate.Reason +
                            (activeGame ? " (game)" : candidate.Role == GameProcessRole.Bootstrap ? " (provisional bootstrap)" : " (provisional process)"));
                        lastDecision = null;
                        continue;
                    }
                }
                string decision = match.Ambiguous ? "Ambiguous candidates: " + string.Join(", ", match.Candidates.Select(candidate =>
                    candidate.Process.Id + "/" + candidate.Score + " " + candidate.Reason)) : "No eligible replacement yet.";
                if (current == null && match.Candidates.Count == 0 && !string.IsNullOrEmpty(evidence.ExpectedName))
                {
                    var rejected = observations.Where(process => string.Equals(ProcessMatcher.ExecutableName(process.Name),
                        evidence.ExpectedName, StringComparison.OrdinalIgnoreCase)).Take(10).Select(process =>
                        process.Id + ": " + (process.Identity == null ? "unreadable creation time" :
                            evidence.BeforeLaunch.Any(old => old.Identity == process.Identity) || process.StartedAtUtc < evidence.StartedAtUtc
                                ? "prelaunch process" : "retired/excluded or insufficient session evidence"));
                    string details = string.Join("; ", rejected);
                    if (details.Length != 0) decision += " Rejected hint candidates: " + details;
                }
                if (current == null && decision != lastDecision) { Report(decision); lastDecision = decision; }
                if (current == null && searchingSince is { } started && clock.Elapsed - started >= searchBudget)
                {
                    Report(match.Ambiguous ? "Session ended with unresolved process ambiguity." :
                        everActiveGame ? "Session completed after bounded exit/handoff grace." : "No game process confirmed within launch budget.");
                    return new GameSessionResult(everActiveGame && !match.Ambiguous ? GameSessionOutcome.Completed : GameSessionOutcome.NotFound, handoffs, final);
                }
                // One retained exit wait per tracked process; delay waits are cancelled
                // when exit wins, avoiding accumulating timers/tasks on each handoff.
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task delay = clock.DelayAsync(options.PollInterval, delayCancellation.Token);
                if (exitTask != null && await Task.WhenAny(exitTask, delay).ConfigureAwait(false) == exitTask)
                {
                    delayCancellation.Cancel();
                    try { await delay.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested) { }
                    await exitTask.ConfigureAwait(false);
                }
                else await delay.ConfigureAwait(false);
            }
        }
        finally
        {
            try { await EndWaitAsync().ConfigureAwait(false); }
            finally
            {
                current?.Dispose();
                foreach (var ancestor in ancestors.Values) ancestor.Dispose();
            }
        }
    }
}
