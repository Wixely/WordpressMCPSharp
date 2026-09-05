using System.Text.Json;
using System.Text.Json.Serialization;
using WordpressMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace WordpressMCPSharp.Services;

/// <summary>
/// Snapshots a site: a database dump plus a wp-content archive, described by a manifest. Both
/// artefacts are produced on the site's own host through the management channel and left there,
/// under a per-endpoint directory, so a restore never has to move data across the wire.
/// </summary>
public sealed class SnapshotService
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ManagementService _management;
    private readonly SnapshotOptions _options;

    public SnapshotService(ManagementService management, IOptions<SnapshotOptions> options)
    {
        _management = management;
        _options = options.Value;
    }

    public SnapshotOptions Options => _options;

    public sealed class Manifest
    {
        public string Id { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string SiteUrl { get; set; } = string.Empty;
        public string InstallPath { get; set; } = string.Empty;
        public string CreatedUtc { get; set; } = string.Empty;
        public string? Label { get; set; }
        public string? WordPressVersion { get; set; }
        public string? ActiveTheme { get; set; }
        public List<string> ActivePlugins { get; set; } = new();
        public string DatabaseFile { get; set; } = string.Empty;
        public long DatabaseBytes { get; set; }
        public string? ContentFile { get; set; }
        public long ContentBytes { get; set; }
        public bool IncludesUploads { get; set; }
    }

    /// <summary>Root directory for snapshots on the site's host.</summary>
    public string RootFor(ManagementService.Target target)
    {
        if (string.IsNullOrWhiteSpace(_options.Directory))
        {
            throw new McpException(
                "Snapshots are not configured. Set Snapshots:Directory to a writable directory on the managed host " +
                "(for example /var/backups/wordpress).");
        }
        return TrimSlash(_options.Directory) + "/" + target.Name;
    }

    public async Task<Manifest> CreateAsync(ManagementService.Target target, string? label, bool? includeUploads, CancellationToken ct)
    {
        var root = RootFor(target);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var directory = $"{root}/{id}";
        var withUploads = includeUploads ?? _options.IncludeUploads;

        await ShellOrThrowAsync(target, $"mkdir -p {Q(directory)}", "creating the snapshot directory", ct);

        // Database dump.
        var databaseFile = $"{directory}/database.sql";
        var dump = await _management.RunAsync(target, new[] { "db", "export", databaseFile, "--add-drop-table" }, ct, longRunning: true);
        if (!dump.Success)
        {
            throw new McpException(ManagementService.DescribeDatabaseFailure(dump, "wp_create_snapshot", target.Name));
        }

        // wp-content archive, optionally excluding uploads (which dominate the size on media-heavy sites).
        var contentFile = $"{directory}/wp-content.tar.gz";
        var exclude = withUploads ? string.Empty : "--exclude=./uploads ";
        var tar = $"tar -czf {Q(contentFile)} -C {Q(target.Path + "/wp-content")} {exclude}.";
        var archive = await _management.RunShellAsync(target, tar, ct);
        if (!archive.Success && !File.Exists(contentFile))
        {
            // tar warns about files changing during read; only treat a missing archive as fatal.
            var check = await _management.RunShellAsync(target, $"test -s {Q(contentFile)} && echo ok", ct, longRunning: false);
            if (check.Output.Trim() != "ok")
            {
                throw new McpException(
                    $"Snapshot failed while archiving wp-content on endpoint '{target.Name}'. {ManagementService.Describe(archive)}");
            }
        }

        var manifest = new Manifest
        {
            Id = id,
            Endpoint = target.Name,
            InstallPath = target.Path,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("u"),
            Label = label,
            DatabaseFile = databaseFile,
            ContentFile = contentFile,
            IncludesUploads = withUploads,
        };

        manifest.SiteUrl = (await _management.RunAsync(target, new[] { "option", "get", "siteurl" }, ct)).Output;
        manifest.WordPressVersion = (await _management.RunAsync(target, new[] { "core", "version" }, ct)).Output;
        manifest.ActiveTheme = (await _management.RunAsync(target, new[] { "theme", "list", "--status=active", "--field=name" }, ct)).Output;

        var plugins = await _management.RunAsync(target, new[] { "plugin", "list", "--status=active", "--field=name" }, ct);
        manifest.ActivePlugins = plugins.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        manifest.DatabaseBytes = await FileSizeAsync(target, databaseFile, ct);
        manifest.ContentBytes = await FileSizeAsync(target, contentFile, ct);

        await WriteManifestAsync(target, directory, manifest, ct);
        await PruneAsync(target, root, ct);
        return manifest;
    }

    public async Task<List<Manifest>> ListAsync(ManagementService.Target target, CancellationToken ct)
    {
        var root = RootFor(target);
        var listing = await _management.RunShellAsync(target, $"cat {Q(root)}/*/manifest.json 2>/dev/null || true", ct, longRunning: false);

        var manifests = new List<Manifest>();
        foreach (var chunk in SplitJsonObjects(listing.StdOut))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(chunk, ManifestJson);
                if (manifest is not null) manifests.Add(manifest);
            }
            catch (JsonException)
            {
                // A partially written manifest should not hide the healthy ones.
            }
        }

        return manifests.OrderByDescending(m => m.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<Manifest> GetAsync(ManagementService.Target target, string id, CancellationToken ct)
    {
        var manifests = await ListAsync(target, ct);
        return manifests.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(
                $"Snapshot '{id}' was not found for endpoint '{target.Name}'. " +
                $"Available: {(manifests.Count == 0 ? "(none)" : string.Join(", ", manifests.Select(m => m.Id)))}.");
    }

    public async Task<(Manifest Manifest, Manifest? SafetySnapshot)> RestoreAsync(
        ManagementService.Target target, string id, bool skipSafetySnapshot, CancellationToken ct)
    {
        var manifest = await GetAsync(target, id, ct);

        // Restoring the wrong site's data is unrecoverable, so the manifest must agree with the endpoint.
        var currentUrl = (await _management.RunAsync(target, new[] { "option", "get", "siteurl" }, ct)).Output;
        if (!string.IsNullOrWhiteSpace(manifest.SiteUrl) && !UrlsEquivalent(manifest.SiteUrl, currentUrl))
        {
            throw new McpException(
                $"Refusing to restore: snapshot '{id}' was taken from '{manifest.SiteUrl}', but endpoint '{target.Name}' " +
                $"currently reports '{currentUrl}'. Restoring it here would overwrite a different site.");
        }

        Manifest? safety = null;
        if (!skipSafetySnapshot)
        {
            safety = await CreateAsync(target, $"pre-restore of {id}", includeUploads: false, ct);
        }

        var import = await _management.RunAsync(target, new[] { "db", "import", manifest.DatabaseFile }, ct, longRunning: true);
        if (!import.Success)
        {
            throw new McpException(ManagementService.DescribeDatabaseFailure(import, "wp_restore_snapshot", target.Name));
        }

        if (!string.IsNullOrWhiteSpace(manifest.ContentFile))
        {
            var extract = $"tar -xzf {Q(manifest.ContentFile!)} -C {Q(target.Path + "/wp-content")}";
            var result = await _management.RunShellAsync(target, extract, ct);
            if (!result.Success)
            {
                throw new McpException(
                    $"Database restored, but extracting wp-content failed on endpoint '{target.Name}'. {ManagementService.Describe(result)}");
            }
        }

        await _management.RunAsync(target, new[] { "cache", "flush" }, ct);
        return (manifest, safety);
    }

    public async Task DeleteAsync(ManagementService.Target target, string id, CancellationToken ct)
    {
        var manifest = await GetAsync(target, id, ct);
        var directory = $"{RootFor(target)}/{manifest.Id}";
        await ShellOrThrowAsync(target, $"rm -rf {Q(directory)}", "deleting the snapshot", ct);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private async Task WriteManifestAsync(ManagementService.Target target, string directory, Manifest manifest, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(manifest, ManifestJson);
        // A quoted heredoc keeps the JSON verbatim, with no shell expansion.
        var command = $"cat > {Q(directory + "/manifest.json")} <<'WPMCP_MANIFEST_EOF'\n{json}\nWPMCP_MANIFEST_EOF";
        await ShellOrThrowAsync(target, command, "writing the snapshot manifest", ct);
    }

    private async Task PruneAsync(ManagementService.Target target, string root, CancellationToken ct)
    {
        if (_options.MaxSnapshots <= 0) return;

        var manifests = await ListAsync(target, ct);
        foreach (var stale in manifests.Skip(_options.MaxSnapshots))
        {
            await _management.RunShellAsync(target, $"rm -rf {Q($"{root}/{stale.Id}")}", ct, longRunning: false);
        }
    }

    private async Task<long> FileSizeAsync(ManagementService.Target target, string path, CancellationToken ct)
    {
        var result = await _management.RunShellAsync(target, $"wc -c < {Q(path)} 2>/dev/null || echo 0", ct, longRunning: false);
        return long.TryParse(result.Output.Trim(), out var bytes) ? bytes : 0;
    }

    private async Task ShellOrThrowAsync(ManagementService.Target target, string command, string what, CancellationToken ct)
    {
        var result = await _management.RunShellAsync(target, command, ct, longRunning: false);
        if (!result.Success)
        {
            throw new McpException($"Failed while {what} on endpoint '{target.Name}'. {ManagementService.Describe(result)}");
        }
    }

    /// <summary>Split concatenated manifest files back into individual JSON objects.</summary>
    private static IEnumerable<string> SplitJsonObjects(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        yield return text[start..(i + 1)];
                        start = -1;
                    }
                    break;
            }
        }
    }

    private static string Q(string value) => ManagementService.ShellQuote(value);

    private static string TrimSlash(string value) => value.TrimEnd('/', '\\');

    private static bool UrlsEquivalent(string a, string b)
    {
        static string Normalize(string value) => value.Trim().TrimEnd('/')
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
