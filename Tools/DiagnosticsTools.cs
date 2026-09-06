using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Triage tooling: reading the debug log, a whole-site health sweep, and a guided plugin-conflict
/// bisect for "the site broke and I don't know which plugin did it".
/// </summary>
[McpServerToolType]
public static class DiagnosticsTools
{
    [McpServerTool(Name = "wp_read_debug_log"),
     Description("Read the tail of wp-content/debug.log, where PHP warnings and fatal errors land when WP_DEBUG_LOG is on. The first place to look when a site is white-screening or a plugin is misbehaving.")]
    public static async Task<string> ReadDebugLog(
        ManagementService management,
        [Description("Number of lines from the end of the log. Defaults to 100.")] int lines = 100,
        [Description("Only return lines containing this text, e.g. `Fatal error` or a plugin name.")] string? filter = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_read_debug_log");
        var path = $"{target.Path}/wp-content/debug.log";
        var count = Math.Clamp(lines, 1, 5000);

        var exists = await management.RunShellAsync(target, $"test -f {Q(path)} && echo ok", ct, longRunning: false);
        if (exists.Output.Trim() != "ok")
        {
            var debugEnabled = await management.RunAsync(target, new[] { "config", "get", "WP_DEBUG_LOG" }, ct);
            return JsonSerializer.Serialize(new
            {
                endpoint = target.Name,
                path,
                exists = false,
                debugLogSetting = debugEnabled.Success ? debugEnabled.Output : "not set",
                hint = "No debug log yet. Turn logging on with wp_set_debug (WP_DEBUG and WP_DEBUG_LOG), reproduce the problem, then read it again.",
            }, JsonOpts.Default);
        }

        var command = string.IsNullOrWhiteSpace(filter)
            ? $"tail -n {count} {Q(path)}"
            : $"grep -F -- {Q(filter)} {Q(path)} | tail -n {count}";

        var result = await management.RunShellAsync(target, command, ct, longRunning: false);
        var text = result.StdOut;
        var truncated = false;
        if (text.Length > 60000)
        {
            text = text[^60000..];
            truncated = true;
        }

        var logLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            path,
            exists = true,
            filter,
            lineCount = logLines.Length,
            fatalErrors = logLines.Count(l => l.Contains("Fatal error", StringComparison.OrdinalIgnoreCase)),
            truncated,
            lines = logLines,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_clear_debug_log"),
     Description("Truncate wp-content/debug.log. Do this before reproducing a problem so the log contains only the relevant run. Requires write mode.")]
    public static async Task<string> ClearDebugLog(
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_clear_debug_log", ct);
        var path = $"{target.Path}/wp-content/debug.log";
        var result = await management.RunShellAsync(target, $": > {Q(path)} && echo ok", ct, longRunning: false);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            path,
            cleared = result.Output.Trim() == "ok",
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_set_debug"),
     Description("Turn WordPress debug logging on or off (WP_DEBUG, WP_DEBUG_LOG, WP_DEBUG_DISPLAY). Leave display off on a live site so visitors never see PHP errors. Requires write mode.")]
    public static async Task<string> SetDebug(
        ManagementService management,
        [Description("Enable WP_DEBUG.")] bool enabled = true,
        [Description("Write errors to wp-content/debug.log. Defaults to the value of `enabled`.")] bool? log = null,
        [Description("Show errors in the page output. Defaults to false — never turn this on for a live site.")] bool display = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_set_debug", ct);
        var wantLog = log ?? enabled;

        foreach (var (name, value) in new[]
                 {
                     ("WP_DEBUG", enabled),
                     ("WP_DEBUG_LOG", wantLog),
                     ("WP_DEBUG_DISPLAY", display),
                 })
        {
            await management.RunOrThrowAsync(
                target,
                new[] { "config", "set", name, value ? "true" : "false", "--raw", "--type=constant" },
                "wp_set_debug", ct);
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            wpDebug = enabled,
            wpDebugLog = wantLog,
            wpDebugDisplay = display,
            logPath = $"{target.Path}/wp-content/debug.log",
            note = display ? "WP_DEBUG_DISPLAY is on — turn it off before the site goes live." : null,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_diagnose"),
     Description("One-shot triage for 'the site is acting up'. Runs everything available on the endpoint's configured channels — front-page response, REST reachability and authentication, WordPress site-health tests, pending updates, overdue cron events, database check and recent fatal errors — and reports what each channel could not cover rather than failing. Start here when something is wrong.")]
    public static async Task<string> Diagnose(
        EndpointRegistry registry,
        ManagementService management,
        SetupDiagnosticsService setup,
        UpdateCheckService updates,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        registry.Options.EnsureSetupDiagnosticsEnabled();
        var (name, endpoint) = registry.Resolve(site, "wp_diagnose");
        var checks = new List<object>();
        var problems = new List<string>();

        // --- REST channel ---
        if (endpoint.HasRest)
        {
            var probe = await setup.ProbeAsync(
                endpoint.RestApi!.BaseUrl, endpoint.RestApi.Username, endpoint.RestApi.ApplicationPassword,
                endpoint.RestApi.AllowInvalidCertificate, ct);

            checks.Add(new { check = "connectivity", channel = "rest", status = probe.IsWordPress ? "ok" : "fail", detail = probe.Summary });
            foreach (var finding in probe.Findings.Where(f => f.Status == "fail"))
            {
                problems.Add($"{finding.Check}: {finding.Detail}");
            }

            var svc = registry.RequireRest(name, "wp_diagnose");

            try
            {
                var health = await RunSiteHealthAsync(svc, ct);
                checks.Add(health.Summary);
                problems.AddRange(health.Critical);
            }
            catch (McpException ex)
            {
                checks.Add(new { check = "site-health", channel = "rest", status = "unavailable", detail = ex.Message });
            }

            try
            {
                var report = await updates.CheckAsync(svc, ct);
                var pending = report.Plugins.Count(p => p.UpdateAvailable) + report.Themes.Count(t => t.UpdateAvailable) + (report.CoreUpdateAvailable ? 1 : 0);
                checks.Add(new { check = "updates", channel = "rest", status = pending == 0 ? "ok" : "warn", detail = report.Summary });
            }
            catch (McpException ex)
            {
                checks.Add(new { check = "updates", channel = "rest", status = "unavailable", detail = ex.Message });
            }
        }
        else
        {
            checks.Add(new { check = "rest", channel = "rest", status = "skipped", detail = "channel not configured" });
        }

        // --- Management channel ---
        if (endpoint.HasManagement && management.Options.AllowCliManagement)
        {
            var target = management.Resolve(name, "wp_diagnose");

            var version = await management.RunAsync(target, new[] { "core", "version" }, ct);
            checks.Add(new
            {
                check = "wp-cli",
                channel = "management",
                status = version.Success ? "ok" : "fail",
                detail = version.Success ? $"WP-CLI reachable; WordPress {version.Output}." : ManagementService.Describe(version),
            });
            if (!version.Success) problems.Add("WP-CLI is not usable on this endpoint: " + ManagementService.Describe(version));

            var cron = await management.RunAsync(target, new[] { "cron", "event", "list", "--fields=hook,next_run_relative", "--format=csv" }, ct);
            var cronLines = cron.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            var overdue = cronLines.Count(l => l.Contains(" ago", StringComparison.OrdinalIgnoreCase));
            checks.Add(new
            {
                check = "cron",
                channel = "management",
                status = overdue > 0 ? "warn" : "ok",
                detail = overdue > 0
                    ? $"{overdue} overdue event(s); WP-Cron may not be firing (check the loopback test)."
                    : $"No overdue events ({cronLines.Count} scheduled).",
            });
            if (overdue > 0) problems.Add($"{overdue} overdue cron event(s).");

            var db = await management.RunAsync(target, new[] { "db", "check" }, ct, longRunning: true);
            var missingTool = ManagementService.MissingDatabaseTool(db);
            checks.Add(new
            {
                check = "database",
                channel = "management",
                // A missing client tool is an environment gap, not a sick database — don't cry wolf.
                status = db.Success ? "ok" : missingTool is not null ? "skipped" : "fail",
                detail = db.Success
                    ? "All tables OK."
                    : missingTool is not null
                        ? $"Not checked: the MySQL client tool '{missingTool}' is not installed on this host."
                        : ManagementService.Describe(db),
            });
            if (!db.Success && missingTool is null) problems.Add("Database check reported errors.");

            var logPath = $"{target.Path}/wp-content/debug.log";
            var fatals = await management.RunShellAsync(target,
                $"test -f {Q(logPath)} && grep -c 'Fatal error' {Q(logPath)} || echo 0", ct, longRunning: false);
            var fatalCount = int.TryParse(fatals.Output.Trim(), out var f) ? f : 0;
            checks.Add(new
            {
                check = "debug-log",
                channel = "management",
                status = fatalCount > 0 ? "fail" : "ok",
                detail = fatalCount > 0
                    ? $"{fatalCount} fatal error(s) in debug.log — read them with wp_read_debug_log."
                    : "No fatal errors logged.",
            });
            if (fatalCount > 0) problems.Add($"{fatalCount} fatal error(s) in debug.log.");
        }
        else
        {
            checks.Add(new
            {
                check = "management",
                channel = "management",
                status = "skipped",
                detail = endpoint.HasManagement
                    ? "channel not enabled (Management:AllowCliManagement=false)"
                    : "channel not configured",
            });
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = name,
            verdict = problems.Count == 0
                ? "No problems found by the checks that could run."
                : $"{problems.Count} problem(s) found.",
            problems,
            checks,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_bisect_plugins"),
     Description("Find which plugin is breaking a site by bisection. `start` records the active plugins and deactivates half; then call `good` (problem gone — the culprit is in the deactivated half) or `bad` (problem persists) to narrow it down; `restore` puts everything back. Always finishes by restoring the original set. Requires write mode.")]
    public static async Task<string> BisectPlugins(
        ManagementService management,
        [Description("Step: start, good, bad, status or restore.")] string step,
        [Description("The plugins that were deactivated for the round you are reporting on — pass back the `remaining` list from the previous step. Not needed for start/restore/status.")] string? remainingJson = null,
        [Description("The full original plugin set, as a JSON array of slugs, returned by `start`. Pass it back on every later step so the tool can restore it.")] string? originalJson = null,
        [Description("The set still under suspicion — pass back the `candidates` list from the previous step. Omit on the first good/bad answer.")] string? candidatesJson = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_bisect_plugins", ct);
        management.RequireFeature(target, f => f.EnablePlugins, "Plugin");
        var verb = step.Trim().ToLowerInvariant();

        if (verb == "status")
        {
            var active = await ActivePluginsAsync(management, target, ct);
            return JsonSerializer.Serialize(new { endpoint = target.Name, activePlugins = active }, JsonOpts.Default);
        }

        if (verb == "start")
        {
            var original = await ActivePluginsAsync(management, target, ct);
            if (original.Count < 2)
                throw new McpException($"Bisection needs at least two active plugins; endpoint '{target.Name}' has {original.Count}.");

            var half = original.Take(original.Count / 2).ToList();
            var rest = original.Skip(original.Count / 2).ToList();
            await SetActiveAsync(management, target, original, rest, ct);

            return JsonSerializer.Serialize(new
            {
                endpoint = target.Name,
                step = "start",
                original,
                deactivated = half,
                stillActive = rest,
                remaining = half,
                next = "Reproduce the problem now. If it is GONE, call this tool again with step='good' (the culprit is among the deactivated plugins). " +
                       "If it PERSISTS, call step='bad'. Pass back both `remaining` and `original`.",
            }, JsonOpts.Default);
        }

        if (verb == "restore")
        {
            var original = ParseList(originalJson, nameof(originalJson));
            if (original.Count == 0)
            {
                // Reporting success here would leave the site with plugins still deactivated while
                // telling the operator everything was put back.
                throw new McpException(
                    "wp_bisect_plugins step='restore' needs originalJson — the plugin list returned by step='start'. " +
                    "Without it nothing can be reactivated. If you have lost it, use wp_list_plugins to see the current " +
                    "state and reactivate manually with wp_activate_plugin.");
            }

            await SetActiveAsync(management, target, original, original, ct);
            var activeNow = await ActivePluginsAsync(management, target, ct);
            var missing = original.Except(activeNow, StringComparer.OrdinalIgnoreCase).ToList();

            return JsonSerializer.Serialize(new
            {
                endpoint = target.Name,
                step = "restore",
                restored = original,
                activeNow,
                fullyRestored = missing.Count == 0,
                note = missing.Count == 0
                    ? "All originally active plugins have been reactivated."
                    : $"These plugins could not be reactivated and need attention: {string.Join(", ", missing)}.",
            }, JsonOpts.Default);
        }

        if (verb is not ("good" or "bad"))
            throw new McpException("step must be one of: start, good, bad, status, restore.");

        var originals = ParseList(originalJson, nameof(originalJson));
        var suspects = ParseList(remainingJson, nameof(remainingJson));
        if (suspects.Count == 0)
            throw new McpException("wp_bisect_plugins: pass the `remaining` list from the previous step in remainingJson.");
        if (originals.Count == 0)
            throw new McpException("wp_bisect_plugins: pass the `original` list returned by step='start' in originalJson, so the tool can restore the site afterwards.");

        // `candidates` from the previous round is the set still under suspicion. Narrowing must happen
        // within it — falling back to the full original set would re-admit plugins already cleared and
        // eventually name an innocent one as the culprit.
        var priorCandidates = ParseList(candidatesJson, nameof(candidatesJson));
        if (priorCandidates.Count == 0) priorCandidates = originals;

        // "good" means the problem disappeared while `remaining` was deactivated, so the culprit is among
        // those. "bad" means it persisted, so the culprit is in the rest of the suspected set.
        var candidates = verb == "good"
            ? suspects
            : priorCandidates.Except(suspects, StringComparer.OrdinalIgnoreCase).ToList();

        if (candidates.Count == 1)
        {
            await SetActiveAsync(management, target, originals, originals, ct);
            return JsonSerializer.Serialize(new
            {
                endpoint = target.Name,
                step = verb,
                culprit = candidates[0],
                conclusion = $"'{candidates[0]}' is the plugin responsible. All original plugins have been reactivated.",
            }, JsonOpts.Default);
        }

        if (candidates.Count == 0)
        {
            await SetActiveAsync(management, target, originals, originals, ct);
            return JsonSerializer.Serialize(new
            {
                endpoint = target.Name,
                step = verb,
                conclusion = "No single plugin explains the problem — it may be the theme, core, or a combination. All plugins have been reactivated.",
            }, JsonOpts.Default);
        }

        var nextHalf = candidates.Take(candidates.Count / 2).ToList();
        var keepActive = originals.Except(nextHalf, StringComparer.OrdinalIgnoreCase).ToList();
        await SetActiveAsync(management, target, originals, keepActive, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            step = verb,
            candidates,
            deactivated = nextHalf,
            stillActive = keepActive,
            remaining = nextHalf,
            original = originals,
            next = "Reproduce again: step='good' if the problem is gone, step='bad' if it persists. " +
                   "Pass back `remaining`, `original` AND `candidates` so the search keeps narrowing. " +
                   "Call step='restore' with `original` at any point to put every plugin back.",
        }, JsonOpts.Default);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static async Task<List<string>> ActivePluginsAsync(ManagementService management, ManagementService.Target target, CancellationToken ct)
    {
        var result = await management.RunOrThrowAsync(target, new[] { "plugin", "list", "--status=active", "--field=name" }, "wp_bisect_plugins", ct);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>Bring the active plugin set to exactly <paramref name="wanted"/>, within <paramref name="universe"/>.</summary>
    private static async Task SetActiveAsync(
        ManagementService management, ManagementService.Target target,
        IReadOnlyCollection<string> universe, IReadOnlyCollection<string> wanted, CancellationToken ct)
    {
        var toDeactivate = universe.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList();
        if (toDeactivate.Count > 0)
        {
            var args = new List<string> { "plugin", "deactivate" };
            args.AddRange(toDeactivate);
            await management.RunAsync(target, args, ct, longRunning: true);
        }

        if (wanted.Count > 0)
        {
            var args = new List<string> { "plugin", "activate" };
            args.AddRange(wanted);
            await management.RunAsync(target, args, ct, longRunning: true);
        }
    }

    private static async Task<(object Summary, List<string> Critical)> RunSiteHealthAsync(WordpressRestClient svc, CancellationToken ct)
    {
        var critical = new List<string>();
        var counts = new Dictionary<string, int> { ["critical"] = 0, ["recommended"] = 0, ["good"] = 0 };

        foreach (var test in new[] { "loopback-requests", "background-updates", "https-status", "dotorg-communication", "authorization-header" })
        {
            var node = await svc.GetJsonAsync($"wp-json/wp-site-health/v1/tests/{test}", ct);
            var status = node?["status"]?.GetValue<string?>() ?? "unknown";
            if (counts.ContainsKey(status)) counts[status]++;
            if (status == "critical")
            {
                critical.Add($"site-health/{test}: {node?["label"]?.GetValue<string?>()}");
            }
        }

        return (new
        {
            check = "site-health",
            channel = "rest",
            status = counts["critical"] > 0 ? "fail" : counts["recommended"] > 0 ? "warn" : "ok",
            detail = $"{counts["good"]} good, {counts["recommended"]} recommended, {counts["critical"]} critical.",
        }, critical);
    }

    private static List<string> ParseList(string? json, string paramName)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>();
        }
        catch (JsonException ex)
        {
            throw new McpException($"{paramName} must be a JSON array of plugin slugs: {ex.Message}");
        }
    }

    private static string Q(string value) => ManagementService.ShellQuote(value);
}
