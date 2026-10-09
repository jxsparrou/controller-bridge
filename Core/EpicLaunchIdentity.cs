using System;
using System.Linq;

namespace SBridge.Core;

internal static class EpicLaunchIdentity
{
    public const string Prefix = "com.epicgames.launcher://apps/";
    public static string Create(string catalogNamespace, string catalogItemId, string appName)
    {
        string identity = catalogNamespace + ":" + catalogItemId + ":" + appName;
        Validate(identity);
        return identity;
    }

    public static string Target(string identity)
    {
        Validate(identity);
        return Prefix + Uri.EscapeDataString(identity) + "?action=launch&silent=true";
    }

    public static void Validate(string identity)
    {
        string[] parts = identity.Split(':');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 256 ||
            part.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.')))
            throw new ArgumentException("Epic requires a namespace:catalog-item:app-name identity.");
    }

    public static void ValidateTarget(string target)
    {
        if (!target.StartsWith(Prefix, StringComparison.Ordinal) || !target.EndsWith("?action=launch&silent=true", StringComparison.Ordinal))
            throw new ArgumentException("Invalid Epic launcher target.");
        string identity = Uri.UnescapeDataString(target[Prefix.Length..target.IndexOf('?')]);
        if (Target(identity) != target) throw new ArgumentException("Epic launcher target must use canonical catalog identity escaping.");
    }
}
