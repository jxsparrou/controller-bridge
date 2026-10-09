using System;
using System.Collections.Generic;

namespace SBridge.Core;

internal enum SteamInputMode { Automatic, Enabled, Disabled }
internal sealed record GameProfile(SteamInputMode SteamInput = SteamInputMode.Automatic, string WatchProcess = "", SisrControllerProfile? Controller = null);

internal sealed class AppSettings
{
    public string SisrPath { get; set; } = "";
    public string SisrArguments { get; set; } = ""; // Legacy advanced escape hatch.
    public bool SisrEnabled { get; set; } = true;
    public bool ManagedSisrStartup { get; set; }
    public bool LogEnabled { get; set; } = true;
    public string SteamGridDbApiKey { get; set; } = ""; // Clear only in memory; the JSON codec protects persistence.
    public Dictionary<string, GameProfile> GameProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Guid, Game> Games { get; } = new();
    public HashSet<string> SelectedSteamAccountIds { get; } = new(StringComparer.Ordinal);

    public GameProfile GetProfile(string target) => GameProfiles.TryGetValue(target, out var profile) ? profile : new GameProfile();

    public bool IsSisrEnabledFor(string target) => GetProfile(target).SteamInput switch
    {
        SteamInputMode.Enabled => true,
        SteamInputMode.Disabled => false,
        _ => SisrEnabled
    };

    public void SetSteamInputMode(string target, SteamInputMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        SetProfile(target, GetProfile(target) with { SteamInput = mode });
    }

    public void SetWatchProcess(string target, string value) => SetProfile(target, GetProfile(target) with { WatchProcess = value });
    public void SetControllerProfile(string target, SisrControllerProfile? value)
    { value?.Validate(); SetProfile(target, GetProfile(target) with { Controller = value }); }

    private void SetProfile(string target, GameProfile profile)
    {
        if (string.IsNullOrWhiteSpace(target) || target.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("A game target is required and cannot contain line breaks or NUL.", nameof(target));
        ArgumentNullException.ThrowIfNull(profile.WatchProcess);
        profile.Controller?.Validate();
        if (profile.SteamInput == SteamInputMode.Automatic && profile.WatchProcess.Length == 0 && profile.Controller == null) GameProfiles.Remove(target);
        else GameProfiles[target] = profile;
    }

    public AppSettings Clone()
    {
        var copy = new AppSettings { SisrPath = SisrPath, SisrArguments = SisrArguments, SisrEnabled = SisrEnabled,
            LogEnabled = LogEnabled, SteamGridDbApiKey = SteamGridDbApiKey, ManagedSisrStartup = ManagedSisrStartup };
        foreach (var pair in GameProfiles) copy.GameProfiles.Add(pair.Key, pair.Value);
        foreach (var pair in Games) copy.Games.Add(pair.Key, pair.Value);
        foreach (string id in SelectedSteamAccountIds) copy.SelectedSteamAccountIds.Add(id);
        return copy;
    }
}
