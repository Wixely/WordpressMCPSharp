using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class RestPassthroughTools
{
    private static readonly HashSet<string> ReadMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD" };

    [McpServerTool(Name = "wp_rest_request"),
     Description("Call any WordPress REST route directly, including routes added by plugins (ACF, Yoast, Rank Math, WooCommerce, …). Discover available namespaces with wp_site_info. GET is allowed in read-only mode; other methods need write mode. Requires Wordpress:AllowRestPassthrough=true. Prefer a dedicated tool where one exists — this is the escape hatch for everything else.")]
    public static async Task<string> RestRequest(
        EndpointRegistry registry,
        [Description("REST route, with or without the wp-json prefix. Examples: `wp/v2/posts`, `/wc/v3/products`, `acf/v3/options/options`.")] string route,
        [Description("HTTP method: GET (default), POST, PUT, PATCH, DELETE.")] string method = "GET",
        [Description("Optional query string, e.g. `per_page=5&status=draft`. May also be embedded in `route`.")] string? query = null,
        [Description("Optional request body as a JSON object, for POST/PUT/PATCH.")] string? bodyJson = null,
        [Description("Maximum characters of response returned inline. Larger responses are truncated with a flag. Defaults to Wordpress:MaxInlineContentBytes.")] int? maxCharacters = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_rest_request");

        if (!svc.Options.AllowRestPassthrough)
        {
            throw new McpException(
                "MCP tool 'wp_rest_request' is disabled by server configuration. " +
                "Set Wordpress:AllowRestPassthrough=true to allow direct REST calls.");
        }

        var verb = method.Trim().ToUpperInvariant();
        if (verb is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE"))
            throw new McpException($"wp_rest_request: unsupported method '{method}'.");

        if (!ReadMethods.Contains(verb))
        {
            svc.EnsureWriteAllowed($"wp_rest_request ({verb})");
        }

        var normalized = NormalizeRoute(route);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var trimmed = query.TrimStart('?', '&');
            normalized += (normalized.Contains('?') ? "&" : "?") + trimmed;
        }

        JsonNode? body = null;
        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            if (ReadMethods.Contains(verb))
                throw new McpException($"wp_rest_request: a body cannot be sent with {verb}.");
            try { body = JsonNode.Parse(bodyJson); }
            catch (JsonException ex) { throw new McpException($"bodyJson is not valid JSON: {ex.Message}"); }
        }

        var result = ReadMethods.Contains(verb)
            ? await svc.GetJsonAsync("wp-json/" + normalized, ct)
            : await svc.SendJsonAsync(new HttpMethod(verb), "wp-json/" + normalized, body, ct);

        var text = result?.ToJsonString(JsonOpts.Default) ?? "null";
        var limit = maxCharacters ?? svc.Options.MaxInlineContentBytes;
        if (limit > 0 && text.Length > limit)
        {
            return JsonSerializer.Serialize(new
            {
                route = normalized,
                method = verb,
                truncated = true,
                length = text.Length,
                body = text[..limit],
            }, JsonOpts.Default);
        }

        return JsonSerializer.Serialize(new
        {
            route = normalized,
            method = verb,
            truncated = false,
            body = result,
        }, JsonOpts.Default);
    }

    private static string NormalizeRoute(string route)
    {
        var trimmed = route.Trim().TrimStart('/');
        if (trimmed.StartsWith("wp-json/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["wp-json/".Length..];
        }
        if (trimmed.Length == 0)
            throw new McpException("wp_rest_request: route is required, e.g. 'wp/v2/posts'.");
        if (trimmed.Contains("..", StringComparison.Ordinal))
            throw new McpException("wp_rest_request: route must not contain '..'.");
        return trimmed;
    }
}
