using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Multi-step content jobs that would otherwise take several round trips: setting a cover image,
/// duplicating a page for client review, and safe find/replace inside one item's body.
/// </summary>
[McpServerToolType]
public static class ContentWorkflowTools
{
    [McpServerTool(Name = "wp_set_featured_image"),
     Description("Set a post or page's featured image (the cover shown in listings and social previews) in one call. Either upload a local file or reuse an existing media id. Requires write mode.")]
    public static async Task<string> SetFeaturedImage(
        EndpointRegistry registry,
        [Description("Post or page id to set the cover on.")] int id,
        [Description("Absolute path to an image on this server to upload. Supply this or mediaId.")] string? filePath = null,
        [Description("Existing media (attachment) id to use. Supply this or filePath.")] int? mediaId = null,
        [Description("Content type of the id: post or page. Defaults to post.")] string type = "post",
        [Description("Alt text for the image (recommended for accessibility and SEO).")] string? altText = null,
        [Description("Optional caption.")] string? caption = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_set_featured_image");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_set_featured_image");

        if (filePath is null == !mediaId.HasValue)
            throw new McpException("wp_set_featured_image: supply exactly one of filePath or mediaId.");

        var collection = ResolveCollection(type);
        int attachmentId;
        string? sourceUrl = null;
        var uploaded = false;

        if (filePath is not null)
        {
            if (!File.Exists(filePath))
                throw new McpException($"File not found: {filePath}");

            var fileName = Path.GetFileName(filePath);
            var media = await svc.UploadMediaAsync(filePath, WpUtil.GuessContentType(filePath), fileName, ct);
            attachmentId = media?["id"]?.GetValue<int?>()
                ?? throw new McpException("Upload succeeded but WordPress did not return an attachment id.");
            sourceUrl = media?["source_url"]?.GetValue<string?>();
            uploaded = true;
        }
        else
        {
            attachmentId = mediaId!.Value;
            var media = await svc.GetJsonAsync($"wp-json/wp/v2/media/{attachmentId}", ct);
            sourceUrl = media?["source_url"]?.GetValue<string?>();
        }

        // Alt text and caption live on the attachment, not the post.
        var mediaPatch = new JsonObject();
        if (altText is not null) mediaPatch["alt_text"] = altText;
        if (caption is not null) mediaPatch["caption"] = caption;
        if (mediaPatch.Count > 0)
        {
            await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/media/{attachmentId}", mediaPatch, ct);
        }

        var result = await svc.SendJsonAsync(
            HttpMethod.Post,
            $"wp-json/wp/v2/{collection}/{id}",
            new JsonObject { ["featured_media"] = attachmentId },
            ct);

