namespace WordpressMCPSharp.Configuration;

public sealed class WordpressOptions
{
    public const string SectionName = "Wordpress";

    /// <summary>Base URL of the WordPress site (no trailing /wp-json). Example: https://blog.example.com/.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080/";

    /// <summary>WordPress username the server authenticates as. Needs an administrator role for full coverage.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Application password for the user (Users → Profile → Application Passwords). Sent as HTTP Basic auth. Spaces are allowed.</summary>
    public string ApplicationPassword { get; set; } = string.Empty;

    /// <summary>When true, all create/update/delete tools are disabled. Default true.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>If false, permanent deletion of posts, pages, media, comments, users, terms, menus and plugins is forbidden even when ReadOnly=false. Trashing (where WordPress supports it) only needs write mode.</summary>
    public bool AllowDelete { get; set; } = false;

    /// <summary>Second gate for installing plugins from the WordPress.org directory. Requires ReadOnly=false as well.</summary>
    public bool AllowPluginInstall { get; set; } = false;

    /// <summary>If false, post/page/media/revision tools are hidden.</summary>
    public bool EnableContent { get; set; } = true;

    /// <summary>If false, comment tools are hidden.</summary>
    public bool EnableComments { get; set; } = true;

    /// <summary>If false, user management tools are hidden.</summary>
    public bool EnableUsers { get; set; } = true;

    /// <summary>If false, category/tag/taxonomy tools are hidden.</summary>
    public bool EnableTaxonomies { get; set; } = true;

    /// <summary>If false, plugin tools are hidden.</summary>
    public bool EnablePlugins { get; set; } = true;

    /// <summary>If false, theme tools are hidden.</summary>
    public bool EnableThemes { get; set; } = true;

    /// <summary>If false, site settings tools are hidden.</summary>
    public bool EnableSettings { get; set; } = true;

    /// <summary>If false, navigation menu tools are hidden.</summary>
    public bool EnableMenus { get; set; } = true;

    /// <summary>Optional directory where downloaded media files are written. Falls back to the system temp directory.</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>Maximum characters of rendered/raw content returned inline by wp_get_post_content. Larger content is truncated with a flag.</summary>
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

    /// <summary>If true, ignore TLS certificate validation errors. Use only for self-signed homelab instances.</summary>
    public bool AllowInvalidCertificate { get; set; } = false;
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
