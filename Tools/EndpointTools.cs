using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

[McpServerToolType]
public static class EndpointTools
{
    [McpServerTool(Name = "wp_list_endpoints"),
     Description("List the WordPress sites this server manages: endpoint names, which channels each has (REST and/or WP-CLI management), URLs, effective safety settings, and any configuration warnings. Call this first to discover valid values for the `site` argument that every other tool accepts.")]
    public static string ListEndpoints(
        EndpointRegistry registry,
        CancellationToken ct = default)
    {
        var endpoints = registry.All.Select(entry =>
        {
            var (name, endpoint) = entry;
            var safety = registry.SafetyFor(endpoint);
            return new
            {
                name,
                description = endpoint.Description,
                url = endpoint.EffectiveUrl,
                channels = new
                {
                    rest = endpoint.HasRest,
                    management = endpoint.ManagementMode,
                },
                restUsername = endpoint.RestApi?.Username,
                managementPath = EndpointRegistry.ManagementPath(endpoint),
                effectiveSafety = new
                {
                    readOnly = safety.ReadOnly,
                    allowDelete = safety.AllowDelete,
                    allowPluginInstall = safety.AllowPluginInstall,
                    restrictedByEndpointOverride = safety.ReadOnlyFromEndpoint || safety.AllowDeleteFromEndpoint || safety.AllowPluginInstallFromEndpoint,
                },
            };
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            count = endpoints.Count,
            defaultSite = ResolveDefaultDescription(registry),
            legacyConfiguration = registry.LegacyMapped,
            endpoints,
            warnings = registry.ConfigurationWarnings,
            hint = endpoints.Count == 0
                ? "No sites configured. Run wp_setup_probe with a site URL to generate a configuration block, or wp_setup_instructions for the full walkthrough."
                : "Pass site=<name> to any tool to target a specific site. Run wp_test_endpoint to verify one end to end.",
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_test_endpoint"),
     Description("Verify a configured endpoint end to end and explain anything that is broken: REST reachability, redirects, TLS, WordPress identity, authentication and the account's capabilities. Use this after configuring a site, or when a tool starts failing, to find out which part of the setup is at fault.")]
    public static async Task<string> TestEndpoint(
        EndpointRegistry registry,
        SetupDiagnosticsService setup,
        ManagementService management,
        [Description("Endpoint name from wp_list_endpoints. Omit to use DefaultSite (or the only configured endpoint).")] string? site = null,
        CancellationToken ct = default)
    {
        registry.Options.EnsureSetupDiagnosticsEnabled();
        var (name, endpoint) = registry.Resolve(site, "wp_test_endpoint");
        var safety = registry.SafetyFor(endpoint);

        object? rest = null;
        var restWorking = false;
        var problems = new List<string>();

        if (endpoint.HasRest)
        {
            var probe = await setup.ProbeAsync(
                endpoint.RestApi!.BaseUrl,
                endpoint.RestApi.Username,
                endpoint.RestApi.ApplicationPassword,
                endpoint.RestApi.AllowInvalidCertificate,
                ct);

            var authenticated = probe.Authentication.GetValueOrDefault("authenticated") as bool? ?? false;
            restWorking = probe.IsWordPress && authenticated;
            problems.AddRange(probe.Findings.Where(f => f.Status == "fail").Select(f => $"REST: {f.Detail}"));

            // The configured BaseUrl should already be the site's real address; a redirect breaks authentication.
            if (!string.IsNullOrWhiteSpace(probe.FinalUrl)
                && !string.Equals(probe.FinalUrl!.TrimEnd('/'), endpoint.RestApi.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"REST: BaseUrl '{endpoint.RestApi.BaseUrl}' resolves to '{probe.FinalUrl}'. " +
                             $"Set Endpoints:{name}:RestApi:BaseUrl to the resolved address.");
            }

            rest = new
            {
                configured = true,
                working = restWorking,
                authenticated,
                resolvedUrl = probe.FinalUrl,
                isWordPress = probe.IsWordPress,
                findings = probe.Findings,
                site = probe.Site,
                tls = probe.Tls,
                authentication = probe.Authentication,
                namespaces = probe.Namespaces,
            };
        }
        else
        {
            rest = new
            {
                configured = false,
                detail = $"Endpoint '{name}' has no RestApi block, so REST tools are unavailable on it.",
                fix = $"Set Endpoints:{name}:RestApi:BaseUrl, :Username and :ApplicationPassword to enable them.",
            };
        }

        object managementResult;
        var managementWorking = false;

        if (!endpoint.HasManagement)
        {
            managementResult = new
            {
                configured = false,
                enabled = management.Options.AllowCliManagement,
                mode = "none",
                detail = $"Endpoint '{name}' has no management block, so WP-CLI tools (core/plugin updates, backups, provisioning) are unavailable on it. " +
                         $"Configure exactly one of Endpoints:{name}:Ssh, :Local or :Docker to enable them.",
            };
        }
        else if (!management.Options.AllowCliManagement)
        {
            managementResult = new
            {
                configured = true,
                enabled = false,
                mode = endpoint.ManagementMode,
                detail = "The management channel is configured but disabled. Set Management:AllowCliManagement=true to enable the WP-CLI tools.",
            };
            problems.Add("Management: channel configured but Management:AllowCliManagement=false.");
        }
        else
        {
            (managementResult, managementWorking) = await TestManagementAsync(management, name, problems, ct);
        }

        var summary = (endpoint.HasRest && restWorking, endpoint.HasManagement && managementWorking) switch
        {
            (true, true) => $"Endpoint '{name}' is fully working: REST authenticated and WP-CLI reachable ({endpoint.ManagementMode}).",
            (true, false) when endpoint.HasManagement => $"Endpoint '{name}' works over REST, but its management channel has a problem — see below.",
            (true, false) => $"Endpoint '{name}' is working over REST. No management channel, so WP-CLI tools (updates, backups, provisioning) are unavailable on it.",
            (false, true) when endpoint.HasRest => $"Endpoint '{name}' has a working management channel, but its REST configuration is not usable — see the failing findings.",
            (false, true) => $"Endpoint '{name}' is management-only ({endpoint.ManagementMode}) and WP-CLI is reachable; REST tools are unavailable on it.",
            _ => $"Endpoint '{name}' is not usable — see the problems listed below.",
        };

        return JsonSerializer.Serialize(new
        {
            endpoint = name,
            summary,
            url = endpoint.EffectiveUrl,
            problems,
            effectiveSafety = new
            {
                readOnly = safety.ReadOnly,
                allowDelete = safety.AllowDelete,
                allowPluginInstall = safety.AllowPluginInstall,
            },
            rest,
            management = managementResult,
            warnings = registry.ConfigurationWarnings.Where(w => w.Contains($"'{name}'", StringComparison.OrdinalIgnoreCase)).ToList(),
        }, JsonOpts.Default);
    }

    /// <summary>
    /// Exercise the management channel the way the tools do: reach WP-CLI, confirm the path holds a
    /// WordPress install, and check that install is the site the endpoint claims.
    /// </summary>
    private static async Task<(object Result, bool Working)> TestManagementAsync(
        ManagementService management, string name, List<string> problems, CancellationToken ct)
    {
        ManagementService.Target target;
        try
        {
            target = management.Resolve(name, "wp_test_endpoint");
        }
        catch (ModelContextProtocol.McpException ex)
        {
            problems.Add("Management: " + ex.Message);
            return (new { configured = true, enabled = true, working = false, detail = ex.Message }, false);
        }

        var version = await management.RunAsync(target, new[] { "core", "version" }, ct);
        if (!version.Success)
        {
            var detail = $"WP-CLI could not read the WordPress install at '{target.Path}'. {ManagementService.Describe(version)}";
            problems.Add("Management: " + detail);
            return (new
            {
                configured = true,
                enabled = true,
                working = false,
                mode = target.Mode,
                path = target.Path,
                detail,
                fix = $"Check Endpoints:{name}:{target.Mode}:Path points at the directory containing wp-config.php, and that WP-CLI is installed.",
            }, false);
        }

        var siteUrl = await management.RunAsync(target, new[] { "option", "get", "siteurl" }, ct);
        var expected = target.Endpoint.EffectiveUrl;
        var identityOk = true;
        string? identityDetail = null;

        if (string.IsNullOrWhiteSpace(expected))
        {
            identityOk = false;
            identityDetail = $"No Url or RestApi:BaseUrl is set, so the identity cross-check cannot run. Set Endpoints:{name}:Url to '{siteUrl.Output}'.";
            problems.Add("Management: " + identityDetail);
        }
        else if (!UrlsEquivalent(siteUrl.Output, expected!))
        {
            identityOk = false;
            identityDetail = $"The install at '{target.Path}' reports '{siteUrl.Output}', but this endpoint expects '{expected}'. " +
                             "Mutating tools will refuse to run until this is resolved — the path may point at a different site on this host.";
            problems.Add("Management: " + identityDetail);
        }

        return (new
        {
            configured = true,
            enabled = true,
            working = identityOk,
            mode = target.Mode,
            path = target.Path,
            wordPressVersion = version.Output,
            siteUrl = siteUrl.Output,
            identityCheck = identityOk ? "passed" : "failed",
            detail = identityDetail ?? $"WP-CLI reachable; WordPress {version.Output} at {siteUrl.Output}.",
        }, identityOk);
    }

    private static bool UrlsEquivalent(string a, string b)
    {
        static string Normalize(string value) => value.Trim().TrimEnd('/')
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDefaultDescription(EndpointRegistry registry)
    {
        try
        {
            var (name, _) = registry.Resolve(null, "wp_list_endpoints");
            return name;
        }
        catch (ModelContextProtocol.McpException)
        {
            return "(none — pass site=<name> explicitly)";
        }
    }
}
