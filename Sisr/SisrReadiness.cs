using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Sisr;

internal sealed record SisrStatusSnapshot(string Version, bool ApiSupported, bool SteamRunning, bool NoSteamMode,
    bool LaunchedViaSteam, bool CefReachable, bool MarkerPresent, bool ViiperConnected, int? DeviceCount,
    string? ControllerType, bool? InitialLaunch, bool? WindowFullscreen = null, bool? WindowShown = null);

internal interface ISisrStatusSource
{
    Task<SisrStatusSnapshot?> ProbeAsync(IOwnedSisrProcess process, CancellationToken token);
}

internal static class SisrReadiness
{
    public static async Task<SisrStatusSnapshot> WaitAsync(IOwnedSisrProcess process, ISisrStatusSource source,
        CancellationToken cancellationToken, TimeSpan? timeout = null, TimeSpan? poll = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("Owned SISR exited before API readiness. Check its session log/configuration.");
                SisrStatusSnapshot? status = null;
                try { status = await source.ProbeAsync(process, budget.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or Win32Exception or System.Text.Json.JsonException or InvalidDataException)
                { /* Transient startup/endpoint failure; keep the total budget. */ }
                if (status != null)
                {
                    if (!status.ApiSupported) throw new InvalidOperationException("Owned SISR has an unsupported API version. Managed startup requires compatible v0.6.1+ v0.6.x.");
                    if (process.HasExited) throw new InvalidOperationException("Owned SISR exited while readiness was being checked.");
                    return status;
                }
                await Task.Delay(poll ?? TimeSpan.FromMilliseconds(250), budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Owned SISR API did not become ready within the startup budget. Check its session log/configuration."); }
    }

    public static IReadOnlyList<string> Describe(SisrStatusSnapshot status)
    {
        string release = status.Version.Split('-')[0].TrimStart('v');
        string safeVersion = Version.TryParse(release, out var parsed) ? parsed.ToString() : "unknown";
        var lines = new List<string> { "Owned SISR API ready: version=" + safeVersion + ", SteamRunning=" + status.SteamRunning +
            ", NoSteam=" + status.NoSteamMode + ", ViaSteam=" + status.LaunchedViaSteam + ", CEFReachable=" + status.CefReachable +
            ", VIIPERConnected=" + status.ViiperConnected + ", Devices=" + (status.DeviceCount?.ToString() ?? "unknown") +
            ", ControllerType=" + (status.ControllerType ?? "unknown") };
        if (status.NoSteamMode) lines.Add("SISR is in no-Steam mode; Steam Input integration is not enabled.");
        else
        {
            if (!status.SteamRunning) lines.Add("SISR reports Steam is not running; controller integration may be unavailable.");
            if (!status.LaunchedViaSteam && !status.MarkerPresent) lines.Add("SISR reports its marker shortcut is missing; first-run Steam setup may be needed.");
            if (!status.CefReachable) lines.Add("SISR reports Steam CEF debugging is unavailable; profile/configurator features may be unavailable.");
        }
        if (!status.ViiperConnected) lines.Add("SISR reports no cached VIIPER connection; API readiness is not controller readiness.");
        if (status.DeviceCount == 0) lines.Add("SISR reports no devices; a connected controller is not required to launch.");
        if (status.InitialLaunch == true) lines.Add("SISR reports initial launch/setup mode; review its Steam setup UI.");
        return lines;
    }
}
