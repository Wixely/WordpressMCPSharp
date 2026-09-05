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

        var management = endpoint.HasManagement
            ? new
            {
                configured = true,
                mode = endpoint.ManagementMode,
                path = EndpointRegistry.ManagementPath(endpoint),
                tested = false,
                detail = "Connectivity testing for the WP-CLI management channel arrives with the management tools; the configuration is present and valid.",
            }
            : new
            {
                configured = false,
                mode = "none",
                path = (string?)null,
                tested = false,
                detail = $"Endpoint '{name}' has no management block, so WP-CLI tools (core/plugin updates, backups, provisioning) are unavailable on it. " +
                         $"Configure exactly one of Endpoints:{name}:Ssh, :Local or :Docker to enable them.",
            };

        var summary = (endpoint.HasRest, restWorking, endpoint.HasManagement) switch
        {
            (true, true, true) => $"Endpoint '{name}' is working: REST authenticated, management channel configured ({endpoint.ManagementMode}).",
            (true, true, false) => $"Endpoint '{name}' is working over REST. No management channel, so WP-CLI tools (updates, backups, provisioning) are unavailable on it.",
            (true, false, _) => $"Endpoint '{name}' has REST configured but it is not usable — see the failing findings below.",
            (false, _, true) => $"Endpoint '{name}' is management-only ({endpoint.ManagementMode}); REST tools are unavailable on it.",
            _ => $"Endpoint '{name}' has no usable channel configured.",
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
            management,
            warnings = registry.ConfigurationWarnings.Where(w => w.Contains($"'{name}'", StringComparison.OrdinalIgnoreCase)).ToList(),
        }, JsonOpts.Default);
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
