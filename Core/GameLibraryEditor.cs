using System;
using System.Linq;

namespace SBridge.Core;

internal static class GameLibraryEditor
{
    public static Game Definition(Game original, string name, string target, string[] arguments, string processHint, string? installDirectory)
    {
        string providerId = original.ProviderId;
        // Standard path/AUMID identities track an intentional target edit. Opaque
        // provider identities (including packaged Win32 registrations) stay fixed.
        if (original.Provider.Equals("win32", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(providerId.Replace('/', '\\'), original.Target.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)) providerId = target;
        else if (original.Provider.Equals("xbox", StringComparison.OrdinalIgnoreCase) && original.LaunchKind == GameLaunchKind.PackagedApplication &&
            string.Equals(providerId, original.Target, StringComparison.OrdinalIgnoreCase)) providerId = target;
        return new Game(original.Id, name, original.Provider, providerId, original.LaunchKind, target, arguments, processHint, installDirectory);
    }

    public static bool TryCommit(AppSettings settings, Game original, GameProfile originalProfile, Game replacement,
        GameProfile profile, Func<bool> persist)
    {
        if (!settings.Games.TryGetValue(original.Id, out var current) || !ReferenceEquals(current, original) ||
            settings.GetProfile(original.ProfileKey) != originalProfile)
            throw new InvalidOperationException("The registered game or profile changed while editing. Reload it before saving.");
        if (replacement.Id != original.Id || replacement.Provider != original.Provider || replacement.LaunchKind != original.LaunchKind)
            throw new ArgumentException("Library edits must retain the UUID, provider and explicit launch kind.");
        var allowed = Definition(original, replacement.Name, replacement.Target, replacement.Arguments.ToArray(), replacement.ProcessHint, replacement.InstallDirectory);
        if (replacement.ProviderId != allowed.ProviderId)
            throw new ArgumentException("Provider identity must remain opaque or follow its standard edited target.");
        if (settings.Games.Values.Any(game => game.Id != original.Id && game.SameRegistration(replacement)))
            throw new InvalidOperationException("Another registered game already has that provider identity and argument variant.");
        var validate = new AppSettings();
        validate.SetSteamInputMode(original.ProfileKey, profile.SteamInput); validate.SetWatchProcess(original.ProfileKey, profile.WatchProcess);
        validate.SetControllerProfile(original.ProfileKey, profile.Controller);
        bool hadProfile = settings.GameProfiles.TryGetValue(original.ProfileKey, out var beforeProfile);
        bool saved = false;
        try
        {
            settings.Games[original.Id] = replacement;
            // Keep a selected game's explicit empty/automatic profile represented
            // so codec extension data survives editing both fields back to defaults.
            settings.GameProfiles[original.ProfileKey] = profile;
            saved = persist(); return saved;
        }
        finally
        {
            if (!saved)
            {
                settings.Games[original.Id] = original;
                if (hadProfile) settings.GameProfiles[original.ProfileKey] = beforeProfile!;
                else settings.GameProfiles.Remove(original.ProfileKey);
            }
        }
    }
}
