using System;

namespace SBridge.Launching;

internal static class LegacyLaunchOptions
{
    public static string ForPackagedGame(string aumid, string? executableHint) =>
        string.IsNullOrEmpty(executableHint) ? WindowsCommandLine.QuoteArgument(aumid)
            : WindowsCommandLine.Join(new[] { aumid, executableHint });

    public static string ForCustomGame(string executable, string rawGameArguments)
    {
        ArgumentNullException.ThrowIfNull(rawGameArguments);
        // The UI accepts an expert-authored command line. Keep it verbatim here;
        // Windows decodes it into tokens on bridge startup, which are then forwarded.
        string target = WindowsCommandLine.QuoteArgument(executable, forceQuotes: true);
        return rawGameArguments.Length == 0 ? target : target + " " + rawGameArguments;
    }
}
