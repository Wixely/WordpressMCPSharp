using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Post meta (custom fields) through WP-CLI. The REST API only exposes meta a plugin has explicitly
/// registered with `show_in_rest`, which most do not — so ACF fields, SEO titles, page-builder data
/// and similar are invisible over REST but fully readable here.
/// </summary>
[McpServerToolType]
public static class PostMetaTools
{
    [McpServerTool(Name = "wp_list_post_meta"),
     Description("List the custom fields (post meta) on a post, page or custom post type item — including fields that plugins such as ACF, Yoast and page builders keep hidden from the REST API. Underscore-prefixed keys are WordPress's internal/protected fields.")]
    public static async Task<string> ListPostMeta(
        ManagementService management,
        [Description("Post, page or custom post type item id.")] int id,
        [Description("Only keys containing this text, e.g. `_yoast` or `acf`.")] string? search = null,
        [Description("Include protected fields whose keys start with an underscore. Defaults to true.")] bool includeProtected = true,
        [Description("Maximum characters of each value returned inline. Defaults to 500.")] int maxValueLength = 500,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_list_post_meta");
        management.RequireFeature(target, f => f.EnableContent, "Content");

        var result = await management.RunAsync(target, new[] { "post", "meta", "list", id.ToString(), "--format=json" }, ct);
        if (!result.Success)
        {
            throw new McpException(
                $"MCP tool 'wp_list_post_meta' could not read meta for post {id} on endpoint '{target.Name}'. {ManagementService.Describe(result)}");
        }

        var parsed = MaintenanceTools.TryParseJson(result.Output);
        var items = new List<object>();
        var hidden = 0;

        if (parsed is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                var key = entry.TryGetProperty("meta_key", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                if (!includeProtected && key.StartsWith('_')) { hidden++; continue; }
                if (!string.IsNullOrWhiteSpace(search) && !key.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;

                string? value = null;
                if (entry.TryGetProperty("meta_value", out var v))
                {
                    value = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                }

                var cap = Math.Clamp(maxValueLength, 20, 20_000);
                var truncated = value is not null && value.Length > cap;

                items.Add(new
                {
                    key,
                    isProtected = key.StartsWith('_'),
                    truncated,
                    value = truncated ? value![..cap] + "…" : value,
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            postId = id,
            count = items.Count,
            protectedFieldsHidden = hidden,
            meta = items,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_get_post_meta"),
     Description("Read one custom field from a post by key. Values stored as PHP-serialised arrays are returned as JSON.")]
    public static async Task<string> GetPostMeta(
        ManagementService management,
        [Description("Post, page or custom post type item id.")] int id,
        [Description("Meta key, e.g. `_yoast_wpseo_title`.")] string key,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_get_post_meta");
        management.RequireFeature(target, f => f.EnableContent, "Content");

        var result = await management.RunAsync(target, new[] { "post", "meta", "get", id.ToString(), key, "--format=json" }, ct);
        if (!result.Success)
        {
            return JsonSerializer.Serialize(new { endpoint = target.Name, postId = id, key, exists = false }, JsonOpts.Default);
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            postId = id,
            key,
            exists = true,
            value = MaintenanceTools.TryParseJson(result.Output) ?? (object)result.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_update_post_meta"),
     Description("Set a custom field on a post — how to write ACF fields, SEO metadata and other plugin data that is not exposed over REST. Pass JSON with isJson=true for array or object values. Requires write mode.")]
    public static async Task<string> UpdatePostMeta(
        ManagementService management,
        [Description("Post, page or custom post type item id.")] int id,
        [Description("Meta key.")] string key,
        [Description("Value to set.")] string value,
        [Description("Treat `value` as JSON (for array/object meta).")] bool isJson = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_update_post_meta", ct);
        management.RequireFeature(target, f => f.EnableContent, "Content");

        var before = await management.RunAsync(target, new[] { "post", "meta", "get", id.ToString(), key, "--format=json" }, ct);

        var args = new List<string> { "post", "meta", "update", id.ToString(), key, value };
        if (isJson) args.Add("--format=json");
        await management.RunOrThrowAsync(target, args, "wp_update_post_meta", ct);

        var after = await management.RunAsync(target, new[] { "post", "meta", "get", id.ToString(), key, "--format=json" }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            postId = id,
            key,
            existedBefore = before.Success,
            previousValue = before.Success ? before.Output : null,
            newValue = after.Success ? after.Output : value,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_delete_post_meta"),
     Description("Delete a custom field from a post. Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeletePostMeta(
        ManagementService management,
        [Description("Post, page or custom post type item id.")] int id,
        [Description("Meta key to remove.")] string key,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_delete_post_meta", ct);
        management.RequireFeature(target, f => f.EnableContent, "Content");
        management.EnsureDeleteAllowed(target, "wp_delete_post_meta");

        var before = await management.RunAsync(target, new[] { "post", "meta", "get", id.ToString(), key, "--format=json" }, ct);
        if (!before.Success)
        {
            return JsonSerializer.Serialize(new { endpoint = target.Name, postId = id, key, existed = false, deleted = false }, JsonOpts.Default);
        }

        await management.RunOrThrowAsync(target, new[] { "post", "meta", "delete", id.ToString(), key }, "wp_delete_post_meta", ct);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            postId = id,
            key,
            existed = true,
            deleted = true,
            deletedValue = before.Output,
        }, JsonOpts.Default);
    }
}
