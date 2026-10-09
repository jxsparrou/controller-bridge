using System;
using System.IO;

namespace SBridge.Configuration;

internal interface IConfigurationFiles
{
    byte[] Read(string path);
    void WriteNew(string path, byte[] bytes);
    void Backup(string path, string backup);
    void Replace(string temporary, string path);
    void MoveNew(string temporary, string path);
    void Delete(string path);
}

internal sealed class ConfigurationFileOperations : IConfigurationFiles
{
    private const int MaxFileBytes = 4 * 1024 * 1024;
    public byte[] Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Settings exceed the 4 MiB limit.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    public void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
    public void Backup(string path, string backup)
    {
        string temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteNew(temporary, Read(path)); File.Move(temporary, backup, overwrite: true); }
        finally { File.Delete(temporary); }
    }
    public void Replace(string temporary, string path) => File.Replace(temporary, path, destinationBackupFileName: null);
    public void MoveNew(string temporary, string path) => File.Move(temporary, path);
    public void Delete(string path) => File.Delete(path);
}