        return JsonSerializer.Serialize(new
        {
            id,
            type,
            featuredMediaId = attachmentId,
            uploaded,
            sourceUrl,
            altText,
            title = WpUtil.Rendered(result?["title"]),
            link = result?["link"]?.GetValue<string?>(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_clone_post"),
     Description("Duplicate a post or page as a new draft, copying its content, excerpt, taxonomy terms, featured image and template. Useful for building a client revision without touching the live version. Requires write mode.")]
    public static async Task<string> ClonePost(
        EndpointRegistry registry,
        [Description("Id of the post or page to duplicate.")] int id,
        [Description("Content type of the id: post or page. Defaults to post.")] string type = "post",
        [Description("Title for the copy. Defaults to the original title with ' (copy)' appended.")] string? newTitle = null,
        [Description("Status for the copy: draft (default), pending, private, publish.")] string status = "draft",
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_clone_post");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        svc.EnsureWriteAllowed("wp_clone_post");

        var collection = ResolveCollection(type);
        var source = await svc.GetJsonAsync($"wp-json/wp/v2/{collection}/{id}?context=edit", ct) as JsonObject
            ?? throw new McpException($"{type} {id} was not found.");

        var body = new JsonObject
        {
            ["title"] = newTitle ?? (WpUtil.Rendered(source["title"]) ?? "Untitled") + " (copy)",
            ["content"] = (source["content"] as JsonObject)?["raw"]?.GetValue<string?>() ?? string.Empty,
            ["excerpt"] = (source["excerpt"] as JsonObject)?["raw"]?.GetValue<string?>() ?? string.Empty,
            ["status"] = status,
        };

        var featured = source["featured_media"]?.GetValue<int?>();
        if (featured is > 0) body["featured_media"] = featured.Value;

        var commentStatus = source["comment_status"]?.GetValue<string?>();
        if (commentStatus is not null) body["comment_status"] = commentStatus;

        if (collection == "posts")
        {
            if (source["categories"] is JsonArray categories) body["categories"] = categories.DeepClone();
            if (source["tags"] is JsonArray tags) body["tags"] = tags.DeepClone();
            var format = source["format"]?.GetValue<string?>();
            if (format is not null) body["format"] = format;
        }
        else
        {
            var parent = source["parent"]?.GetValue<int?>();
            if (parent is > 0) body["parent"] = parent.Value;
            var template = source["template"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(template)) body["template"] = template;
            var menuOrder = source["menu_order"]?.GetValue<int?>();
            if (menuOrder.HasValue) body["menu_order"] = menuOrder.Value;
        }

        var created = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/{collection}", body, ct);
        return JsonSerializer.Serialize(new
        {
            sourceId = id,
            newId = created?["id"]?.GetValue<int?>(),
            type,
            title = WpUtil.Rendered(created?["title"]),
            status = created?["status"]?.GetValue<string?>(),
            link = created?["link"]?.GetValue<string?>(),
            copiedFeaturedImage = featured is > 0,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_replace_in_post"),
     Description("Find and replace text inside one post or page's body. Reports how many occurrences matched and, unless applied, changes nothing — run it as a preview first. Requires write mode when apply=true.")]
    public static async Task<string> ReplaceInPost(
        EndpointRegistry registry,
        [Description("Post or page id.")] int id,
        [Description("Text to find (plain text, matched literally).")] string find,
        [Description("Replacement text.")] string replace,
        [Description("Content type of the id: post or page. Defaults to post.")] string type = "post",
        [Description("Set true to write the change. When false (default) the tool only reports what would change.")] bool apply = false,
        [Description("Match case-sensitively. Defaults to true.")] bool caseSensitive = true,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_replace_in_post");
        svc.EnsureFeature(svc.Options.EnableContent, "Content");
        if (apply) svc.EnsureWriteAllowed("wp_replace_in_post");

        if (string.IsNullOrEmpty(find))
            throw new McpException("wp_replace_in_post: 'find' must not be empty.");

        var collection = ResolveCollection(type);
        var source = await svc.GetJsonAsync($"wp-json/wp/v2/{collection}/{id}?context=edit", ct) as JsonObject
            ?? throw new McpException($"{type} {id} was not found.");

        var content = (source["content"] as JsonObject)?["raw"]?.GetValue<string?>() ?? string.Empty;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var occurrences = CountOccurrences(content, find, comparison);
        if (occurrences == 0)
        {
            return JsonSerializer.Serialize(new
            {
                id,
                type,
                occurrences = 0,
                applied = false,
                message = $"'{find}' was not found in the {type} body; nothing to change.",
            }, JsonOpts.Default);
        }

        var updated = content.Replace(find, replace, comparison);

        if (!apply)
        {
            return JsonSerializer.Serialize(new
            {
                id,
                type,
                occurrences,
                applied = false,
                preview = Excerpt(updated, replace, comparison),
                message = $"{occurrences} occurrence(s) would be replaced. Re-run with apply=true to write the change.",
            }, JsonOpts.Default);
        }

        var result = await svc.SendJsonAsync(
            HttpMethod.Post,
            $"wp-json/wp/v2/{collection}/{id}",
            new JsonObject { ["content"] = updated },
            ct);

        return JsonSerializer.Serialize(new
        {
            id,
            type,
            occurrences,
            applied = true,
            title = WpUtil.Rendered(result?["title"]),
            modified = result?["modified"]?.GetValue<string?>(),
        }, JsonOpts.Default);
    }

    private static int CountOccurrences(string haystack, string needle, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, comparison)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>Show the first replacement in context so a preview is meaningful.</summary>
    private static string? Excerpt(string updated, string replacement, StringComparison comparison)
    {
        if (replacement.Length == 0) return null;
        var index = updated.IndexOf(replacement, comparison);
        if (index < 0) return null;
        var start = Math.Max(0, index - 60);
        var end = Math.Min(updated.Length, index + replacement.Length + 60);
        return (start > 0 ? "…" : string.Empty) + updated[start..end] + (end < updated.Length ? "…" : string.Empty);
    }

    private static string ResolveCollection(string type) => type.ToLowerInvariant() switch
    {
        "post" or "posts" => "posts",
        "page" or "pages" => "pages",
        _ => throw new McpException("type must be 'post' or 'page'."),
    };
}
