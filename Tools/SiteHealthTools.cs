using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class SiteHealthTools
{
    /// <summary>The direct tests WordPress exposes over REST, each returning a status and a description.</summary>
    private static readonly (string Route, string Label)[] DirectTests =
    {
        ("loopback-requests", "Loopback requests (needed for cron and the theme/plugin editors)"),
        ("background-updates", "Background updates"),
        ("https-status", "HTTPS status"),
        ("dotorg-communication", "Communication with WordPress.org"),
        ("authorization-header", "Authorization header pass-through"),
    };

    [McpServerTool(Name = "wp_site_health"),
     Description("Run WordPress's built-in site health tests and return an aggregated report: loopback requests, background updates, HTTPS status, WordPress.org communication and Authorization header handling. Use this when a site is misbehaving or before handing it to a client.")]
    public static async Task<string> SiteHealth(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_site_health");
        svc.EnsureFeature(svc.Options.EnableSiteHealth, "Site health");

        var results = new List<object>();
        var critical = 0;
        var recommended = 0;
        var good = 0;

        foreach (var (route, label) in DirectTests)
        {
            try
            {
                var node = await svc.GetJsonAsync($"wp-json/wp-site-health/v1/tests/{route}", ct);
                var status = node?["status"]?.GetValue<string?>() ?? "unknown";
                switch (status.ToLowerInvariant())
                {
                    case "critical": critical++; break;
                    case "recommended": recommended++; break;
                    case "good": good++; break;
                }

                results.Add(new
                {
                    test = route,
                    label,
                    status,
                    headline = node?["label"]?.GetValue<string?>(),
                    detail = StripHtml(node?["description"]?.GetValue<string?>()),
                    action = StripHtml(node?["actions"]?.GetValue<string?>()),
                });
            }
            catch (ModelContextProtocol.McpException ex)
            {
                results.Add(new { test = route, label, status = "unavailable", headline = (string?)null, detail = ex.Message, action = (string?)null });
            }
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = svc.EndpointName,
            summary = critical > 0
                ? $"{critical} critical issue(s) and {recommended} recommendation(s)."
                : recommended > 0
                    ? $"No critical issues; {recommended} recommendation(s)."
                    : $"All {good} tests passed.",
            counts = new { critical, recommended, good },
            tests = results,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_directory_sizes"),
     Description("Report the disk space used by the WordPress installation: core, themes, plugins, uploads and the database. Useful before a backup or when a host is running out of space. Can take several seconds on large sites.")]
    public static async Task<string> DirectorySizes(
        EndpointRegistry registry,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_directory_sizes");
        svc.EnsureFeature(svc.Options.EnableSiteHealth, "Site health");

        var node = await svc.GetJsonAsync("wp-json/wp-site-health/v1/directory-sizes", ct);
        if (node is not JsonObject obj) return node?.ToJsonString(JsonOpts.Default) ?? "null";

        var entries = obj
            .Where(kv => kv.Value is JsonObject)
            .Select(kv => new
            {
                name = kv.Key,
                size = (kv.Value as JsonObject)?["size"]?.GetValue<string?>(),
                bytes = (kv.Value as JsonObject)?["raw"]?.GetValue<long?>(),
            })
            .OrderByDescending(e => e.bytes ?? 0)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            endpoint = svc.EndpointName,
            totalBytes = entries.FirstOrDefault(e => e.name == "total_size")?.bytes,
            entries,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_check_updates"),
     Description("Report what is out of date: the WordPress core version against the latest release, and every installed plugin and theme against its WordPress.org listing. Read-only — it never installs anything. Use the CLI update tools (or wp-admin) to apply what it finds.")]
    public static async Task<string> CheckUpdates(
        EndpointRegistry registry,
        UpdateCheckService updates,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = registry.RequireRest(site, "wp_check_updates");
        svc.EnsureFeature(svc.Options.EnableSiteHealth, "Site health");
        var report = await updates.CheckAsync(svc, ct);
        return JsonSerializer.Serialize(report, JsonOpts.Default);
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 600 ? text[..600] + "…" : text;
    }
}
