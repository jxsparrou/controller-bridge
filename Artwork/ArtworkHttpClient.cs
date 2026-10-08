using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SBridge.Artwork;

internal sealed class ArtworkHttpClient
{
    private readonly HttpClient client;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeSpan requestTimeout;
    public ArtworkHttpClient(HttpClient client, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? requestTimeout = null)
    { this.client = client; this.delay = delay ?? Task.Delay; this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20); }

    public static ArtworkHttpClient CreateShared()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("sBridge/1.0");
        return new ArtworkHttpClient(client);
    }

    public async Task<byte[]> GetAsync(Uri uri, string? apiKey, int maxBytes, CancellationToken cancellationToken)
    {
        ValidateUri(uri);
        if (apiKey != null && apiKey.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new InvalidDataException("Artwork API credential has an invalid header format.");
        if (apiKey != null && (uri.Host != "www.steamgriddb.com" || !uri.AbsolutePath.StartsWith("/api/v2/", StringComparison.Ordinal)))
            throw new InvalidDataException("Artwork credentials may only be sent to the SteamGridDB API.");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            TimeSpan retry = TimeSpan.FromSeconds(1 << attempt);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (apiKey != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and <= 399)
                    throw new InvalidDataException("Artwork HTTP redirects are not followed.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode is 500 or 502 or 503 or 504)
                {
                    if (attempt == 2) throw new InvalidDataException("Artwork HTTP retry budget exhausted (status " + (int)response.StatusCode + ").");
                    var after = response.Headers.RetryAfter;
                    retry = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : retry);
                    if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
                    if (retry > TimeSpan.FromSeconds(10))
                        throw new InvalidDataException("Artwork Retry-After exceeds this job's retry budget; retry later.");
                }
                else
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidDataException("Artwork HTTP request failed (status " + (int)response.StatusCode + ").");
                    if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Artwork response exceeds its byte limit.");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var output = new MemoryStream();
                    byte[] buffer = new byte[8192]; int read;
                    while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                    {
                        if (output.Length + read > maxBytes) throw new InvalidDataException("Artwork response exceeds its byte limit.");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("Artwork HTTP request exceeded its time budget."); }
            catch (HttpRequestException) when (attempt < 2) { }
            catch (HttpRequestException) { throw new InvalidDataException("Artwork network request failed after bounded retries."); }
            await delay(retry, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidDataException("Artwork HTTP retry budget exhausted.");
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort)
            throw new InvalidDataException("Artwork URLs must be absolute HTTPS URLs without user information or fragments.");
    }
}
