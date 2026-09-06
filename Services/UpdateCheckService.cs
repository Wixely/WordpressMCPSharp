using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Configuration;
using Microsoft.Extensions.Options;

namespace WordpressMCPSharp.Services;

/// <summary>
/// Compares what a site has installed against the WordPress.org catalogue. Read-only: it asks
/// api.wordpress.org for current versions and reports the gap, never installing anything.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    private readonly HttpClient _http;
    // Registered as a singleton and reached from concurrent tool calls, so these must be concurrent
    // collections — parallel writes to a plain Dictionary can corrupt it and hang the call.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _pluginVersionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _themeVersionCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _latestCore;
    private bool _disposed;

    public UpdateCheckService(IOptions<WordpressOptions> options)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.Value.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public sealed record ComponentUpdate(string Name, string Slug, string? Installed, string? Latest, bool UpdateAvailable, string? Note = null);

    public sealed class UpdateReport
    {
        public string Endpoint { get; init; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? CoreInstalled { get; set; }
        public string? CoreLatest { get; set; }
        public bool CoreUpdateAvailable { get; set; }
        public List<ComponentUpdate> Plugins { get; } = new();
        public List<ComponentUpdate> Themes { get; } = new();
        public List<string> Notes { get; } = new();
    }

    public async Task<UpdateReport> CheckAsync(WordpressRestClient svc, CancellationToken ct)
    {
        var report = new UpdateReport { Endpoint = svc.EndpointName };

        report.CoreInstalled = await GetCoreVersionAsync(svc, ct);
        report.CoreLatest = await GetLatestCoreAsync(ct);
        report.CoreUpdateAvailable = IsOlder(report.CoreInstalled, report.CoreLatest);
        if (report.CoreInstalled is null)
        {
            report.Notes.Add(
                "The WordPress core version could not be read (the site does not publish a generator tag). " +
                "Configure a management channel and use wp_core_version for an exact reading.");
        }

        await CollectPluginsAsync(svc, report, ct);
        await CollectThemesAsync(svc, report, ct);

        var pluginUpdates = report.Plugins.Count(p => p.UpdateAvailable);
        var themeUpdates = report.Themes.Count(t => t.UpdateAvailable);
        var parts = new List<string>();
        if (report.CoreUpdateAvailable) parts.Add($"core {report.CoreInstalled} → {report.CoreLatest}");
        if (pluginUpdates > 0) parts.Add($"{pluginUpdates} plugin update(s)");
        if (themeUpdates > 0) parts.Add($"{themeUpdates} theme update(s)");
        report.Summary = parts.Count == 0 ? "Everything is up to date." : "Updates available: " + string.Join(", ", parts) + ".";

        return report;
    }

    private static Task<string?> GetCoreVersionAsync(WordpressRestClient svc, CancellationToken ct)
        => svc.TryGetCoreVersionAsync(ct);

    private async Task<string?> GetLatestCoreAsync(CancellationToken ct)
    {
        if (_latestCore is not null) return _latestCore;
        try
        {
            var text = await _http.GetStringAsync("https://api.wordpress.org/core/version-check/1.7/", ct);
            var node = JsonNode.Parse(text);
            var offers = node?["offers"] as JsonArray;
            return _latestCore = (offers?.FirstOrDefault() as JsonObject)?["current"]?.GetValue<string?>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task CollectPluginsAsync(WordpressRestClient svc, UpdateReport report, CancellationToken ct)
    {
        JsonNode? installed;
        try
        {
            installed = await svc.GetJsonAsync("wp-json/wp/v2/plugins", ct);
        }
        catch (ModelContextProtocol.McpException ex)
        {
            report.Notes.Add("Plugin versions unavailable: " + ex.Message);
            return;
        }

        foreach (var entry in (installed as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var file = entry["plugin"]?.GetValue<string?>() ?? string.Empty;
            var slug = file.Contains('/') ? file[..file.IndexOf('/')] : file;
            var version = entry["version"]?.GetValue<string?>();
            var latest = await GetLatestPluginAsync(slug, ct);

            report.Plugins.Add(new ComponentUpdate(
                entry["name"]?.GetValue<string?>() ?? slug,
                file,
                version,
                latest,
                IsOlder(version, latest),
                latest is null ? "Not found on WordPress.org (premium or custom plugin); check for updates manually." : null));
        }
    }

    private async Task CollectThemesAsync(WordpressRestClient svc, UpdateReport report, CancellationToken ct)
    {
        JsonNode? installed;
        try
        {
            installed = await svc.GetJsonAsync("wp-json/wp/v2/themes", ct);
        }
        catch (ModelContextProtocol.McpException ex)
        {
            report.Notes.Add("Theme versions unavailable: " + ex.Message);
            return;
        }

        foreach (var entry in (installed as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var slug = entry["stylesheet"]?.GetValue<string?>() ?? string.Empty;
            var version = entry["version"]?.GetValue<string?>();
            var latest = await GetLatestThemeAsync(slug, ct);
            var name = (entry["name"] as JsonObject)?["raw"]?.GetValue<string?>()
                ?? (entry["name"] as JsonObject)?["rendered"]?.GetValue<string?>()
                ?? slug;

            report.Themes.Add(new ComponentUpdate(
                name,
                slug,
                version,
                latest,
                IsOlder(version, latest),
                latest is null ? "Not found on WordPress.org (premium or custom theme); check for updates manually." : null));
        }
    }

    private async Task<string?> GetLatestPluginAsync(string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        if (_pluginVersionCache.TryGetValue(slug, out var cached)) return cached;

        var version = await FetchVersionAsync(
            $"https://api.wordpress.org/plugins/info/1.2/?action=plugin_information&request[slug]={Uri.EscapeDataString(slug)}", ct);
        return _pluginVersionCache[slug] = version;
    }

    private async Task<string?> GetLatestThemeAsync(string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        if (_themeVersionCache.TryGetValue(slug, out var cached)) return cached;

        var version = await FetchVersionAsync(
            $"https://api.wordpress.org/themes/info/1.2/?action=theme_information&request[slug]={Uri.EscapeDataString(slug)}", ct);
        return _themeVersionCache[slug] = version;
    }

    private async Task<string?> FetchVersionAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(text);
            // A missing slug returns {"error": "..."} rather than a 404.
            return node?["version"]?.GetValue<string?>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Compare dotted version strings; unknown values never report an update.</summary>
    public static bool IsOlder(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest)) return false;
        if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase)) return false;

        var a = ParseParts(installed);
        var b = ParseParts(latest);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var left = i < a.Count ? a[i] : 0;
            var right = i < b.Count ? b[i] : 0;
            if (left != right) return left < right;
        }
        return false;
    }

    private static List<int> ParseParts(string version)
    {
        var parts = new List<int>();
        foreach (var chunk in version.Split('.', '-', '+'))
        {
            var digits = new string(chunk.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            parts.Add(int.Parse(digits));
        }
        return parts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
