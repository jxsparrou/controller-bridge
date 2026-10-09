using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SBridge.Artwork;
using SBridge.Steam;
using Xunit;

namespace SBridge.Tests;

public class ArtworkTests
{
    internal static byte[] Png()
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] data)
        {
            byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); output.Write(length);
            byte[] tag = Encoding.ASCII.GetBytes(name); output.Write(tag); output.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (byte value in tag.Concat(data))
            { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u); }
            BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xFFFFFFFF); output.Write(length);
        }
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 1); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(new byte[] { 0, 255, 0, 0, 255 });
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return output.ToArray();
    }
    internal static ArtworkRequest Request(string root = "unused") => new(new SteamAccount(root, "1", "Test account"), "遊戲 Café", 0xF1234567, "\"C:\\Bridge\\sBridge.exe\"", "launch test", "");
    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TypedSearchUsesEscapedUnicodeAndImagesNeverReceiveApiCredentials()
    {
        var requests = new List<(Uri Uri, string? Auth)>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
            if (request.RequestUri!.Host != "www.steamgriddb.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) });
            if (request.RequestUri.AbsolutePath.Contains("search")) return Task.FromResult(Json("{\"success\":true,\"data\":[{\"id\":9,\"name\":\"Unrelated\"},{\"id\":10,\"name\":\"遊戲 Café\"}]}"));
            Assert.Contains("/game/10", request.RequestUri.AbsolutePath);
            return Task.FromResult(Json("{\"success\":true,\"data\":[{\"url\":\"https://cdn.example/image.jpg?token=not-logged\"}]}"));
        }));
        var store = new MemoryStore();
        var result = await new ArtworkService(new ArtworkHttpClient(client), (bytes, _) => new ArtworkImage(bytes, ".png"), store)
            .DownloadAsync(Request(), "private-key", CancellationToken.None);
        Assert.Equal(4, result.Saved); Assert.Empty(result.Warnings); Assert.NotNull(result.IconPath);
        Assert.Contains(Uri.EscapeDataString("遊戲 Café"), requests[0].Uri.AbsoluteUri);
        Assert.All(requests.Where(request => request.Uri.Host == "www.steamgriddb.com"), request => Assert.Equal("private-key", request.Auth));
        Assert.All(requests.Where(request => request.Uri.Host != "www.steamgriddb.com"), request => Assert.Null(request.Auth));
        Assert.Equal(new[] { ArtworkKind.Portrait, ArtworkKind.Hero, ArtworkKind.Logo, ArtworkKind.Icon }, store.Saved);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RateLimitsHonorRetryAfterWithinThreeAttemptBudget(int status)
    {
        int count = 0; var waits = new List<TimeSpan>();
        using var client = new HttpClient(new Handler((_, _) =>
        {
            count++; var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)); return Task.FromResult(response);
        }));
        var http = new ArtworkHttpClient(client, (delay, _) => { waits.Add(delay); return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidDataException>(() => http.GetAsync(new Uri("https://www.steamgriddb.com/api/v2/search/test"), "key", 100, CancellationToken.None));
        Assert.Equal(3, count); Assert.Equal(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3) }, waits);
    }

    [Fact]
    public async Task LongRetryAfterIsNotTruncatedIntoAnEarlyRetry()
    {
        int count = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        { count++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)); return Task.FromResult(response); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArtworkHttpClient(client).GetAsync(new Uri("https://cdn.example/image"), null, 100, CancellationToken.None));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RetryAfterDateAndCancellationDuringBackoffAreHonored()
    {
        int count = 0; TimeSpan observed = TimeSpan.Zero;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            count++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(5)); return Task.FromResult(response);
        }));
        using var cancellation = new CancellationTokenSource();
        var http = new ArtworkHttpClient(client, (delay, token) => { observed = delay; cancellation.Cancel(); return Task.Delay(delay, token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync(new Uri("https://cdn.example/image"), null, 100, cancellation.Token));
        Assert.InRange(observed.TotalSeconds, 3, 5); Assert.Equal(1, count);
    }

    [Fact]
    public async Task NoMatchAndInvalidImageLeaveShortcutIndependentAndNeverPublishAnIcon()
    {
        using var noMatch = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"success\":true,\"data\":[]}"))));
        var store = new MemoryStore();
        var result = await new ArtworkService(new ArtworkHttpClient(noMatch), (bytes, _) => new ArtworkImage(bytes, ".png"), store)
            .DownloadAsync(Request(), "key", CancellationToken.None);
        Assert.Equal(0, result.Saved); Assert.Null(result.IconPath); Assert.Empty(store.Saved);
        using var invalidImage = new HttpClient(new Handler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("search")) return Task.FromResult(Json("{\"success\":true,\"data\":[{\"id\":1,\"name\":\"Game\"}]}"));
            if (path.Contains("/game/")) return Task.FromResult(Json("{\"success\":true,\"data\":[{\"url\":\"https://cdn.example/looks-like.png\"}]}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") });
        }));
        result = await new ArtworkService(new ArtworkHttpClient(invalidImage), (bytes, _) => throw new Exception("Invalid header must not reach decoder"), store)
            .DownloadAsync(Request(), "key", CancellationToken.None);
        Assert.Equal(0, result.Saved); Assert.Null(result.IconPath); Assert.Equal(4, result.Warnings.Length); Assert.Empty(store.Saved);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(302)]
    public async Task PermanentFailuresAndRedirectsDoNotRetryOrExposeBody(int status)
    {
        int count = 0;
        using var client = new HttpClient(new Handler((_, _) => { count++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret body key") }); }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ArtworkHttpClient(client).GetAsync(new Uri("https://www.steamgriddb.com/api/v2/test"), "key", 100, CancellationToken.None));
        Assert.Equal(1, count); Assert.DoesNotContain("secret body", error.Message);
    }

    [Fact]
    public async Task RequestTimeoutJobTimeoutAndCancellationAreBounded()
    {
        using var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception("unreachable"); }));
        await Assert.ThrowsAsync<TimeoutException>(() => new ArtworkHttpClient(client, requestTimeout: TimeSpan.FromMilliseconds(50))
            .GetAsync(new Uri("https://cdn.example/image"), null, 100, CancellationToken.None));
        var result = await new ArtworkService(new ArtworkHttpClient(client), (bytes, _) => new ArtworkImage(bytes, ".png"), new MemoryStore(), TimeSpan.FromMilliseconds(50))
            .DownloadAsync(Request(), "key", CancellationToken.None);
        Assert.True(result.Cancelled); Assert.Equal(0, result.Saved);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArtworkHttpClient(client).GetAsync(new Uri("https://cdn.example/image"), null, 100, cancellation.Token));
    }

    [Fact]
    public async Task TransportRetriesAndDeclaredOrChunkedOversizeResponsesAreRejected()
    {
        int count = 0;
        using var failed = new HttpClient(new Handler((_, _) => { count++; throw new HttpRequestException("possibly-sensitive URL"); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArtworkHttpClient(failed, (_, _) => Task.CompletedTask).GetAsync(new Uri("https://cdn.example/image"), null, 100, CancellationToken.None));
        Assert.Equal(3, count);
        using var declared = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[101]) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArtworkHttpClient(declared).GetAsync(new Uri("https://cdn.example/image"), null, 100, CancellationToken.None));
        using var chunked = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekable(new byte[101])) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArtworkHttpClient(chunked).GetAsync(new Uri("https://cdn.example/image"), null, 100, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://cdn.example/image")]
    [InlineData("https://user:password@cdn.example/image")]
    [InlineData("file:///C:/image.png")]
    [InlineData("https://cdn.example:1234/image")]
    public async Task UnsupportedUrlsAreRejectedBeforeSendingAnything(string url)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("Must not send")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArtworkHttpClient(client).GetAsync(new Uri(url), null, 100, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArtworkHttpClient(client).GetAsync(new Uri("https://cdn.example/image"), "key", 100, CancellationToken.None));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"success\":false,\"data\":[]}")]
    [InlineData("{\"success\":true,\"data\":null}")]
    [InlineData("{\"success\":true,\"data\":[{\"id\":\"wrong\",\"name\":\"Game\"}]}")]
    public async Task InvalidApiJsonProducesFailureWithoutWritesOrCredentialLogging(string json)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(json)))); var store = new MemoryStore();
        var result = await new ArtworkService(new ArtworkHttpClient(client), (bytes, _) => new ArtworkImage(bytes, ".png"), store)
            .DownloadAsync(Request(), "must-never-appear", CancellationToken.None);
        Assert.Equal(0, result.Saved); Assert.NotEmpty(result.Warnings); Assert.Empty(store.Saved);
        Assert.DoesNotContain("must-never-appear", string.Join(" ", result.Warnings));
    }

    [Fact]
    public async Task PartialArtworkAndDiskFailuresDoNotSuppressOtherAssets()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("search")) return Task.FromResult(Json("{\"success\":true,\"data\":[{\"id\":1,\"name\":\"Game\"}]}"));
            if (path.Contains("heroes")) return Task.FromResult(Json("{\"success\":true,\"data\":[]}"));
            if (path.Contains("/game/")) return Task.FromResult(Json("{\"success\":true,\"data\":[{\"url\":\"https://cdn.example/image\"}]}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) });
        }));
        var store = new MemoryStore { Fail = ArtworkKind.Logo };
        var result = await new ArtworkService(new ArtworkHttpClient(client), (bytes, _) => new ArtworkImage(bytes, ".png"), store)
            .DownloadAsync(Request(), "key", CancellationToken.None);
        Assert.Equal(2, result.Saved); Assert.Equal(2, result.Warnings.Length); Assert.NotNull(result.IconPath);
    }

    [Fact]
    public void HeadersBoundPixelsAndNeverInferImageExtensionFromUrl()
    {
        Assert.Equal(".png", ArtworkImageLimits.Inspect(Png()));
        Assert.Throws<InvalidDataException>(() => ArtworkImageLimits.Inspect(Encoding.UTF8.GetBytes("<html>not an image</html>")));
        var oversized = Png(); BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(16), 10000); BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(20), 10000);
        Assert.Throws<InvalidDataException>(() => ArtworkImageLimits.Inspect(oversized));
        Assert.Throws<InvalidDataException>(() => ArtworkImageLimits.Inspect(new byte[ArtworkImageLimits.MaxBytes + 1]));
    }

    internal sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class MemoryStore : IArtworkStore
    {
        public List<ArtworkKind> Saved { get; } = new();
        public ArtworkKind? Fail { get; init; }
        public Task<string> SaveAsync(ArtworkRequest request, ArtworkKind kind, ArtworkImage image, CancellationToken token)
        { if (Fail == kind) throw new IOException("disk failed"); Saved.Add(kind); return Task.FromResult(kind + image.Extension); }
    }
    private sealed class NonSeekable : Stream
    {
        private readonly MemoryStream inner;
        public NonSeekable(byte[] bytes) => inner = new MemoryStream(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
