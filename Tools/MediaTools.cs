using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class MediaTools
{
    [McpServerTool(Name = "wp_list_media"),
     Description("List media library items. Returns slim metadata plus pagination totals.")]
    public static async Task<string> ListMedia(
        WordpressService svc,
        [Description("Free-text search.")] string? search = null,
        [Description("Filter: media type — image, video, audio, application, text.")] string? mediaType = null,
        [Description("Filter: MIME type, e.g. image/png.")] string? mimeType = null,
        [Description("Filter: parent post id (0 for unattached).")] int? parentId = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(mediaType)) qs.Add($"media_type={Uri.EscapeDataString(mediaType)}");
        if (!string.IsNullOrWhiteSpace(mimeType)) qs.Add($"mime_type={Uri.EscapeDataString(mimeType)}");
        if (parentId.HasValue) qs.Add($"parent={parentId.Value}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wp/v2/media?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(m => m is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            date = d["date"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            title = WpUtil.Rendered(d["title"]),
            media_type = d["media_type"]?.GetValue<string?>(),
            mime_type = d["mime_type"]?.GetValue<string?>(),
            source_url = d["source_url"]?.GetValue<string?>(),
            link = d["link"]?.GetValue<string?>(),
            post = d["post"]?.GetValue<int?>(),
            alt_text = d["alt_text"]?.GetValue<string?>(),
        } : (object?)m);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_get_media"),
     Description("Get full metadata for one media item by id, including sizes and source URLs.")]
    public static async Task<string> GetMedia(
        WordpressService svc,
        [Description("Media (attachment) id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/media/{id}", ct);
        if (node is JsonObject obj) obj.Remove("_links");
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_upload_media"),
     Description("Upload a file from the MCP server's filesystem into the media library. Requires write mode.")]
    public static async Task<string> UploadMedia(
        WordpressService svc,
        [Description("Absolute path to a file on the MCP server's filesystem to upload.")] string filePath,
        [Description("Optional title for the attachment.")] string? title = null,
        [Description("Optional alt text (images).")] string? altText = null,
        [Description("Optional caption.")] string? caption = null,
        [Description("Optional post id to attach the media to.")] int? postId = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_upload_media");

        if (!File.Exists(filePath))
            throw new McpException($"File not found: {filePath}");

        var fileName = Path.GetFileName(filePath);
        var uploaded = await svc.UploadMediaAsync(filePath, WpUtil.GuessContentType(filePath), fileName, ct);
        var id = uploaded?["id"]?.GetValue<int?>()
            ?? throw new McpException("Upload succeeded but WordPress did not return an attachment id.");

        var patch = new JsonObject();
        if (title is not null) patch["title"] = title;
        if (altText is not null) patch["alt_text"] = altText;
        if (caption is not null) patch["caption"] = caption;
        if (postId.HasValue) patch["post"] = postId.Value;
        if (patch.Count > 0)
        {
            uploaded = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/media/{id}", patch, ct);
        }

        return JsonSerializer.Serialize(new
        {
            id,
            fileName,
            mime_type = uploaded?["mime_type"]?.GetValue<string?>(),
            source_url = uploaded?["source_url"]?.GetValue<string?>(),
            link = uploaded?["link"]?.GetValue<string?>(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_update_media"),
     Description("Update media metadata (title, alt text, caption, description, attached post). Requires write mode.")]
    public static async Task<string> UpdateMedia(
        WordpressService svc,
        [Description("Media (attachment) id.")] int id,
        [Description("New title.")] string? title = null,
        [Description("New alt text.")] string? altText = null,
        [Description("New caption.")] string? caption = null,
        [Description("New description.")] string? description = null,
        [Description("Post id to attach to (0 to detach).")] int? postId = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_update_media");

        var body = new JsonObject();
        if (title is not null) body["title"] = title;
        if (altText is not null) body["alt_text"] = altText;
        if (caption is not null) body["caption"] = caption;
        if (description is not null) body["description"] = description;
        if (postId.HasValue) body["post"] = postId.Value;

        if (body.Count == 0)
            throw new McpException("wp_update_media: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/media/{id}", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_media"),
     Description("Permanently delete a media item (media does not support trash, so force is always applied). Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteMedia(
        WordpressService svc,
        [Description("Media (attachment) id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureDeleteAllowed("wp_delete_media");
        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/media/{id}?force=true", null, ct);
        return JsonSerializer.Serialize(new { deleted = true, id }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_download_media"),
     Description("Download a media item's file to the server's DownloadDirectory. Returns the local file path.")]
    public static async Task<string> DownloadMedia(
        WordpressService svc,
        [Description("Media (attachment) id.")] int id,
        [Description("Optional override file name (without directory). Defaults to the attachment's file name.")] string? fileName = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/media/{id}", ct);
        var sourceUrl = node?["source_url"]?.GetValue<string?>()
            ?? throw new McpException($"Media {id} has no source_url.");

        var (bytes, contentType, _) = await svc.DownloadBytesAsync(sourceUrl, ct);
        var name = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(new Uri(sourceUrl).LocalPath)
            : fileName!;
        var dir = svc.ResolveDownloadDirectory();
        var path = Path.Combine(dir, WpUtil.SanitizeFileName(name));
        await File.WriteAllBytesAsync(path, bytes, ct);
        return JsonSerializer.Serialize(new { id, path, bytes = bytes.Length, contentType }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_download_media_inline"),
     Description("Return a media item's bytes inline as base64. Falls back to writing to disk and returning the path if it would exceed Wordpress:MaxInlineBinaryBytes.")]
    public static async Task<string> DownloadMediaInline(
        WordpressService svc,
        [Description("Media (attachment) id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/media/{id}", ct);
        var sourceUrl = node?["source_url"]?.GetValue<string?>()
            ?? throw new McpException($"Media {id} has no source_url.");

        var (bytes, contentType, _) = await svc.DownloadBytesAsync(sourceUrl, ct);
        var name = WpUtil.SanitizeFileName(Path.GetFileName(new Uri(sourceUrl).LocalPath));
        if (bytes.Length > svc.Options.MaxInlineBinaryBytes)
        {
            var dir = svc.ResolveDownloadDirectory();
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, bytes, ct);
            return JsonSerializer.Serialize(new
            {
                id,
                inline = false,
                reason = "size_exceeds_MaxInlineBinaryBytes",
                bytes = bytes.Length,
                contentType,
                path,
            }, JsonOpts.Default);
        }
        return JsonSerializer.Serialize(new
        {
            id,
            inline = true,
            bytes = bytes.Length,
            contentType,
            fileName = name,
            base64 = Convert.ToBase64String(bytes),
        }, JsonOpts.Default);
    }
}
