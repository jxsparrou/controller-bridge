using System;
using System.Text;

namespace SBridge.Diagnostics;

internal sealed record DiagnosticSnapshot(string AppVersion, string RuntimeVersion, bool LoggingEnabled, int LogFailures,
    bool SteamRunning, int SteamAccounts, int SelectedAccounts, int UnavailableAccounts, int Games, int Profiles,
    bool SisrEnabled, bool ManagedSisr, bool SisrPathConfigured, bool SisrFileExists, int ExternalSisr,
    bool ArtworkKeyConfigured, ManagedSessionSummary? LastManagedSession)
{
    public string SafeReport()
    {
        var text = new StringBuilder("sBridge diagnostics (read-only summary)\r\n");
        text.AppendLine("Captured UTC: " + DateTimeOffset.UtcNow.ToString("O"));
        text.AppendLine("App: " + SafeVersion(AppVersion) + "; .NET: " + SafeVersion(RuntimeVersion));
        text.AppendLine("Configuration: schema 1; per-user LocalAppData; credentials protected with current-user DPAPI");
        text.AppendLine("Bridge logs: " + (LoggingEnabled ? "enabled" : "disabled") + "; 2 MiB active + 3 archives; failed writes this process=" + LogFailures);
        text.AppendLine("Steam: running=" + SteamRunning + "; discovered accounts=" + SteamAccounts + "; selected=" + SelectedAccounts + "; unavailable=" + UnavailableAccounts);
        text.AppendLine("Library: games=" + Games + "; profiles=" + Profiles);
        text.AppendLine("SISR: enabled=" + SisrEnabled + "; managed startup=" + ManagedSisr + "; path configured=" + SisrPathConfigured + "; executable exists=" + SisrFileExists + "; external instances=" + ExternalSisr);
        text.AppendLine("SteamGridDB key configured=" + ArtworkKeyConfigured + "; images/requests bounded; no network probe performed");
        text.AppendLine("Managed SISR retention: 8 completed sessions; 4 MiB/completed SISR log; active sessions are not trimmed");
        var last = LastManagedSession;
        if (last == null) text.AppendLine("Last managed SISR status: unavailable");
        else
        {
            // Never copy arbitrary on-disk strings, paths, identifiers or log text.
            string phase = last.Phase is "starting" or "ready" or "ended" or "cleanup-incomplete" ? last.Phase : "unknown";
            string controller = last.ControllerType is "xbox360" or "dualshock4" or "dualsense" or "dualsenseedge" or "ns2pro" ? last.ControllerType : "unknown";
            text.AppendLine("Last managed SISR status (historical, not a live API probe): " + last.UpdatedUtc.ToString("O") + "; phase=" + phase + "; API ready=" + last.ApiReady);
            text.AppendLine("  version=" + SafeVersion(last.Version) + "; Steam=" + last.SteamRunning + "; no-Steam=" + last.NoSteam + "; VIIPER=" + last.ViiperConnected + "; devices=" + (last.Devices is >= 0 and <= 256 ? last.Devices.ToString() : "unknown") + "; controller=" + controller);
        }
        text.AppendLine("Readiness is not proof of controller input. Live Steam readback/profiles/controllers require validation.");
        text.AppendLine("This copy omits paths, usernames/account IDs, game titles/targets/arguments, device IDs, credentials and raw logs.");
        return text.ToString();
    }

    private static string SafeVersion(string? value) => Version.TryParse(value, out var version) ? version.ToString() : "unknown";
}
