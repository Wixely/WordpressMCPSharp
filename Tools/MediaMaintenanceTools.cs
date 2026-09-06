using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Media jobs that need the site's own filesystem: pulling an image straight from a URL, and
/// rebuilding thumbnails after a theme change alters the registered image sizes.
/// </summary>
[McpServerToolType]
public static class MediaMaintenanceTools
{
    [McpServerTool(Name = "wp_import_media_from_url"),
     Description("Download a file straight into the media library from a URL, without it passing through this server. The usual way to pull in client-supplied or stock imagery when building a site. Optionally attaches it to a post and sets it as that post's cover. Requires write mode.")]
    public static async Task<string> ImportMediaFromUrl(
        ManagementService management,
        [Description("Public URL of the image or file to import.")] string url,
        [Description("Optional title for the attachment.")] string? title = null,
        [Description("Optional alt text (recommended for images).")] string? altText = null,
        [Description("Optional caption.")] string? caption = null,
        [Description("Optional post id to attach the media to.")] int? postId = null,
        [Description("Also set the imported file as that post's featured image. Requires postId.")] bool setAsFeatured = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_import_media_from_url", ct);
        management.RequireFeature(target, f => f.EnableContent, "Content");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new McpException($"wp_import_media_from_url: '{url}' is not a valid http(s) URL.");
        }

        if (setAsFeatured && !postId.HasValue)
            throw new McpException("wp_import_media_from_url: setAsFeatured requires postId.");

        var args = new List<string> { "media", "import", url, "--porcelain" };
        if (!string.IsNullOrWhiteSpace(title)) args.Add($"--title={title}");
        if (!string.IsNullOrWhiteSpace(caption)) args.Add($"--caption={caption}");
        if (!string.IsNullOrWhiteSpace(altText)) args.Add($"--alt={altText}");
        if (postId.HasValue) args.Add($"--post_id={postId.Value}");
        if (setAsFeatured) args.Add("--featured_image");

        var result = await management.RunOrThrowAsync(target, args, "wp_import_media_from_url", ct, longRunning: true);

        // --porcelain makes the new attachment id the only output.
        var attachmentId = int.TryParse(result.Output.Trim(), out var parsedId) ? parsedId : (int?)null;
        string? sourceUrl = null;
        if (attachmentId.HasValue)
        {
            var lookup = await management.RunAsync(target, new[] { "post", "get", attachmentId.Value.ToString(), "--field=guid" }, ct);
            if (lookup.Success) sourceUrl = lookup.Output;
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            sourceUrl = url,
            attachmentId,
            fileUrl = sourceUrl,
            attachedToPost = postId,
            setAsFeatured,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_regenerate_thumbnails"),
     Description("Rebuild the resized versions of images in the media library. Run this after switching theme or changing image sizes, when existing images look cropped wrongly or fail to appear. Can take a long time on a large library. Requires write mode.")]
    public static async Task<string> RegenerateThumbnails(
        ManagementService management,
        [Description("Only regenerate this attachment id. Omit to process the whole library.")] int? attachmentId = null,
        [Description("Only create sizes that are missing, instead of rebuilding everything. Much faster.")] bool onlyMissing = true,
        [Description("Delete resized files belonging to sizes the theme no longer registers.")] bool deleteUnknownSizes = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_regenerate_thumbnails", ct);
        management.RequireFeature(target, f => f.EnableContent, "Content");

        var args = new List<string> { "media", "regenerate", "--yes" };
        if (attachmentId.HasValue) args.Add(attachmentId.Value.ToString());
        if (onlyMissing) args.Add("--only-missing");
        if (deleteUnknownSizes) args.Add("--delete-unknown-image-sizes");

        var result = await management.RunAsync(target, args, ct, longRunning: true);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            scope = attachmentId.HasValue ? $"attachment {attachmentId}" : "entire media library",
            onlyMissing,
            succeeded = result.Success,
            output = Tail(result.Output, 3000),
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    /// <summary>Regeneration prints a line per image; the summary at the end is the useful part.</summary>
    private static string Tail(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : "…(earlier output trimmed)\n" + trimmed[^max..];
    }
}
