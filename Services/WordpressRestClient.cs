using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Configuration;
using ModelContextProtocol;

namespace WordpressMCPSharp.Services;

/// <summary>
/// Thin client over the WordPress REST API (https://developer.wordpress.org/rest-api/) for one
/// endpoint. Authenticates with an application password over HTTP Basic auth. All higher-level
/// conveniences are layered on top so tool classes stay declarative.
/// </summary>
public sealed class WordpressRestClient : IDisposable
{
    private readonly RestApiOptions _rest;
    private readonly WordpressOptions _options;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private bool? _restPrefixPretty;
    private bool _disposed;

    public WordpressRestClient(
        string endpointName,
        RestApiOptions rest,
        WordpressOptions options,
        EffectiveSafety safety)
    {
        EndpointName = endpointName;
        _rest = rest;
        _options = options;
        Safety = safety;

        var baseUrl = rest.BaseUrl.EndsWith('/') ? rest.BaseUrl : rest.BaseUrl + "/";
        _baseUri = new Uri(baseUrl, UriKind.Absolute);

        var handler = new HttpClientHandler();
        if (rest.AllowInvalidCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds)),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(rest.Username))
        {
            var credential = $"{rest.Username}:{rest.ApplicationPassword}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
    }

    /// <summary>Registry name of the endpoint this client talks to. Used in error messages.</summary>
    public string EndpointName { get; }

    public WordpressOptions Options => _options;
    public RestApiOptions Rest => _rest;
    public EffectiveSafety Safety { get; }
    public bool IsReadOnly => Safety.ReadOnly;

    public void EnsureWriteAllowed(string operation)
    {
        if (Safety.ReadOnly)
        {
            throw new McpException(
                $"MCP tool '{operation}' is blocked by server configuration for endpoint '{EndpointName}'. " +
                "Set Wordpress:ReadOnly=false to allow writes" +
                (Safety.ReadOnlyFromEndpoint
                    ? $" (and clear the per-endpoint override Endpoints:{EndpointName}:ReadOnly)."
                    : "."));
        }
    }

    public void EnsureDeleteAllowed(string operation)
    {
        EnsureWriteAllowed(operation);
        if (!Safety.AllowDelete)
        {
            throw new McpException(
                $"MCP tool '{operation}' requires Wordpress:AllowDelete=true (in addition to Wordpress:ReadOnly=false)" +
                (Safety.AllowDeleteFromEndpoint
                    ? $", and the per-endpoint override Endpoints:{EndpointName}:AllowDelete must not be false."
                    : "."));
        }
    }

    public void EnsurePluginInstallAllowed(string operation)
    {
        EnsureWriteAllowed(operation);
        if (!Safety.AllowPluginInstall)
        {
            throw new McpException(
                $"MCP tool '{operation}' requires Wordpress:AllowPluginInstall=true (in addition to Wordpress:ReadOnly=false)" +
                (Safety.AllowPluginInstallFromEndpoint
                    ? $", and the per-endpoint override Endpoints:{EndpointName}:AllowPluginInstall must not be false."
                    : "."));
        }
    }

    public void EnsureFeature(bool flag, string feature)
    {
        if (!flag)
        {
            throw new McpException($"{feature} tools are disabled by server configuration.");
        }
    }

    public string ResolveDownloadDirectory()
    {
        var dir = string.IsNullOrWhiteSpace(_options.DownloadDirectory)
            ? Path.Combine(Path.GetTempPath(), "WordpressMCPSharp")
            : _options.DownloadDirectory!;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<JsonNode?> GetJsonAsync(string relativePath, CancellationToken ct)
    {
        using var response = await _http.GetAsync(await ResolveUrlAsync(relativePath, ct), ct);
        await EnsureSuccessAsync(response, ct);
        return await ParseJsonAsync(response, ct);
    }

    /// <summary>GET that also surfaces WordPress pagination headers (X-WP-Total / X-WP-TotalPages).</summary>
    public async Task<(JsonNode? Node, int? Total, int? TotalPages)> GetJsonPagedAsync(string relativePath, CancellationToken ct)
    {
        using var response = await _http.GetAsync(await ResolveUrlAsync(relativePath, ct), ct);
        await EnsureSuccessAsync(response, ct);
        var total = ReadIntHeader(response, "X-WP-Total");
        var totalPages = ReadIntHeader(response, "X-WP-TotalPages");
        var node = await ParseJsonAsync(response, ct);
        return (node, total, totalPages);
    }

    public async Task<JsonNode?> SendJsonAsync(HttpMethod method, string relativePath, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, await ResolveUrlAsync(relativePath, ct));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return null;

        return await ParseJsonAsync(response, ct);
    }

    /// <summary>Auto-paginate `?page=N` array results capped by Options.MaxPages and merge into a flat list.</summary>
    public async Task<JsonArray> ListAllAsync(string relativePath, CancellationToken ct)
    {
        var aggregate = new JsonArray();
        var page = 1;
        while (page <= Math.Max(1, _options.MaxPages))
        {
            var sep = relativePath.Contains('?') ? '&' : '?';
            var url = $"{relativePath}{sep}page={page}&per_page={_options.DefaultPageSize}";
            var (node, _, totalPages) = await GetJsonPagedAsync(url, ct);
            if (node is not JsonArray arr || arr.Count == 0) break;

            foreach (var item in arr)
            {
                aggregate.Add(item?.DeepClone());
            }

            if (totalPages.HasValue && page >= totalPages.Value)
                break;
            page++;
        }
        return aggregate;
    }

    public async Task<(byte[] Bytes, string? ContentType, string? FileName)> DownloadBytesAsync(string urlOrPath, CancellationToken ct)
    {
        // Media source URLs come back absolute; strip to relative so BaseAddress + auth apply.
        var relative = TrimToRelative(urlOrPath);
        using var response = await _http.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        return (bytes, contentType, fileName);
    }

    /// <summary>Upload a media file via POST wp/v2/media with a Content-Disposition filename.</summary>
    public async Task<JsonNode?> UploadMediaAsync(string filePath, string contentType, string fileName, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = $"\"{fileName}\"",
        };
        using var response = await _http.PostAsync(await ResolveUrlAsync("wp-json/wp/v2/media", ct), content, ct);
        await EnsureSuccessAsync(response, ct);
        return await ParseJsonAsync(response, ct);
    }

    /// <summary>True when the site serves the REST API from pretty /wp-json/ URLs.</summary>
    public async Task<bool> UsesPrettyPermalinksAsync(CancellationToken ct)
        => _restPrefixPretty ??= await DetectPrettyPermalinksAsync(ct);

    /// <summary>
    /// A fresh WordPress install ships with "plain" permalinks, where /wp-json/ is not routed and the REST
    /// API only answers on index.php?rest_route=/. Probe once and address the API in whichever form works.
    /// </summary>
    private async Task<string> ResolveUrlAsync(string relativePath, CancellationToken ct)
    {
        var (route, query) = SplitRoute(relativePath);
        _restPrefixPretty ??= await DetectPrettyPermalinksAsync(ct);
        if (_restPrefixPretty.Value)
        {
            return "wp-json/" + route + (query is null ? string.Empty : "?" + query);
        }
        return "index.php?rest_route=/" + route + (query is null ? string.Empty : "&" + query);
    }

    private static (string Route, string? Query) SplitRoute(string relativePath)
    {
        var path = relativePath.StartsWith("wp-json/", StringComparison.OrdinalIgnoreCase)
            ? relativePath["wp-json/".Length..]
            : relativePath;
        var queryIndex = path.IndexOf('?');
        return queryIndex < 0 ? (path, null) : (path[..queryIndex], path[(queryIndex + 1)..]);
    }

    private async Task<bool> DetectPrettyPermalinksAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("wp-json/", ct);
            return response.IsSuccessStatusCode
                && (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private string TrimToRelative(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && _baseUri.IsBaseOf(abs))
        {
            return _baseUri.MakeRelativeUri(abs).ToString();
        }
        return url;
    }

    private static int? ReadIntHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var parsed)
            ? parsed
            : null;

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch { body = string.Empty; }

        // WordPress errors are JSON like {"code":"rest_forbidden","message":"...","data":{"status":401}} — keep them intact.
        if (body.Length > 2000) body = body[..2000] + "…(truncated)";

        var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? $" Run wp_test_endpoint with site='{EndpointName}' to diagnose authentication."
            : string.Empty;

        throw new McpException(
            $"WordPress API returned {(int)response.StatusCode} {response.ReasonPhrase} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} on endpoint '{EndpointName}'. Body: {body}{hint}");
    }

    private async Task<JsonNode?> ParseJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"WordPress returned {mediaType} instead of JSON for {response.RequestMessage?.RequestUri} on endpoint '{EndpointName}'. " +
                $"Check that the endpoint's RestApi:BaseUrl points at the WordPress site root. " +
                $"Run wp_setup_probe with url='{_baseUri}' for a full diagnosis.");
        }
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

/// <summary>Safety values for one endpoint after combining global settings with per-endpoint overrides (stricter wins).</summary>
public sealed record EffectiveSafety(
    bool ReadOnly,
    bool AllowDelete,
    bool AllowPluginInstall,
    bool ReadOnlyFromEndpoint,
    bool AllowDeleteFromEndpoint,
    bool AllowPluginInstallFromEndpoint)
{
    public static EffectiveSafety From(WordpressOptions global, EndpointOptions endpoint)
    {
        // Stricter wins: an endpoint may lock a site down further, never open it beyond the global posture.
        var readOnly = global.ReadOnly || (endpoint.ReadOnly ?? false);
        var allowDelete = global.AllowDelete && (endpoint.AllowDelete ?? true);
        var allowPluginInstall = global.AllowPluginInstall && (endpoint.AllowPluginInstall ?? true);

        return new EffectiveSafety(
            readOnly,
            allowDelete,
            allowPluginInstall,
            ReadOnlyFromEndpoint: !global.ReadOnly && readOnly,
            AllowDeleteFromEndpoint: global.AllowDelete && !allowDelete,
            AllowPluginInstallFromEndpoint: global.AllowPluginInstall && !allowPluginInstall);
    }
}
