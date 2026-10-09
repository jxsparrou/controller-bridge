using System;
using System.Collections.Generic;
using System.Linq;

namespace SBridge.Sessions;

internal enum GameProcessRole { Game, Bootstrap, Excluded }
internal sealed record ProcessCandidate(ProcessObservation Process, int Score, GameProcessRole Role, string Reason);
internal sealed record ProcessMatch(ProcessCandidate? Selected, bool Ambiguous, IReadOnlyList<ProcessCandidate> Candidates);

internal static class ProcessMatcher
{
    public static string ExecutableName(string value)
    {
        string normalized = value.Replace('/', '\\');
        int slash = normalized.LastIndexOf('\\');
        string name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static GameProcessRole Role(string name)
    {
        string bare = ExecutableName(name).ToLowerInvariant();
        if (bare is "sisr" or "viiper" or "steam" or "steamwebhelper" or "werfault" or
            "conhost" or "openconsole" or "dllhost" or "runtimebroker" ||
            bare.StartsWith("crashreport", StringComparison.Ordinal) || bare.StartsWith("uninstall", StringComparison.Ordinal) ||
            bare is "updater" or "update" or "installer") return GameProcessRole.Excluded;
        if (bare is "launcher" or "bootstrap" or "bootstrapper" or "gamelaunchhelper" or "applicationframehost" or
            "eadesktop" or "epicgameslauncher" or "ubisoftconnect" or "start_protected_game" ||
            bare.StartsWith("easyanticheat", StringComparison.Ordinal) || bare.StartsWith("eac_launcher", StringComparison.Ordinal))
            return GameProcessRole.Bootstrap;
        return GameProcessRole.Game;
    }

    public static bool IsExpected(ProcessObservation process, GameLaunchEvidence evidence) =>
        (!string.IsNullOrEmpty(evidence.ExpectedPath) && SamePath(process.ExecutablePath, evidence.ExpectedPath)) ||
        (!string.IsNullOrEmpty(evidence.ExpectedName) && string.Equals(ExecutableName(process.Name), evidence.ExpectedName, StringComparison.OrdinalIgnoreCase));

    public static ProcessMatch Select(GameLaunchEvidence evidence, IReadOnlyList<ProcessObservation> observations,
        IReadOnlyDictionary<int, ProcessLifetime> lineage, ISet<ProcessIdentity> exited, ProcessIdentity? current = null,
        DateTimeOffset? bootstrapNotBefore = null)
    {
        var candidates = new List<ProcessCandidate>();
        foreach (var observation in observations)
        {
            if (observation.Identity is not { } identity || identity == current || exited.Contains(identity)) continue;
            // Unknown creation time is not evidence of a new game. A baseline PID
            // with a different, post-launch creation time may legitimately be reused.
            if (evidence.BeforeLaunch.Any(old => old.Identity == identity ||
                (old.Id == identity.Id && old.StartedAtUtc == null && identity.StartedAtUtc <= evidence.StartedAtUtc))) continue;
            if (identity.StartedAtUtc < evidence.StartedAtUtc) continue;
            var role = Role(observation.Name);
            if (role == GameProcessRole.Excluded) continue;
            if (role == GameProcessRole.Bootstrap && bootstrapNotBefore is { } earliest && identity.StartedAtUtc < earliest) continue;
            bool path = !string.IsNullOrEmpty(evidence.ExpectedPath) && SamePath(observation.ExecutablePath, evidence.ExpectedPath);
            bool name = !string.IsNullOrEmpty(evidence.ExpectedName) &&
                string.Equals(ExecutableName(observation.Name), evidence.ExpectedName, StringComparison.OrdinalIgnoreCase);
            bool descendant = IsDescendant(observation, observations, lineage);
            bool installed = IsWithinDirectory(observation.ExecutablePath, evidence.InstallDirectory);
            if (name && !path && !string.IsNullOrEmpty(evidence.ExpectedPath) &&
                !string.IsNullOrEmpty(observation.ExecutablePath) && !descendant) continue;
            if (!path && !name && !descendant && !(installed && observation.HasVisibleWindow)) continue;
            // A bootstrap service is provisional evidence, not the final game.
            // Do not select a random anti-cheat process merely by install directory.
            if (role == GameProcessRole.Bootstrap && !descendant && !path && !name) continue;
            int score = (path ? 140 : 0) + (name ? 80 : 0) + (descendant ? 100 : 0) +
                (installed ? 35 : 0) + (observation.HasVisibleWindow ? 15 : 0) + (role == GameProcessRole.Game ? 20 : 0);
            string reason = string.Join(", ", new[] { path ? "path" : null, name ? "name" : null,
                descendant ? "descendant" : null, installed ? "install directory" : null,
                observation.HasVisibleWindow ? "visible window" : null }.Where(signal => signal != null));
            candidates.Add(new ProcessCandidate(observation, score, role, reason));
        }
        var ordered = candidates.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Process.Id).ToArray();
        // PID ordering is for diagnostics only, never a tie-break for adoption.
        bool ambiguous = ordered.Length > 1 && ordered[0].Score - ordered[1].Score < 20;
        return new ProcessMatch(ordered.Length == 0 || ambiguous ? null : ordered[0], ambiguous, ordered);
    }

    public static bool IsDescendant(ProcessObservation child, IReadOnlyList<ProcessObservation> observations,
        IReadOnlyDictionary<int, ProcessLifetime> lineage)
    {
        if (child.StartedAtUtc == null) return false;
        var visited = new HashSet<int> { child.Id };
        ProcessObservation cursor = child;
        for (int depth = 0; depth < 32 && cursor.ParentId is { } parentId; depth++)
        {
            if (!visited.Add(parentId)) return false;
            var parent = observations.FirstOrDefault(observation => observation.Id == parentId);
            if (lineage.TryGetValue(parentId, out var known))
            {
                if (parent != null && parent.Identity != known.Identity) return false; // Reused parent PID.
                return cursor.StartedAtUtc >= known.Identity.StartedAtUtc &&
                    (known.ExitedAtUtc == null || cursor.StartedAtUtc <= known.ExitedAtUtc);
            }
            if (parent?.Identity == null || cursor.StartedAtUtc < parent.StartedAtUtc) return false;
            cursor = parent;
        }
        return false;
    }

    public static bool SamePath(string? left, string? right) => !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsWithinDirectory(string? path, string? directory) => !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(directory) &&
        NormalizePath(path).StartsWith(NormalizePath(directory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string value) => value.Replace('/', '\\');
}
