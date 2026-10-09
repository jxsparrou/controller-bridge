using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SBridge.Steam;

internal sealed record SteamAccount(string SteamDirectory, string Id, string DisplayName)
{
    public string AccountDirectory => Path.Combine(Path.GetFullPath(SteamDirectory), "userdata", Id);
    public string ShortcutPath => Path.Combine(AccountDirectory, "config", "shortcuts.vdf");
    public static bool ValidId(string id) => uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) &&
        value != 0 && value.ToString(CultureInfo.InvariantCulture) == id;
}

internal sealed record SteamAccountInventory(IReadOnlyList<SteamAccount> Accounts, IReadOnlyList<string> Warnings);

internal static class SteamAccountSelection
{
    public static IReadOnlyList<SteamAccount> Resolve(IReadOnlyList<SteamAccount> accounts, IEnumerable<string> selected)
    {
        string[] ids = selected.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Select at least one Steam account on the Steam Accounts tab.");
        var result = new List<SteamAccount>();
        foreach (string id in ids)
        {
            var matches = accounts.Where(account => account.Id == id).ToArray();
            if (!SteamAccount.ValidId(id) || matches.Length != 1)
                throw new InvalidOperationException("A selected Steam account is unavailable or ambiguous. Refresh Steam Accounts and review the selection.");
            result.Add(matches[0]);
        }
        return result;
    }
}

internal static class SteamAccountDiscovery
{
    // Account-name and first-file concepts adapted from Luke1505's PR #1.
    // https://github.com/jxsparrou/controller-bridge/pull/1
    public static SteamAccountInventory Discover(string steamDirectory)
    {
        string root = Path.GetFullPath(steamDirectory);
        var accounts = new List<SteamAccount>();
        var warnings = new List<string>();
        var names = new Dictionary<string, string>();
        try
        {
            using var stream = new FileStream(Path.Combine(root, "config", "loginusers.vdf"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > SteamLoginUsersCodec.MaxBytes) throw new InvalidDataException("loginusers.vdf exceeds the 4 MiB limit.");
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            names = SteamLoginUsersCodec.Decode(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        { warnings.Add("Steam account names unavailable; showing account IDs. " + ex.Message); }
        try
        {
            string userdata = Path.Combine(root, "userdata");
            if ((File.GetAttributes(userdata) & FileAttributes.ReparsePoint) != 0)
                return new SteamAccountInventory(accounts, new[] { "Linked Steam userdata is not a supported write target." });
            foreach (string directory in Directory.EnumerateDirectories(userdata))
            {
                if (accounts.Count >= 4096) throw new InvalidDataException("Steam account inventory exceeds its limit.");
                string id = Path.GetFileName(directory);
                if (!SteamAccount.ValidId(id)) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                { warnings.Add("Linked Steam account directory omitted: " + id); continue; }
                try
                {
                    if ((File.GetAttributes(Path.Combine(directory, "config")) & FileAttributes.ReparsePoint) != 0)
                    { warnings.Add("Linked Steam config directory omitted: " + id); continue; }
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
                string steamId = (76561197960265728UL + uint.Parse(id, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
                accounts.Add(new SteamAccount(root, id, names.TryGetValue(steamId, out string? name) ? name + " [" + id + "]" : "Account " + id));
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        return new SteamAccountInventory(accounts.OrderBy(account => uint.Parse(account.Id, CultureInfo.InvariantCulture)).ToArray(), warnings.ToArray());
    }
}

// Small bounded text-VDF reader, separate from binary shortcuts.vdf. Supports
// quoted keys/values, braces, escaped quotes/backslashes, and // comments.
internal static class SteamLoginUsersCodec
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public static Dictionary<string, string> Decode(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("loginusers.vdf exceeds the 4 MiB limit.");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        var reader = new Reader(text.TrimStart('\uFEFF'));
        var root = reader.Map(false, 0);
        if (root.Count != 1 || !root.TryGetValue("users", out var usersValue) || usersValue is not Dictionary<string, object> users)
            throw new InvalidDataException("Invalid Steam login-user root.");
        var names = new Dictionary<string, string>();
        foreach (var pair in users)
        {
            if (pair.Value is not Dictionary<string, object> user || !ulong.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("Invalid Steam login-user entry.");
            string persona = user.TryGetValue("PersonaName", out var p) && p is string ps ? ps : "";
            string login = user.TryGetValue("AccountName", out var a) && a is string ac ? ac : "";
            string name = persona.Length != 0 ? persona : login;
            if (name.Length != 0) names.Add(pair.Key, name);
        }
        return names;
    }

    private sealed class Reader
    {
        private readonly string text;
        private int index;
        private int count;
        public Reader(string text) => this.text = text;
        public Dictionary<string, object> Map(bool nested, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Steam login-user nesting limit exceeded.");
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                Skip();
                if (index == text.Length) { if (nested) throw new InvalidDataException("Unterminated Steam login-user map."); return result; }
                if (text[index] == '}') { if (!nested) throw new InvalidDataException("Unexpected Steam login-user close."); index++; return result; }
                if (++count > 100000) throw new InvalidDataException("Steam login-user element limit exceeded.");
                string key = Quoted(); Skip();
                object value;
                if (index < text.Length && text[index] == '{') { index++; value = Map(true, depth + 1); }
                else value = Quoted();
                if (!result.TryAdd(key, value)) throw new InvalidDataException("Duplicate Steam login-user key.");
            }
        }
        private void Skip()
        {
            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index])) { index++; continue; }
                if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
                { while (index < text.Length && text[index] != '\n') index++; continue; }
                break;
            }
        }
        private string Quoted()
        {
            if (index >= text.Length || text[index++] != '"') throw new InvalidDataException("Expected a quoted Steam login-user token.");
            var value = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return value.ToString();
                if (c == '\0') throw new InvalidDataException("NUL in Steam login-user token.");
                if (c == '\\')
                {
                    if (index >= text.Length) break;
                    char escaped = text[index++];
                    c = escaped switch { '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t',
                        _ => throw new InvalidDataException("Unsupported Steam login-user escape.") };
                }
                value.Append(c);
            }
            throw new InvalidDataException("Unterminated Steam login-user token.");
        }
    }
}
