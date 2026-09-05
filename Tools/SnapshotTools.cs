using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class SnapshotTools
{
    [McpServerTool(Name = "wp_create_snapshot"),
     Description("Take a snapshot of a site: a database dump plus a wp-content archive, with a manifest recording the WordPress version, active theme and plugin list. Stored on the site's own host under Snapshots:Directory. Take one before any update, migration or risky change. Requires write mode.")]
    public static async Task<string> CreateSnapshot(
        ManagementService management,
        SnapshotService snapshots,
        [Description("Optional label describing why the snapshot was taken, e.g. 'before WooCommerce update'.")] string? label = null,
        [Description("Include wp-content/uploads. Defaults to the Snapshots:IncludeUploads setting; turn off for a much smaller snapshot that excludes media.")] bool? includeUploads = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_create_snapshot", ct);
        var manifest = await snapshots.CreateAsync(target, label, includeUploads, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            snapshot = manifest,
            totalBytes = manifest.DatabaseBytes + manifest.ContentBytes,
            note = manifest.IncludesUploads
                ? null
                : "Uploads were excluded, so restoring this snapshot will not bring media files back.",
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_list_snapshots"),
     Description("List the snapshots held for a site, newest first, with their labels, sizes and what each captured.")]
    public static async Task<string> ListSnapshots(
        ManagementService management,
        SnapshotService snapshots,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_list_snapshots");
        var manifests = await snapshots.ListAsync(target, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            directory = snapshots.RootFor(target),
            count = manifests.Count,
            retention = snapshots.Options.MaxSnapshots > 0
                ? $"Keeping the newest {snapshots.Options.MaxSnapshots}; older ones are pruned after each capture."
                : "Pruning is disabled (Snapshots:MaxSnapshots=0).",
            snapshots = manifests,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_get_snapshot"),
     Description("Get one snapshot's manifest and verify its files are still present and non-empty on the host.")]
    public static async Task<string> GetSnapshot(
        ManagementService management,
        SnapshotService snapshots,
        [Description("Snapshot id from wp_list_snapshots, e.g. `20260905-143000`.")] string id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_get_snapshot");
        var manifest = await snapshots.GetAsync(target, id, ct);

        var databaseOk = await FileUsableAsync(management, target, manifest.DatabaseFile, ct);
        var contentOk = string.IsNullOrWhiteSpace(manifest.ContentFile)
            || await FileUsableAsync(management, target, manifest.ContentFile!, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            snapshot = manifest,
            integrity = new
            {
                databaseFilePresent = databaseOk,
                contentFilePresent = contentOk,
                restorable = databaseOk && contentOk,
            },
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_restore_snapshot"),
     Description("Restore a snapshot over the live site: import the database dump and extract the wp-content archive. This overwrites current content. Requires Snapshots:AllowRestore=true, write mode, and confirm set to the snapshot id. A safety snapshot is taken first unless skipped.")]
    public static async Task<string> RestoreSnapshot(
        ManagementService management,
        SnapshotService snapshots,
        [Description("Snapshot id to restore, e.g. `20260905-143000`.")] string id,
        [Description("Must equal the snapshot id, confirming you intend to overwrite the live site.")] string confirm,
        [Description("Skip the automatic pre-restore safety snapshot. Leave false unless disk space forces it.")] bool skipSafetySnapshot = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_restore_snapshot", ct);

        if (!snapshots.Options.AllowRestore)
            throw new McpException(
                "MCP tool 'wp_restore_snapshot' requires Snapshots:AllowRestore=true. Restoring overwrites the live site's " +
                "database and wp-content, so it is gated separately from other writes.");

        if (!string.Equals(confirm, id, StringComparison.OrdinalIgnoreCase))
            throw new McpException(
                $"wp_restore_snapshot: confirm must equal the snapshot id ('{id}') to proceed. This restore would overwrite " +
                $"the live database and wp-content of endpoint '{target.Name}'.");

        var (manifest, safety) = await snapshots.RestoreAsync(target, id, skipSafetySnapshot, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            restored = manifest.Id,
            restoredFrom = manifest.CreatedUtc,
            siteUrl = manifest.SiteUrl,
            wordPressVersion = manifest.WordPressVersion,
            safetySnapshot = safety?.Id,
            note = safety is null
                ? "No safety snapshot was taken; the previous state is not recoverable through this server."
                : $"The previous state was captured as snapshot '{safety.Id}' before restoring.",
            uploadsRestored = manifest.IncludesUploads,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_delete_snapshot"),
     Description("Permanently delete a stored snapshot and its files. Requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteSnapshot(
        ManagementService management,
        SnapshotService snapshots,
        [Description("Snapshot id to delete.")] string id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_delete_snapshot", ct);
        management.EnsureDeleteAllowed(target, "wp_delete_snapshot");
        await snapshots.DeleteAsync(target, id, ct);
        return JsonSerializer.Serialize(new { endpoint = target.Name, deleted = id }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_export_content"),
     Description("Export the site's content as a WXR (WordPress eXtended RSS) file — the portable format for moving posts, pages and media between WordPress sites. Requires write mode (it writes a file on the host).")]
    public static async Task<string> ExportContent(
        ManagementService management,
        [Description("Directory on the managed host to write the export into. Defaults to the endpoint's install path.")] string? directory = null,
        [Description("Restrict to a post type, e.g. `post` or `page`. Omit to export everything.")] string? postType = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_export_content", ct);
        var destination = string.IsNullOrWhiteSpace(directory) ? target.Path : directory!;

        var args = new List<string> { "export", $"--dir={destination}" };
        if (!string.IsNullOrWhiteSpace(postType)) args.Add($"--post_type={postType}");

        var result = await management.RunOrThrowAsync(target, args, "wp_export_content", ct, longRunning: true);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            directory = destination,
            postType = postType ?? "all",
            output = result.Output,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_import_content"),
     Description("Import a WXR export file into the site, creating any missing authors. Requires the wordpress-importer plugin (install it with wp_install_plugin). Requires write mode.")]
    public static async Task<string> ImportContent(
        ManagementService management,
        [Description("Path to the WXR file on the managed host.")] string filePath,
        [Description("How to map authors: create (default), mapping.csv path, or skip.")] string authors = "create",
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_import_content", ct);

        var result = await management.RunAsync(target, new[] { "import", filePath, $"--authors={authors}" }, ct, longRunning: true);
        if (!result.Success && result.StdErr.Contains("not a registered wp command", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                "wp_import_content requires the WordPress Importer plugin. Install it first: " +
                "wp_install_plugin with slug 'wordpress-importer'.");
        }

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            filePath,
            succeeded = result.Success,
            output = result.Output,
            error = result.Success ? null : ManagementService.Describe(result),
        }, JsonOpts.Default);
    }

    private static async Task<bool> FileUsableAsync(ManagementService management, ManagementService.Target target, string path, CancellationToken ct)
    {
        var result = await management.RunShellAsync(target, $"test -s {ManagementService.ShellQuote(path)} && echo ok", ct, longRunning: false);
        return result.Output.Trim() == "ok";
    }
}
