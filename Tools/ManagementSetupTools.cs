using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Configuration;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Onboarding for the management channel: check a host works, and discover the WordPress installs
/// on it so endpoints are configured from what is actually there rather than from guessed paths.
/// </summary>
[McpServerToolType]
public static class ManagementSetupTools
{
    /// <summary>Where multi-site hosts usually keep their installs.</summary>
    private const string DefaultSearchRoots = "/var/www /srv/www /home /usr/share/nginx";

    [McpServerTool(Name = "wp_setup_probe_ssh"),
     Description("Check an SSH host for WordPress management and discover the sites on it. Verifies the connection, whether WP-CLI is installed, and searches common web roots for wp-config.php — reporting each install's path, WordPress version and site URL. Use this to configure endpoints on a host serving several sites, so each path is verified rather than guessed. Takes connection details as parameters so it works before any endpoint exists, and requires Management:AllowCliManagement=true because it uses the WP-CLI channel.")]
    public static async Task<string> SetupProbeSsh(
        EndpointRegistry registry,
        ManagementService management,
        [Description("SSH host name or IP.")] string host,
        [Description("SSH username.")] string username,
        [Description("Path to a private key file on this server. Supply this or password.")] string? privateKeyPath = null,
        [Description("SSH password, or the private key's passphrase.")] string? password = null,
        [Description("SSH port. Defaults to 22.")] int port = 22,
        [Description("Specific install path to check, e.g. /var/www/example.com. Omit to search the common web roots.")] string? path = null,
        [Description("Space-separated directories to search. Defaults to /var/www /srv/www /home /usr/share/nginx.")] string? searchRoots = null,
        [Description("WP-CLI executable on the host. Defaults to `wp`.")] string wpCliPath = "wp",
        CancellationToken ct = default)
    {
        registry.Options.EnsureSetupDiagnosticsEnabled();
        // This runs commands over SSH, so it sits behind the same master gate as every other CLI tool.
        management.EnsureCliEnabled("wp_setup_probe_ssh");

        var probeEndpoint = new EndpointOptions
        {
            Ssh = new SshManagementOptions
            {
                Host = host,
                Port = port,
                Username = username,
                PrivateKeyPath = privateKeyPath,
                Password = password,
                Path = path ?? "/",
                WpCliPath = wpCliPath,
            },
        };

        var findings = new List<object>();

        // 1. Connectivity.
        ManagementService.CliResult whoami;
        try
        {
            whoami = await management.RunProbeShellAsync(probeEndpoint, "whoami && uname -sr", "wp_setup_probe_ssh", ct);
        }
        catch (McpException ex)
        {
            return JsonSerializer.Serialize(new
            {
                host,
                connected = false,
                summary = "Could not connect over SSH.",
                findings = new[]
                {
                    new { check = "ssh", status = "fail", detail = ex.Message, fix = "Check the host, port, username and key/password, and that this server can reach the host." },
                },
            }, JsonOpts.Default);
        }

        findings.Add(new { check = "ssh", status = "ok", detail = $"Connected as {whoami.Output.Replace('\n', ' ').Trim()}.", fix = (string?)null });

        // 2. WP-CLI availability.
        var wpVersion = await management.RunProbeShellAsync(
            probeEndpoint, $"{ManagementService.ShellQuote(wpCliPath)} --version 2>&1 || echo MISSING", "wp_setup_probe_ssh", ct);
        var hasWpCli = !wpVersion.Output.Contains("MISSING", StringComparison.OrdinalIgnoreCase)
                       && wpVersion.Output.Contains("WP-CLI", StringComparison.OrdinalIgnoreCase);
        findings.Add(new
        {
            check = "wp-cli",
            status = hasWpCli ? "ok" : "fail",
            detail = hasWpCli ? wpVersion.Output.Split('\n').FirstOrDefault()?.Trim() ?? "present" : $"WP-CLI was not found at '{wpCliPath}'.",
            fix = hasWpCli ? null : "Install WP-CLI on the host (https://wp-cli.org/#installing), or set the endpoint's WpCliPath to its full path.",
        });

        // 3. Discover installs. Every caller-supplied value is quoted before it reaches the shell —
        // these come from the MCP client, not from configuration.
        var roots = string.IsNullOrWhiteSpace(searchRoots) ? DefaultSearchRoots : searchRoots!;
        var quotedRoots = string.Join(' ', roots
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ManagementService.ShellQuote));

