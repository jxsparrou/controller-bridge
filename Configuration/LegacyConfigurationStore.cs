using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using SBridge.Core;

namespace SBridge.Configuration;

internal sealed class LegacyConfigurationDocument
{
    private byte[]? fingerprint;
    internal LegacyConfigurationDocument(string path, AppSettings settings, string[] lines, byte[]? bytes)
    {
        Path = path; Settings = settings; Lines = lines;
        fingerprint = bytes == null ? null : SHA256.HashData(bytes);
    }
    public string Path { get; }
    public AppSettings Settings { get; }
    internal string[] Lines { get; private set; }
    public bool IsNew => fingerprint == null;
    internal bool Matches(byte[]? bytes) => bytes == null ? IsNew : fingerprint != null &&
        fingerprint.AsSpan().SequenceEqual(SHA256.HashData(bytes));
    internal void Accept(byte[] bytes) { fingerprint = SHA256.HashData(bytes); Lines = LegacyConfigurationCodec.Decode(bytes); }
}

internal sealed record SettingsSaveResult(bool Succeeded, string? Error);

internal sealed class LegacyConfigurationStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfigurationFiles files;
    public LegacyConfigurationStore(IConfigurationFiles? files = null) => this.files = files ?? new ConfigurationFileOperations();

    public LegacyConfigurationDocument Load(string path, AppSettings defaults)
    {
        path = System.IO.Path.GetFullPath(path);
        byte[]? bytes = ReadIfMissing(path);
        string[] lines = bytes == null ? Array.Empty<string>() : LegacyConfigurationCodec.Decode(bytes);
        return new LegacyConfigurationDocument(path, LegacyConfigurationCodec.Parse(lines, defaults), lines, bytes);
    }

    public SettingsSaveResult Save(LegacyConfigurationDocument document)
    {
        lock (Gates.GetOrAdd(document.Path, _ => new object()))
        {
            string temporary = document.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool staged = false;
            SettingsSaveResult result;
            string? cleanupError = null;
            try
            {
                CheckOriginal(document);
                byte[] output = LegacyConfigurationCodec.Serialize(document.Settings, document.Lines);
                staged = true;
                files.WriteNew(temporary, output);
                byte[] validation = files.Read(temporary);
                LegacyConfigurationCodec.Parse(LegacyConfigurationCodec.Decode(validation), new AppSettings());
                if (!output.AsSpan().SequenceEqual(validation)) throw new InvalidDataException("Temporary settings did not match serialized data.");
                CheckOriginal(document);
                if (document.IsNew) files.MoveNew(temporary, document.Path);
                else
                {
                    files.Backup(document.Path, document.Path + ".bak");
                    CheckOriginal(document);
                    files.Replace(temporary, document.Path);
                }
                staged = false;
                document.Accept(output);
                result = new SettingsSaveResult(true, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException)
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

    private byte[]? ReadIfMissing(string path)
    {
        try { return files.Read(path); }
        catch (FileNotFoundException) { return null; }
    }
    private void CheckOriginal(LegacyConfigurationDocument document)
    {
        if (!document.Matches(ReadIfMissing(document.Path)))
            throw new IOException("Settings changed or were removed since loading. Reopen settings before saving.");
    }
}
