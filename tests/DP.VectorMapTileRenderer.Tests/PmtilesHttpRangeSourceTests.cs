using System.Net;
using System.Net.Http.Headers;
using DP.VectorMapTileRenderer.Pmtiles;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class PmtilesHttpRangeSourceTests
{
    private static readonly Uri ArchiveUrl = new("https://files.example/tiles/basemap.pmtiles");

    [Fact]
    public async Task ReadsRequestedRangeFromPartialResponse()
    {
        var archive = TestArchives.Build([1, 2, 3, 4], PmtilesTileType.Png);
        var handler = new RangeHandler(archive);
        using var client = new HttpClient(handler);
        var source = new PmtilesHttpRangeSource(client, ArchiveUrl);

        var header = await source.ReadAsync(0, PmtilesCodec.HeaderSize, TestContext.Current.CancellationToken);

        Assert.Equal(archive[..PmtilesCodec.HeaderSize], header);
        Assert.Equal(archive.Length, source.TotalLength);
        Assert.Equal("\"v1\"", source.ETag);
        Assert.Equal(ArchiveUrl.AbsoluteUri, source.Key);
        var request = Assert.Single(handler.Requests);
        var range = Assert.Single(request.Headers.Range!.Ranges);
        Assert.Equal(0, range.From);
        Assert.Equal(PmtilesCodec.HeaderSize - 1, range.To);
        Assert.Equal("identity", request.Headers.AcceptEncoding.ToString());
    }

    [Fact]
    public async Task ReturnsEmptySliceWithoutRequesting()
    {
        var handler = new RangeHandler([]);
        using var client = new HttpClient(handler);
        var source = new PmtilesHttpRangeSource(client, ArchiveUrl);

        var bytes = await source.ReadAsync(10, 0, TestContext.Current.CancellationToken);

        Assert.Empty(bytes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsOriginThatIgnoresTheRangeHeader()
    {
        var archive = TestArchives.Build([9, 9, 9], PmtilesTileType.Png);
        using var client = new HttpClient(new RangeHandler(archive, supportsRange: false));
        var source = new PmtilesHttpRangeSource(client, ArchiveUrl);

        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => source.ReadAsync(0, 127, TestContext.Current.CancellationToken));

        Assert.Contains("Accept-Ranges", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsContentRangeThatStartsSomewhereElse()
    {
        using var client = new HttpClient(new RangeHandler([1, 2, 3, 4], rangeStartOverride: 1));
        var source = new PmtilesHttpRangeSource(client, ArchiveUrl);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.ReadAsync(0, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsPartialResponseWithoutContentRange()
    {
        using var client = new HttpClient(new RangeHandler([1, 2, 3, 4], includeContentRange: false));
        var source = new PmtilesHttpRangeSource(client, ArchiveUrl);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.ReadAsync(0, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReturnsEmptyForUnsatisfiableRange()
    {
        var archive = TestArchives.Build([1], PmtilesTileType.Png);
        var handler = new RangeHandler(archive) { StatusOverride = HttpStatusCode.RequestedRangeNotSatisfiable };
        using var client = new HttpClient(handler);
        var source = new PmtilesHttpRangeSource(client, ArchiveUrl);

        Assert.Empty(await source.ReadAsync(archive.Length, 16, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportsHttpFailuresWithTheirStatus()
    {
        var handler = new RangeHandler([1]) { StatusOverride = HttpStatusCode.NotFound };
        using var client = new HttpClient(handler);
        var source = new PmtilesHttpRangeSource(client, ArchiveUrl);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => source.ReadAsync(0, 1, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task ReadsTileFromArchiveOverHttp()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 11, 22, 33, 44 };
        var archive = TestArchives.Build(payload, PmtilesTileType.Png);
        var handler = new RangeHandler(archive);
        using var client = new HttpClient(handler);

        using var source = new PmtilesTileSource(client, ArchiveUrl);
        var bytes = await source.GetTileBytesAsync(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.Equal(payload, bytes);
        // header, root directory and the tile itself — never the whole archive.
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.NotNull(request.Headers.Range));
    }

    [Fact]
    public void RejectsRelativeArchiveUri()
    {
        using var client = new HttpClient(new RangeHandler([]));

        Assert.Throws<ArgumentException>(
            () => new PmtilesHttpRangeSource(client, new Uri("/tiles/a.pmtiles", UriKind.Relative)));
    }

    /// <summary>
    /// Serves an in-memory archive as HTTP range responses, and records every request so a test can
    /// prove that only the requested slice travelled over the wire.
    /// </summary>
    private sealed class RangeHandler(
        byte[] archive,
        bool supportsRange = true,
        long? rangeStartOverride = null,
        bool includeContentRange = true) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpStatusCode? StatusOverride { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (StatusOverride is { } status)
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (!supportsRange || range is null)
            {
                var full = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archive)
                };
                full.Content.Headers.ContentLength = archive.Length;
                return Task.FromResult(full);
            }

            var start = rangeStartOverride ?? range.From ?? 0;
            var end = range.To ?? archive.Length - 1;
            end = Math.Min(end, archive.Length - 1);

            var slice = start > end ? [] : archive[(int)start..(int)(end + 1)];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Content.Headers.ContentLength = slice.Length;
            if (includeContentRange)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, archive.Length);
            }

            return Task.FromResult(response);
        }
    }
}
