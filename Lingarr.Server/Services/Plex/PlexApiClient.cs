using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Services.Plex;

public class PlexApiClient : IPlexApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlexApiClient> _logger;

    public PlexApiClient(IHttpClientFactory httpClientFactory, ILogger<PlexApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PlexIdentity?> GetIdentity(
        string baseUrl,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var parsedBase))
        {
            _logger.LogWarning("Plex identity check skipped: invalid URL {Url}", baseUrl);
            return null;
        }

        var identityUri = new Uri(parsedBase, "/identity");
        var client = _httpClientFactory.CreateClient("Plex");
        using var request = new HttpRequestMessage(HttpMethod.Get, identityUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", accessToken);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", PlexHeaders.Product);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Plex identity check failed with {StatusCode} for {Url}",
                    (int)response.StatusCode, identityUri);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<PlexIdentityApiResponse>(
                PlexJson.Options, cancellationToken);
            return body?.MediaContainer;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Plex identity check failed for {Url}", identityUri);
            return null;
        }
    }

    public async Task<PlexMetadata?> GetMetadata(
        string baseUrl,
        string accessToken,
        string ratingKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(ratingKey))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var parsedBase))
        {
            _logger.LogWarning("Plex metadata fetch skipped: invalid URL {Url}", baseUrl);
            return null;
        }

        var metadataUri = new Uri(parsedBase, $"/library/metadata/{Uri.EscapeDataString(ratingKey)}");
        var client = _httpClientFactory.CreateClient("Plex");
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", accessToken);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", PlexHeaders.Product);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Plex metadata not found for ratingKey {RatingKey}", ratingKey);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var item = PlexMetadataMapper.FirstMetadataItem(document.RootElement);
        return item == null ? null : PlexMetadataMapper.FromJson(item.Value);
    }

    public async Task<PlexChildrenPage?> GetChildren(
        string baseUrl,
        string accessToken,
        string ratingKey,
        int start = 0,
        int size = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(ratingKey))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var parsedBase))
        {
            _logger.LogWarning("Plex children fetch skipped: invalid URL {Url}", baseUrl);
            return null;
        }

        if (start < 0)
        {
            start = 0;
        }

        if (size <= 0)
        {
            size = 200;
        }

        var childrenUri = new Uri(parsedBase, $"/library/metadata/{Uri.EscapeDataString(ratingKey)}/children");
        var client = _httpClientFactory.CreateClient("Plex");
        using var request = new HttpRequestMessage(HttpMethod.Get, childrenUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", accessToken);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", PlexHeaders.Product);
        request.Headers.TryAddWithoutValidation(
            "X-Plex-Container-Start",
            start.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            "X-Plex-Container-Size",
            size.ToString(CultureInfo.InvariantCulture));

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Plex children not found for ratingKey {RatingKey}", ratingKey);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        return PlexMetadataMapper.ReadChildrenPage(document.RootElement);
    }

    public async Task<byte[]?> DownloadStream(
        string baseUrl,
        string accessToken,
        string streamKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(accessToken) ||
            !PlexStreamKey.IsSafe(streamKey))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var parsedBase))
        {
            _logger.LogWarning("Plex stream download skipped: invalid URL {Url}", baseUrl);
            return null;
        }

        var streamUri = new Uri(parsedBase, streamKey);
        var client = _httpClientFactory.CreateClient("Plex");
        using var request = new HttpRequestMessage(HttpMethod.Get, streamUri);
        request.Headers.TryAddWithoutValidation("X-Plex-Token", accessToken);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", PlexHeaders.Product);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Plex subtitle stream not found at {StreamKey}", streamKey);
            return null;
        }

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > PlexStreamKey.MaxBytes)
        {
            _logger.LogWarning(
                "Plex subtitle stream {StreamKey} is {Length} bytes; skipping.",
                streamKey,
                response.Content.Headers.ContentLength);
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > PlexStreamKey.MaxBytes)
            {
                _logger.LogWarning(
                    "Plex subtitle stream {StreamKey} exceeded {MaxBytes} bytes; skipping.",
                    streamKey,
                    PlexStreamKey.MaxBytes);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return total == 0 ? null : buffer.ToArray();
    }
}
