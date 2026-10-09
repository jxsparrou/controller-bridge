using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Artwork;

internal sealed class ArtworkService
{
    private sealed class Envelope<T>
    {
        public required bool Success { get; init; }
        public required T[] Data { get; init; }
    }
    private sealed class SearchGame
    {
        public required long Id { get; init; }
        public required string Name { get; init; }
    }
    private sealed class Asset
    {
        public required string Url { get; init; }
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ArtworkHttpClient http;
    private readonly Func<byte[], bool, ArtworkImage> processImage;
    private readonly IArtworkStore store;
    private readonly TimeSpan jobTimeout;
    public ArtworkService(ArtworkHttpClient http, Func<byte[], bool, ArtworkImage> processImage, IArtworkStore? store = null, TimeSpan? jobTimeout = null)
    { this.http = http; this.processImage = processImage; this.store = store ?? new ArtworkStore(); this.jobTimeout = jobTimeout ?? TimeSpan.FromMinutes(2); }

    public async Task<ArtworkResult> DownloadAsync(ArtworkRequest request, string apiKey, CancellationToken cancellationToken)
    {
        var warnings = new List<string>(); int saved = 0; string? icon = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(jobTimeout);
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return new ArtworkResult(0, null, new[] { "No artwork API key configured." });
            var games = await ApiAsync<SearchGame>("search/autocomplete/" + Uri.EscapeDataString(request.Name), apiKey, budget.Token).ConfigureAwait(false);
            if (games.Length == 0) return new ArtworkResult(0, null, new[] { "No SteamGridDB game match found." });
            var game = games.FirstOrDefault(game => string.Equals(game.Name, request.Name, StringComparison.OrdinalIgnoreCase)) ?? games[0];
            if (game.Id <= 0 || string.IsNullOrWhiteSpace(game.Name)) throw new InvalidDataException("Invalid SteamGridDB game identity.");
            foreach (var kind in Enum.GetValues<ArtworkKind>())
            {
                budget.Token.ThrowIfCancellationRequested();
                try
                {
                    string category = kind switch { ArtworkKind.Portrait => "grids", ArtworkKind.Hero => "heroes", ArtworkKind.Logo => "logos", _ => "icons" };
                    string filters = kind is ArtworkKind.Logo or ArtworkKind.Icon ? "?mimes=image/png" : "?types=static&mimes=image/png,image/jpeg";
                    if (kind == ArtworkKind.Portrait) filters += "&dimensions=600x900,342x482,660x930";
                    var assets = await ApiAsync<Asset>(category + "/game/" + game.Id + filters, apiKey, budget.Token).ConfigureAwait(false);
                    if (assets.Length == 0) { warnings.Add(kind + ": no artwork available."); continue; }
                    if (!Uri.TryCreate(assets[0].Url, UriKind.Absolute, out var uri)) throw new InvalidDataException("Invalid artwork URL.");
                    // Authentication is deliberately absent on image/CDN requests.
                    byte[] bytes = await http.GetAsync(uri, null, ArtworkImageLimits.MaxBytes, budget.Token).ConfigureAwait(false);
                    ArtworkImageLimits.Inspect(bytes); // Bound pixels before native decoding.
                    var image = processImage(bytes, kind == ArtworkKind.Icon);
                    if (image.Extension != ArtworkImageLimits.Inspect(image.Bytes) || (kind == ArtworkKind.Icon && image.Extension != ".png"))
                        throw new InvalidDataException("Processed artwork format does not match its extension.");
                    string path = await store.SaveAsync(request, kind, image, budget.Token).ConfigureAwait(false);
                    saved++; if (kind == ArtworkKind.Icon) icon = path;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or ArgumentException or OverflowException or UnauthorizedAccessException or FormatException)
                { warnings.Add(kind + ": artwork unavailable or invalid (" + ErrorCategory(ex) + ")."); }
            }
        }
        catch (OperationCanceledException)
        {
            warnings.Add(cancellationToken.IsCancellationRequested ? "Artwork cancelled." : "Artwork job time budget exceeded.");
            return new ArtworkResult(saved, icon, warnings.ToArray(), Cancelled: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or ArgumentException or OverflowException or UnauthorizedAccessException or JsonException or FormatException)
        { warnings.Add("Artwork job failed (" + ErrorCategory(ex) + ")."); }
        return new ArtworkResult(saved, icon, warnings.ToArray());
    }

    private async Task<T[]> ApiAsync<T>(string relative, string apiKey, CancellationToken token)
    {
        byte[] bytes = await http.GetAsync(new Uri("https://www.steamgriddb.com/api/v2/" + relative), apiKey, 1024 * 1024, token).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(bytes, Json);
            if (envelope == null || !envelope.Success || envelope.Data == null || envelope.Data.Length > 1000 || envelope.Data.Any(item => item == null))
                throw new InvalidDataException("Invalid SteamGridDB response.");
            return envelope.Data;
        }
        catch (JsonException) { throw new InvalidDataException("Malformed SteamGridDB JSON."); }
    }

    private static string ErrorCategory(Exception ex) => ex switch
    {
        TimeoutException => "timeout", UnauthorizedAccessException => "access denied", IOException => "file/network error",
        InvalidDataException => ex.Message,
        _ => "response/image validation"
    };
}
