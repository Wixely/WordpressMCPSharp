# WordpressMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **[WordPress](https://wordpress.org/)** over Streamable HTTP. Talks to the official [WordPress REST API](https://developer.wordpress.org/rest-api/) and can run and administer anything from a freshly installed site to an established blog.

## Features

- HTTP MCP server using the Streamable HTTP transport.
- **Read-only mode by default** — all create/update tools stay disabled until explicitly enabled. Permanent deletes and plugin installs need their own second gates.
- Content tools: posts, pages, revisions, and media (upload, download, inline base64) with content-size caps.
- Site administration: settings, users, application passwords, plugins (list/install/activate/deactivate/delete), themes, navigation menus.
- Comments: list/moderate/create/reply/delete.
- Taxonomies: categories, tags, and custom taxonomies through one set of term tools.
- Works with a **fresh minimal install**: automatically falls back to `?rest_route=` addressing when the site still uses plain permalinks.
- Configuration via `WordpressMCPSharp.json`, environment variables, or command line.
- Serilog logging to console and rolling files (daily + 50 MB rollover, 14-file retention).
- Runs as a console app, Windows Service, or Docker container.

## Connecting to WordPress

The server authenticates with a WordPress **application password** over HTTP Basic auth:

1. In wp-admin go to **Users → Profile → Application Passwords**, name it (e.g. `wordpressmcpsharp`) and create it. Or with WP-CLI: `wp user application-password create admin wordpressmcpsharp --porcelain`.
2. Put the username and generated password in `WordpressMCPSharp.Local.json` (spaces in the password are fine).
3. Use an **administrator** account for full coverage; lesser roles work but hide the tools their capabilities don't allow.

> **Plain HTTP note:** WordPress only accepts application passwords over HTTPS. For a local/dev site served over plain HTTP, add `define( 'WP_ENVIRONMENT_TYPE', 'local' );` to `wp-config.php`.

## Configuration

Configure via `WordpressMCPSharp.json` or environment variables. Environment variables win over JSON; in Docker, use the `WORDPRESSMCP_` prefix and `__` for nested keys.

| Setting | Default | Description |
| --- | --- | --- |
| `Wordpress:BaseUrl` | `http://localhost:8080/` | WordPress site root (no `/wp-json`). |
| `Wordpress:Username` | _(none)_ | User the server operates as. |
| `Wordpress:ApplicationPassword` | _(none)_ | Application password for the user. Sent as HTTP Basic auth. |
| `Wordpress:ReadOnly` | `true` | When `true`, create/update/delete tools are disabled. |
| `Wordpress:AllowDelete` | `false` | Second gate for permanent deletion (posts/pages/comments with `force=true`, media, users, terms, menus, plugins). Trashing only needs write mode. |
| `Wordpress:AllowPluginInstall` | `false` | Second gate for `wp_install_plugin`. |
| `Wordpress:EnableContent` | `true` | Hides post/page/media tools when `false`. |
| `Wordpress:EnableComments` | `true` | Hides comment tools when `false`. |
| `Wordpress:EnableUsers` | `true` | Hides user and application-password tools when `false`. |
| `Wordpress:EnableTaxonomies` | `true` | Hides category/tag/taxonomy tools when `false`. |
| `Wordpress:EnablePlugins` | `true` | Hides plugin tools when `false`. |
| `Wordpress:EnableThemes` | `true` | Hides theme tools when `false`. |
| `Wordpress:EnableSettings` | `true` | Hides site settings tools when `false`. |
| `Wordpress:EnableMenus` | `true` | Hides navigation menu tools when `false`. |
| `Wordpress:DownloadDirectory` | _(temp)_ | Where `wp_download_media` writes files. Defaults to `<TEMP>/WordpressMCPSharp`. |
| `Wordpress:MaxInlineContentBytes` | `65536` | Cap on inline post/page content returned by `wp_get_post_content`. |
| `Wordpress:MaxInlineBinaryBytes` | `2000000` | Cap on inline base64 bytes from `wp_download_media_inline`; above this, the file is written to disk. |
| `Wordpress:DefaultPageSize` | `20` | Page size for list operations (WordPress caps at 100). |
| `Wordpress:MaxPages` | `5` | Max pages traversed when auto-paginating. |
| `Wordpress:RequestTimeoutSeconds` | `100` | HTTP timeout. |
| `Wordpress:UserAgent` | `WordpressMCPSharp` | UA header. |
| `Wordpress:AllowInvalidCertificate` | `false` | Skip TLS verification (self-signed homelab only). |
| `Server:Host` | `localhost` | Host to bind. |
| `Server:Port` | `5720` | HTTP port. |
| `Server:Path` | `/mcp` | MCP endpoint path. |
| `Server:WindowsServiceName` | `WordpressMCPSharp` | Service name when running under SCM. |
| `Server:Password` | blank | Optional MCP endpoint password; blank disables password auth. |

When `Server:Password` is set, MCP requests must provide the password as `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

Nested keys use `__` in environment variables, for example `WORDPRESSMCP_Wordpress__BaseUrl=https://blog.example.com/`. Booleans use `true` or `false`.

## Tools

