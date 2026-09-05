using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Block-theme building blocks: templates, template parts, patterns and block navigation.
/// Modern themes (Twenty Twenty-Four onward) lay pages out with these rather than classic menus.
/// </summary>
[McpServerToolType]
public static class BlockThemeTools
{
    [McpServerTool(Name = "wp_list_templates"),
     Description("List the active block theme's templates (front page, single, archive, 404, …), showing which are theme defaults and which have been customised in the database.")]
    public static async Task<string> ListTemplates(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_templates");
        svc.EnsureFeature(svc.Options.EnableThemes, "Theme");
        var node = await svc.GetJsonAsync("wp-json/wp/v2/templates?context=edit", ct);
        return JsonSerializer.Serialize(Summarize(node), JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_get_template"),
     Description("Get one block template's block markup by id (for example `twentytwentyfive//front-page`). Use wp_list_templates to find ids.")]
    public static async Task<string> GetTemplate(
        EndpointRegistry registry,
        [Description("Template id, e.g. `twentytwentyfive//front-page`.")] string id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_get_template");
        svc.EnsureFeature(svc.Options.EnableThemes, "Theme");

        var node = await svc.GetJsonAsync($"wp-json/wp/v2/templates/{EncodeId(id)}?context=edit", ct);
        var content = (node?["content"] as JsonObject)?["raw"]?.GetValue<string?>() ?? string.Empty;
        var truncated = false;
        var limit = svc.Options.MaxInlineContentBytes;
        if (limit > 0 && content.Length > limit)
        {
            content = content[..limit];
            truncated = true;
        }

        return JsonSerializer.Serialize(new
        {
            id = node?["id"]?.GetValue<string?>(),
            slug = node?["slug"]?.GetValue<string?>(),
            title = WpUtil.Rendered(node?["title"]),
            description = node?["description"]?.GetValue<string?>(),
            source = node?["source"]?.GetValue<string?>(),
            theme = node?["theme"]?.GetValue<string?>(),
            truncated,
            content,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_update_template"),
     Description("Replace a block template's markup. This customises the theme template in the database; the theme's own file is untouched and the change can be reverted in the Site Editor. Requires write mode.")]
    public static async Task<string> UpdateTemplate(
        EndpointRegistry registry,
        [Description("Template id, e.g. `twentytwentyfive//front-page`.")] string id,
        [Description("New block markup for the template.")] string content,
        [Description("Optional new title.")] string? title = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_update_template");
        svc.EnsureFeature(svc.Options.EnableThemes, "Theme");
        svc.EnsureWriteAllowed("wp_update_template");

        var body = new JsonObject { ["content"] = content };
        if (title is not null) body["title"] = title;

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/templates/{EncodeId(id)}", body, ct);
        return JsonSerializer.Serialize(new
        {
            id = result?["id"]?.GetValue<string?>(),
            title = WpUtil.Rendered(result?["title"]),
            source = result?["source"]?.GetValue<string?>(),
            modified = result?["modified"]?.GetValue<string?>(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_template_parts"),
     Description("List the active block theme's template parts (header, footer, sidebar, …).")]
    public static async Task<string> ListTemplateParts(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_template_parts");
        svc.EnsureFeature(svc.Options.EnableThemes, "Theme");
        var node = await svc.GetJsonAsync("wp-json/wp/v2/template-parts?context=edit", ct);
        return JsonSerializer.Serialize(Summarize(node), JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_block_patterns"),
     Description("List the block patterns available on the site — ready-made layout blocks (hero sections, pricing tables, galleries) that can be pasted into page content.")]
    public static async Task<string> ListBlockPatterns(
        EndpointRegistry registry,
        [Description("Optional category filter, e.g. `header`, `gallery`, `call-to-action`.")] string? category = null,
        [Description("Optional free-text filter on the pattern title.")] string? search = null,
        [Description("Include each pattern's full block markup. Off by default because the payload is large.")] bool includeContent = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_block_patterns");
        svc.EnsureFeature(svc.Options.EnableThemes, "Theme");

        var node = await svc.GetJsonAsync("wp-json/wp/v2/block-patterns/patterns", ct);
        var patterns = (node as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(p => string.IsNullOrWhiteSpace(category)
                || (p["categories"] as JsonArray)?.Any(c => string.Equals(c?.GetValue<string?>(), category, StringComparison.OrdinalIgnoreCase)) == true)
            .Where(p => string.IsNullOrWhiteSpace(search)
                || (p["title"]?.GetValue<string?>()?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(p => new
            {
                name = p["name"]?.GetValue<string?>(),
                title = p["title"]?.GetValue<string?>(),
                description = p["description"]?.GetValue<string?>(),
                categories = (p["categories"] as JsonArray)?.Select(c => c?.GetValue<string?>()),
                blockTypes = (p["blockTypes"] as JsonArray)?.Select(b => b?.GetValue<string?>()),
                content = includeContent ? p["content"]?.GetValue<string?>() : null,
            })
            .ToList();

        return JsonSerializer.Serialize(new { count = patterns.Count, patterns }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_navigation"),
     Description("List block-theme navigation menus (the wp_navigation posts used by block themes). Classic themes use wp_list_menus instead.")]
    public static async Task<string> ListNavigation(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_navigation");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");

        var node = await svc.GetJsonAsync("wp-json/wp/v2/navigation?context=edit", ct);
        var items = (node as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(d => new
        {
            id = d["id"]?.GetValue<int?>(),
            title = WpUtil.Rendered(d["title"]),
            slug = d["slug"]?.GetValue<string?>(),
            status = d["status"]?.GetValue<string?>(),
            modified = d["modified"]?.GetValue<string?>(),
            content = (d["content"] as JsonObject)?["raw"]?.GetValue<string?>(),
        });
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    private static object Summarize(JsonNode? node)
    {
        var items = (node as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(d => new
        {
            id = d["id"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            title = WpUtil.Rendered(d["title"]),
            description = d["description"]?.GetValue<string?>(),
            // "theme" means the theme's own file; "custom" means it has been edited in the Site Editor.
            source = d["source"]?.GetValue<string?>(),
            theme = d["theme"]?.GetValue<string?>(),
            area = d["area"]?.GetValue<string?>(),
            hasCustomisations = string.Equals(d["source"]?.GetValue<string?>(), "custom", StringComparison.OrdinalIgnoreCase),
        });
        return items;
    }

    /// <summary>Template ids contain `//`, which must survive as a single path segment.</summary>
    private static string EncodeId(string id)
    {
        var trimmed = id.Trim();
        if (trimmed.Length == 0) throw new McpException("Template id is required, e.g. 'twentytwentyfive//front-page'.");
        return Uri.EscapeDataString(trimmed);
    }
}
