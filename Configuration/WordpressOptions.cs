namespace WordpressMCPSharp.Configuration;

/// <summary>
/// Global behaviour shared by every endpoint: safety gates, feature toggles and tuning.
/// Connection details live per-site in <see cref="EndpointOptions"/>.
/// </summary>
public sealed class WordpressOptions
{
    public const string SectionName = "Wordpress";

    // ---- Legacy single-site connection fields -------------------------------------------------
    // Kept so v0.1 configurations keep working; the registry maps them to an endpoint named
    // "default" when no Endpoints section is present. Prefer Endpoints for new configurations.

    /// <summary>Legacy: base URL of a single WordPress site. Prefer Endpoints:&lt;name&gt;:RestApi:BaseUrl.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Legacy: username for the single-site configuration.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Legacy: application password for the single-site configuration.</summary>
    public string ApplicationPassword { get; set; } = string.Empty;

    /// <summary>Legacy: TLS override for the single-site configuration.</summary>
    public bool AllowInvalidCertificate { get; set; }

    // ---- Safety gates -------------------------------------------------------------------------

    /// <summary>When true, all write/update/delete tools are disabled. Default true. Endpoints may make this stricter, never looser.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>If false, permanent deletion is forbidden even when ReadOnly=false. Trashing (where WordPress supports it) only needs write mode.</summary>
    public bool AllowDelete { get; set; }

    /// <summary>Second gate for installing plugins from the WordPress.org directory. Requires ReadOnly=false as well.</summary>
    public bool AllowPluginInstall { get; set; }

    /// <summary>Second gate for the generic REST passthrough tool.</summary>
    public bool AllowRestPassthrough { get; set; }

    // ---- Feature toggles ----------------------------------------------------------------------

    /// <summary>If false, post/page/media/revision tools are disabled.</summary>
    public bool EnableContent { get; set; } = true;

    /// <summary>If false, comment tools are disabled.</summary>
    public bool EnableComments { get; set; } = true;

    /// <summary>If false, user management tools are disabled.</summary>
    public bool EnableUsers { get; set; } = true;

    /// <summary>If false, category/tag/taxonomy tools are disabled.</summary>
    public bool EnableTaxonomies { get; set; } = true;

    /// <summary>If false, plugin tools are disabled.</summary>
    public bool EnablePlugins { get; set; } = true;

    /// <summary>If false, theme tools are disabled.</summary>
    public bool EnableThemes { get; set; } = true;

    /// <summary>If false, site settings tools are disabled.</summary>
    public bool EnableSettings { get; set; } = true;

    /// <summary>If false, navigation menu tools are disabled.</summary>
    public bool EnableMenus { get; set; } = true;

    /// <summary>If false, the setup/diagnostic tools are disabled.</summary>
    public bool EnableSetupDiagnostics { get; set; } = true;

    // ---- Tuning -------------------------------------------------------------------------------

    /// <summary>Optional directory where downloaded media files are written. Falls back to the system temp directory.</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>Maximum characters of content returned inline by wp_get_post_content. Larger content is truncated with a flag.</summary>
    public int MaxInlineContentBytes { get; set; } = 65_536;

    /// <summary>Maximum bytes of binary media returned inline (base64) by wp_download_media_inline. Above this, the tool writes to disk and returns a path.</summary>
    public int MaxInlineBinaryBytes { get; set; } = 2_000_000;

    /// <summary>Default page size for list operations. WordPress caps per_page at 100.</summary>
    public int DefaultPageSize { get; set; } = 20;

    /// <summary>Max pages traversed when auto-paginating. Guards against runaway calls.</summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 100;

    /// <summary>User-Agent header sent to WordPress.</summary>
    public string UserAgent { get; set; } = "WordpressMCPSharp";

    /// <summary>Gate for the onboarding tools, which run before any endpoint exists.</summary>
    public void EnsureSetupDiagnosticsEnabled()
    {
        if (!EnableSetupDiagnostics)
        {
            throw new ModelContextProtocol.McpException(
                "Setup diagnostic tools are disabled by server configuration. Set Wordpress:EnableSetupDiagnostics=true to enable them.");
        }
    }
}

/// <summary>Top-level site registry, bound from the configuration root.</summary>
public sealed class RegistryOptions
{
    /// <summary>Named WordPress sites this server manages.</summary>
    public Dictionary<string, EndpointOptions> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Endpoint used when a tool call omits the `site` argument.</summary>
    public string? DefaultSite { get; set; }
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5720;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "WordpressMCPSharp";

    /// <summary>Optional MCP endpoint password. Blank disables MCP password auth.</summary>
    public string Password { get; set; } = string.Empty;
}
