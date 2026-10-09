using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Artwork;

internal interface IArtworkStore
{
    Task<string> SaveAsync(ArtworkRequest request, ArtworkKind kind, ArtworkImage image, CancellationToken token);
}

internal sealed class ArtworkStore : IArtworkStore
{
    public async Task<string> SaveAsync(ArtworkRequest request, ArtworkKind kind, ArtworkImage image, CancellationToken token)
    {
        string config = Path.GetDirectoryName(request.Account.ShortcutPath)!;
        string grid = Path.Combine(config, "grid");
        ValidateScope(request, grid);
        Directory.CreateDirectory(grid); ValidateScope(request, grid);
        string suffix = kind switch { ArtworkKind.Portrait => "p", ArtworkKind.Hero => "_hero", ArtworkKind.Logo => "_logo", _ => "_icon" };
        if (image.Extension is not (".png" or ".jpg")) throw new InvalidDataException("Unsupported artwork extension.");
        string target = Path.Combine(grid, request.AppId.ToString(CultureInfo.InvariantCulture) + suffix + image.Extension);
        // Preserve existing artwork unless an explicit future repair requests a replacement.
        if (File.Exists(target)) throw new IOException("Existing artwork retained; explicit repair is required.");
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(image.Bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested(); ValidateScope(request, grid);
            File.Move(temporary, target, overwrite: false);
            return target;
        }
        finally { File.Delete(temporary); }
    }

    private static void ValidateScope(ArtworkRequest request, string grid)
    {
        if (!Steam.SteamAccount.ValidId(request.Account.Id) || !File.Exists(request.Account.ShortcutPath))
            throw new IOException("Artwork target shortcut file is no longer available.");
        foreach (string directory in new[] { Path.GetDirectoryName(request.Account.AccountDirectory)!, request.Account.AccountDirectory,
            Path.GetDirectoryName(request.Account.ShortcutPath)! })
            if ((File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Artwork target directory is missing or linked.");
        if ((File.GetAttributes(request.Account.ShortcutPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked shortcut files are not artwork targets.");
        try
        {
            if ((File.GetAttributes(grid) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Linked artwork directories are not write targets.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
    }
}
