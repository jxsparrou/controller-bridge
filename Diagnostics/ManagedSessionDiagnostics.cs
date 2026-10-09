using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SBridge.Sisr;

namespace SBridge.Diagnostics;

internal sealed record ManagedSessionSummary(int SchemaVersion, DateTimeOffset UpdatedUtc, string Phase, bool ApiReady,
    string? Version, bool? SteamRunning, bool? NoSteam, bool? ViiperConnected, int? Devices, string? ControllerType);

internal static class ManagedSessionDiagnostics
{
    internal const string MarkerName = ".sbridge-session";
    internal const string Marker = "sBridge managed SISR session v1";
    public const int RetainedSessions = 8;
    public const int MaxCompletedLogBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> OwnedFiles = new(StringComparer.OrdinalIgnoreCase) { MarkerName, "startup.json", "SISR.log", "summary.json" };

    public static FileStream MarkActive(string directory)
    {
        var file = new FileStream(Path.Combine(directory, MarkerName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try { file.Write(Encoding.UTF8.GetBytes(Marker)); file.Flush(flushToDisk: true); return file; }
        catch { file.Dispose(); throw; }
    }

    public static void WriteSummary(string directory, string phase, SisrStatusSnapshot? status)
    {
        if (phase is not ("starting" or "ready" or "ended" or "cleanup-incomplete")) throw new ArgumentException("Unknown diagnostic phase.");
        string? version = null;
        if (status != null && Version.TryParse(status.Version.Split('-')[0].TrimStart('v'), out var parsed)) version = parsed.ToString();
        string? controller = status?.ControllerType is "xbox360" or "dualshock4" or "dualsense" or "dualsenseedge" or "ns2pro" ? status.ControllerType : null;
        var summary = new ManagedSessionSummary(1, DateTimeOffset.UtcNow, phase, status != null && status.ApiSupported, version,
            status?.SteamRunning, status?.NoSteamMode, status?.ViiperConnected, status?.DeviceCount, controller);
        string path = Path.Combine(directory, "summary.json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(summary)); output.Flush(flushToDisk: true); }
            if (File.Exists(path)) { BoundedLog.RejectLink(path); File.Replace(temporary, path, null); }
            else File.Move(temporary, path, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }

    public static ManagedSessionSummary? ReadLatest(string dataDirectory)
    {
        foreach (string directory in Candidates(dataDirectory).OrderByDescending(Directory.GetCreationTimeUtc))
        {
            string path = Path.Combine(directory, "summary.json");
            try
            {
                BoundedLog.RejectLink(path);
                var summary = ReadSummary(path);
                if (summary != null && summary.SchemaVersion == 1 && summary.Phase is "starting" or "ready" or "ended" or "cleanup-incomplete")
                    return summary;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    public static void Prune(string dataDirectory)
    {
        int retained = 0;
        foreach (string directory in Candidates(dataDirectory).OrderByDescending(Directory.GetCreationTimeUtc))
        {
            // An active bridge holds the marker handle. Lock acquisition also
            // serializes cleanup of one session across settings/executable copies.
            FileStream? lease = null;
            try
            {
                lease = new FileStream(Path.Combine(directory, MarkerName), FileMode.Open, FileAccess.Read, FileShare.Delete);
                string[] files = Directory.GetFiles(directory);
                if (Directory.EnumerateDirectories(directory).Any() || files.Any(file => !OwnedFiles.Contains(Path.GetFileName(file)))) continue;
                foreach (string file in files) BoundedLog.RejectLink(file);
                string summaryPath = Path.Combine(directory, "summary.json");
                var summary = ReadSummary(summaryPath);
                // A crashed bridge may leave an orphaned SISR root. An unlocked
                // marker alone does not prove that session completed safely.
                if (summary?.SchemaVersion != 1 || summary.Phase != "ended") continue;
                string log = Path.Combine(directory, "SISR.log");
                if (File.Exists(log)) BoundedLog.TrimTail(log, MaxCompletedLogBytes);
                if (++retained <= RetainedSessions) continue;
                foreach (string file in files.Where(file => Path.GetFileName(file) != MarkerName)) File.Delete(file);
                File.Delete(Path.Combine(directory, MarkerName));
                lease.Dispose(); lease = null;
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            finally { lease?.Dispose(); }
        }
    }

    private static IEnumerable<string> Candidates(string dataDirectory)
    {
        string root = Path.Combine(dataDirectory, "sisr", "sessions");
        var result = new List<string>();
        try
        {
            BoundedLog.RejectLink(root);
            BoundedLog.RejectLink(Path.GetDirectoryName(root)!);
            foreach (string directory in Directory.EnumerateDirectories(root).Take(4096))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                try
                {
                    BoundedLog.RejectLink(directory); string marker = Path.Combine(directory, MarkerName); BoundedLog.RejectLink(marker);
                    using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length != Encoding.UTF8.GetByteCount(Marker)) continue;
                    byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
                    if (Encoding.UTF8.GetString(bytes) == Marker) result.Add(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static ManagedSessionSummary? ReadSummary(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 4096) return null;
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        return JsonSerializer.Deserialize<ManagedSessionSummary>(bytes);
    }
}
