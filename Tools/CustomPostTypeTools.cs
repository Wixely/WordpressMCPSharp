using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// CRUD for any registered post type — portfolios, events, testimonials, products and whatever else
/// a theme or plugin registers. The dedicated post and page tools cover the two built-in types;
/// these cover everything else without falling back to the raw REST passthrough.
/// </summary>
[McpServerToolType]
public static class CustomPostTypeTools
{
    [McpServerTool(Name = "wp_list_custom_posts"),
     Description("List items of any registered post type (use wp_list_post_types to discover them, e.g. `portfolio`, `event`, `product`). Returns slim metadata plus pagination totals.")]
    public static async Task<string> ListCustomPosts(
        EndpointRegistry registry,
        [Description("Post type slug or its REST base, e.g. `portfolio`.")] string postType,
        [Description("Free-text search.")] string? search = null,
        [Description("Status filter: publish, draft, pending, private, future, trash, any. Defaults to publish.")] string? status = null,
        [Description("Order by: date, modified, title, slug, id, menu_order.")] string? orderby = null,
        [Description("Order direction: asc or desc.")] string? order = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_custom_posts");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var restBase = await ResolveRestBaseAsync(svc, postType, "wp_list_custom_posts", ct);

        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}", "context=edit" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(orderby)) qs.Add($"orderby={Uri.EscapeDataString(orderby)}");
        if (!string.IsNullOrWhiteSpace(order)) qs.Add($"order={Uri.EscapeDataString(order)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync($"wp-json/wp/v2/{restBase}?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(WpUtil.SummarizeContentItem);
        return WpUtil.PagedResult(total, totalPages, page, items);
    }

