using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class PageTools
{
    [McpServerTool(Name = "wp_list_pages"),
     Description("List pages with optional filters. Returns slim metadata plus pagination totals. Use wp_get_page for full detail.")]
    public static async Task<string> ListPages(
        WordpressService svc,
        [Description("Free-text search across title and content.")] string? search = null,
        [Description("Status filter: publish, future, draft, pending, private, trash, or any. Defaults to publish.")] string? status = null,
        [Description("Filter: parent page id (0 for top-level pages).")] int? parentId = null,
        [Description("Order by: date, modified, title, slug, id, menu_order. Defaults to date.")] string? orderby = null,
        [Description("Order direction: asc or desc. Defaults to desc.")] string? order = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}", "context=edit" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (parentId.HasValue) qs.Add($"parent={parentId.Value}");
        if (!string.IsNullOrWhiteSpace(orderby)) qs.Add($"orderby={Uri.EscapeDataString(orderby)}");
        if (!string.IsNullOrWhiteSpace(order)) qs.Add($"order={Uri.EscapeDataString(order)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wp/v2/pages?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(WpUtil.SummarizeContentItem);
        return WpUtil.PagedResult(total, totalPages, page, items);
    }

    [McpServerTool(Name = "wp_get_page"),
     Description("Get full metadata for one page by id (content body omitted — use wp_get_post_content with type=page).")]
    public static async Task<string> GetPage(
        WordpressService svc,
        [Description("Page id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/pages/{id}?context=edit", ct);
        return node is JsonObject obj
            ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default)
            : node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_create_page"),
     Description("Create a page. Content accepts HTML or block markup. Requires write mode.")]
    public static async Task<string> CreatePage(
        WordpressService svc,
        [Description("Page title.")] string title,
        [Description("Page body (HTML or block markup).")] string content,
        [Description("Status: draft, publish, pending, private, future. Defaults to draft.")] string status = "draft",
        [Description("Optional URL slug.")] string? slug = null,
        [Description("Optional parent page id for hierarchy.")] int? parentId = null,
        [Description("Optional menu order (integer used for manual ordering).")] int? menuOrder = null,
        [Description("Optional author user id.")] int? authorId = null,
        [Description("Optional theme template file name.")] string? template = null,
        [Description("Comment status: open or closed.")] string? commentStatus = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_create_page");

        var body = new JsonObject
        {
            ["title"] = title,
            ["content"] = content,
            ["status"] = status,
        };
        if (slug is not null) body["slug"] = slug;
        if (parentId.HasValue) body["parent"] = parentId.Value;
        if (menuOrder.HasValue) body["menu_order"] = menuOrder.Value;
        if (authorId.HasValue) body["author"] = authorId.Value;
        if (template is not null) body["template"] = template;
        if (commentStatus is not null) body["comment_status"] = commentStatus;

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/pages", body, ct);
        return result is JsonObject obj ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default) : result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_page"),
     Description("Update a page's fields (PATCH semantics — only supplied fields change). Requires write mode.")]
    public static async Task<string> UpdatePage(
        WordpressService svc,
        [Description("Page id.")] int id,
        [Description("New title.")] string? title = null,
        [Description("New body (HTML or block markup).")] string? content = null,
        [Description("New status: draft, publish, pending, private, future.")] string? status = null,
        [Description("New URL slug.")] string? slug = null,
        [Description("New parent page id (0 for top-level).")] int? parentId = null,
        [Description("New menu order.")] int? menuOrder = null,
        [Description("New author user id.")] int? authorId = null,
        [Description("New theme template file name.")] string? template = null,
        [Description("Comment status: open or closed.")] string? commentStatus = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_update_page");

        var body = new JsonObject();
        if (title is not null) body["title"] = title;
        if (content is not null) body["content"] = content;
        if (status is not null) body["status"] = status;
        if (slug is not null) body["slug"] = slug;
        if (parentId.HasValue) body["parent"] = parentId.Value;
        if (menuOrder.HasValue) body["menu_order"] = menuOrder.Value;
        if (authorId.HasValue) body["author"] = authorId.Value;
        if (template is not null) body["template"] = template;
        if (commentStatus is not null) body["comment_status"] = commentStatus;

        if (body.Count == 0)
            throw new McpException("wp_update_page: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/pages/{id}", body, ct);
        return result is JsonObject obj ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default) : result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_page"),
     Description("Move a page to trash (default), or permanently delete with force=true. Permanent deletion requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeletePage(
        WordpressService svc,
        [Description("Page id.")] int id,
        [Description("If true, bypass trash and delete permanently.")] bool force = false,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        if (force) svc.EnsureDeleteAllowed("wp_delete_page");
        else svc.EnsureWriteAllowed("wp_delete_page");

        var result = await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/pages/{id}" + (force ? "?force=true" : string.Empty), null, ct);
        return JsonSerializer.Serialize(new
        {
            id,
            trashed = !force,
            deleted = force,
            title = WpUtil.Rendered(result?["title"] ?? (result?["previous"] as JsonObject)?["title"]),
        }, JsonOpts.Default);
    }
}
