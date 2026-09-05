using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace WordpressMCPSharp.Tools;

/// <summary>Shared helpers for shaping WordPress REST responses and validating tool input.</summary>
internal static class WpUtil
{
    /// <summary>Unwrap a WordPress rendered field ({"rendered":"...","raw":"..."} or plain string).</summary>
    public static string? Rendered(JsonNode? node) => node switch
    {
        JsonObject obj => obj["raw"]?.GetValue<string?>() ?? obj["rendered"]?.GetValue<string?>(),
        JsonValue value => value.GetValue<string?>(),
        _ => null,
    };

    public static JsonArray ParseIntArray(string json, string paramName)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new McpException($"{paramName} is not valid JSON: {ex.Message}"); }
        if (node is not JsonArray arr)
            throw new McpException($"{paramName} must be a JSON array of integers.");
        var result = new JsonArray();
        foreach (var item in arr)
        {
            if (item is null || item is not JsonValue v || !v.TryGetValue<int>(out var i))
                throw new McpException($"{paramName} must contain only integers.");
            result.Add(i);
        }
        return result;
    }

    public static JsonNode ParseJsonObject(string json, string paramName)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new McpException($"{paramName} is not valid JSON: {ex.Message}"); }
        return node is JsonObject obj ? obj : throw new McpException($"{paramName} must be a JSON object.");
    }

    /// <summary>Slim a post/page object for list output.</summary>
    public static object SummarizeContentItem(JsonNode? item)
    {
        if (item is not JsonObject d) return item!;
        return new
        {
            id = d["id"]?.GetValue<int?>(),
            date = d["date"]?.GetValue<string?>(),
            modified = d["modified"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            status = d["status"]?.GetValue<string?>(),
            type = d["type"]?.GetValue<string?>(),
            link = d["link"]?.GetValue<string?>(),
            title = Rendered(d["title"]),
            author = d["author"]?.GetValue<int?>(),
            parent = d["parent"]?.GetValue<int?>(),
            featured_media = d["featured_media"]?.GetValue<int?>(),
            comment_status = d["comment_status"]?.GetValue<string?>(),
            sticky = d["sticky"]?.GetValue<bool?>(),
            categories = (d["categories"] as JsonArray)?.Select(t => t?.GetValue<int?>()),
            tags = (d["tags"] as JsonArray)?.Select(t => t?.GetValue<int?>()),
        };
    }

    /// <summary>Full detail for a post/page with content stripped (use the content tool for the body).</summary>
    public static JsonObject StripContent(JsonObject item)
    {
        var clone = (JsonObject)item.DeepClone();
        if (clone["content"] is not null)
        {
            clone["content"] = $"(omitted — use wp_get_post_content; {Rendered(item["content"])?.Length ?? 0} chars rendered)";
        }
        clone.Remove("_links");
        return clone;
    }

    public static string PagedResult(int? total, int? totalPages, int page, IEnumerable<object> items) =>
        JsonSerializer.Serialize(new { total, totalPages, page, items }, JsonOpts.Default);

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    public static string GuessContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".ico" => "image/x-icon",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".zip" => "application/zip",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };
}
