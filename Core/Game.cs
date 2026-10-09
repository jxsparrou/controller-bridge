using System;
using System.Collections.Generic;
using System.Linq;

namespace SBridge.Core;

internal enum GameLaunchKind { Executable, PackagedApplication, EpicLauncher }

internal sealed class Game
{
    public Game(Guid id, string name, string provider, string providerId, GameLaunchKind launchKind,
        string target, IEnumerable<string> arguments, string processHint, string? installDirectory)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty sBridge game ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("A game requires a name, provider identity, and launch target.");
        if (!Enum.IsDefined(launchKind)) throw new ArgumentOutOfRangeException(nameof(launchKind));
        foreach (string value in new[] { name, provider, providerId, target, processHint }) ValidateText(value);
        if (installDirectory != null) ValidateText(installDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        string[] tokens = arguments.ToArray();
        foreach (string token in tokens) ValidateText(token);
        if (launchKind == GameLaunchKind.EpicLauncher)
        {
            if (!provider.Equals("epic", StringComparison.OrdinalIgnoreCase) || target != EpicLaunchIdentity.Target(providerId))
                throw new ArgumentException("Epic registrations require a matching catalog identity and launcher target.");
            if (tokens.Length != 0) throw new ArgumentException("Extra Epic arguments are unsupported; configure them in Epic Launcher.");
        }
        Id = id; Name = name; Provider = provider; ProviderId = providerId; LaunchKind = launchKind;
        Target = target; Arguments = Array.AsReadOnly(tokens); ProcessHint = processHint; InstallDirectory = installDirectory;
    }

    public Guid Id { get; }
    public string ProfileKey => Id.ToString("D");
    public string Name { get; }
    public string Provider { get; }
    public string ProviderId { get; }
    public GameLaunchKind LaunchKind { get; }
    public string Target { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string ProcessHint { get; }
    public string? InstallDirectory { get; }
    internal string RegistrationProviderId => Provider.Equals("win32", StringComparison.OrdinalIgnoreCase) ? ProviderId.Replace('/', '\\') : ProviderId;

    public bool SameRegistration(Game other)
    {
        StringComparison identityComparison = Provider.Equals("win32", StringComparison.OrdinalIgnoreCase) || Provider.Equals("xbox", StringComparison.OrdinalIgnoreCase)
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Provider, other.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(RegistrationProviderId, other.RegistrationProviderId, identityComparison) && LaunchKind == other.LaunchKind &&
            Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal);
    }

    internal Game WithId(Guid id) => new(id, Name, Provider, ProviderId, LaunchKind, Target, Arguments, ProcessHint, InstallDirectory);

    private static void ValidateText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("Game text/arguments cannot contain NUL.");
    }
}

internal static class GameCatalog
{
    public static bool TryCommit(AppSettings settings, IReadOnlyList<Game> definitions, IReadOnlyList<GameProfile?> choices,
        Func<bool> persist, out List<Game> registered)
    {
        var before = settings.Clone();
        registered = new List<Game>();
        bool committed = false;
        try
        {
            if (definitions.Count != choices.Count) throw new ArgumentException("Each game definition requires one profile choice.");
            for (int index = 0; index < definitions.Count; index++) registered.Add(Register(settings, definitions[index], choices[index]));
            committed = persist();
            return committed;
        }
        finally
        {
            if (!committed)
            {
                // Scalar UI input remains for retry; registration/profile changes
                // are one persistence unit and must not leak after a failed save.
                settings.Games.Clear(); foreach (var pair in before.Games) settings.Games.Add(pair.Key, pair.Value);
                settings.GameProfiles.Clear(); foreach (var pair in before.GameProfiles) settings.GameProfiles.Add(pair.Key, pair.Value);
                registered.Clear();
            }
        }
    }

    public static Game Register(AppSettings settings, Game definition, GameProfile? profile = null)
    {
        var matches = settings.Games.Values.Where(game => game.SameRegistration(definition)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Ambiguous stored game identity; registration was not changed.");
        bool existing = matches.Length == 1;
        Game game = definition.WithId(existing ? matches[0].Id : definition.Id);
        if (!existing && settings.Games.ContainsKey(game.Id)) throw new InvalidOperationException("Game ID collision; registration was not changed.");
        var choice = profile ?? settings.GetProfile(existing ? game.ProfileKey : game.Target);
        if (existing && profile != null && profile.Controller == null)
            choice = choice with { Controller = settings.GetProfile(game.ProfileKey).Controller };
        var validation = new AppSettings();
        validation.SetSteamInputMode(game.ProfileKey, choice.SteamInput);
        validation.SetWatchProcess(game.ProfileKey, choice.WatchProcess);
        validation.SetControllerProfile(game.ProfileKey, choice.Controller);
        settings.Games[game.Id] = game;
        if (profile != null || !existing)
        {
            settings.SetSteamInputMode(game.ProfileKey, choice.SteamInput);
            settings.SetWatchProcess(game.ProfileKey, choice.WatchProcess);
            settings.SetControllerProfile(game.ProfileKey, choice.Controller);
        }
        return game;
    }
}

internal sealed class GameRegistrationComparer : IEqualityComparer<Game>
{
    public bool Equals(Game? left, Game? right) => ReferenceEquals(left, right) || left != null && right != null && left.SameRegistration(right);
    public int GetHashCode(Game game)
    {
        var hash = new HashCode();
        hash.Add(game.Provider, StringComparer.OrdinalIgnoreCase);
        hash.Add(game.RegistrationProviderId, game.Provider.Equals("win32", StringComparison.OrdinalIgnoreCase) || game.Provider.Equals("xbox", StringComparison.OrdinalIgnoreCase)
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        hash.Add(game.LaunchKind);
        foreach (string argument in game.Arguments) hash.Add(argument, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
