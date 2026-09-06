using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// The wp_options table — where almost every plugin keeps its configuration. The REST settings
/// endpoint only exposes a couple of dozen whitelisted core settings, so this is the only way to
/// read or change plugin configuration, and the place the classic autoloaded-options performance
/// problem shows up.
/// </summary>
[McpServerToolType]
public static class OptionTools
{
    /// <summary>Option names whose values are credentials often enough to be worth hiding by default.</summary>
    private static readonly string[] SecretHints =
    {
        "key", "secret", "token", "password", "passwd", "pwd", "salt", "nonce",
        "auth", "credential", "private", "license", "api",
    };

    [McpServerTool(Name = "wp_get_option"),
     Description("Read one option from the wp_options table by name — this is where plugin configuration lives (for example `woocommerce_currency`, `blogname`, a plugin's settings array). Values stored as PHP-serialised arrays are returned as JSON.")]
    public static async Task<string> GetOption(
        ManagementService management,
        [Description("Option name, e.g. `blogname` or `woocommerce_store_address`.")] string name,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_get_option");
        var result = await management.RunAsync(target, new[] { "option", "get", name, "--format=json" }, ct);

        if (!result.Success)
        {
            // "Could not get 'x' option. Does it exist?" is WP-CLI's way of saying the option is absent —
            // a normal answer for a caller checking whether a plugin has been configured yet.
            var stderr = result.StdErr;
            var missing = stderr.Contains("Does it exist", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("could not be found", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("not found", StringComparison.OrdinalIgnoreCase);

            if (missing)
            {
                return JsonSerializer.Serialize(new { endpoint = target.Name, name, exists = false }, JsonOpts.Default);
            }
            throw new McpException($"MCP tool 'wp_get_option' failed on endpoint '{target.Name}'. {ManagementService.Describe(result)}");
        }

        var autoload = await management.RunAsync(target, new[] { "option", "list", "--search=" + name, "--fields=autoload", "--format=csv" }, ct);
        var autoloadValue = autoload.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()?.Trim();

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            name,
            exists = true,
            autoload = autoloadValue,
            // Flagged rather than hidden: reading one named option is a deliberate act, but the caller
            // should know when the value it just received is likely to be a credential.
            looksSensitive = LooksSensitive(name),
            value = MaintenanceTools.TryParseJson(result.Output) ?? (object)result.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_options"),
     Description("List or search options in wp_options with their sizes and autoload flag. Values are hidden by default because options commonly hold plugin API keys — set includeValues=true to see them. Use this to find the option a plugin stores its settings under.")]
    public static async Task<string> ListOptions(
        ManagementService management,
        [Description("Filter option names by this substring (wildcards allowed, e.g. `woocommerce_*`). Omit to list all.")] string? search = null,
        [Description("Only options that are autoloaded on every page request.")] bool autoloadedOnly = false,
        [Description("Include the option values. Off by default — options often contain plugin credentials.")] bool includeValues = false,
        [Description("Maximum options to return, largest first. Defaults to 50.")] int limit = 50,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_list_options");

        var args = new List<string> { "option", "list", "--fields=option_name,size_bytes,autoload", "--format=json" };
        if (!string.IsNullOrWhiteSpace(search))
        {
            // WP-CLI matches the pattern literally, so a bare term finds nothing. Treat a term without
            // wildcards as a substring search, which is what asking for "woocommerce" clearly means.
            var pattern = search.Contains('*') || search.Contains('?') ? search : $"*{search}*";
            args.Add($"--search={pattern}");
        }
        if (autoloadedOnly) args.Add("--autoload=on");

        var result = await management.RunOrThrowAsync(target, args, "wp_list_options", ct);
        var parsed = MaintenanceTools.TryParseJson(result.Output);

        var rows = new List<(string Name, long Size, string? Autoload)>();
        if (parsed is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                var name = entry.TryGetProperty("option_name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                long size = 0;
                if (entry.TryGetProperty("size_bytes", out var s))
                {
                    size = s.ValueKind == JsonValueKind.Number ? s.GetInt64() : long.TryParse(s.GetString(), out var parsedSize) ? parsedSize : 0;
                }
                var autoload = entry.TryGetProperty("autoload", out var a)
                    ? (a.ValueKind == JsonValueKind.String ? a.GetString() : a.ToString())
                    : null;
                rows.Add((name, size, autoload));
            }
        }

        var top = rows.OrderByDescending(r => r.Size).Take(Math.Clamp(limit, 1, 500)).ToList();

        var items = new List<object>();
        foreach (var row in top)
        {
            string? value = null;
            if (includeValues)
            {
                var read = await management.RunAsync(target, new[] { "option", "get", row.Name, "--format=json" }, ct);
                value = read.Success ? Truncate(read.Output) : null;
            }

            items.Add(new
            {
                name = row.Name,
                sizeBytes = row.Size,
                autoload = row.Autoload,
                looksSensitive = LooksSensitive(row.Name),
                value,
            });
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            matched = rows.Count,
            returned = items.Count,
            totalBytes = rows.Sum(r => r.Size),
            valuesIncluded = includeValues,
            note = includeValues
                ? "Values are included — some options hold plugin API keys and secrets."
                : "Values hidden. Re-run with includeValues=true, or use wp_get_option for a single option.",
            options = items,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_autoloaded_options"),
     Description("Report the options WordPress autoloads on every single page request, largest first, with the total. Bloated autoloaded data (often left behind by removed plugins) is the most common cause of a site that is slow everywhere — anything much over 1 MB total is worth investigating.")]
    public static async Task<string> ListAutoloadedOptions(
        ManagementService management,
        [Description("How many of the largest autoloaded options to return. Defaults to 25.")] int limit = 25,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_list_autoloaded_options");
        var result = await management.RunOrThrowAsync(
            target,
            new[] { "option", "list", "--autoload=on", "--fields=option_name,size_bytes", "--format=json" },
            "wp_list_autoloaded_options", ct);

        var parsed = MaintenanceTools.TryParseJson(result.Output);
        var rows = new List<(string Name, long Size)>();
        if (parsed is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                var name = entry.TryGetProperty("option_name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                long size = 0;
                if (entry.TryGetProperty("size_bytes", out var s))
                {
                    size = s.ValueKind == JsonValueKind.Number ? s.GetInt64() : long.TryParse(s.GetString(), out var p) ? p : 0;
                }
                rows.Add((name, size));
            }
        }

        var total = rows.Sum(r => r.Size);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            autoloadedCount = rows.Count,
            totalBytes = total,
            totalReadable = $"{total / 1024.0:0.0} KB",
            verdict = total switch
            {
                > 2_000_000 => "Very high — autoloaded options are large enough to slow every page load. Review the largest entries below for data left behind by removed plugins.",
                > 1_000_000 => "High — worth reviewing the largest entries; over 1 MB is loaded on every request.",
                > 500_000 => "Slightly high but usually acceptable.",
                _ => "Healthy.",
            },
            largest = rows.OrderByDescending(r => r.Size).Take(Math.Clamp(limit, 1, 200))
                .Select(r => new { name = r.Name, sizeBytes = r.Size, looksSensitive = LooksSensitive(r.Name) }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_update_option"),
     Description("Set an option in wp_options — the way to configure a plugin that has no REST endpoint. Pass a JSON value for options that hold arrays or objects. Requires write mode.")]
    public static async Task<string> UpdateOption(
        ManagementService management,
        [Description("Option name.")] string name,
        [Description("New value. For an array/object option pass JSON and set isJson=true.")] string value,
        [Description("Treat `value` as JSON (needed for options holding arrays or objects).")] bool isJson = false,
        [Description("Autoload behaviour: leave unset to keep the current setting, or pass true/false to change it.")] bool? autoload = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_update_option", ct);
        management.RequireFeature(target, f => f.EnableSettings, "Settings");
        EnsureOptionWritable(name);

        var before = await management.RunAsync(target, new[] { "option", "get", name, "--format=json" }, ct);

        var args = new List<string> { "option", "update", name, value };
        if (isJson) args.Add("--format=json");
        if (autoload.HasValue) args.Add($"--autoload={(autoload.Value ? "yes" : "no")}");

        await management.RunOrThrowAsync(target, args, "wp_update_option", ct);
        var after = await management.RunAsync(target, new[] { "option", "get", name, "--format=json" }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            name,
            existedBefore = before.Success,
            previousValue = before.Success ? Truncate(before.Output) : null,
            newValue = after.Success ? Truncate(after.Output) : value,
            autoloadChangedTo = autoload,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_delete_option"),
     Description("Delete an option from wp_options. Useful for clearing settings left behind by a removed plugin. Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteOption(
        ManagementService management,
        [Description("Option name to delete.")] string name,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_delete_option", ct);
        management.EnsureDeleteAllowed(target, "wp_delete_option");

        var before = await management.RunAsync(target, new[] { "option", "get", name, "--format=json" }, ct);
        if (!before.Success)
        {
            return JsonSerializer.Serialize(new { endpoint = target.Name, name, existed = false, deleted = false }, JsonOpts.Default);
        }

        await management.RunOrThrowAsync(target, new[] { "option", "delete", name }, "wp_delete_option", ct);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            name,
            existed = true,
            deleted = true,
            deletedValue = Truncate(before.Output),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_flush_transients"),
     Description("Delete cached transients. Expired ones are safe to clear routinely; clearing all of them is a standard fix for a site showing stale data or a stuck update/licence check. Requires write mode.")]
    public static async Task<string> FlushTransients(
        ManagementService management,
        [Description("Delete every transient, not just expired ones. Defaults to expired only.")] bool all = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_flush_transients", ct);

        var args = new List<string> { "transient", "delete", all ? "--all" : "--expired" };
        var result = await management.RunAsync(target, args, ct, longRunning: true);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            scope = all ? "all transients" : "expired transients only",
            succeeded = result.Success,
            output = result.Output,
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    /// <summary>
    /// Options that grant access or move the site rather than configure a plugin. Writing them through a
    /// generic option tool would sidestep the gates that guard the same changes elsewhere — granting
    /// administrator to every new registrant, or pointing the site at another domain.
    /// </summary>
    private static readonly Dictionary<string, string> ProtectedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wp_user_roles"] = "rewrites every role's capabilities. Use wp_add_user_role / wp_remove_user_role.",
        ["default_role"] = "sets the role given to new registrations. Change it in wp_update_settings if the site really needs it.",
        ["users_can_register"] = "opens or closes public registration. Change it deliberately through wp_update_settings.",
        ["siteurl"] = "moves the site's address; changing it alone breaks the install. Use wp_search_replace for a domain move.",
        ["home"] = "moves the site's address; changing it alone breaks the install. Use wp_search_replace for a domain move.",
        ["admin_email"] = "controls where administrative mail goes. Change it through wp_update_settings.",
    };

    private static void EnsureOptionWritable(string name)
    {
        if (ProtectedOptions.TryGetValue(name, out var reason))
        {
            throw new McpException(
                $"wp_update_option refuses to change '{name}' because it {reason}");
        }
    }

    private static bool LooksSensitive(string name) =>
        SecretHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static string? Truncate(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 4000 ? trimmed[..4000] + "…(truncated)" : trimmed;
    }
}
