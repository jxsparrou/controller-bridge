using SBridge.Artwork;
using SBridge.Steam;
using Xunit;

namespace SBridge.Tests;

[Collection("Windows integration")]
public sealed class ArtworkPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sBridge-artwork-" + Guid.NewGuid().ToString("N"));
    private readonly ArtworkRequest request;
    private readonly SteamShortcutRepository repository = new(() => false);
    public ArtworkPersistenceTests()
    {
        request = ArtworkTests.Request(directory);
        Directory.CreateDirectory(request.Account.AccountDirectory);
        var document = repository.LoadForImport(request.Account);
        var entry = SteamShortcutBuilder.Create(0, request.Name, @"C:\Bridge\sBridge.exe", @"C:\Bridge", request.LaunchOptions, "");
        entry.Children.Single(field => field.Name == "appid").IntValue = unchecked((int)request.AppId); // Stored identity, not a recalculated ID.
        document.Root.Children.Add(entry);
        Assert.True(repository.Save(document).Succeeded);
    }

    [Fact]
    public async Task StoredAppIdNamesAtomicImagesAndIconUpdatePreservesUnrelatedEdits()
    {
        var image = new ArtworkImage(ArtworkTests.Png(), ".png");
        string icon = await new ArtworkStore().SaveAsync(request, ArtworkKind.Icon, image, CancellationToken.None);
        Assert.Equal(request.AppId + "_icon.png", Path.GetFileName(icon));
        var edit = repository.LoadExisting(request.Account.ShortcutPath);
        edit.Root.Children[0].Children.Add(new Program.VdfElement { Type = 1, Name = "Unrelated", StringValue = "Keep me" });
        Assert.True(repository.Save(edit).Succeeded);
        var result = SteamArtworkIconUpdater.Update(repository, request, icon);
        Assert.True(result.Succeeded, result.Error);
        var saved = repository.LoadExisting(request.Account.ShortcutPath).Root.Children[0];
        Assert.Equal(icon, saved.Children.Single(field => field.Name == "icon").StringValue);
        Assert.Equal("Keep me", saved.Children.Single(field => field.Name == "Unrelated").StringValue);
        Assert.Equal(request.AppId, unchecked((uint)saved.Children.Single(field => field.Name == "appid").IntValue));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingArtworkCancellationAndDeletedShortcutNeverOverwriteOrRecreate()
    {
        var store = new ArtworkStore(); var image = new ArtworkImage(ArtworkTests.Png(), ".png");
        string icon = await store.SaveAsync(request, ArtworkKind.Icon, image, CancellationToken.None);
        byte[] original = File.ReadAllBytes(icon);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(request, ArtworkKind.Icon, image, CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(icon));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(request, ArtworkKind.Logo, image, cancelled.Token));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        File.Delete(request.Account.ShortcutPath);
        Assert.False(SteamArtworkIconUpdater.Update(repository, request, icon).Succeeded);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(request, ArtworkKind.Hero, image, CancellationToken.None));
        Assert.False(File.Exists(request.Account.ShortcutPath));
    }

    [Fact]
    public async Task ChangedIconAndRunningSteamLeaveVdfUntouched()
    {
        string icon = await new ArtworkStore().SaveAsync(request, ArtworkKind.Icon, new ArtworkImage(ArtworkTests.Png(), ".png"), CancellationToken.None);
        byte[] before = File.ReadAllBytes(request.Account.ShortcutPath);
        Assert.False(SteamArtworkIconUpdater.Update(new SteamShortcutRepository(() => true), request, icon).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(request.Account.ShortcutPath));
        var edited = repository.LoadExisting(request.Account.ShortcutPath);
        edited.Root.Children[0].Children.Single(field => field.Name == "icon").StringValue = "User icon";
        Assert.True(repository.Save(edited).Succeeded); before = File.ReadAllBytes(request.Account.ShortcutPath);
        Assert.False(SteamArtworkIconUpdater.Update(repository, request, icon).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(request.Account.ShortcutPath));
    }

    [Fact]
    public async Task RemovedOrAmbiguousIdentityCannotUpdateAnotherShortcut()
    {
        string icon = await new ArtworkStore().SaveAsync(request, ArtworkKind.Icon, new ArtworkImage(ArtworkTests.Png(), ".png"), CancellationToken.None);
        var edit = repository.LoadExisting(request.Account.ShortcutPath);
        edit.Root.Children[0].Children.Single(field => field.Name == "appid").IntValue = 42;
        Assert.True(repository.Save(edit).Succeeded);
        byte[] before = File.ReadAllBytes(request.Account.ShortcutPath);
        Assert.False(SteamArtworkIconUpdater.Update(repository, request, icon).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(request.Account.ShortcutPath));
        edit = repository.LoadExisting(request.Account.ShortcutPath);
        var first = edit.Root.Children[0]; first.Children.Single(field => field.Name == "appid").IntValue = unchecked((int)request.AppId);
        var duplicate = Program.ParseShortcuts(Program.SerializeShortcuts(edit.Root)).Children[0]; duplicate.Name = "1";
        edit.Root.Children.Add(duplicate); Assert.True(repository.Save(edit).Succeeded);
        before = File.ReadAllBytes(request.Account.ShortcutPath);
        Assert.False(SteamArtworkIconUpdater.Update(repository, request, icon).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(request.Account.ShortcutPath));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
