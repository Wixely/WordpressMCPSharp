using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class MenuTools
{
    [McpServerTool(Name = "wp_list_menus"),
     Description("List classic navigation menus and their assigned theme locations.")]
    public static async Task<string> ListMenus(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_menus");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        var node = await svc.ListAllAsync("wp-json/wp/v2/menus?context=edit", ct);
        var items = node.Select(m => m is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            name = d["name"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            locations = (d["locations"] as JsonArray)?.Select(l => l?.GetValue<string?>()),
        } : (object?)m);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_create_menu"),
     Description("Create a classic navigation menu, optionally assigning theme locations. Requires write mode.")]
    public static async Task<string> CreateMenu(
        EndpointRegistry registry,
        [Description("Menu name.")] string name,
        [Description("Optional theme location slugs as JSON array, e.g. `[\"primary\"]` (see wp_list_menu_locations).")] string? locationsJson = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_create_menu");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        svc.EnsureWriteAllowed("wp_create_menu");

        var body = new JsonObject { ["name"] = name };
        if (locationsJson is not null)
        {
            var parsed = JsonNode.Parse(locationsJson) as JsonArray
                ?? throw new McpException("locationsJson must be a JSON array of location slugs.");
            body["locations"] = parsed;
        }

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/menus", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_menu"),
     Description("Permanently delete a navigation menu and its items. Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteMenu(
        EndpointRegistry registry,
        [Description("Menu id.")] int id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_delete_menu");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        svc.EnsureDeleteAllowed("wp_delete_menu");
        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/menus/{id}?force=true", null, ct);
        return JsonSerializer.Serialize(new { deleted = true, id }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_menu_items"),
     Description("List the items of a navigation menu in order.")]
    public static async Task<string> ListMenuItems(
        EndpointRegistry registry,
        [Description("Menu id.")] int menuId,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_menu_items");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        var node = await svc.ListAllAsync($"wp-json/wp/v2/menu-items?menus={menuId}&context=edit&orderby=menu_order&order=asc", ct);
        var items = node.Select(m => m is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            title = WpUtil.Rendered(d["title"]),
            url = d["url"]?.GetValue<string?>(),
            type = d["type"]?.GetValue<string?>(),
            @object = d["object"]?.GetValue<string?>(),
            object_id = d["object_id"]?.GetValue<int?>(),
            parent = d["parent"]?.GetValue<int?>(),
            menu_order = d["menu_order"]?.GetValue<int?>(),
            target = d["target"]?.GetValue<string?>(),
        } : (object?)m);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_create_menu_item"),
     Description("Add an item to a navigation menu — a custom link, or a link to a post/page/category/tag. Requires write mode.")]
    public static async Task<string> CreateMenuItem(
        EndpointRegistry registry,
        [Description("Menu id to add the item to.")] int menuId,
        [Description("Item label.")] string title,
        [Description("Custom link URL (for type=custom). Omit when linking site content.")] string? url = null,
        [Description("Linked object kind for site content: post, page, category, post_tag. Omit for a custom link.")] string? objectType = null,
        [Description("Id of the linked post/page/term (required with objectType).")] int? objectId = null,
        [Description("Optional parent menu item id for a submenu.")] int? parentItemId = null,
        [Description("Optional position within the menu (1-based).")] int? menuOrder = null,
        [Description("Optional link target, e.g. `_blank`.")] string? target = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_create_menu_item");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        svc.EnsureWriteAllowed("wp_create_menu_item");

        var body = new JsonObject
        {
            ["menus"] = menuId,
            ["title"] = title,
            ["status"] = "publish",
        };
        if (objectType is not null)
        {
            if (!objectId.HasValue)
                throw new McpException("objectId is required when objectType is supplied.");
            body["type"] = objectType is "category" or "post_tag" ? "taxonomy" : "post_type";
            body["object"] = objectType;
            body["object_id"] = objectId.Value;
        }
        else
        {
            body["type"] = "custom";
            body["url"] = url ?? throw new McpException("url is required for a custom link menu item.");
        }
        if (parentItemId.HasValue) body["parent"] = parentItemId.Value;
        if (menuOrder.HasValue) body["menu_order"] = menuOrder.Value;
        if (target is not null) body["target"] = target;

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/menu-items", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_menu_item"),
     Description("Update a menu item's label, URL, order, or parent. Requires write mode.")]
    public static async Task<string> UpdateMenuItem(
        EndpointRegistry registry,
        [Description("Menu item id.")] int id,
        [Description("New label.")] string? title = null,
        [Description("New URL (custom links only).")] string? url = null,
        [Description("New parent menu item id (0 for top level).")] int? parentItemId = null,
        [Description("New position within the menu (1-based).")] int? menuOrder = null,
        [Description("New link target.")] string? target = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_update_menu_item");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        svc.EnsureWriteAllowed("wp_update_menu_item");

        var body = new JsonObject();
        if (title is not null) body["title"] = title;
        if (url is not null) body["url"] = url;
        if (parentItemId.HasValue) body["parent"] = parentItemId.Value;
        if (menuOrder.HasValue) body["menu_order"] = menuOrder.Value;
        if (target is not null) body["target"] = target;

        if (body.Count == 0)
            throw new McpException("wp_update_menu_item: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/menu-items/{id}", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_menu_item"),
     Description("Permanently delete a menu item. Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteMenuItem(
        EndpointRegistry registry,
        [Description("Menu item id.")] int id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_delete_menu_item");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        svc.EnsureDeleteAllowed("wp_delete_menu_item");
        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/menu-items/{id}?force=true", null, ct);
        return JsonSerializer.Serialize(new { deleted = true, id }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_menu_locations"),
     Description("List the theme's registered menu locations and which menu is assigned to each.")]
    public static async Task<string> ListMenuLocations(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_list_menu_locations");
        svc.EnsureFeature(svc.Options.EnableMenus, "Menu");
        var node = await svc.GetJsonAsync("wp-json/wp/v2/menu-locations", ct);
        var items = (node as JsonObject ?? new JsonObject()).Select(kv => kv.Value is JsonObject d ? new
        {
            location = kv.Key,
            name = d["name"]?.GetValue<string?>(),
            description = d["description"]?.GetValue<string?>(),
            menu = d["menu"]?.GetValue<int?>(),
        } : (object?)kv.Value);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }
}
