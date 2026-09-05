using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class PostTools
{
    [McpServerTool(Name = "wp_list_posts"),
     Description("List posts with optional filters. Returns slim metadata plus pagination totals. Use wp_get_post for full detail.")]
    public static async Task<string> ListPosts(
        WordpressService svc,
        [Description("Free-text search across title and content.")] string? search = null,
        [Description("Status filter: publish, future, draft, pending, private, trash, or any (comma-separated allowed). Defaults to publish.")] string? status = null,
        [Description("Filter: author user id.")] int? authorId = null,
        [Description("Filter: category term id.")] int? categoryId = null,
        [Description("Filter: tag term id.")] int? tagId = null,
        [Description("Only posts published after this ISO 8601 date-time.")] string? after = null,
        [Description("Only posts published before this ISO 8601 date-time.")] string? before = null,
        [Description("Order by: date, modified, title, slug, id, author, include. Defaults to date.")] string? orderby = null,
        [Description("Order direction: asc or desc. Defaults to desc.")] string? order = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}", "context=edit" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (authorId.HasValue) qs.Add($"author={authorId.Value}");
        if (categoryId.HasValue) qs.Add($"categories={categoryId.Value}");
        if (tagId.HasValue) qs.Add($"tags={tagId.Value}");
        if (!string.IsNullOrWhiteSpace(after)) qs.Add($"after={Uri.EscapeDataString(after)}");
        if (!string.IsNullOrWhiteSpace(before)) qs.Add($"before={Uri.EscapeDataString(before)}");
        if (!string.IsNullOrWhiteSpace(orderby)) qs.Add($"orderby={Uri.EscapeDataString(orderby)}");
        if (!string.IsNullOrWhiteSpace(order)) qs.Add($"order={Uri.EscapeDataString(order)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wp/v2/posts?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(WpUtil.SummarizeContentItem);
        return WpUtil.PagedResult(total, totalPages, page, items);
    }

    [McpServerTool(Name = "wp_get_post"),
     Description("Get full metadata for one post by id (content body omitted — use wp_get_post_content).")]
    public static async Task<string> GetPost(
        WordpressService svc,
        [Description("Post id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/posts/{id}?context=edit", ct);
        return node is JsonObject obj
            ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default)
            : node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_get_post_content"),
     Description("Return a post or page body. Content larger than Wordpress:MaxInlineContentBytes is truncated with a flag.")]
    public static async Task<string> GetPostContent(
        WordpressService svc,
        [Description("Post or page id.")] int id,
        [Description("Content type of the id: post or page. Defaults to post.")] string type = "post",
        [Description("If true, return the raw block/HTML source instead of the rendered HTML.")] bool raw = false,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var collection = ResolveCollection(type);
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/{collection}/{id}?context=edit", ct);
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
            type,
            title = WpUtil.Rendered(node?["title"]),
            raw,
            length = (raw ? contentNode?["raw"] : contentNode?["rendered"])?.GetValue<string?>()?.Length ?? 0,
            truncated,
            content,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_create_post"),
     Description("Create a post. Content accepts HTML or block markup. Requires write mode.")]
    public static async Task<string> CreatePost(
        WordpressService svc,
        [Description("Post title.")] string title,
        [Description("Post body (HTML or block markup).")] string content,
        [Description("Status: draft, publish, pending, private, future. Defaults to draft.")] string status = "draft",
        [Description("Optional excerpt.")] string? excerpt = null,
        [Description("Optional URL slug.")] string? slug = null,
        [Description("Optional author user id.")] int? authorId = null,
        [Description("Optional category term ids as JSON array, e.g. `[1,2]`.")] string? categoryIdsJson = null,
        [Description("Optional tag term ids as JSON array, e.g. `[3]`.")] string? tagIdsJson = null,
        [Description("Optional featured media (attachment) id.")] int? featuredMediaId = null,
        [Description("Comment status: open or closed.")] string? commentStatus = null,
        [Description("Publish date (ISO 8601, site timezone). Combine with status=future to schedule.")] string? date = null,
        [Description("Pin the post to the front page.")] bool sticky = false,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_create_post");

        var body = new JsonObject
        {
            ["title"] = title,
            ["content"] = content,
            ["status"] = status,
        };
        if (excerpt is not null) body["excerpt"] = excerpt;
        if (slug is not null) body["slug"] = slug;
        if (authorId.HasValue) body["author"] = authorId.Value;
        if (categoryIdsJson is not null) body["categories"] = WpUtil.ParseIntArray(categoryIdsJson, nameof(categoryIdsJson));
        if (tagIdsJson is not null) body["tags"] = WpUtil.ParseIntArray(tagIdsJson, nameof(tagIdsJson));
        if (featuredMediaId.HasValue) body["featured_media"] = featuredMediaId.Value;
        if (commentStatus is not null) body["comment_status"] = commentStatus;
        if (date is not null) body["date"] = date;
        if (sticky) body["sticky"] = true;

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/posts", body, ct);
        return result is JsonObject obj ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default) : result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_post"),
     Description("Update a post's fields (PATCH semantics — only supplied fields change). Requires write mode.")]
    public static async Task<string> UpdatePost(
        WordpressService svc,
        [Description("Post id.")] int id,
        [Description("New title.")] string? title = null,
        [Description("New body (HTML or block markup).")] string? content = null,
        [Description("New status: draft, publish, pending, private, future.")] string? status = null,
        [Description("New excerpt.")] string? excerpt = null,
        [Description("New URL slug.")] string? slug = null,
        [Description("New author user id.")] int? authorId = null,
        [Description("Replacement category term ids as JSON array. Omit to leave unchanged.")] string? categoryIdsJson = null,
        [Description("Replacement tag term ids as JSON array. Omit to leave unchanged.")] string? tagIdsJson = null,
        [Description("New featured media (attachment) id, or 0 to clear.")] int? featuredMediaId = null,
        [Description("Comment status: open or closed.")] string? commentStatus = null,
        [Description("New publish date (ISO 8601).")] string? date = null,
        [Description("Set sticky state.")] bool? sticky = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_update_post");

        var body = new JsonObject();
        if (title is not null) body["title"] = title;
        if (content is not null) body["content"] = content;
        if (status is not null) body["status"] = status;
        if (excerpt is not null) body["excerpt"] = excerpt;
        if (slug is not null) body["slug"] = slug;
        if (authorId.HasValue) body["author"] = authorId.Value;
        if (categoryIdsJson is not null) body["categories"] = WpUtil.ParseIntArray(categoryIdsJson, nameof(categoryIdsJson));
        if (tagIdsJson is not null) body["tags"] = WpUtil.ParseIntArray(tagIdsJson, nameof(tagIdsJson));
        if (featuredMediaId.HasValue) body["featured_media"] = featuredMediaId.Value;
        if (commentStatus is not null) body["comment_status"] = commentStatus;
        if (date is not null) body["date"] = date;
        if (sticky.HasValue) body["sticky"] = sticky.Value;

        if (body.Count == 0)
            throw new McpException("wp_update_post: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/posts/{id}", body, ct);
        return result is JsonObject obj ? WpUtil.StripContent(obj).ToJsonString(JsonOpts.Default) : result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_post"),
     Description("Move a post to trash (default), or permanently delete with force=true. Permanent deletion requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeletePost(
        WordpressService svc,
        [Description("Post id.")] int id,
        [Description("If true, bypass trash and delete permanently.")] bool force = false,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        if (force) svc.EnsureDeleteAllowed("wp_delete_post");
        else svc.EnsureWriteAllowed("wp_delete_post");

        var result = await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/posts/{id}" + (force ? "?force=true" : string.Empty), null, ct);
        return JsonSerializer.Serialize(new
        {
            id,
            trashed = !force,
            deleted = force,
            title = WpUtil.Rendered(result?["title"] ?? (result?["previous"] as JsonObject)?["title"]),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_post_revisions"),
     Description("List revisions of a post or page.")]
    public static async Task<string> ListPostRevisions(
        WordpressService svc,
        [Description("Post or page id.")] int id,
        [Description("Content type of the id: post or page. Defaults to post.")] string type = "post",
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var collection = ResolveCollection(type);
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/{collection}/{id}/revisions", ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(r => r is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            parent = d["parent"]?.GetValue<int?>(),
            author = d["author"]?.GetValue<int?>(),
            date = d["date"]?.GetValue<string?>(),
            modified = d["modified"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            title = WpUtil.Rendered(d["title"]),
        } : (object?)r);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    private static string ResolveCollection(string type) => type.ToLowerInvariant() switch
    {
        "post" or "posts" => "posts",
        "page" or "pages" => "pages",
        _ => throw new McpException("type must be 'post' or 'page'."),
    };
}