    [McpServerTool(Name = "wp_get_custom_post"),
     Description("Get one item of a custom post type by id, including its registered meta fields. The body is omitted — fetch it with wp_get_custom_post_content.")]
    public static async Task<string> GetCustomPost(
        EndpointRegistry registry,
        [Description("Post type slug or REST base.")] string postType,
        [Description("Item id.")] int id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_get_custom_post");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var restBase = await ResolveRestBaseAsync(svc, postType, "wp_get_custom_post", ct);

        var node = await svc.GetJsonAsync($"wp-json/wp/v2/{restBase}/{id}?context=edit", ct);
        return node is JsonObject obj
            ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default)
            : node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_get_custom_post_content"),
     Description("Return the body of a custom post type item, rendered or raw, capped by Wordpress:MaxInlineContentBytes.")]
    public static async Task<string> GetCustomPostContent(
        EndpointRegistry registry,
        [Description("Post type slug or REST base.")] string postType,
        [Description("Item id.")] int id,
        [Description("Return the raw block/HTML source instead of the rendered output.")] bool raw = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_get_custom_post_content");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var restBase = await ResolveRestBaseAsync(svc, postType, "wp_get_custom_post_content", ct);

        var node = await svc.GetJsonAsync($"wp-json/wp/v2/{restBase}/{id}?context=edit", ct);
        var contentNode = node?["content"] as JsonObject;
        var content = (raw ? contentNode?["raw"]?.GetValue<string?>() : contentNode?["rendered"]?.GetValue<string?>()) ?? string.Empty;

        var truncated = false;
        var limit = svc.Options.MaxInlineContentBytes;
        if (limit > 0 && content.Length > limit)
        {
            content = content[..limit];
            truncated = true;
        }

        return JsonSerializer.Serialize(new
        {
            id,
            postType,
            title = WpUtil.Rendered(node?["title"]),
            raw,
            truncated,
            content,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_create_custom_post"),
     Description("Create an item of any registered post type. Pass extra type-specific fields as JSON in fieldsJson. Requires write mode.")]
    public static async Task<string> CreateCustomPost(
        EndpointRegistry registry,
        [Description("Post type slug or REST base, e.g. `portfolio`.")] string postType,
        [Description("Title.")] string title,
        [Description("Body content (HTML or block markup).")] string? content = null,
        [Description("Status: draft (default), publish, pending, private, future.")] string status = "draft",
        [Description("Optional excerpt.")] string? excerpt = null,
        [Description("Optional URL slug.")] string? slug = null,
        [Description("Optional featured media (attachment) id.")] int? featuredMediaId = null,
        [Description("Any additional fields as a JSON object, e.g. `{\"menu_order\":3,\"meta\":{\"my_field\":\"x\"}}`.")] string? fieldsJson = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_create_custom_post");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_create_custom_post");
        var restBase = await ResolveRestBaseAsync(svc, postType, "wp_create_custom_post", ct);

        var body = new JsonObject { ["title"] = title, ["status"] = status };
        if (content is not null) body["content"] = content;
        if (excerpt is not null) body["excerpt"] = excerpt;
        if (slug is not null) body["slug"] = slug;
        if (featuredMediaId.HasValue) body["featured_media"] = featuredMediaId.Value;
        MergeExtraFields(body, fieldsJson);

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/{restBase}", body, ct);
        return result is JsonObject obj
            ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default)
            : result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_custom_post"),
     Description("Update an item of any registered post type. Only the fields supplied change. Requires write mode.")]
    public static async Task<string> UpdateCustomPost(
        EndpointRegistry registry,
        [Description("Post type slug or REST base.")] string postType,
        [Description("Item id.")] int id,
        [Description("New title.")] string? title = null,
        [Description("New body content.")] string? content = null,
        [Description("New status.")] string? status = null,
        [Description("New excerpt.")] string? excerpt = null,
        [Description("New slug.")] string? slug = null,
        [Description("New featured media id (0 to clear).")] int? featuredMediaId = null,
        [Description("Any additional fields as a JSON object, e.g. `{\"meta\":{\"my_field\":\"x\"}}`.")] string? fieldsJson = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_update_custom_post");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_update_custom_post");
        var restBase = await ResolveRestBaseAsync(svc, postType, "wp_update_custom_post", ct);

        var body = new JsonObject();
        if (title is not null) body["title"] = title;
        if (content is not null) body["content"] = content;
        if (status is not null) body["status"] = status;
        if (excerpt is not null) body["excerpt"] = excerpt;
        if (slug is not null) body["slug"] = slug;
        if (featuredMediaId.HasValue) body["featured_media"] = featuredMediaId.Value;
        MergeExtraFields(body, fieldsJson);

        if (body.Count == 0)
            throw new McpException("wp_update_custom_post: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/{restBase}/{id}", body, ct);
        return result is JsonObject obj
            ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default)
            : result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_custom_post"),
     Description("Trash an item of a custom post type, or delete it permanently with force=true. Permanent deletion requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteCustomPost(
        EndpointRegistry registry,
        [Description("Post type slug or REST base.")] string postType,
        [Description("Item id.")] int id,
        [Description("Bypass trash and delete permanently.")] bool force = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_delete_custom_post");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        if (force) svc.EnsureDeleteAllowed("wp_delete_custom_post");
        else svc.EnsureWriteAllowed("wp_delete_custom_post");
        var restBase = await ResolveRestBaseAsync(svc, postType, "wp_delete_custom_post", ct);

        var result = await svc.SendJsonAsync(
            HttpMethod.Delete,
            $"wp-json/wp/v2/{restBase}/{id}" + (force ? "?force=true" : string.Empty), null, ct);

        return JsonSerializer.Serialize(new
        {
            id,
            postType,
            trashed = !force,
            deleted = force,
            title = WpUtil.Rendered(result?["title"] ?? (result?["previous"] as JsonObject)?["title"]),
        }, JsonOpts.Default);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Map a post type slug to its REST base, confirming it is actually registered. Resolving against
    /// the site's own type list keeps this from becoming an unrestricted route-access tool.
    /// </summary>
    private static async Task<string> ResolveRestBaseAsync(
        WordpressRestClient svc, string postType, string operation, CancellationToken ct)
    {
        var requested = postType.Trim().Trim('/');
        if (requested.Length == 0 || requested.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
            throw new McpException($"{operation}: postType must be a post type slug such as 'portfolio'.");

        var types = await svc.GetJsonAsync("wp-json/wp/v2/types?context=edit", ct) as JsonObject
            ?? throw new McpException($"{operation}: could not read the site's registered post types.");

        var available = new List<string>();
        foreach (var (slug, value) in types)
        {
            var restBase = (value as JsonObject)?["rest_base"]?.GetValue<string?>();
            if (string.IsNullOrWhiteSpace(restBase)) continue;
            available.Add(slug);

            if (string.Equals(slug, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(restBase, requested, StringComparison.OrdinalIgnoreCase))
            {
                return restBase!;
            }
        }

        throw new McpException(
            $"{operation}: post type '{postType}' is not registered on endpoint '{svc.EndpointName}', or it is not exposed over REST. " +
            $"Available types: {string.Join(", ", available.OrderBy(a => a))}. Use wp_list_post_types for detail.");
    }

    private static void MergeExtraFields(JsonObject body, string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson)) return;

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(fieldsJson); }
        catch (JsonException ex) { throw new McpException($"fieldsJson is not valid JSON: {ex.Message}"); }

        if (parsed is not JsonObject extra)
            throw new McpException("fieldsJson must be a JSON object.");

        foreach (var (key, value) in extra)
        {
            body[key] = value?.DeepClone();
        }
    }
}
