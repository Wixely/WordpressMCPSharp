using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class ThemeTools
{
    [McpServerTool(Name = "wp_list_themes"),
     Description("List installed themes with status and versions. The core REST API cannot switch themes — activation must happen in wp-admin or WP-CLI.")]
    public static async Task<string> ListThemes(
        EndpointRegistry registry,
        [Description("Filter: active or inactive.")] string? status = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_themes");
        svc.EnsureFeature(svc.Options.EnableThemes, "Theme");
        var url = "wp-json/wp/v2/themes" + (string.IsNullOrWhiteSpace(status) ? string.Empty : $"?status={Uri.EscapeDataString(status)}");
        var node = await svc.GetJsonAsync(url, ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(t => t is JsonObject d ? new
        {
            stylesheet = d["stylesheet"]?.GetValue<string?>(),
            template = d["template"]?.GetValue<string?>(),
            status = d["status"]?.GetValue<string?>(),
            name = WpUtil.Rendered(d["name"]),
            version = d["version"]?.GetValue<string?>(),
            author = WpUtil.Rendered(d["author"]),
            is_block_theme = d["is_block_theme"]?.GetValue<bool?>(),
            requires_wp = d["requires_wp"]?.GetValue<string?>(),
            requires_php = d["requires_php"]?.GetValue<string?>(),
        } : (object?)t);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_get_active_theme"),
     Description("Get full detail for the currently active theme, including supports and theme URI.")]
    public static async Task<string> GetActiveTheme(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_get_active_theme");
        svc.EnsureFeature(svc.Options.EnableThemes, "Theme");
        var node = await svc.GetJsonAsync("wp-json/wp/v2/themes?status=active", ct);
        var active = (node as JsonArray)?.FirstOrDefault() as JsonObject;
        active?.Remove("_links");
        return active?.ToJsonString(JsonOpts.Default) ?? "null";
    }
}
