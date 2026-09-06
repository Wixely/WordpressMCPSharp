using System.Collections.Concurrent;
using WordpressMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace WordpressMCPSharp.Services;

/// <summary>
/// The named site registry. Resolves the `site` argument of every tool against configuration —
/// an MCP client can never supply a URL, host, or filesystem path — and hands back the channel
/// the tool needs, or a clear error naming the endpoint and the configuration keys to set.
/// </summary>
public sealed class EndpointRegistry : IDisposable
{
    private readonly Dictionary<string, EndpointOptions> _endpoints;
    private readonly WordpressOptions _options;
    private readonly string? _defaultSite;
    // Lazy values so two concurrent first-calls for the same endpoint cannot each build a client and
    // silently leak the loser's HttpClient.
    private readonly ConcurrentDictionary<string, Lazy<WordpressRestClient>> _restClients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public EndpointRegistry(IOptions<RegistryOptions> registry, IOptions<WordpressOptions> options)
    {
        _options = options.Value;
        var configured = registry.Value.Endpoints ?? new Dictionary<string, EndpointOptions>(StringComparer.OrdinalIgnoreCase);
        _endpoints = new Dictionary<string, EndpointOptions>(configured, StringComparer.OrdinalIgnoreCase);
        _defaultSite = registry.Value.DefaultSite;

        // Legacy single-site configuration: map flat Wordpress:{BaseUrl,Username,...} to "default".
        if (_endpoints.Count == 0 && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _endpoints["default"] = new EndpointOptions
            {
                RestApi = new RestApiOptions
                {
                    BaseUrl = _options.BaseUrl,
                    Username = _options.Username,
                    ApplicationPassword = _options.ApplicationPassword,
                    AllowInvalidCertificate = _options.AllowInvalidCertificate,
                },
                Description = "Legacy single-site configuration (Wordpress:BaseUrl).",
            };
            LegacyMapped = true;
        }

        ConfigurationWarnings = Validate();
    }

    /// <summary>True when the endpoint list came from the legacy flat configuration.</summary>
    public bool LegacyMapped { get; }

    /// <summary>Non-fatal configuration problems, surfaced at startup and by wp_list_endpoints.</summary>
    public IReadOnlyList<string> ConfigurationWarnings { get; }

    public IReadOnlyCollection<string> Names => _endpoints.Keys;
    public int Count => _endpoints.Count;
    public WordpressOptions Options => _options;

    public IEnumerable<(string Name, EndpointOptions Endpoint)> All =>
        _endpoints.Select(kv => (kv.Key, kv.Value));

    /// <summary>Resolve a site name to its configuration. Never accepts anything but a configured name.</summary>
    public (string Name, EndpointOptions Endpoint) Resolve(string? site, string operation)
    {
        if (_endpoints.Count == 0)
        {
            throw new McpException(
                $"MCP tool '{operation}' needs a configured site, but no endpoints are configured. " +
                "Add an Endpoints section (Endpoints:<name>:RestApi:BaseUrl / Username / ApplicationPassword). " +
                "Run wp_setup_probe with your site URL to work out the right values.");
        }

        if (!string.IsNullOrWhiteSpace(site))
        {
            if (_endpoints.TryGetValue(site, out var named))
                return (Canonical(site), named);

            throw new McpException(
                $"MCP tool '{operation}': unknown site '{site}'. Configured endpoints: {NameList()}.");
        }

        if (!string.IsNullOrWhiteSpace(_defaultSite))
        {
            if (_endpoints.TryGetValue(_defaultSite, out var fallback))
                return (Canonical(_defaultSite), fallback);

            throw new McpException(
                $"MCP tool '{operation}': DefaultSite is set to '{_defaultSite}', which is not a configured endpoint. " +
                $"Configured endpoints: {NameList()}.");
        }

        if (_endpoints.Count == 1)
        {
            var only = _endpoints.First();
            return (only.Key, only.Value);
        }

        throw new McpException(
            $"MCP tool '{operation}': no site specified and no DefaultSite configured, but {_endpoints.Count} endpoints exist. " +
            $"Pass site=<name> or set DefaultSite. Configured endpoints: {NameList()}.");
    }

