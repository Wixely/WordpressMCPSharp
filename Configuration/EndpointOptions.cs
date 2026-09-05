namespace WordpressMCPSharp.Configuration;

/// <summary>
/// One managed WordPress site. Each endpoint independently declares REST access
/// (<see cref="RestApi"/>) and/or management access (exactly one of <see cref="Ssh"/>,
/// <see cref="Local"/>, <see cref="Docker"/>). Neither channel is required, but an
/// endpoint with no channel at all is a configuration error.
/// </summary>
public sealed class EndpointOptions
{
    /// <summary>WordPress REST API access. Omit for a management-only endpoint.</summary>
    public RestApiOptions? RestApi { get; set; }

    /// <summary>Run WP-CLI over SSH.</summary>
    public SshManagementOptions? Ssh { get; set; }

    /// <summary>Run WP-CLI on the machine hosting this MCP server.</summary>
    public LocalManagementOptions? Local { get; set; }

    /// <summary>Run WP-CLI through `docker exec` against a container.</summary>
    public DockerManagementOptions? Docker { get; set; }

    /// <summary>
    /// Expected site URL, used for the identity cross-check before mutating CLI operations.
    /// Defaults to <c>RestApi.BaseUrl</c>; set it explicitly on management-only endpoints so
    /// the cross-check can still protect against a stale or mistyped path.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Optional description shown by wp_list_endpoints.</summary>
    public string? Description { get; set; }

    /// <summary>Per-endpoint read-only override. Stricter wins: true here forces read-only even when the global setting allows writes.</summary>
    public bool? ReadOnly { get; set; }

    /// <summary>Per-endpoint delete override. Stricter wins: false here blocks permanent deletion even when the global setting allows it.</summary>
    public bool? AllowDelete { get; set; }

    /// <summary>Per-endpoint plugin-install override. Stricter wins.</summary>
    public bool? AllowPluginInstall { get; set; }

    /// <summary>True when any management block is present.</summary>
    public bool HasManagement => Ssh is not null || Local is not null || Docker is not null;

    /// <summary>True when REST access is usable (a base URL is present).</summary>
    public bool HasRest => !string.IsNullOrWhiteSpace(RestApi?.BaseUrl);

    /// <summary>Names the configured management mode for diagnostics.</summary>
    public string ManagementMode => Ssh is not null ? "Ssh"
        : Local is not null ? "Local"
        : Docker is not null ? "Docker"
        : "none";

    /// <summary>The site URL used for identity cross-checks, if one can be determined.</summary>
    public string? EffectiveUrl => string.IsNullOrWhiteSpace(Url) ? RestApi?.BaseUrl : Url;
}

public sealed class RestApiOptions
{
    /// <summary>Base URL of the WordPress site (site root, no /wp-json). Example: https://blog.example.com/.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>WordPress username the server authenticates as. An administrator account gives full coverage.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Application password for the user (Users → Profile → Application Passwords). Sent as HTTP Basic auth. Spaces are allowed.</summary>
    public string ApplicationPassword { get; set; } = string.Empty;

    /// <summary>If true, ignore TLS certificate validation errors for this site. Use only for self-signed homelab instances.</summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>Optional WooCommerce consumer key, for stores that require key/secret auth instead of the application password.</summary>
    public string? WooCommerceConsumerKey { get; set; }

    /// <summary>Optional WooCommerce consumer secret, paired with WooCommerceConsumerKey.</summary>
    public string? WooCommerceConsumerSecret { get; set; }
}

public abstract class ManagementOptionsBase
{
    /// <summary>Absolute path to the WordPress installation (the directory holding wp-config.php).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>WP-CLI executable. Defaults to `wp` on the PATH.</summary>
    public string WpCliPath { get; set; } = "wp";
}

public sealed class SshManagementOptions : ManagementOptionsBase
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    /// <summary>Path to a private key file. Preferred over Password.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Passphrase for the private key, or the account password when no key is used.</summary>
    public string? Password { get; set; }
}

public sealed class LocalManagementOptions : ManagementOptionsBase
{
}

public sealed class DockerManagementOptions : ManagementOptionsBase
{
    /// <summary>Container name or id running WordPress.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Docker executable. Defaults to `docker` on the PATH.</summary>
    public string DockerPath { get; set; } = "docker";
}
