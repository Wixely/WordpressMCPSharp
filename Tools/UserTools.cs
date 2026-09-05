using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class UserTools
{
    [McpServerTool(Name = "wp_list_users"),
     Description("List users with roles and emails. Returns slim metadata plus pagination totals.")]
    public static async Task<string> ListUsers(
        WordpressService svc,
        [Description("Free-text search (name, email, login).")] string? search = null,
        [Description("Filter: role slug, e.g. administrator, editor, author, contributor, subscriber.")] string? role = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}", "context=edit" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(role)) qs.Add($"roles={Uri.EscapeDataString(role)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wp/v2/users?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(u => u is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            username = d["username"]?.GetValue<string?>(),
            name = d["name"]?.GetValue<string?>(),
            email = d["email"]?.GetValue<string?>(),
            registered_date = d["registered_date"]?.GetValue<string?>(),
            roles = (d["roles"] as JsonArray)?.Select(r => r?.GetValue<string?>()),
            link = d["link"]?.GetValue<string?>(),
        } : (object?)u);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_get_user"),
     Description("Get one user by id, including roles and capabilities.")]
    public static async Task<string> GetUser(
        WordpressService svc,
        [Description("User id.")] int id,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/users/{id}?context=edit", ct);
        if (node is JsonObject obj) obj.Remove("_links");
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_get_me"),
     Description("Get the currently authenticated user (the account this MCP server operates as).")]
    public static async Task<string> GetMe(
        WordpressService svc,
        CancellationToken ct = default)
    {
        var node = await svc.GetJsonAsync("wp-json/wp/v2/users/me?context=edit", ct);
        if (node is JsonObject obj) obj.Remove("_links");
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_create_user"),
     Description("Create a user account. Requires write mode.")]
    public static async Task<string> CreateUser(
        WordpressService svc,
        [Description("Login name.")] string username,
        [Description("Email address.")] string email,
        [Description("Initial password.")] string password,
        [Description("Role slug: administrator, editor, author, contributor, subscriber. Defaults to subscriber.")] string role = "subscriber",
        [Description("Optional display name.")] string? displayName = null,
        [Description("Optional first name.")] string? firstName = null,
        [Description("Optional last name.")] string? lastName = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        svc.EnsureWriteAllowed("wp_create_user");

        var body = new JsonObject
        {
            ["username"] = username,
            ["email"] = email,
            ["password"] = password,
            ["roles"] = new JsonArray(role),
        };
        if (displayName is not null) body["name"] = displayName;
        if (firstName is not null) body["first_name"] = firstName;
        if (lastName is not null) body["last_name"] = lastName;

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wp/v2/users", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_update_user"),
     Description("Update a user's fields (email, role, names, password). Requires write mode.")]
    public static async Task<string> UpdateUser(
        WordpressService svc,
        [Description("User id.")] int id,
        [Description("New email address.")] string? email = null,
        [Description("New role slug (replaces existing roles).")] string? role = null,
        [Description("New display name.")] string? displayName = null,
        [Description("New first name.")] string? firstName = null,
        [Description("New last name.")] string? lastName = null,
        [Description("New password.")] string? password = null,
        [Description("New description/bio.")] string? description = null,
        [Description("New website URL.")] string? url = null,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        svc.EnsureWriteAllowed("wp_update_user");

        var body = new JsonObject();
        if (email is not null) body["email"] = email;
        if (role is not null) body["roles"] = new JsonArray(role);
        if (displayName is not null) body["name"] = displayName;
        if (firstName is not null) body["first_name"] = firstName;
        if (lastName is not null) body["last_name"] = lastName;
        if (password is not null) body["password"] = password;
        if (description is not null) body["description"] = description;
        if (url is not null) body["url"] = url;

        if (body.Count == 0)
            throw new McpException("wp_update_user: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/users/{id}", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_user"),
     Description("Permanently delete a user, reassigning their content to another user. Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteUser(
        WordpressService svc,
        [Description("User id to delete.")] int id,
        [Description("User id that inherits the deleted user's posts.")] int reassignToUserId,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        svc.EnsureDeleteAllowed("wp_delete_user");
        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/users/{id}?force=true&reassign={reassignToUserId}", null, ct);
        return JsonSerializer.Serialize(new { deleted = true, id, reassignedTo = reassignToUserId }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_application_passwords"),
     Description("List application passwords registered for a user (names and last-used metadata only, never secrets).")]
    public static async Task<string> ListApplicationPasswords(
        WordpressService svc,
        [Description("User id, or 'me' via wp_get_me first.")] int userId,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        var node = await svc.GetJsonAsync($"wp-json/wp/v2/users/{userId}/application-passwords", ct);
        return node?.ToJsonString(JsonOpts.Default) ?? "[]";
    }

    [McpServerTool(Name = "wp_create_application_password"),
     Description("Create a new application password for a user. The plaintext password is returned ONCE by WordPress — store it securely. Requires write mode.")]
    public static async Task<string> CreateApplicationPassword(
        WordpressService svc,
        [Description("User id.")] int userId,
        [Description("Name identifying what the password is for.")] string name,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        svc.EnsureWriteAllowed("wp_create_application_password");
        var result = await svc.SendJsonAsync(HttpMethod.Post, $"wp-json/wp/v2/users/{userId}/application-passwords", new { name }, ct);
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_delete_application_password"),
     Description("Revoke one of a user's application passwords by UUID. Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteApplicationPassword(
        WordpressService svc,
        [Description("User id.")] int userId,
        [Description("Application password UUID (from wp_list_application_passwords).")] string uuid,
        CancellationToken ct = default)
    {
        svc.EnsureFeature(svc.Options.EnableUsers, "User");
        svc.EnsureDeleteAllowed("wp_delete_application_password");
        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wp/v2/users/{userId}/application-passwords/{Uri.EscapeDataString(uuid)}", null, ct);
        return JsonSerializer.Serialize(new { deleted = true, userId, uuid }, JsonOpts.Default);
    }
}
