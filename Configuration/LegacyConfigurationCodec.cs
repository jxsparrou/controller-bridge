using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SBridge.Core;

namespace SBridge.Configuration;

internal static class LegacyConfigurationCodec
{
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string[] Decode(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Legacy settings exceed the 4 MiB limit.");
        Encoding encoding = Utf8;
        int offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) { encoding = new UTF32Encoding(false, false, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, false, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) offset = 3;
        var lines = new List<string>();
        try
        {
            using var reader = new StringReader(encoding.GetString(bytes, offset, bytes.Length - offset));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains('\0')) throw new InvalidDataException("Legacy settings contain NUL.");
                lines.Add(line);
            }
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Legacy settings contain invalid text encoding.", ex); }
        return lines.ToArray();
    }

    public static AppSettings Parse(IReadOnlyList<string> lines, AppSettings defaults)
    {
        var settings = defaults.Clone();
        foreach (string line in lines)
        {
            if (!TryEntry(line, out string key, out string value))
            {
                string trimmed = line.Trim();
                if (trimmed.Length != 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith(';'))
                    throw new InvalidDataException("Malformed legacy settings line; expected key=value or a comment.");
                continue;
            }
            if (key.Equals("SisrPath", StringComparison.OrdinalIgnoreCase)) settings.SisrPath = value;
            else if (key.Equals("SisrArguments", StringComparison.OrdinalIgnoreCase)) settings.SisrArguments = value;
            else if (key.Equals("SisrEnabled", StringComparison.OrdinalIgnoreCase)) settings.SisrEnabled = Boolean(key, value);
            else if (key.Equals("LogEnabled", StringComparison.OrdinalIgnoreCase)) settings.LogEnabled = Boolean(key, value);
            else if (key.Equals("SgdbApiKey", StringComparison.OrdinalIgnoreCase)) settings.SteamGridDbApiKey = value;
            else if (key.StartsWith("Sisr_", StringComparison.OrdinalIgnoreCase))
                SetMode(settings, key[5..].Trim(), Boolean(key, value));
            else if (key.StartsWith("Watch_", StringComparison.OrdinalIgnoreCase))
                SetWatch(settings, key[6..].Trim(), value);
        }
        return settings;
    }

    public static byte[] Serialize(AppSettings settings, IReadOnlyList<string> originalLines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SisrPath"] = settings.SisrPath, ["SisrArguments"] = settings.SisrArguments,
            ["SisrEnabled"] = settings.SisrEnabled.ToString().ToLowerInvariant(),
            ["SgdbApiKey"] = settings.SteamGridDbApiKey, ["LogEnabled"] = settings.LogEnabled.ToString().ToLowerInvariant()
        };
        foreach (var pair in settings.GameProfiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.Contains('=') || pair.Key != pair.Key.Trim())
                throw new InvalidDataException("The legacy configuration cannot represent this game key. No settings were written.");
            // Validate even direct dictionary additions; no malformed keys should
            // become subtly different overrides on the next load.
            var validation = new AppSettings();
            validation.SetSteamInputMode(pair.Key, pair.Value.SteamInput);
            validation.SetWatchProcess(pair.Key, pair.Value.WatchProcess);
            if (pair.Value.SteamInput != SteamInputMode.Automatic)
                values["Sisr_" + pair.Key] = (pair.Value.SteamInput == SteamInputMode.Enabled).ToString().ToLowerInvariant();
            if (pair.Value.WatchProcess.Length != 0) values["Watch_" + pair.Key] = pair.Value.WatchProcess;
        }
        foreach (string value in values.Values)
            if (value == null || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || value != value.Trim())
                throw new InvalidDataException("Legacy setting values cannot contain line breaks/NUL or edge whitespace.");

        var output = new List<string>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (originalLines.Count == 0) { output.Add("# sBridge Configuration"); output.Add(""); }
        foreach (string line in originalLines)
        {
            if (TryEntry(line, out string key, out _) && IsKnownKey(key))
            {
                if (values.TryGetValue(key, out string? value) && written.Add(key)) output.Add(key + "=" + value);
                // Removed inherited/empty overrides and duplicate known keys are omitted.
            }
            else output.Add(line); // Comments, blank lines, and unknown settings are retained.
        }
        foreach (var pair in values) if (written.Add(pair.Key)) output.Add(pair.Key + "=" + pair.Value);
        byte[] bytes;
        try { bytes = Utf8.GetBytes(string.Join(Environment.NewLine, output) + Environment.NewLine); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Legacy settings contain invalid Unicode.", ex); }
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Legacy settings exceed the 4 MiB limit.");
        return bytes;
    }

    private static bool TryEntry(string line, out string key, out string value)
    {
        string trimmed = line.Trim();
        int equals = trimmed.IndexOf('=');
        if (trimmed.StartsWith('#') || trimmed.StartsWith(';') || equals <= 0)
        { key = ""; value = ""; return false; }
        key = trimmed[..equals].Trim(); value = trimmed[(equals + 1)..].Trim();
        return true;
    }

    private static bool IsKnownKey(string key) => key.Equals("SisrPath", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SisrArguments", StringComparison.OrdinalIgnoreCase) || key.Equals("SisrEnabled", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("LogEnabled", StringComparison.OrdinalIgnoreCase) || key.Equals("SgdbApiKey", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("Sisr_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Watch_", StringComparison.OrdinalIgnoreCase);

    private static bool Boolean(string key, string value) => bool.TryParse(value, out bool result) ? result :
        throw new InvalidDataException("Invalid boolean for legacy setting '" + key + "'. Expected true or false.");

    private static void SetMode(AppSettings settings, string target, bool enabled)
    {
        try { settings.SetSteamInputMode(target, enabled ? SteamInputMode.Enabled : SteamInputMode.Disabled); }
        catch (ArgumentException ex) { throw new InvalidDataException("Invalid per-game SISR key.", ex); }
    }
    private static void SetWatch(AppSettings settings, string target, string value)
    {
        try { settings.SetWatchProcess(target, value); }
        catch (ArgumentException ex) { throw new InvalidDataException("Invalid per-game watch key.", ex); }
    }
}
