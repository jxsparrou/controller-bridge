using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using SBridge.Core;

namespace SBridge.Configuration;

internal sealed class JsonSettingsDocument
{
    private byte[]? fingerprint;
    internal JsonSettingsDocument(string path, AppSettings settings, JsonSettingsData? data, byte[]? original,
        LegacyMigrationInfo? migration = null)
    {
        Path = path; Settings = settings; Data = data; Migration = migration;
        fingerprint = original == null ? null : SHA256.HashData(original);
    }
    public string Path { get; }
    public AppSettings Settings { get; }
    public JsonSettingsData? Data { get; private set; }
    public LegacyMigrationInfo? Migration { get; }
    public bool IsNew => fingerprint == null;
    internal bool Matches(byte[]? bytes) => bytes == null ? IsNew : fingerprint != null && fingerprint.AsSpan().SequenceEqual(SHA256.HashData(bytes));
    internal void Accept(byte[] bytes, JsonSettingsData data) { fingerprint = SHA256.HashData(bytes); Data = data; }
}

internal sealed class JsonSettingsStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISecretProtector protector;
    private readonly IConfigurationFiles files;

    public JsonSettingsStore(ISecretProtector protector, IConfigurationFiles? files = null)
    { this.protector = protector; this.files = files ?? new ConfigurationFileOperations(); }

    public JsonSettingsDocument LoadOrMigrate(string path, string legacyPath, AppSettings defaults)
    {
        path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        lock (Gates.GetOrAdd(path, _ => new object()))
        {
            byte[]? existing = ReadMissing(path);
            if (existing != null)
            {
                var decoded = JsonSettingsCodec.Decode(existing, protector);
                return new JsonSettingsDocument(path, decoded.Settings, decoded.Data, existing);
            }
            legacyPath = System.IO.Path.GetFullPath(legacyPath);
            byte[]? legacy = ReadMissing(legacyPath);
            AppSettings settings = legacy == null ? defaults.Clone() :
                LegacyConfigurationCodec.Parse(LegacyConfigurationCodec.Decode(legacy), defaults);
            var migration = legacy == null ? null : new LegacyMigrationInfo(legacyPath,
                Convert.ToHexString(SHA256.HashData(legacy)), DateTimeOffset.UtcNow);
            var document = new JsonSettingsDocument(path, settings, null, null, migration);
            var result = Save(document, () =>
            {
                byte[]? now = ReadMissing(legacyPath);
                if ((legacy == null) != (now == null) || (legacy != null && now != null && !legacy.AsSpan().SequenceEqual(now)))
                    throw new IOException("Legacy settings changed during migration. Retry after the edit completes.");
            });
            if (!result.Succeeded)
            {
                // Another executable copy can win initial migration between our
                // absence check and non-overwriting move. Existing JSON is still
                // authoritative: validate/load it, never merge or overwrite it.
                byte[]? winner = ReadMissing(path);
                if (winner != null)
                {
                    var decoded = JsonSettingsCodec.Decode(winner, protector);
                    return new JsonSettingsDocument(path, decoded.Settings, decoded.Data, winner);
                }
                throw new IOException("Could not create/migrate JSON settings. The legacy file was retained. " + result.Error);
            }
            return document;
        }
    }

    public SettingsSaveResult Save(JsonSettingsDocument document) => Save(document, null);

    private SettingsSaveResult Save(JsonSettingsDocument document, Action? checkMigrationSource)
    {
        lock (Gates.GetOrAdd(document.Path, _ => new object()))
        {
            string temporary = document.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool staged = false;
            SettingsSaveResult result;
            string? cleanupError = null;
            try
            {
                CheckOriginal(document); checkMigrationSource?.Invoke();
                byte[] output = JsonSettingsCodec.Encode(document.Settings, document.Data, protector, document.Migration);
                staged = true;
                files.WriteNew(temporary, output);
                byte[] validated = files.Read(temporary);
                var decoded = JsonSettingsCodec.Decode(validated, protector);
                if (!output.AsSpan().SequenceEqual(validated) || decoded.Settings.SteamGridDbApiKey != document.Settings.SteamGridDbApiKey)
                    throw new InvalidDataException("Temporary JSON settings did not match the intended document.");
                CheckOriginal(document); checkMigrationSource?.Invoke();
                if (document.IsNew) files.MoveNew(temporary, document.Path);
                else
                {
                    files.Backup(document.Path, document.Path + ".bak");
                    CheckOriginal(document);
                    files.Replace(temporary, document.Path);
                }
                staged = false;
                document.Accept(output, decoded.Data);
                result = new SettingsSaveResult(true, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or CryptographicException or System.Security.SecurityException)
            { result = new SettingsSaveResult(false, ex.Message); }
            finally
            {
                if (staged)
                {
                    try { files.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupError = " Temporary cleanup failed: " + temporary; }
                }
            }
            return cleanupError == null ? result : result with { Error = result.Error + cleanupError };
        }
    }

    private byte[]? ReadMissing(string path)
    {
        try { return files.Read(path); }
        catch (FileNotFoundException) { return null; }
    }
    private void CheckOriginal(JsonSettingsDocument document)
    {
        if (!document.Matches(ReadMissing(document.Path))) throw new IOException("JSON settings changed or were removed since loading. Reopen settings before saving.");
    }
}
