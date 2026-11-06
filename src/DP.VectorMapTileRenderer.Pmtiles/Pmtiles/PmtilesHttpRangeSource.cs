using System.Net;
using System.Net.Http.Headers;

namespace DP.VectorMapTileRenderer.Pmtiles;

/// <summary>
/// Byte-range access to a PMTiles v3 archive served over HTTP(S): object storage, a CDN, or the
/// portal gateway. Only one archive header read, one root-directory read and one range read per
/// tile travel over the wire — the archive itself is never downloaded.
/// </summary>
/// <remarks>
/// The origin must honour <c>Range</c> requests. A partial read is accepted only as <c>206</c> with
/// a matching <c>Content-Range</c>; a <c>200</c> fails fast instead of silently turning every slice
/// into a full-archive download (the same rule the portal's MinIO range reader follows, ADR 024).
/// <para>
/// The <see cref="HttpClient"/> belongs to the caller and is never disposed here, so timeouts,
/// retries and connection pooling stay in one named client. Automatic decompression must be
/// disabled on that client — ranges address the stored bytes, and a transparently decoded body
/// shifts every offset inside the archive. This type holds no per-read state and is safe to share
/// between concurrent readers.
/// </para>
/// </remarks>
public sealed class PmtilesHttpRangeSource : IPmtilesRangeSource
{
    private readonly HttpClient _client;
    private readonly Uri _uri;
    private string? _etag;
    private long _totalLength = -1;

    /// <param name="client">Caller-owned client; the source never disposes it.</param>
    /// <param name="uri">Absolute <c>http</c>/<c>https</c> URL of the archive.</param>
    public PmtilesHttpRangeSource(HttpClient client, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "A PMTiles archive URL must be an absolute http(s) URI.",
                nameof(uri));
        }

        _client = client;
        _uri = uri;
    }

    /// <param name="client">Caller-owned client; the source never disposes it.</param>
    /// <param name="url">Absolute <c>http</c>/<c>https</c> URL of the archive.</param>
    public PmtilesHttpRangeSource(HttpClient client, string url)
        : this(client, new Uri(url, UriKind.Absolute))
    {
    }

    /// <summary>Archive URL; also the archive identity used in diagnostics and cache keys.</summary>
    public string Key => _uri.AbsoluteUri;

    public Uri Uri => _uri;

    /// <summary>
    /// Validator (strong or weak) the origin reported, captured from the first response.
    /// <see langword="null"/> until the archive has been read at least once.
    /// </summary>
    public string? ETag => Volatile.Read(ref _etag);

    /// <summary>
    /// Total archive length from <c>Content-Range</c>; <see langword="null"/> until known.
    /// </summary>
    public long? TotalLength
    {
        get
        {
            var value = Volatile.Read(ref _totalLength);
            return value < 0 ? null : value;
        }
    }

    public async Task<byte[]> ReadAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return [];
        }

        if (offset > long.MaxValue - (length - 1L))
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "The requested range end exceeds Int64.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
        // Byte offsets address the stored archive: never accept a transfer-encoded body.
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        switch (response.StatusCode)
        {
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                // The slice starts past the end of the archive: no data, not a failure.
                return [];

            case HttpStatusCode.OK:
                throw new NotSupportedException(
                    $"'{_uri}' answered HTTP 200 to a Range request. The origin must serve PMTiles " +
                    "archives with byte ranges (Accept-Ranges: bytes); downloading the whole archive " +
                    "for every slice is not supported.");

            case HttpStatusCode.PartialContent:
                break;

            default:
                throw new HttpRequestException(
                    $"PMTiles range request to '{_uri}' failed with HTTP " +
                    $"{(int)response.StatusCode} {response.ReasonPhrase}.",
                    inner: null,
                    response.StatusCode);
        }

        Remember(response);

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.From is not { } from || contentRange.To is not { } to || to < from)
        {
            throw new InvalidDataException(
                $"'{_uri}' answered 206 without a usable Content-Range for bytes {offset}-" +
                $"{offset + length - 1}.");
        }

        if (from != offset)
        {
            throw new InvalidDataException(
                $"'{_uri}' returned Content-Range '{contentRange}' for a request of bytes " +
                $"{offset}-{offset + length - 1}; the origin or an intermediary rewrote the range.");
        }

        var announced = to - from + 1;
        if (announced > length)
        {
            throw new InvalidDataException(
                $"'{_uri}' returned {announced} bytes for a {length}-byte request ({contentRange}).");
        }

        var bytes = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        if (bytes.LongLength != announced)
        {
            throw new InvalidDataException(
                $"'{_uri}' announced {announced} bytes ({contentRange}) but sent {bytes.LongLength}.");
        }

        return bytes;
    }

    private void Remember(HttpResponseMessage response)
    {
        if (response.Headers.ETag is { } etag)
        {
            Volatile.Write(ref _etag, etag.ToString());
        }

        if (response.Content.Headers.ContentRange?.Length is { } total)
        {
            Volatile.Write(ref _totalLength, total);
        }
    }
}
