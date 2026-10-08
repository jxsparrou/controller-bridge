using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Providers;

internal sealed record DiscoveryCommandOutput(string StandardOutput, string StandardError, int ExitCode);

internal sealed class DiscoveryProcessRunner
{
    private readonly Action<string> report;
    public DiscoveryProcessRunner(Action<string>? report = null) => this.report = report ?? (_ => { });
    public async Task<DiscoveryCommandOutput> RunAsync(ProcessStartInfo info, TimeSpan timeout,
        CancellationToken cancellationToken, int maxOutputBytes = 4 * 1024 * 1024)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(timeout));
        info.UseShellExecute = false; info.CreateNoWindow = true;
        info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Discovery process did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var stopOnCancel = deadline.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { report("Owned discovery cancellation cleanup: " + ex.Message); }
        });
        Task<byte[]> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, maxOutputBytes, deadline);
        Task<byte[]> stderr = ReadBoundedAsync(process.StandardError.BaseStream, Math.Min(maxOutputBytes, 256 * 1024), deadline);
        Task exited = process.WaitForExitAsync(deadline.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, exited).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var utf8 = new UTF8Encoding(false, true);
            return new DiscoveryCommandOutput(utf8.GetString(stdout.Result), utf8.GetString(stderr.Result), process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (stdout.IsFaulted) await stdout.ConfigureAwait(false);
            if (stderr.IsFaulted) await stderr.ConfigureAwait(false);
            throw new TimeoutException("Packaged discovery exceeded its bounded execution budget.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: false); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { report("Owned discovery process could not be stopped: " + ex.Message); }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { report("Owned discovery process did not exit within cleanup budget."); }
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationTokenSource deadline)
    {
        try
        {
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("Discovery output exceeded the configured size limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch { deadline.Cancel(); throw; }
    }
}
