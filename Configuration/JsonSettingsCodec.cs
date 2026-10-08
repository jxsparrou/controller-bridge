using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SBridge.Core;
using SBridge.Steam;

namespace SBridge.Configuration;

internal sealed class JsonGameProfile
{
    public required SteamInputMode SteamInput { get; set; }
    public required string WatchProcess { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class JsonGameData
{
    public required string Name { get; set; }
    public required string Provider { get; set; }
    public required string ProviderId { get; set; }
    public required GameLaunchKind LaunchKind { get; set; }
    public required string Target { get; set; }
    public required string[] Arguments { get; set; }
    public required string ProcessHint { get; set; }
    public required string? InstallDirectory { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed record LegacyMigrationInfo(string SourcePath, string SourceSha256, DateTimeOffset ImportedAtUtc)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

internal sealed class JsonSettingsData
{
    public required int SchemaVersion { get; set; }
    public required string SisrPath { get; set; }
    public required string SisrArguments { get; set; }
    public required bool SisrEnabled { get; set; }
    public bool ManagedSisrStartup { get; set; }
    public required bool LogEnabled { get; set; }
    public required string? ProtectedSteamGridDbApiKey { get; set; }
    public required Dictionary<string, JsonGameProfile> GameProfiles { get; set; }
    public LegacyMigrationInfo? LegacyMigration { get; set; }
    // Optional in schema 1: prior 3A files load without a destructive version rewrite.
    public Dictionary<string, JsonGameData>? Games { get; set; }
    public string[]? SelectedSteamAccountIds { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed record DecodedSettings(AppSettings Settings, JsonSettingsData Data);

internal static class JsonSettingsCodec
{
    public const int SchemaVersion = 1;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter<SteamInputMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<GameLaunchKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static DecodedSettings Decode(byte[] bytes, ISecretProtector protector)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("JSON settings exceed the 4 MiB limit.");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("JSON settings must be an object.");
            ValidateObject(document.RootElement);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != SchemaVersion)
                throw new InvalidDataException("Unsupported or missing settings schemaVersion. Existing settings were left unchanged.");
            if (document.RootElement.EnumerateObject().Any(property => property.Name.Equals("steamGridDbApiKey", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("sgdbApiKey", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("JSON settings must use a protected credential, not a plaintext key.");
            var data = JsonSerializer.Deserialize<JsonSettingsData>(bytes, Options) ?? throw new InvalidDataException("JSON settings must be an object.");
            ValidateText(data.SisrPath); ValidateText(data.SisrArguments);
            if (data.GameProfiles == null) throw new InvalidDataException("Missing gameProfiles object.");
            if (document.RootElement.TryGetProperty("games", out var catalog) && catalog.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The stored games catalog must be an object.");
            var settings = new AppSettings { SisrPath = data.SisrPath, SisrArguments = data.SisrArguments,
                SisrEnabled = data.SisrEnabled, LogEnabled = data.LogEnabled, ManagedSisrStartup = data.ManagedSisrStartup,
                SteamGridDbApiKey = data.ProtectedSteamGridDbApiKey == null ? "" : protector.Unprotect(data.ProtectedSteamGridDbApiKey) };
            if (document.RootElement.TryGetProperty("selectedSteamAccountIds", out var selected) && selected.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("selectedSteamAccountIds must be an array.");
            foreach (string id in data.SelectedSteamAccountIds ?? Array.Empty<string>())
                if (id == null || !SteamAccount.ValidId(id) || !settings.SelectedSteamAccountIds.Add(id))
                    throw new InvalidDataException("Invalid or duplicate selected Steam account ID.");
            foreach (var pair in data.GameProfiles)
            {
                ValidateText(pair.Key);
                if (pair.Value == null || !Enum.IsDefined(pair.Value.SteamInput)) throw new InvalidDataException("Invalid game profile.");
                ValidateText(pair.Value.WatchProcess);
                if (settings.GameProfiles.ContainsKey(pair.Key)) throw new InvalidDataException("Duplicate case-insensitive game target.");
                settings.SetSteamInputMode(pair.Key, pair.Value.SteamInput);
                settings.SetWatchProcess(pair.Key, pair.Value.WatchProcess);
                // Keep automatic/empty entries represented so casing duplicates
                // cannot evade validation merely because the domain prunes defaults.
                settings.GameProfiles[pair.Key] = new GameProfile(pair.Value.SteamInput, pair.Value.WatchProcess);
            }
            ValidateText(settings.SteamGridDbApiKey);
            if (data.Games != null)
            {
                foreach (var pair in data.Games)
                {
                    if (!Guid.TryParseExact(pair.Key, "D", out Guid id) || id == Guid.Empty || pair.Value == null || settings.Games.ContainsKey(id))
                        throw new InvalidDataException("Invalid or duplicate sBridge game ID.");
                    var value = pair.Value;
                    ValidateText(value.Name); ValidateText(value.Provider); ValidateText(value.ProviderId);
                    ValidateText(value.Target); ValidateText(value.ProcessHint);
                    if (value.InstallDirectory != null) ValidateText(value.InstallDirectory);
                    if (value.Arguments == null) throw new InvalidDataException("Game arguments must be an array.");
                    foreach (string argument in value.Arguments) ValidateText(argument);
                    settings.Games.Add(id, new Game(id, value.Name, value.Provider, value.ProviderId, value.LaunchKind,
                        value.Target, value.Arguments, value.ProcessHint, value.InstallDirectory));
                }
                ValidateGameIdentities(settings);
            }
            return new DecodedSettings(settings, data);
        }
        catch (JsonException ex) { throw new InvalidDataException("Malformed JSON settings.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Invalid JSON game target or setting.", ex); }
    }

    public static byte[] Encode(AppSettings settings, JsonSettingsData? previous, ISecretProtector protector,
        LegacyMigrationInfo? migration = null)
    {
        ValidateText(settings.SisrPath); ValidateText(settings.SisrArguments); ValidateText(settings.SteamGridDbApiKey);
        var profiles = new Dictionary<string, JsonGameProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.GameProfiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            ValidateText(pair.Key);
            var validation = new AppSettings();
            validation.SetSteamInputMode(pair.Key, pair.Value.SteamInput); validation.SetWatchProcess(pair.Key, pair.Value.WatchProcess);
            ValidateText(pair.Value.WatchProcess);
            var old = previous?.GameProfiles.FirstOrDefault(entry => string.Equals(entry.Key, pair.Key, StringComparison.OrdinalIgnoreCase)).Value;
            profiles.Add(pair.Key, new JsonGameProfile { SteamInput = pair.Value.SteamInput, WatchProcess = pair.Value.WatchProcess,
                Extra = old?.Extra });
        }
        string? protectedKey = null;
        if (settings.SteamGridDbApiKey.Length != 0)
            protectedKey = previous?.ProtectedSteamGridDbApiKey is { } stored && protector.Unprotect(stored) == settings.SteamGridDbApiKey
                ? stored : protector.Protect(settings.SteamGridDbApiKey);
        ValidateGameIdentities(settings);
        var games = new Dictionary<string, JsonGameData>();
        foreach (var pair in settings.Games.OrderBy(entry => entry.Key))
        {
            var game = pair.Value;
            if (pair.Key != game.Id || pair.Key == Guid.Empty) throw new InvalidDataException("Mismatched sBridge game ID.");
            ValidateText(game.Name); ValidateText(game.Provider); ValidateText(game.ProviderId); ValidateText(game.Target); ValidateText(game.ProcessHint);
            if (game.InstallDirectory != null) ValidateText(game.InstallDirectory);
            foreach (string argument in game.Arguments) ValidateText(argument);
            games.Add(game.ProfileKey, new JsonGameData { Name = game.Name, Provider = game.Provider, ProviderId = game.ProviderId,
                LaunchKind = game.LaunchKind, Target = game.Target, Arguments = game.Arguments.ToArray(), ProcessHint = game.ProcessHint,
                InstallDirectory = game.InstallDirectory, Extra = previous?.Games?.FirstOrDefault(entry =>
                    string.Equals(entry.Key, game.ProfileKey, StringComparison.OrdinalIgnoreCase)).Value?.Extra });
        }
        var data = new JsonSettingsData
        {
            SchemaVersion = SchemaVersion, SisrPath = settings.SisrPath, SisrArguments = settings.SisrArguments,
            ManagedSisrStartup = settings.ManagedSisrStartup,
            SisrEnabled = settings.SisrEnabled, LogEnabled = settings.LogEnabled,
            ProtectedSteamGridDbApiKey = protectedKey,
            GameProfiles = profiles, Games = games, SelectedSteamAccountIds = settings.SelectedSteamAccountIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            LegacyMigration = migration ?? previous?.LegacyMigration, Extra = previous?.Extra
        };
        if (settings.SelectedSteamAccountIds.Any(id => id == null || !SteamAccount.ValidId(id)))
            throw new InvalidDataException("Invalid selected Steam account ID.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, Options);
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("JSON settings exceed the 4 MiB limit.");
        return bytes;
    }

    private static void ValidateText(string? value)
    {
        if (value == null || value.Contains('\0')) throw new InvalidDataException("JSON setting text cannot be null or contain NUL.");
        try { _ = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("JSON setting text contains invalid Unicode.", ex); }
    }

    private static void ValidateGameIdentities(AppSettings settings)
    {
        var seen = new HashSet<Game>(new GameRegistrationComparer());
        foreach (var game in settings.Games.Values)
        {
            if (!seen.Add(game)) throw new InvalidDataException("Duplicate provider/game/argument registration.");
        }
    }

    private static void ValidateObject(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON setting/property name.");
                ValidateObject(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateObject(item);
    }
}
