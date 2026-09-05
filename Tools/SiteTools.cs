using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class SiteTools
{
    [McpServerTool(Name = "wp_site_info"),
     Description("Get the site's name, description, URL, WordPress timezone, and available REST namespaces. Good first call to confirm connectivity and detect plugin APIs.")]
    public static async Task<string> SiteInfo(
        WordpressService svc,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("wp-json/", ct);
        if (node is not JsonObject d) return node?.ToJsonString(JsonOpts.Default) ?? "null";
        return JsonSerializer.Serialize(new
        {
            name = d["name"]?.GetValue<string?>(),
            description = d["description"]?.GetValue<string?>(),
            url = d["url"]?.GetValue<string?>(),
            home = d["home"]?.GetValue<string?>(),
            gmt_offset = d["gmt_offset"]?.ToString(),
            timezone_string = d["timezone_string"]?.GetValue<string?>(),
            site_icon_url = d["site_icon_url"]?.GetValue<string?>(),
            namespaces = (d["namespaces"] as JsonArray)?.Select(n => n?.GetValue<string?>()),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_search"),
     Description("Site-wide search across posts, pages, and other content types. Returns matches with type and URL.")]
    public static async Task<string> Search(
        WordpressService svc,
        [Description("Search terms.")] string query,
        [Description("Restrict to a type: post, term, or post-format.")] string? type = null,
        [Description("Restrict to a subtype, e.g. post, page, category, post_tag, or any.")] string? subtype = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            $"search={Uri.EscapeDataString(query)}",
            $"page={Math.Max(1, page)}",
            $"per_page={svc.Options.DefaultPageSize}",
        };
        if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrWhiteSpace(subtype)) qs.Add($"subtype={Uri.EscapeDataString(subtype)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wp/v2/search?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(r => r is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            title = d["title"]?.GetValue<string?>(),
            type = d["type"]?.GetValue<string?>(),
            subtype = d["subtype"]?.GetValue<string?>(),
            url = d["url"]?.GetValue<string?>(),
        } : (object?)r);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_list_post_types"),
     Description("List registered post types with their REST collection names.")]
    public static async Task<string> ListPostTypes(
        WordpressService svc,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("wp-json/wp/v2/types?context=edit", ct);
        var items = (node as JsonObject ?? new JsonObject()).Select(kv => kv.Value is JsonObject d ? new
        {
            slug = kv.Key,
            name = d["name"]?.GetValue<string?>(),
            rest_base = d["rest_base"]?.GetValue<string?>(),
            hierarchical = d["hierarchical"]?.GetValue<bool?>(),
            taxonomies = (d["taxonomies"] as JsonArray)?.Select(t => t?.GetValue<string?>()),
        } : (object?)kv.Value);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_post_statuses"),
     Description("List registered post statuses (publish, draft, pending, private, future, trash, ...).")]
    public static async Task<string> ListPostStatuses(
        WordpressService svc,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("wp-json/wp/v2/statuses?context=edit", ct);
        var items = (node as JsonObject ?? new JsonObject()).Select(kv => kv.Value is JsonObject d ? new
        {
            slug = kv.Key,
            name = d["name"]?.GetValue<string?>(),
            @public = d["public"]?.GetValue<bool?>(),
            @private = d["private"]?.GetValue<bool?>(),
            @protected = d["protected"]?.GetValue<bool?>(),
        } : (object?)kv.Value);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }
}
