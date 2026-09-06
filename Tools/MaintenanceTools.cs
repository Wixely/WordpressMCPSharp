using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Maintenance operations the WordPress REST API cannot perform: core and plugin/theme updates,
/// theme switching, permalinks, cron, cache and database checks. All run through WP-CLI on the
/// endpoint's management channel.
/// </summary>
[McpServerToolType]
public static class MaintenanceTools
{
    [McpServerTool(Name = "wp_core_version"),
     Description("Report the exact WordPress core version installed, and whether an update is available. Unlike wp_check_updates this reads the installation directly, so it works even when the site hides its generator tag.")]
    public static async Task<string> CoreVersion(
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_core_version");
        var version = await management.RunOrThrowAsync(target, new[] { "core", "version" }, "wp_core_version", ct);
        var check = await management.RunAsync(target, new[] { "core", "check-update", "--format=json" }, ct);

        object? available = null;
        if (check.Success && check.Output.StartsWith('['))
        {
            try { available = JsonSerializer.Deserialize<object>(check.Output); }
            catch (JsonException) { available = check.Output; }
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            version = version.Output,
            updateAvailable = check.Success && check.Output.Length > 2 && check.Output != "[]",
            available,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_core_update"),
     Description("Update WordPress core to the latest release (or a specific version) and run the database upgrade routine. Take a snapshot first. Requires write mode.")]
    public static async Task<string> CoreUpdate(
        ManagementService management,
        [Description("Target version, e.g. `6.7.1`. Omit to update to the latest release.")] string? version = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_core_update", ct);

        var before = await management.RunAsync(target, new[] { "core", "version" }, ct);
        var args = new List<string> { "core", "update" };
        if (!string.IsNullOrWhiteSpace(version)) args.Add($"--version={version}");

        var update = await management.RunOrThrowAsync(target, args, "wp_core_update", ct, longRunning: true);
        var db = await management.RunAsync(target, new[] { "core", "update-db" }, ct, longRunning: true);
        var after = await management.RunAsync(target, new[] { "core", "version" }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            versionBefore = before.Output,
            versionAfter = after.Output,
            updated = !string.Equals(before.Output, after.Output, StringComparison.Ordinal),
            databaseUpgrade = db.Output,
            output = update.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_core_verify_checksums"),
     Description("Verify every WordPress core file against the official checksums. Modified or unexpected files are reported — the quickest check for a compromised or corrupted installation.")]
    public static async Task<string> VerifyChecksums(
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_core_verify_checksums");
        var result = await management.RunAsync(target, new[] { "core", "verify-checksums" }, ct, longRunning: true);

        var problems = result.StdErr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) || line.Contains("doesn't verify", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // WP-CLI exits 0 for "unexpected file" warnings, so a clean result needs both signals.
        var clean = result.Success && problems.Count == 0;
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            verified = clean,
            checksumsMatch = result.Success,
            summary = clean
                ? "All core files match the official checksums."
                : result.Success
                    ? $"Core files match, but {problems.Count} unexpected or extra file(s) were reported — review them below."
                    : $"{problems.Count} problem(s) reported — core files may have been modified. Review each file below.",
            problems,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_update_plugin"),
     Description("Update one plugin, or every plugin with `all=true`. This is the operation the REST API cannot do. Take a snapshot first. Requires write mode.")]
    public static async Task<string> UpdatePlugin(
        ManagementService management,
        [Description("Plugin slug, e.g. `woocommerce`. Omit when all=true.")] string? plugin = null,
        [Description("Update every plugin with an available update.")] bool all = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_update_plugin", ct);
        management.RequireFeature(target, f => f.EnablePlugins, "Plugin");

        if (!all && string.IsNullOrWhiteSpace(plugin))
            throw new McpException("wp_update_plugin: supply a plugin slug, or set all=true.");

        var args = new List<string> { "plugin", "update" };
        if (all) args.Add("--all");
        else args.Add(plugin!);
        args.Add("--format=json");

        var result = await management.RunAsync(target, args, ct, longRunning: true);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            target = all ? "all plugins" : plugin,
            succeeded = result.Success,
            updates = TryParseJson(result.Output),
            output = result.Output.Length > 0 ? result.Output : null,
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_update_theme"),
     Description("Update one theme, or every theme with `all=true`. Requires write mode.")]
    public static async Task<string> UpdateTheme(
        ManagementService management,
        [Description("Theme slug, e.g. `twentytwentyfive`. Omit when all=true.")] string? theme = null,
        [Description("Update every theme with an available update.")] bool all = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_update_theme", ct);
        management.RequireFeature(target, f => f.EnableThemes, "Theme");

        if (!all && string.IsNullOrWhiteSpace(theme))
            throw new McpException("wp_update_theme: supply a theme slug, or set all=true.");

        var args = new List<string> { "theme", "update" };
        if (all) args.Add("--all");
        else args.Add(theme!);
        args.Add("--format=json");

        var result = await management.RunAsync(target, args, ct, longRunning: true);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            target = all ? "all themes" : theme,
            succeeded = result.Success,
            updates = TryParseJson(result.Output),
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_install_theme"),
     Description("Install a theme from the WordPress.org directory, optionally activating it. Requires write mode.")]
    public static async Task<string> InstallTheme(
        ManagementService management,
        [Description("WordPress.org theme slug, e.g. `twentytwentyfour`.")] string slug,
        [Description("Activate the theme once installed.")] bool activate = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_install_theme", ct);
        management.RequireFeature(target, f => f.EnableThemes, "Theme");

        var args = new List<string> { "theme", "install", slug };
        if (activate) args.Add("--activate");

        var result = await management.RunOrThrowAsync(target, args, "wp_install_theme", ct, longRunning: true);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            slug,
            activated = activate,
            output = result.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_activate_theme"),
     Description("Switch the site to another installed theme. The core REST API cannot do this. Requires write mode.")]
    public static async Task<string> ActivateTheme(
        ManagementService management,
        [Description("Theme slug (stylesheet), e.g. `twentytwentyfour`.")] string theme,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_activate_theme", ct);
        management.RequireFeature(target, f => f.EnableThemes, "Theme");
        var before = await management.RunAsync(target, new[] { "theme", "list", "--status=active", "--field=name" }, ct);
        var result = await management.RunOrThrowAsync(target, new[] { "theme", "activate", theme }, "wp_activate_theme", ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            previousTheme = before.Output,
            activeTheme = theme,
            output = result.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_set_permalink_structure"),
     Description("Set the site's permalink structure and flush rewrite rules. A fresh install uses plain permalinks, which is why /wp-json/ does not resolve. Requires write mode.")]
    public static async Task<string> SetPermalinkStructure(
        ManagementService management,
        [Description("Permalink structure, e.g. `/%postname%/` (recommended) or `/%year%/%monthnum%/%postname%/`. Pass an empty string for plain permalinks.")] string structure = "/%postname%/",
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_set_permalink_structure", ct);
        management.RequireFeature(target, f => f.EnableSettings, "Settings");
        var before = await management.RunAsync(target, new[] { "option", "get", "permalink_structure" }, ct);
        await management.RunOrThrowAsync(target, new[] { "rewrite", "structure", structure, "--hard" }, "wp_set_permalink_structure", ct);
        var after = await management.RunAsync(target, new[] { "option", "get", "permalink_structure" }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            previous = before.Output,
            current = after.Output,
            note = "Rewrite rules were flushed. The REST API now answers on /wp-json/ as well as ?rest_route=.",
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_flush_cache"),
     Description("Flush the WordPress object cache and rewrite rules. First thing to try when a site serves stale content or 404s after a structural change. Requires write mode.")]
    public static async Task<string> FlushCache(
        ManagementService management,
        [Description("Also flush rewrite rules. Defaults to true.")] bool includeRewrites = true,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_flush_cache", ct);
        var cache = await management.RunAsync(target, new[] { "cache", "flush" }, ct);
        var rewrites = includeRewrites ? await management.RunAsync(target, new[] { "rewrite", "flush", "--hard" }, ct) : null;

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            objectCache = cache.Success ? "flushed" : ManagementService.Describe(cache),
            rewriteRules = rewrites is null ? "skipped" : rewrites.Success ? "flushed" : ManagementService.Describe(rewrites),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_maintenance_mode"),
     Description("Turn WordPress maintenance mode on or off, or report its current state. Use it around risky changes so visitors see a maintenance page instead of a half-updated site. Requires write mode to change.")]
    public static async Task<string> MaintenanceMode(
        ManagementService management,
        [Description("Action: status (default), activate, deactivate.")] string action = "status",
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var verb = action.Trim().ToLowerInvariant();
        if (verb is not ("status" or "activate" or "deactivate"))
            throw new McpException("action must be 'status', 'activate' or 'deactivate'.");

        var target = verb == "status"
            ? management.Resolve(site, "wp_maintenance_mode")
            : await management.ResolveForWriteAsync(site, "wp_maintenance_mode", ct);

        var result = await management.RunAsync(target, new[] { "maintenance-mode", verb }, ct);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            action = verb,
            succeeded = result.Success,
            state = result.Output.Length > 0 ? result.Output : result.StdErr.Trim(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_cron_events"),
     Description("List scheduled WordPress cron events with their next run time. Overdue events usually mean loopback requests are failing, which stops scheduled publishing, backups and update checks.")]
    public static async Task<string> ListCronEvents(
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_list_cron_events");
        var result = await management.RunOrThrowAsync(target, new[] { "cron", "event", "list", "--format=json" }, "wp_list_cron_events", ct);

        var events = TryParseJson(result.Output);
        var overdue = 0;
        if (events is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            overdue = array.EnumerateArray().Count(e =>
                e.TryGetProperty("next_run_relative", out var rel) &&
                (rel.GetString() ?? string.Empty).Contains("ago", StringComparison.OrdinalIgnoreCase));
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            overdueCount = overdue,
            note = overdue > 0
                ? "Overdue events usually mean WP-Cron is not firing — check the loopback test in wp_site_health, or set up a real cron job."
                : null,
            events,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_run_cron_event"),
     Description("Run a scheduled cron event immediately, or all due events. Useful to unstick a backlog or test a plugin's scheduled task. Requires write mode.")]
    public static async Task<string> RunCronEvent(
        ManagementService management,
        [Description("Event hook name, e.g. `wp_version_check`. Omit when dueNow=true.")] string? hook = null,
        [Description("Run every event that is currently due.")] bool dueNow = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_run_cron_event", ct);

        if (!dueNow && string.IsNullOrWhiteSpace(hook))
            throw new McpException("wp_run_cron_event: supply a hook name, or set dueNow=true.");

        var args = new List<string> { "cron", "event", "run" };
        if (dueNow) args.Add("--due-now");
        else args.Add(hook!);

        var result = await management.RunAsync(target, args, ct, longRunning: true);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            ran = dueNow ? "all due events" : hook,
            succeeded = result.Success,
            output = result.Output,
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_db_check"),
     Description("Run a database integrity check across all WordPress tables. Read-only.")]
    public static async Task<string> DbCheck(
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_db_check");
        var result = await management.RunAsync(target, new[] { "db", "check" }, ct, longRunning: true);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            healthy = result.Success,
            output = result.Output,
            error = result.Success ? null : ManagementService.DescribeDatabaseFailure(result, "wp_db_check", target.Name),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_db_optimize"),
     Description("Optimise the WordPress database tables, reclaiming space left by deleted rows. Requires write mode.")]
    public static async Task<string> DbOptimize(
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_db_optimize", ct);
        var result = await management.RunAsync(target, new[] { "db", "optimize" }, ct, longRunning: true);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            succeeded = result.Success,
            output = result.Output,
            error = result.Success ? null : ManagementService.DescribeDatabaseFailure(result, "wp_db_optimize", target.Name),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_search_replace"),
     Description("Search and replace a string across the whole database — the correct way to move a site between domains, because it rewrites serialised data safely. Runs as a dry run by default, reporting how many rows would change. Requires write mode when apply=true.")]
    public static async Task<string> SearchReplace(
        ManagementService management,
        [Description("String to find, e.g. `http://old.example.com`.")] string find,
        [Description("Replacement string, e.g. `https://new.example.com`.")] string replace,
        [Description("Set true to write the changes. When false (default) it reports what would change without touching anything.")] bool apply = false,
        [Description("Also process the wp_options table's autoloaded values and all tables including multisite ones.")] bool allTables = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = apply
            ? await management.ResolveForWriteAsync(site, "wp_search_replace", ct)
            : management.Resolve(site, "wp_search_replace");

        if (string.IsNullOrEmpty(find))
            throw new McpException("wp_search_replace: 'find' must not be empty.");

        // search-replace only renders a table (its --format accepts table/count), so read that output.
        var args = new List<string> { "search-replace", find, replace, "--report-changed-only" };
        if (!apply) args.Add("--dry-run");
        if (allTables) args.Add("--all-tables");

        var result = await management.RunAsync(target, args, ct, longRunning: true);

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // WP-CLI ends with "Success: Made N replacements" (or "…would be made" for a dry run).
        var summaryLine = lines.LastOrDefault(l => l.StartsWith("Success:", StringComparison.OrdinalIgnoreCase));
        var replacements = 0;
        if (summaryLine is not null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(summaryLine, @"(\d+)\s+replacement");
            if (match.Success) replacements = int.Parse(match.Groups[1].Value);
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            find,
            replace,
            applied = apply,
            succeeded = result.Success,
            replacements,
            summary = summaryLine,
            changedColumns = lines.Where(l => l.StartsWith("wp_", StringComparison.OrdinalIgnoreCase) || l.Contains('|')).Take(50).ToList(),
            note = apply ? null : "Dry run — nothing was changed. Re-run with apply=true to write.",
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    internal static object? TryParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed[0] is not ('[' or '{')) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(trimmed); }
        catch (JsonException) { return null; }
    }
}
