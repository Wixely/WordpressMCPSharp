using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Roles, capabilities and password resets — the parts of user administration the REST API does not
/// expose. The REST user tools cover creating and editing accounts; these cover what those accounts
/// are allowed to do.
/// </summary>
[McpServerToolType]
public static class UserRoleTools
{
    [McpServerTool(Name = "wp_list_roles"),
     Description("List the roles registered on the site with their user counts — including custom roles added by plugins such as WooCommerce (customer, shop_manager) or membership plugins.")]
    public static async Task<string> ListRoles(
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_list_roles");
        management.RequireFeature(target, f => f.EnableUsers, "User");

        var result = await management.RunOrThrowAsync(target, new[] { "role", "list", "--format=json" }, "wp_list_roles", ct);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            roles = MaintenanceTools.TryParseJson(result.Output),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_role_capabilities"),
     Description("List the capabilities granted to a role — what a user with that role can actually do. Use it to work out why an account cannot perform an action.")]
    public static async Task<string> ListRoleCapabilities(
        ManagementService management,
        [Description("Role slug, e.g. `editor`, `shop_manager`.")] string role,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_list_role_capabilities");
        management.RequireFeature(target, f => f.EnableUsers, "User");

        var result = await management.RunAsync(target, new[] { "cap", "list", role, "--format=json" }, ct);
        if (!result.Success)
        {
            throw new McpException(
                $"MCP tool 'wp_list_role_capabilities' could not read role '{role}' on endpoint '{target.Name}'. " +
                $"{ManagementService.Describe(result)} Use wp_list_roles to see the valid role slugs.");
        }

        var caps = MaintenanceTools.TryParseJson(result.Output);
        return JsonSerializer.Serialize(new { endpoint = target.Name, role, capabilities = caps }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_add_user_role"),
     Description("Give a user an additional role, without replacing the roles they already have (wp_update_user replaces them). Requires write mode.")]
    public static async Task<string> AddUserRole(
        ManagementService management,
        [Description("User id, login or email.")] string user,
        [Description("Role slug to add, e.g. `shop_manager`.")] string role,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_add_user_role", ct);
        management.RequireFeature(target, f => f.EnableUsers, "User");

        await management.RunOrThrowAsync(target, new[] { "user", "add-role", user, role }, "wp_add_user_role", ct);
        var after = await management.RunAsync(target, new[] { "user", "get", user, "--field=roles" }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            user,
            roleAdded = role,
            currentRoles = after.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_remove_user_role"),
     Description("Remove one role from a user, leaving their other roles intact. Requires write mode.")]
    public static async Task<string> RemoveUserRole(
        ManagementService management,
        [Description("User id, login or email.")] string user,
        [Description("Role slug to remove.")] string role,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_remove_user_role", ct);
        management.RequireFeature(target, f => f.EnableUsers, "User");

        await management.RunOrThrowAsync(target, new[] { "user", "remove-role", user, role }, "wp_remove_user_role", ct);
        var after = await management.RunAsync(target, new[] { "user", "get", user, "--field=roles" }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            user,
            roleRemoved = role,
            currentRoles = after.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_reset_user_password"),
     Description("Set a new password for a user, optionally emailing them about the change. The new password is returned once — pass it on securely and do not store it. Requires write mode.")]
    public static async Task<string> ResetUserPassword(
        ManagementService management,
        [Description("User id, login or email.")] string user,
        [Description("New password. Omit to have a strong one generated.")] string? newPassword = null,
        [Description("Send WordPress's password-change notification to the user. Defaults to false.")] bool notifyUser = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_reset_user_password", ct);
        management.RequireFeature(target, f => f.EnableUsers, "User");

        var generated = string.IsNullOrWhiteSpace(newPassword);
        var password = generated ? GeneratePassword() : newPassword!;

        var args = new List<string> { "user", "update", user, $"--user_pass={password}" };
        if (!notifyUser) args.Add("--skip-email");

        await management.RunOrThrowAsync(target, args, "wp_reset_user_password", ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            user,
            passwordGenerated = generated,
            newPassword = password,
            userNotified = notifyUser,
            note = "This password is shown once. Deliver it through a secure channel; it is not stored by this server.",
        }, JsonOpts.Default);
    }

    /// <summary>Cryptographically random password from an unambiguous alphabet.</summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*-_";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
