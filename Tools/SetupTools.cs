using System.ComponentModel;
using System.Text.Json;
using WordpressMCPSharp.Services;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// Onboarding tools. These work before anything is configured — they take connection details as
/// parameters rather than reading the registry — so they can be used to work out what to configure.
/// </summary>
[McpServerToolType]
public static class SetupTools
{
    [McpServerTool(Name = "wp_setup_probe"),
     Description("Diagnose a WordPress site and produce the configuration needed to manage it. Works with no server configuration: pass a URL (credentials optional but recommended). Checks DNS, reachability and redirects, TLS certificate validity/expiry/hostname match, that the target is WordPress, which REST addressing works, available REST namespaces (including WooCommerce), and — with credentials — whether the application password is accepted and which tool groups the account can drive. Returns findings with fixes plus a ready-to-paste Endpoints block. Use this first when setting up.")]
    public static async Task<string> SetupProbe(
        SetupDiagnosticsService setup,
        EndpointRegistry registry,
        [Description("Site URL, e.g. https://example.com/ (the site root, not /wp-admin or /wp-json). A bare host is treated as https://.")] string url,
        [Description("Optional WordPress username to test authentication with.")] string? username = null,
        [Description("Optional application password for that user (Users → Profile → Application Passwords). Spaces are allowed; it is never echoed back.")] string? applicationPassword = null,
        [Description("Set true to continue past TLS certificate errors when probing a self-signed homelab site.")] bool allowInvalidCertificate = false,
        CancellationToken ct = default)
    {
        registry.Options.EnsureSetupDiagnosticsEnabled();
        var result = await setup.ProbeAsync(url, username, applicationPassword, allowInvalidCertificate, ct);
        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_setup_instructions"),
     Description("Step-by-step instructions for preparing a WordPress site for this server: creating an application password, the role needed, the HTTPS/local-development requirement, and the configuration block to fill in. Needs no connectivity — use it when the site is not reachable from this server yet.")]
    public static string SetupInstructions(
        EndpointRegistry registry,
        [Description("Optional site URL, used to tailor the example configuration.")] string? url = null,
        [Description("Optional endpoint name to use in the example. Defaults to a name derived from the URL.")] string? siteName = null,
        CancellationToken ct = default)
    {
        registry.Options.EnsureSetupDiagnosticsEnabled();

        var name = !string.IsNullOrWhiteSpace(siteName) ? siteName!
            : !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                ? parsed.Host.Split('.').First()
                : "site1";
        var baseUrl = string.IsNullOrWhiteSpace(url) ? "https://example.com/" : url!;

        return JsonSerializer.Serialize(new
        {
            steps = new object[]
            {
                new
                {
                    step = 1,
                    title = "Pick the account",
                    detail = "Use an administrator account for full coverage. A lesser role works, but tools whose capabilities it lacks will fail; wp_setup_probe reports exactly which groups an account can drive.",
                },
                new
                {
                    step = 2,
                    title = "Create an application password",
                    detail = "In wp-admin: Users → Profile → Application Passwords → enter a name (e.g. wordpressmcpsharp) → Add New Application Password. Copy the generated password; WordPress shows it only once. Spaces in it are fine.",
                    wpCli = "wp user application-password create <username> wordpressmcpsharp --porcelain",
                },
                new
                {
                    step = 3,
                    title = "Confirm HTTPS (or mark the site local)",
                    detail = "WordPress only accepts application passwords over HTTPS. For a local development site served over plain HTTP, add define( 'WP_ENVIRONMENT_TYPE', 'local' ); to wp-config.php.",
                    wpCli = "wp config set WP_ENVIRONMENT_TYPE local",
                },
                new
                {
                    step = 4,
                    title = "Verify before configuring",
                    detail = $"Run wp_setup_probe with url='{baseUrl}', your username and the application password. It reports any remaining problems with the fix, and returns the exact configuration block to paste.",
                },
                new
                {
                    step = 5,
                    title = "Configure the endpoint",
                    detail = "Put the block below in WordpressMCPSharp.Local.json next to the executable — never in the checked-in WordpressMCPSharp.json — then restart the server. Environment variables work too, e.g. WORDPRESSMCP_Endpoints__" + name + "__RestApi__BaseUrl.",
                },
                new
                {
                    step = 6,
                    title = "Test and enable writes",
                    detail = $"Run wp_test_endpoint with site='{name}'. The server starts read-only: set Wordpress:ReadOnly=false to allow writes, and the separate Wordpress:AllowDelete / Wordpress:AllowPluginInstall gates for destructive operations.",
                },
            },
            exampleConfiguration = new Dictionary<string, object?>
            {
                ["Endpoints"] = new Dictionary<string, object?>
                {
                    [name] = new Dictionary<string, object?>
                    {
                        ["RestApi"] = new Dictionary<string, object?>
                        {
                            ["BaseUrl"] = baseUrl,
                            ["Username"] = "<wordpress-username>",
                            ["ApplicationPassword"] = "<application password>",
                        },
                    },
                },
                ["DefaultSite"] = name,
            },
            notes = new[]
            {
                "Several sites can be configured at once: add more entries under Endpoints and pass site=<name> to any tool.",
                "An endpoint may declare REST access, management access (Ssh/Local/Docker for WP-CLI), or both — neither channel is required.",
                "Tools whose channel is not configured on the resolved endpoint return an error naming the keys to set.",
            },
        }, JsonOpts.Default);
    }
}