    /// <summary>Resolve a site and return its REST client, or explain what is missing.</summary>
    public WordpressRestClient RequireRest(string? site, string operation)
    {
        var (name, endpoint) = Resolve(site, operation);

        if (!endpoint.HasRest)
        {
            var reason = endpoint.HasManagement
                ? $"endpoint '{name}' is management-only ({endpoint.ManagementMode})"
                : $"endpoint '{name}' has no RestApi block";
            throw new McpException(
                $"MCP tool '{operation}' requires REST access, but {reason}. " +
                $"Set Endpoints:{name}:RestApi:BaseUrl, :Username and :ApplicationPassword. " +
                "Run wp_setup_probe with the site URL to work out the right values.");
        }

        return _restClients.GetOrAdd(name, key => new Lazy<WordpressRestClient>(
            () => new WordpressRestClient(key, endpoint.RestApi!, _options, EffectiveSafety.From(_options, endpoint)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>Resolve a site that must have a management channel. Used by CLI-backed tools.</summary>
    public (string Name, EndpointOptions Endpoint) RequireManagement(string? site, string operation)
    {
        var (name, endpoint) = Resolve(site, operation);

        if (!endpoint.HasManagement)
        {
            var reason = endpoint.HasRest
                ? $"endpoint '{name}' is REST-only"
                : $"endpoint '{name}' has no management block";
            throw new McpException(
                $"MCP tool '{operation}' requires management access (WP-CLI), but {reason}. " +
                $"Configure exactly one of Endpoints:{name}:Ssh, :Local or :Docker.");
        }

        return (name, endpoint);
    }

    /// <summary>Effective safety for an endpoint, combining global settings with per-endpoint overrides.</summary>
    public EffectiveSafety SafetyFor(EndpointOptions endpoint) => EffectiveSafety.From(_options, endpoint);

    /// <summary>Look up an endpoint without applying the default-site rules.</summary>
    public bool TryGet(string name, out EndpointOptions endpoint) => _endpoints.TryGetValue(name, out endpoint!);

    public string NameList() => _endpoints.Count == 0
        ? "(none)"
        : string.Join(", ", _endpoints.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    private string Canonical(string requested) =>
        _endpoints.Keys.FirstOrDefault(k => string.Equals(k, requested, StringComparison.OrdinalIgnoreCase)) ?? requested;

    private List<string> Validate()
    {
        var warnings = new List<string>();

        foreach (var (name, endpoint) in _endpoints)
        {
            var managementBlocks = new[]
            {
                endpoint.Ssh is null ? null : "Ssh",
                endpoint.Local is null ? null : "Local",
                endpoint.Docker is null ? null : "Docker",
            }.Where(b => b is not null).ToList();

            if (managementBlocks.Count > 1)
            {
                warnings.Add(
                    $"Endpoint '{name}' declares {managementBlocks.Count} management blocks ({string.Join(", ", managementBlocks)}); " +
                    "configure exactly one. Management tools will refuse this endpoint.");
            }

            if (!endpoint.HasRest && !endpoint.HasManagement)
            {
                warnings.Add(
                    $"Endpoint '{name}' has no usable channel: add a RestApi block (with BaseUrl) or one of Ssh/Local/Docker.");
            }

            if (endpoint.RestApi is { } rest && !string.IsNullOrWhiteSpace(rest.BaseUrl))
            {
                if (!Uri.TryCreate(rest.BaseUrl, UriKind.Absolute, out var uri))
                {
                    warnings.Add($"Endpoint '{name}': RestApi:BaseUrl '{rest.BaseUrl}' is not an absolute URL.");
                }
                else
                {
                    if (uri.AbsolutePath.Contains("wp-json", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add(
                            $"Endpoint '{name}': RestApi:BaseUrl should be the site root, not the REST route. " +
                            $"Use {uri.GetLeftPart(UriPartial.Authority)}/ instead of '{rest.BaseUrl}'.");
                    }

                    if (uri.Scheme == Uri.UriSchemeHttp
                        && !uri.IsLoopback
                        && !string.IsNullOrWhiteSpace(rest.ApplicationPassword))
                    {
                        warnings.Add(
                            $"Endpoint '{name}': BaseUrl uses plain HTTP. WordPress rejects application passwords over HTTP " +
                            "unless the site sets WP_ENVIRONMENT_TYPE=local. Prefer https://.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(rest.Username) && string.IsNullOrWhiteSpace(rest.ApplicationPassword))
                {
                    warnings.Add($"Endpoint '{name}': Username is set but ApplicationPassword is blank; REST calls will be unauthenticated.");
                }
            }

            if (endpoint.HasManagement && string.IsNullOrWhiteSpace(ManagementPath(endpoint)))
            {
                warnings.Add($"Endpoint '{name}': the {endpoint.ManagementMode} block has no Path (the directory containing wp-config.php).");
            }

            if (endpoint.HasManagement && string.IsNullOrWhiteSpace(endpoint.EffectiveUrl))
            {
                warnings.Add(
                    $"Endpoint '{name}' is management-capable but has no Url, so the site identity cross-check cannot run. " +
                    $"Set Endpoints:{name}:Url to the site's address to guard against a mistyped path.");
            }
        }

        if (!string.IsNullOrWhiteSpace(_defaultSite) && !_endpoints.ContainsKey(_defaultSite))
        {
            warnings.Add($"DefaultSite '{_defaultSite}' is not a configured endpoint. Configured endpoints: {NameList()}.");
        }

        if (_endpoints.Count == 0)
        {
            warnings.Add("No endpoints configured. Run wp_setup_probe with a site URL to generate a configuration block.");
        }

        return warnings;
    }

    public static string? ManagementPath(EndpointOptions endpoint) =>
        endpoint.Ssh?.Path ?? endpoint.Local?.Path ?? endpoint.Docker?.Path;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in _restClients.Values)
        {
            if (client.IsValueCreated) client.Value.Dispose();
        }
        _restClients.Clear();
    }
}