        var searchCommand = path is not null
            ? $"test -f {ManagementService.ShellQuote(path.TrimEnd('/') + "/wp-config.php")} && echo {ManagementService.ShellQuote(path.TrimEnd('/'))}"
            : $"find {quotedRoots} -maxdepth 4 -name wp-config.php -not -path '*/wp-content/*' 2>/dev/null | head -50 | xargs -r -n1 dirname";

        var found = await management.RunProbeShellAsync(probeEndpoint, searchCommand + " || true", "wp_setup_probe_ssh", ct);
        var installPaths = found.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.StartsWith('/'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        findings.Add(new
        {
            check = "installs",
            status = installPaths.Count > 0 ? "ok" : "warn",
            detail = installPaths.Count > 0
                ? $"Found {installPaths.Count} WordPress installation(s)."
                : "No wp-config.php found in the searched directories.",
            fix = installPaths.Count > 0 ? null : $"Searched: {(path ?? roots)}. Pass `path` for a specific install, or widen `searchRoots`.",
        });

        // 4. Identify each install.
        var sites = new List<object>();
        var suggested = new Dictionary<string, object?>();

        foreach (var installPath in installPaths)
        {
            var siteEndpoint = new EndpointOptions
            {
                Ssh = new SshManagementOptions
                {
                    Host = host, Port = port, Username = username,
                    PrivateKeyPath = privateKeyPath, Password = password,
                    Path = installPath, WpCliPath = wpCliPath,
                },
            };

            string? siteUrl = null, version = null, title = null;
            if (hasWpCli)
            {
                siteUrl = (await management.RunProbeAsync(siteEndpoint, new[] { "option", "get", "siteurl" }, "wp_setup_probe_ssh", ct)).Output;
                version = (await management.RunProbeAsync(siteEndpoint, new[] { "core", "version" }, "wp_setup_probe_ssh", ct)).Output;
                title = (await management.RunProbeAsync(siteEndpoint, new[] { "option", "get", "blogname" }, "wp_setup_probe_ssh", ct)).Output;
            }

            var name = SuggestName(siteUrl, installPath);
            sites.Add(new
            {
                path = installPath,
                siteUrl = string.IsNullOrWhiteSpace(siteUrl) ? null : siteUrl,
                title = string.IsNullOrWhiteSpace(title) ? null : title,
                wordPressVersion = string.IsNullOrWhiteSpace(version) ? null : version,
                suggestedEndpointName = name,
            });

            suggested[name] = new Dictionary<string, object?>
            {
                ["Ssh"] = new Dictionary<string, object?>
                {
                    ["Host"] = host,
                    ["Port"] = port,
                    ["Username"] = username,
                    ["PrivateKeyPath"] = privateKeyPath ?? "<path to private key>",
                    ["Path"] = installPath,
                },
                ["Url"] = string.IsNullOrWhiteSpace(siteUrl) ? "<site url>" : siteUrl,
            };
        }

        return JsonSerializer.Serialize(new
        {
            host,
            connected = true,
            summary = installPaths.Count switch
            {
                0 => "Connected, but no WordPress installations were found in the searched directories.",
                1 => $"Connected. Found 1 WordPress installation{(hasWpCli ? string.Empty : " (install WP-CLI to manage it)")}.",
                _ => $"Connected. Found {installPaths.Count} WordPress installations — configure one endpoint per site so each path is targeted explicitly.",
            },
            findings,
            sites,
            suggestedConfiguration = new Dictionary<string, object?>
            {
                ["Endpoints"] = suggested,
                ["_note"] = "Each endpoint's Url is used to verify the install before any change is written, which prevents a stale path from modifying the wrong site on this host. Add a RestApi block to an endpoint to enable the content tools as well.",
            },
            nextSteps = new[]
            {
                "Add the suggested Endpoints block to WordpressMCPSharp.Local.json and restart the server.",
                "Set Management:AllowCliManagement=true to enable the WP-CLI tools.",
                "Run wp_test_endpoint for each new endpoint to confirm the channel works.",
            },
        }, JsonOpts.Default);
    }

    private static string SuggestName(string? siteUrl, string path)
    {
        var candidate = siteUrl;
        if (!string.IsNullOrWhiteSpace(candidate) && Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            var label = host.Split('.').FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(label)) return Clean(label!);
        }

        var directory = path.TrimEnd('/').Split('/').LastOrDefault() ?? "site";
        return Clean(directory);
    }

    private static string Clean(string value)
    {
        var cleaned = new string(value.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
        return cleaned.Length == 0 ? "site" : cleaned;
    }
}
