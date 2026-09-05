using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class TaxonomyTools
{
    [McpServerTool(Name = "wp_list_taxonomies"),
     Description("List registered taxonomies (category, post_tag, and any added by plugins) with their REST collection names.")]
    public static async Task<string> ListTaxonomies(
        WordpressService svc,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableTaxonomies, "Taxonomy");
        var node = await svc.GetJsonAsync("wp-json/wp/v2/taxonomies?context=edit", ct);
        var items = (node as JsonObject ?? new JsonObject()).Select(kv => kv.Value is JsonObject d ? new
        {
            slug = kv.Key,
            name = d["name"]?.GetValue<string?>(),
            rest_base = d["rest_base"]?.GetValue<string?>(),
            hierarchical = d["hierarchical"]?.GetValue<bool?>(),
            types = (d["types"] as JsonArray)?.Select(t => t?.GetValue<string?>()),
        } : (object?)kv.Value);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_terms"),
     Description("List terms of a taxonomy (categories, tags, or a custom taxonomy's rest_base). Returns slim metadata plus pagination totals.")]
    public static async Task<string> ListTerms(
        WordpressService svc,
        [Description("REST collection: categories, tags, or a custom taxonomy rest_base from wp_list_taxonomies.")] string collection,
        [Description("Free-text search.")] string? search = null,
        [Description("Filter: parent term id (hierarchical taxonomies only).")] int? parentId = null,
        [Description("If true, include terms with no posts (hide_empty is off by default in the REST API; this toggles it on=false).")] bool hideEmpty = false,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableTaxonomies, "Taxonomy");
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (parentId.HasValue) qs.Add($"parent={parentId.Value}");
        if (hideEmpty) qs.Add("hide_empty=true");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync($"wp-json/wp/v2/{ValidateCollection(collection)}?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(t => t is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            name = d["name"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            taxonomy = d["taxonomy"]?.GetValue<string?>(),
            parent = d["parent"]?.GetValue<int?>(),
            count = d["count"]?.GetValue<int?>(),
            description = d["description"]?.GetValue<string?>(),
            link = d["link"]?.GetValue<string?>(),
        } : (object?)t);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_create_term"),
     Description("Create a term (category, tag, or custom taxonomy term). Requires write mode.")]
    public static async Task<string> CreateTerm(
        WordpressService svc,
        [Description("REST collection: categories, tags, or a custom taxonomy rest_base.")] string collection,
        [Description("Term name.")] string name,
        [Description("Optional URL slug.")] string? slug = null,
        [Description("Optional description.")] string? description = null,
        [Description("Optional parent term id (hierarchical taxonomies only).")] int? parentId = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableTaxonomies, "Taxonomy");
        svc.EnsureWriteAllowed("wp_create_term");

        var body = new JsonObject { ["name"] = name };
        if (slug is not null) body["slug"] = slug;
        if (description is not null) body["description"] = description;
        if (parentId.HasValue) body["parent"] = parentId.Value;

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/{ValidateCollection(collection)}", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_term"),
     Description("Update a term's name, slug, description, or parent. Requires write mode.")]
    public static async Task<string> UpdateTerm(
        WordpressService svc,
        [Description("REST collection: categories, tags, or a custom taxonomy rest_base.")] string collection,
        [Description("Term id.")] int id,
        [Description("New name.")] string? name = null,
        [Description("New slug.")] string? slug = null,
        [Description("New description.")] string? description = null,
        [Description("New parent term id (hierarchical taxonomies only).")] int? parentId = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableTaxonomies, "Taxonomy");
        svc.EnsureWriteAllowed("wp_update_term");

        var body = new JsonObject();
        if (name is not null) body["name"] = name;
        if (slug is not null) body["slug"] = slug;
        if (description is not null) body["description"] = description;
        if (parentId.HasValue) body["parent"] = parentId.Value;

        if (body.Count == 0)
            throw new McpException("wp_update_term: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/{ValidateCollection(collection)}/{id}", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_term"),
     Description("Permanently delete a term (terms do not support trash). Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteTerm(
        WordpressService svc,
        [Description("REST collection: categories, tags, or a custom taxonomy rest_base.")] string collection,
        [Description("Term id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableTaxonomies, "Taxonomy");
        svc.EnsureDeleteAllowed("wp_delete_term");
        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/{ValidateCollection(collection)}/{id}?force=true", null, ct);
        return JsonSerializer.Serialize(new { deleted = true, collection, id }, JsonOpts.Default);
    }

    private static string ValidateCollection(string collection)
    {
        var trimmed = collection.Trim().Trim('/');
        if (trimmed.Length == 0 || trimmed.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
            throw new McpException("collection must be a REST collection slug like 'categories' or 'tags'.");
        return trimmed;
    }
}
