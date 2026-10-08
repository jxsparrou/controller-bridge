using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace SBridge.Steam;

internal sealed class SteamShortcutDocument
{
    private byte[]? fingerprint;

    internal SteamShortcutDocument(string path, Program.VdfElement root, byte[]? original, string? creationAccountDirectory = null)
    {
        Path = path;
        Root = root;
        fingerprint = original == null ? null : SHA256.HashData(original);
        CreationAccountDirectory = creationAccountDirectory;
    }

    public string Path { get; }
    public Program.VdfElement Root { get; }
    internal string? CreationAccountDirectory { get; }
    internal bool IsNew => fingerprint == null;

    internal bool Matches(byte[] bytes) => fingerprint != null && fingerprint.AsSpan().SequenceEqual(SHA256.HashData(bytes));
    internal void AcceptSavedBytes(byte[] bytes) => fingerprint = SHA256.HashData(bytes);
}

internal sealed record SteamShortcutSaveResult(string Path, bool Succeeded, string? Error);

// A narrow filesystem seam permits failures at real transaction boundaries.
internal interface IShortcutFileOperations
{
    byte[] Read(string path);
    void WriteNew(string path, byte[] bytes);
    void Backup(string path, string backupPath);
    void Replace(string temporaryPath, string path);
    void Delete(string path);
    void PrepareCreation(string accountDirectory, string configDirectory);
    void Create(string temporaryPath, string path);
}

internal sealed class ShortcutFileOperations : IShortcutFileOperations
{
    public byte[] Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > Program.MaxVdfFileBytes)
            throw new InvalidDataException("Shortcut file exceeds the 32 MiB limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public void Backup(string path, string backupPath)
    {
        // Stage the backup too: a failed backup write must not truncate a prior .bak.
        string temporaryBackup = backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(temporaryBackup, Read(path));
            File.Move(temporaryBackup, backupPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryBackup);
        }
    }

    public void Replace(string temporaryPath, string path)
    {
        // Fail closed on filesystems without atomic replacement. Never fall back
        // to deleting/truncating the original.
        File.Replace(temporaryPath, path, destinationBackupFileName: null);
    }

    public void Delete(string path) => File.Delete(path);

    public void PrepareCreation(string accountDirectory, string configDirectory)
    {
        // Only a config directory within an existing, discovered numeric account
        // may be created. Do not fabricate userdata/account directories or follow
        // junctions into unrelated locations.
        if (!SteamAccount.ValidId(Path.GetFileName(accountDirectory)) ||
            !string.Equals(Path.Combine(accountDirectory, "config"), configDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid Steam account creation scope.");
        foreach (string directory in new[] { Path.GetDirectoryName(accountDirectory)!, accountDirectory })
            if ((File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Steam account directory is missing or linked; refresh accounts.");
        Directory.CreateDirectory(configDirectory);
        if ((File.GetAttributes(configDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked Steam config directories are not creation targets.");
    }

    public void Create(string temporaryPath, string path) => File.Move(temporaryPath, path, overwrite: false);
}

internal sealed class SteamShortcutRepository
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<bool> isSteamRunning;
    private readonly IShortcutFileOperations files;

    public SteamShortcutRepository(Func<bool> isSteamRunning, IShortcutFileOperations? files = null)
    {
        this.isSteamRunning = isSteamRunning;
        this.files = files ?? new ShortcutFileOperations();
    }

    public SteamShortcutDocument LoadExisting(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        byte[] bytes = files.Read(fullPath);
        return new SteamShortcutDocument(fullPath, Program.ParseShortcuts(bytes), bytes);
    }

    public SteamShortcutDocument LoadForImport(SteamAccount account)
    {
        if (!SteamAccount.ValidId(account.Id)) throw new ArgumentException("Invalid Steam account ID.");
        byte[] bytes;
        try { bytes = files.Read(account.ShortcutPath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Explicit first-file snapshot only. Other read/parse errors must
            // propagate, and deleted loaded originals never enter this branch.
            return new SteamShortcutDocument(account.ShortcutPath,
                new Program.VdfElement { Type = 0x00, Name = "shortcuts" }, null, account.AccountDirectory);
        }
        var root = Program.ParseShortcuts(bytes);
        files.PrepareCreation(account.AccountDirectory, System.IO.Path.GetDirectoryName(account.ShortcutPath)!);
        return new SteamShortcutDocument(account.ShortcutPath, root, bytes, account.AccountDirectory);
    }

    public SteamShortcutSaveResult Save(SteamShortcutDocument document)
    {
        string path = document.Path;
        lock (Gates.GetOrAdd(path, _ => new object()))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool temporaryCreated = false;
            SteamShortcutSaveResult result;
            string? cleanupError = null;
            try
            {
                EnsureCanCommit(document);
                byte[] output = Program.SerializeShortcuts(document.Root);
                if (document.CreationAccountDirectory != null)
                    files.PrepareCreation(document.CreationAccountDirectory, System.IO.Path.GetDirectoryName(path)!);
                // Set before writing so a partial temporary write is also cleaned up.
                temporaryCreated = true;
                files.WriteNew(temporary, output);
                byte[] staged = files.Read(temporary);
                Program.ParseShortcuts(staged);
                if (!output.AsSpan().SequenceEqual(staged))
                    throw new InvalidDataException("Temporary shortcut output did not match serialized data.");

                EnsureCanCommit(document);
                if (!document.IsNew) files.Backup(path, path + ".bak");
                // Recheck after backup; Steam can restart while an operation is in progress.
                EnsureCanCommit(document);
                if (document.CreationAccountDirectory != null)
                    files.PrepareCreation(document.CreationAccountDirectory, System.IO.Path.GetDirectoryName(path)!);
                if (document.IsNew)
                {
                    files.Create(temporary, path);
                }
                else files.Replace(temporary, path);
                temporaryCreated = false;
                document.AcceptSavedBytes(output);
                result = new SteamShortcutSaveResult(path, true, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                result = new SteamShortcutSaveResult(path, false, ex.Message);
            }
            finally
            {
                if (temporaryCreated)
                {
                    try { files.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        cleanupError = " Temporary cleanup failed for " + temporary + ": " + ex.Message;
                    }
                }
            }
            if (cleanupError != null)
                result = result with { Error = result.Error + cleanupError };
            return result;
        }
    }

    private void EnsureCanCommit(SteamShortcutDocument document)
    {
        if (isSteamRunning())
            throw new IOException("Steam is running. Close Steam and reload shortcuts before saving.");
        if (document.IsNew)
        {
            try { _ = files.Read(document.Path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
            throw new IOException("Shortcut file appeared after first-file preparation. Reload before editing.");
        }
        byte[] current = files.Read(document.Path);
        Program.ParseShortcuts(current);
        if (!document.Matches(current))
            throw new IOException("Shortcut file changed since it was loaded. Reload before editing.");
    }
}
