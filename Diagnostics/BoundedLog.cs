using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SBridge.Diagnostics;

internal static class DiagnosticRedaction
{
    private static readonly Regex Secrets = new(@"(?<key>(?:--?[\w.-]*(?:password|secret|token|api[-_]?key)|\b(?:password|secret|token|api[-_]?key))\s*(?:=|:)\s*|--?[\w.-]*(?:password|secret|token|api[-_]?key)\s+)(?:""(?:\\.|[^""\\])*""|'[^']*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Bearer = new(@"Bearer\s+[^\s,""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static string LogMessage(string message, string? apiKey)
    {
        if (message.Length > 16384) return "[oversized diagnostic message omitted]";
        if (!string.IsNullOrEmpty(apiKey)) message = message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        try
        {
            message = Secrets.Replace(message, "${key}[redacted]");
            message = Bearer.Replace(message, "Bearer [redacted]");
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic message omitted during redaction]"; }
        return message.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\0", "", StringComparison.Ordinal);
    }
}

internal sealed class BoundedLog
{
    public const int DefaultMaxBytes = 2 * 1024 * 1024;
    public const int DefaultArchives = 3;
    private readonly string path;
    private readonly int maxBytes, archives;
    private readonly string mutexName;
    private readonly Guid bridgeId = Guid.NewGuid();
    private int failures;
    public int FailedWrites => Volatile.Read(ref failures);
    public BoundedLog(string path, int maxBytes = DefaultMaxBytes, int archives = DefaultArchives)
    {
        this.path = Path.GetFullPath(path); this.maxBytes = maxBytes; this.archives = archives;
        if (maxBytes < 1024 || archives is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        mutexName = "sBridge.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.path.ToUpperInvariant())));
    }

    public bool Write(string message, string? apiKey = null)
    {
        Mutex? mutex = null; bool held = false;
        try
        {
            mutex = new Mutex(false, mutexName);
            try { held = mutex.WaitOne(250); } catch (AbandonedMutexException) { held = true; }
            if (!held) { Interlocked.Increment(ref failures); return false; }
            string directory = Path.GetDirectoryName(path)!;
            try { RejectLink(directory); } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            Directory.CreateDirectory(directory); RejectLink(directory);
            message = DiagnosticRedaction.LogMessage(message, apiKey);
            string prefix = "[" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) +
                "] [bridge=" + bridgeId.ToString("N") + " pid=" + Environment.ProcessId + "] ";
            byte[] bytes = Encoding.UTF8.GetBytes(prefix + message + "\r\n");
            if (bytes.Length > maxBytes) bytes = Encoding.UTF8.GetBytes(prefix + "[oversized diagnostic message omitted]\r\n");
            for (int index = 0; index <= archives; index++)
            {
                string file = index == 0 ? path : path + "." + index;
                try { RejectLink(file); TrimTail(file, maxBytes); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            }
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxBytes)
            {
                if (archives == 0) File.Delete(path);
                else
                {
                    for (int index = archives - 1; index >= 1; index--)
                        if (File.Exists(path + "." + index)) File.Move(path + "." + index, path + "." + (index + 1), overwrite: true);
                    File.Move(path, path + ".1", overwrite: true);
                }
            }
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { Interlocked.Increment(ref failures); return false; }
        finally { if (held) mutex!.ReleaseMutex(); mutex?.Dispose(); }
    }

    internal static void RejectLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked diagnostic files/directories are not owned write targets."); }

    internal static void TrimTail(string path, int maxBytes)
    {
        if (new FileInfo(path).Length <= maxBytes) return;
        byte[] tail = new byte[maxBytes];
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        { input.Seek(-maxBytes, SeekOrigin.End); input.ReadExactly(tail); }
        int newline = Array.IndexOf(tail, (byte)'\n'); int start = newline < 0 ? tail.Length : newline + 1;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(tail.AsSpan(start)); output.Flush(flushToDisk: true); }
            File.Replace(temporary, path, null);
        }
        finally { File.Delete(temporary); }
    }
}
