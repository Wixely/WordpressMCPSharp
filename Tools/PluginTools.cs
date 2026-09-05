using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class PluginTools
{
    [McpServerTool(Name = "wp_list_plugins"),
     Description("List installed plugins with their activation status and versions.")]
    public static async Task<string> ListPlugins(
        EndpointRegistry registry,
        [Description("Filter: active or inactive.")] string? status = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_plugins");
        svc.EnsureFeature(svc.Options.EnablePlugins, "Plugin");
        var url = "wp-json/wp/v2/plugins" + (string.IsNullOrWhiteSpace(status) ? string.Empty : $"?status={Uri.EscapeDataString(status)}");
        var node = await svc.GetJsonAsync(url, ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(p => p is JsonObject d ? new
        {
            plugin = d["plugin"]?.GetValue<string?>(),
            name = d["name"]?.GetValue<string?>(),
            status = d["status"]?.GetValue<string?>(),
            version = d["version"]?.GetValue<string?>(),
            author = d["author"]?.GetValue<string?>(),
            requires_wp = d["requires_wp"]?.GetValue<string?>(),
            requires_php = d["requires_php"]?.GetValue<string?>(),
        } : (object?)p);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_get_plugin"),
     Description("Get one installed plugin by its plugin file identifier, e.g. `akismet/akismet`.")]
    public static async Task<string> GetPlugin(
        EndpointRegistry registry,
        [Description("Plugin file identifier from wp_list_plugins, e.g. `akismet/akismet`.")] string plugin,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_get_plugin");
        svc.EnsureFeature(svc.Options.EnablePlugins, "Plugin");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/plugins/{ValidatePlugin(plugin)}", ct);
        if (node is JsonObject obj) obj.Remove("_links");
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_install_plugin"),
     Description("Install a plugin from the WordPress.org directory by slug (e.g. `classic-editor`). Optionally activate it. Requires Wordpress:ReadOnly=false and Wordpress:AllowPluginInstall=true.")]
    public static async Task<string> InstallPlugin(
        EndpointRegistry registry,
        [Description("WordPress.org plugin slug, e.g. `classic-editor`.")] string slug,
        [Description("If true, activate the plugin immediately after installing.")] bool activate = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_install_plugin");
        svc.EnsureFeature(svc.Options.EnablePlugins, "Plugin");
        svc.EnsurePluginInstallAllowed("wp_install_plugin");

        var body = new JsonObject { ["slug"] = slug };
        if (activate) body["status"] = "active";
        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/plugins", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_activate_plugin"),
     Description("Activate an installed plugin. Requires write mode.")]
    public static async Task<string> ActivatePlugin(
        EndpointRegistry registry,
        [Description("Plugin file identifier, e.g. `akismet/akismet`.")] string plugin,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
        => await SetPluginStatus(registry.RequireRest(site, "wp_activate_plugin"), plugin, "active", "wp_activate_plugin", ct);

    [McpServerTool(Name = "wp_deactivate_plugin"),
     Description("Deactivate an installed plugin. Requires write mode.")]
    public static async Task<string> DeactivatePlugin(
        EndpointRegistry registry,
        [Description("Plugin file identifier, e.g. `akismet/akismet`.")] string plugin,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
        => await SetPluginStatus(registry.RequireRest(site, "wp_deactivate_plugin"), plugin, "inactive", "wp_deactivate_plugin", ct);

    [McpServerTool(Name = "wp_delete_plugin"),
     Description("Delete an installed plugin's files (the plugin must be inactive first). Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeletePlugin(
        EndpointRegistry registry,
        [Description("Plugin file identifier, e.g. `akismet/akismet`.")] string plugin,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_delete_plugin");
        svc.EnsureFeature(svc.Options.EnablePlugins, "Plugin");
        svc.EnsureDeleteAllowed("wp_delete_plugin");
        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/plugins/{ValidatePlugin(plugin)}", null, ct);
        return JsonSerializer.Serialize(new { deleted = true, plugin }, JsonOpts.Default);
    }

    private static async Task<string> SetPluginStatus(WordpressRestClient svc, string plugin, string status, string operation, CancellationToken ct)
    {
        svc.EnsureFeature(svc.Options.EnablePlugins, "Plugin");
        svc.EnsureWriteAllowed(operation);
        var result = await svc.SendJsonAsync(HttpMethod.Put, $"wp-json/wp/v2/plugins/{ValidatePlugin(plugin)}", new { status }, ct);
        return JsonSerializer.Serialize(new
        {
            plugin = result?["plugin"]?.GetValue<string?>() ?? plugin,
            name = result?["name"]?.GetValue<string?>(),
            status = result?["status"]?.GetValue<string?>(),
        }, JsonOpts.Default);
    }

    private static string ValidatePlugin(string plugin)
    {
        var trimmed = plugin.Trim().Trim('/');
        if (trimmed.Length == 0 || trimmed.Contains("..", StringComparison.Ordinal))
            throw new McpException("plugin must be a plugin file identifier like 'akismet/akismet'.");
        return trimmed;
    }
}
