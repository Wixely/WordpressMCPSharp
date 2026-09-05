using System.ComponentModel;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class SettingsTools
{
    [McpServerTool(Name = "wp_get_settings"),
     Description("Get site settings: title, tagline, URL, admin email, timezone, date/time formats, language, default category/post format, discussion defaults, page-on-front, and more.")]
    public static async Task<string> GetSettings(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_get_settings");
        svc.EnsureFeature(svc.Options.EnableSettings, "Settings");
        var node = await svc.GetJsonAsync("wp-json/wp/v2/settings", ct);
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_settings"),
     Description("Update site settings. Pass a JSON object of setting keys to values, e.g. `{\"title\":\"My Site\",\"blogname\":\"...\",\"timezone\":\"Europe/Dublin\"}`. Valid keys are the ones returned by wp_get_settings. Requires write mode.")]
    public static async Task<string> UpdateSettings(
        EndpointRegistry registry,
        [Description("JSON object of settings to change, e.g. `{\"title\":\"My Site\",\"description\":\"Just another site\"}`.")] string settingsJson,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_update_settings");
        svc.EnsureFeature(svc.Options.EnableSettings, "Settings");
        svc.EnsureWriteAllowed("wp_update_settings");

        var body = WpUtil.ParseJsonObject(settingsJson, nameof(settingsJson));
        if (body is JsonObject obj && obj.Count == 0)
            throw new McpException("wp_update_settings: settingsJson must contain at least one setting.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/settings", body, ct);
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }
}
