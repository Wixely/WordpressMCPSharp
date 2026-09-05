using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Building a site from nothing, and editing wp-config.php — the parts of the lifecycle that exist
/// before (or underneath) the REST API.
/// </summary>
[McpServerToolType]
public static class ProvisioningTools
{
    /// <summary>wp-config values that carry credentials or invalidate every session if changed.</summary>
    private static readonly HashSet<string> ProtectedConstants = new(StringComparer.OrdinalIgnoreCase)
    {
        "DB_NAME", "DB_USER", "DB_PASSWORD", "DB_HOST", "DB_CHARSET", "DB_COLLATE",
        "AUTH_KEY", "SECURE_AUTH_KEY", "LOGGED_IN_KEY", "NONCE_KEY",
        "AUTH_SALT", "SECURE_AUTH_SALT", "LOGGED_IN_SALT", "NONCE_SALT",
    };

    [McpServerTool(Name = "wp_provision_site"),
     Description("Build a new WordPress site from nothing on a management endpoint: download core, write wp-config.php, and run the install. Idempotent — it reports and skips steps already done. Requires Wordpress:ReadOnly=false, Management:AllowCliManagement=true and Management:AllowProvisioning=true, plus confirm=true.")]
    public static async Task<string> ProvisionSite(
        ManagementService management,
        [Description("Site URL the new install will answer on, e.g. https://newsite.example.com.")] string url,
        [Description("Site title.")] string title,
        [Description("Administrator username to create.")] string adminUser,
        [Description("Administrator password.")] string adminPassword,
        [Description("Administrator email address.")] string adminEmail,
        [Description("Database name.")] string dbName,
        [Description("Database user.")] string dbUser,
        [Description("Database password.")] string dbPassword,
        [Description("Must be set to true. Provisioning writes files and creates database tables at the endpoint's configured path.")] bool confirm = false,
        [Description("Database host. Defaults to localhost.")] string dbHost = "localhost",
        [Description("Table prefix. Defaults to wp_.")] string dbPrefix = "wp_",
        [Description("WordPress version to install. Defaults to the latest release.")] string? version = null,
        [Description("Site locale, e.g. en_GB.")] string? locale = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_provision_site");

        if (target.Safety.ReadOnly)
            throw new McpException(
                $"MCP tool 'wp_provision_site' is blocked for endpoint '{target.Name}'. Set Wordpress:ReadOnly=false to allow writes.");

        if (!management.Options.AllowProvisioning)
            throw new McpException(
                "MCP tool 'wp_provision_site' requires Management:AllowProvisioning=true (in addition to Management:AllowCliManagement=true and Wordpress:ReadOnly=false).");

        if (!confirm)
            throw new McpException(
                $"MCP tool 'wp_provision_site' needs confirm=true. It will download WordPress into '{target.Path}' on endpoint " +
                $"'{target.Name}' and create tables in database '{dbName}'. Re-run with confirm=true to proceed.");

        var steps = new List<object>();

        // 1. Core files.
        var installed = await management.RunAsync(target, new[] { "core", "is-installed" }, ct);
        var coreFiles = await management.RunAsync(target, new[] { "core", "version" }, ct);
        if (coreFiles.Success)
        {
            steps.Add(new { step = "core download", status = "skipped", detail = $"WordPress {coreFiles.Output} files are already present." });
        }
        else
        {
            var args = new List<string> { "core", "download" };
            if (!string.IsNullOrWhiteSpace(version)) args.Add($"--version={version}");
            if (!string.IsNullOrWhiteSpace(locale)) args.Add($"--locale={locale}");
            var download = await management.RunOrThrowAsync(target, args, "wp_provision_site", ct, longRunning: true);
            steps.Add(new { step = "core download", status = "done", detail = download.Output });
        }

        // 2. wp-config.php.
        var hasConfig = await management.RunAsync(target, new[] { "config", "path" }, ct);
        if (hasConfig.Success)
        {
            steps.Add(new { step = "wp-config.php", status = "skipped", detail = $"Already present at {hasConfig.Output}." });
        }
        else
        {
            var configArgs = new List<string>
            {
                "config", "create",
                $"--dbname={dbName}", $"--dbuser={dbUser}", $"--dbpass={dbPassword}",
                $"--dbhost={dbHost}", $"--dbprefix={dbPrefix}", "--skip-check",
            };
            await management.RunOrThrowAsync(target, configArgs, "wp_provision_site", ct);
            steps.Add(new { step = "wp-config.php", status = "done", detail = "Created." });
        }

        // 3. Install.
        if (installed.Success)
        {
            steps.Add(new { step = "core install", status = "skipped", detail = "WordPress is already installed in this database." });
        }
        else
        {
            var installArgs = new List<string>
            {
                "core", "install",
                $"--url={url}", $"--title={title}",
                $"--admin_user={adminUser}", $"--admin_password={adminPassword}", $"--admin_email={adminEmail}",
                "--skip-email",
            };
            await management.RunOrThrowAsync(target, installArgs, "wp_provision_site", ct, longRunning: true);
            steps.Add(new { step = "core install", status = "done", detail = $"Installed at {url}." });
        }

        var finalVersion = await management.RunAsync(target, new[] { "core", "version" }, ct);
        var siteUrl = await management.RunAsync(target, new[] { "option", "get", "siteurl" }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            path = target.Path,
            version = finalVersion.Output,
            siteUrl = siteUrl.Output,
            steps,
            nextSteps = new[]
            {
                "Set pretty permalinks with wp_set_permalink_structure so /wp-json/ resolves.",
                $"Create an application password for '{adminUser}' and add a RestApi block to endpoint '{target.Name}' to enable the content tools.",
                "Run wp_test_endpoint to confirm both channels work.",
            },
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_config_get"),
     Description("Read constants and variables from wp-config.php (WP_DEBUG, WP_ENVIRONMENT_TYPE, table prefix, and so on). Database passwords and authentication salts are redacted.")]
    public static async Task<string> ConfigGet(
        ManagementService management,
        [Description("Constant or variable name, e.g. `WP_DEBUG`. Omit to list everything.")] string? name = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = management.Resolve(site, "wp_config_get");

        if (!string.IsNullOrWhiteSpace(name))
        {
            if (IsSecret(name!))
                throw new McpException($"wp_config_get will not return '{name}' because it holds a credential or authentication salt.");

            var single = await management.RunAsync(target, new[] { "config", "get", name! }, ct);
            return JsonSerializer.Serialize(new
            {
                endpoint = target.Name,
                name,
                value = single.Success ? single.Output : null,
                found = single.Success,
                error = single.Success ? null : ManagementService.Describe(single),
            }, JsonOpts.Default);
        }

        var result = await management.RunOrThrowAsync(target, new[] { "config", "list", "--format=json" }, "wp_config_get", ct);
        var parsed = MaintenanceTools.TryParseJson(result.Output);

        var entries = new List<object>();
        if (parsed is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                // `wp config list` names the field "name"; reading the wrong key here would leave every
                // value unredacted, so fall back across both spellings and redact unknown names too.
                var key = (entry.TryGetProperty("name", out var n) ? Scalar(n) : null)
                    ?? (entry.TryGetProperty("key", out var k) ? Scalar(k) : null);
                var value = entry.TryGetProperty("value", out var v) ? Scalar(v) : null;
                var redact = string.IsNullOrWhiteSpace(key) || IsSecret(key!);

                entries.Add(new
                {
                    name = key ?? "(unknown)",
                    type = entry.TryGetProperty("type", out var t) ? Scalar(t) : null,
                    value = redact ? "(redacted)" : value,
                });
            }
        }

        return JsonSerializer.Serialize(new { endpoint = target.Name, entries }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_config_set"),
     Description("Set a constant in wp-config.php — for example WP_DEBUG, WP_DEBUG_LOG or WP_ENVIRONMENT_TYPE (which is what lets application passwords work on a local HTTP site). Database credentials and authentication salts are refused. Requires write mode.")]
    public static async Task<string> ConfigSet(
        ManagementService management,
        [Description("Constant name, e.g. `WP_DEBUG`.")] string name,
        [Description("Value to set, e.g. `true`, `false`, `local`.")] string value,
        [Description("Value type: constant (default) or variable.")] string type = "constant",
        [Description("Raw PHP value (so `true` is written as a boolean rather than the string \"true\"). Defaults to true for true/false/null/numbers.")] bool? raw = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_config_set", ct);

        if (IsSecret(name))
            throw new McpException(
                $"wp_config_set will not change '{name}': it is a database credential or authentication salt. " +
                "Change those directly on the host — altering salts logs every user out.");

        var useRaw = raw ?? IsRawLiteral(value);
        var args = new List<string> { "config", "set", name, value, $"--type={type}" };
        if (useRaw) args.Add("--raw");

        await management.RunOrThrowAsync(target, args, "wp_config_set", ct);
        var confirmed = await management.RunAsync(target, new[] { "config", "get", name }, ct);

        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            name,
            value = confirmed.Success ? confirmed.Output : value,
            raw = useRaw,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_cli"),
     Description("Run an arbitrary WP-CLI command on an endpoint — the escape hatch for anything without a dedicated tool. Requires Management:AllowArbitraryCli=true as well as write mode, and refuses the commands listed in Management:DeniedCliCommands (eval, eval-file, shell by default).")]
    public static async Task<string> Cli(
        ManagementService management,
        [Description("WP-CLI arguments as a JSON array, without the leading `wp`. Example: `[\"plugin\",\"list\",\"--status=active\"]`.")] string argumentsJson,
        [Description("Treat this as a long-running command (uses Management:LongCommandTimeoutSeconds).")] bool longRunning = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var target = await management.ResolveForWriteAsync(site, "wp_cli", ct);

        if (!management.Options.AllowArbitraryCli)
            throw new McpException(
                "MCP tool 'wp_cli' requires Management:AllowArbitraryCli=true (in addition to Management:AllowCliManagement=true and Wordpress:ReadOnly=false).");

        var arguments = ParseArguments(argumentsJson);
        if (arguments.Count == 0)
            throw new McpException("wp_cli: argumentsJson must contain at least one argument, e.g. [\"plugin\",\"list\"].");

        var denied = management.Options.DeniedCliCommands ?? new List<string>();
        if (denied.Any(d => string.Equals(d, arguments[0], StringComparison.OrdinalIgnoreCase)))
            throw new McpException(
                $"wp_cli: the '{arguments[0]}' command is on the deny-list (Management:DeniedCliCommands) because it executes arbitrary PHP or opens a shell.");

        var result = await management.RunAsync(target, arguments, ct, longRunning);
        return JsonSerializer.Serialize(new
        {
            endpoint = target.Name,
            command = "wp " + string.Join(' ', arguments),
            exitCode = result.ExitCode,
            succeeded = result.Success,
            stdout = Truncate(result.StdOut),
            stderr = Truncate(result.StdErr),
        }, JsonOpts.Default);
    }

    private static List<string> ParseArguments(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            return parsed ?? new List<string>();
        }
        catch (JsonException ex)
        {
            throw new McpException($"argumentsJson must be a JSON array of strings: {ex.Message}");
        }
    }

    /// <summary>wp-config values come back as strings, booleans or numbers; render any of them as text.</summary>
    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.ToString(),
    };

    private static bool IsSecret(string name) =>
        ProtectedConstants.Contains(name)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SALT", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase);

    private static bool IsRawLiteral(string value) =>
        value is "true" or "false" or "null" || long.TryParse(value, out _);

    private static string? Truncate(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 8000 ? trimmed[..8000] + "…(truncated)" : trimmed;
    }
}