### Site
- `wp_site_info` — site name, URL, timezone, and REST namespaces (good first call; detects plugin APIs).
- `wp_search` — site-wide search across posts, pages, and terms.
- `wp_list_post_types`, `wp_list_post_statuses` — registered types and statuses.

### Posts & pages
- `wp_list_posts`, `wp_get_post`, `wp_create_post`, `wp_update_post`, `wp_delete_post` (trash by default, `force=true` for permanent).
- `wp_list_pages`, `wp_get_page`, `wp_create_page`, `wp_update_page`, `wp_delete_page`.
- `wp_get_post_content` — rendered or raw body for a post or page, capped by `MaxInlineContentBytes`.
- `wp_list_post_revisions` — revision history.

### Media
- `wp_list_media`, `wp_get_media`, `wp_update_media`.
- `wp_upload_media` — upload a local file into the media library (with title/alt/caption, optional post attachment).
- `wp_download_media` — write the file to `DownloadDirectory`; `wp_download_media_inline` — base64 inline with a disk fallback.
- `wp_delete_media` — permanent (requires `AllowDelete=true`).

### Comments
- `wp_list_comments`, `wp_get_comment`, `wp_create_comment` (supports threaded replies), `wp_update_comment` (content + moderation status), `wp_delete_comment`.

### Users
- `wp_list_users`, `wp_get_user`, `wp_get_me`, `wp_create_user`, `wp_update_user`, `wp_delete_user` (reassigns content, requires `AllowDelete=true`).
- `wp_list_application_passwords`, `wp_create_application_password`, `wp_delete_application_password`.

### Taxonomies
- `wp_list_taxonomies` — discover collections (including plugin-registered ones).
- `wp_list_terms`, `wp_create_term`, `wp_update_term`, `wp_delete_term` — work on `categories`, `tags`, or any custom taxonomy `rest_base`.

### Plugins & themes
- `wp_list_plugins`, `wp_get_plugin`.
- `wp_install_plugin` — install by WordPress.org slug (requires `AllowPluginInstall=true`), optionally activate.
- `wp_activate_plugin`, `wp_deactivate_plugin`, `wp_delete_plugin`.
- `wp_list_themes`, `wp_get_active_theme` (the core REST API cannot switch themes).

### Menus
- `wp_list_menus`, `wp_create_menu`, `wp_delete_menu`, `wp_list_menu_locations`.
- `wp_list_menu_items`, `wp_create_menu_item` (custom links or post/page/category/tag links), `wp_update_menu_item`, `wp_delete_menu_item`.

### Settings
- `wp_get_settings`, `wp_update_settings` — title, tagline, timezone, formats, discussion defaults, front-page settings, and any other keys the settings endpoint exposes.

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5720/mcp`.

## Docker

Tagged releases publish a multi-arch image to GitHub Container Registry:

```sh
docker pull ghcr.io/wixely/wordpressmcpsharp:<version>
docker run --rm -p 5720:5720 \
  -e WORDPRESSMCP_Wordpress__BaseUrl=https://blog.example.com/ \
  -e WORDPRESSMCP_Wordpress__Username=admin \
  -e "WORDPRESSMCP_Wordpress__ApplicationPassword=xxxx xxxx xxxx xxxx xxxx xxxx" \
  -e WORDPRESSMCP_Server__Password=change-me \
  ghcr.io/wixely/wordpressmcpsharp:<version>
```

The image supports `linux/amd64` and `linux/arm64`. Read-only mode is on by default; set `WORDPRESSMCP_Wordpress__ReadOnly=false` (and `WORDPRESSMCP_Wordpress__AllowDelete=true` / `WORDPRESSMCP_Wordpress__AllowPluginInstall=true` for the destructive tools) only when you want write tools.

## Running as a Windows Service

The host detects when it's launched by the Service Control Manager and switches to service mode automatically (config and logs resolve from the executable directory, not the SCM's `C:\Windows\System32` working directory).

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\WordpressMCPSharp

sc.exe create WordpressMCPSharp `
    binPath= "C:\Services\WordpressMCPSharp\WordpressMCPSharp.exe" `
    start= auto `
    DisplayName= "WordPress MCP (C#)"
sc.exe description WordpressMCPSharp "MCP server for WordPress."
sc.exe start WordpressMCPSharp
```

Put credentials in `C:\Services\WordpressMCPSharp\WordpressMCPSharp.Local.json` (or set `WORDPRESSMCP_Wordpress__ApplicationPassword` as a machine-level env var) — never in `WordpressMCPSharp.json`, which is checked in.

To remove:

```powershell
sc.exe stop WordpressMCPSharp
sc.exe delete WordpressMCPSharp
```

Logs land in `<install-dir>\logs\wordpressmcp-*.log`.

## Safety model

- **Read-only by default.** All write tools call `EnsureWriteAllowed` and fail with a clear error naming the config key to flip.
- **Delete double-gate.** Permanent deletion (bypassing trash) also requires `Wordpress:AllowDelete=true`; moving to trash only needs write mode.
- **Plugin install double-gate.** Installing code from the plugin directory requires `Wordpress:AllowPluginInstall=true`.
- **Feature toggles.** Content, comments, users, taxonomies, plugins, themes, settings, and menus can each be hidden.
- **Inline size caps.** Post content and binary media are capped; oversized payloads spill to disk instead of being returned through the MCP channel.
