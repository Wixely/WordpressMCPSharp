using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class CommentTools
{
    [McpServerTool(Name = "wp_list_comments"),
     Description("List comments with optional filters. Returns slim metadata plus pagination totals.")]
    public static async Task<string> ListComments(
        WordpressService svc,
        [Description("Filter: post id.")] int? postId = null,
        [Description("Status filter: approve, hold, spam, trash, or all. Defaults to approve.")] string? status = null,
        [Description("Free-text search.")] string? search = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableComments, "Comment");
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}", "context=edit" };
        if (postId.HasValue) qs.Add($"post={postId.Value}");
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wp/v2/comments?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(c => c is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            post = d["post"]?.GetValue<int?>(),
            parent = d["parent"]?.GetValue<int?>(),
            author = d["author"]?.GetValue<int?>(),
            author_name = d["author_name"]?.GetValue<string?>(),
            author_email = d["author_email"]?.GetValue<string?>(),
            date = d["date"]?.GetValue<string?>(),
            status = d["status"]?.GetValue<string?>(),
            content = WpUtil.Rendered(d["content"]),
            link = d["link"]?.GetValue<string?>(),
        } : (object?)c);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_get_comment"),
     Description("Get one comment by id.")]
    public static async Task<string> GetComment(
        WordpressService svc,
        [Description("Comment id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableComments, "Comment");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/comments/{id}?context=edit", ct);
        if (node is JsonObject obj) obj.Remove("_links");
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_create_comment"),
     Description("Create a comment on a post (as the authenticated user, or with an explicit author name/email). Requires write mode.")]
    public static async Task<string> CreateComment(
        WordpressService svc,
        [Description("Post id to comment on.")] int postId,
        [Description("Comment text (HTML allowed).")] string content,
        [Description("Optional parent comment id for a threaded reply.")] int? parentId = null,
        [Description("Optional author display name (for comments not tied to a WP user).")] string? authorName = null,
        [Description("Optional author email (for comments not tied to a WP user).")] string? authorEmail = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableComments, "Comment");
        svc.EnsureWriteAllowed("wp_create_comment");

        var body = new JsonObject
        {
            ["post"] = postId,
            ["content"] = content,
        };
        if (parentId.HasValue) body["parent"] = parentId.Value;
        if (authorName is not null) body["author_name"] = authorName;
        if (authorEmail is not null) body["author_email"] = authorEmail;

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/comments", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_comment"),
     Description("Update a comment's content or moderation status (approve/hold/spam/trash). Requires write mode.")]
    public static async Task<string> UpdateComment(
        WordpressService svc,
        [Description("Comment id.")] int id,
        [Description("New comment text.")] string? content = null,
        [Description("New status: approve (or approved), hold, spam, trash.")] string? status = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableComments, "Comment");
        svc.EnsureWriteAllowed("wp_update_comment");

        var body = new JsonObject();
        if (content is not null) body["content"] = content;
        if (status is not null) body["status"] = status;

        if (body.Count == 0)
            throw new McpException("wp_update_comment: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/comments/{id}", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_comment"),
     Description("Move a comment to trash (default), or permanently delete with force=true. Permanent deletion requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteComment(
        WordpressService svc,
        [Description("Comment id.")] int id,
        [Description("If true, bypass trash and delete permanently.")] bool force = false,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableComments, "Comment");
        if (force) svc.EnsureDeleteAllowed("wp_delete_comment");
        else svc.EnsureWriteAllowed("wp_delete_comment");

        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/comments/{id}" + (force ? "?force=true" : string.Empty), null, ct);
        return JsonSerializer.Serialize(new { id, trashed = !force, deleted = force }, JsonOpts.Default);
    }
}
