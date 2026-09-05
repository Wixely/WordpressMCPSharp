# WordpressMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **[WordPress](https://wordpress.org/)** over Streamable HTTP. Talks to the official [WordPress REST API](https://developer.wordpress.org/rest-api/) and manages **any number of sites** from one server — from a freshly installed site to an established blog.

## Features

- HTTP MCP server using the Streamable HTTP transport.
- **Multi-site**: every tool takes an optional `site` argument naming a configured endpoint.
- **Guided setup**: `wp_setup_probe` diagnoses a site from just its URL — reachability, redirects, TLS, REST availability, authentication, account capabilities — and hands back the configuration block to paste.
- **Read-only mode by default** — all create/update tools stay disabled until explicitly enabled. Permanent deletes and plugin installs need their own second gates, and individual sites can be locked down further.
- Content tools: posts, pages, revisions, and media (upload, download, inline base64) with content-size caps.
- Site administration: settings, users, application passwords, plugins (list/install/activate/deactivate/delete), themes, navigation menus.
- Comments: list/moderate/create/reply/delete.
- Taxonomies: categories, tags, and custom taxonomies through one set of term tools.
- Works with a **fresh minimal install**: automatically falls back to `?rest_route=` addressing when the site still uses plain permalinks.
- Serilog logging to console and rolling files (daily + 50 MB rollover, 14-file retention).
- Runs as a console app, Windows Service, or Docker container.

## Getting started

The server starts with no sites configured, so you can use its setup tools to work out what to configure.

1. Start the server (`dotnet run`) and point your MCP client at `http://localhost:5720/mcp`.
2. Run **`wp_setup_probe`** with your site URL — and, if you already have one, a username and application password:

   ```
   wp_setup_probe(url: "https://example.com", username: "admin", applicationPassword: "xxxx xxxx xxxx xxxx")
   ```

   It reports each check as `ok` / `warn` / `fail` with the fix, and returns a ready-to-paste `Endpoints` block. Common things it catches: a URL that redirects (authentication breaks across redirects, so the final address is the one to configure), certificate problems, a site whose REST API is disabled by a security plugin, an application password rejected because the site is on plain HTTP, and an Authorization header being stripped by the web server.
3. Don't have an application password yet? **`wp_setup_instructions`** gives the wp-admin steps and the WP-CLI one-liner.
4. Put the suggested block in **`WordpressMCPSharp.Local.json`** next to the executable (it's gitignored — never put credentials in the checked-in `WordpressMCPSharp.json`), then restart.
5. Run **`wp_test_endpoint`** to confirm the configured endpoint works, and **`wp_list_endpoints`** to see everything this server manages.
6. When you're ready to make changes, set `Wordpress:ReadOnly=false`. Deleting and plugin installation stay behind their own gates.

## Configuring sites

Sites live under `Endpoints`. Each one independently declares REST access, management access, or both:

```json
{
  "Endpoints": {
    "clienta": {
      "RestApi": {
        "BaseUrl": "https://clienta.com/",
        "Username": "admin",
        "ApplicationPassword": "xxxx xxxx xxxx xxxx xxxx xxxx"
      }
    },
    "clientb": {
      "RestApi": {
        "BaseUrl": "https://clientb.net/",
        "Username": "admin",
        "ApplicationPassword": "xxxx xxxx xxxx xxxx xxxx xxxx"
      },
      "Description": "Look, don't touch",
      "ReadOnly": true
    }
  },
  "DefaultSite": "clienta"
}
```

**Choosing a site.** Every tool accepts `site`. When it's omitted the server uses `DefaultSite`, or the only configured endpoint if there's just one; with several endpoints and no default, the call fails rather than guessing. Unknown names fail with the list of valid ones. A tool can only ever reach a site named in configuration — an MCP client cannot supply a URL, host, or filesystem path.

**Per-site safety.** `ReadOnly`, `AllowDelete` and `AllowPluginInstall` can be set per endpoint, but only to make a site *stricter* than the global setting. `"ReadOnly": true` on one client's site keeps it read-only even while the server allows writes elsewhere.

**Management channel.** An endpoint may also declare exactly one of `Ssh`, `Local` or `Docker` for WP-CLI operations (core/plugin updates, backups, provisioning). Those tools arrive in a later release; the configuration is validated today. Endpoints with no `RestApi` block are management-only, and REST tools on them fail with an error naming the keys to set.

**Legacy configuration.** A v0.1-style flat `Wordpress:{BaseUrl,Username,ApplicationPassword}` section still works and is mapped to an endpoint named `default`.

## Configuration reference

Environment variables win over JSON; use the `WORDPRESSMCP_` prefix and `__` for nesting, e.g. `WORDPRESSMCP_Endpoints__clienta__RestApi__BaseUrl=https://clienta.com/`.

### Per endpoint (`Endpoints:<name>:…`)

| Setting | Description |
| --- | --- |
| `RestApi:BaseUrl` | WordPress site root (no `/wp-json`). |
| `RestApi:Username` | User the server operates as; administrator for full coverage. |
| `RestApi:ApplicationPassword` | Application password (Users → Profile → Application Passwords). Spaces are fine. |
| `RestApi:AllowInvalidCertificate` | Skip TLS verification for this site (self-signed homelab only). |
| `Ssh` / `Local` / `Docker` | Management channel for WP-CLI. At most one per endpoint. |
| `Url` | Expected site address, used for identity checks on management-only endpoints. |
| `Description` | Free text shown by `wp_list_endpoints`. |
| `ReadOnly` / `AllowDelete` / `AllowPluginInstall` | Per-site overrides. Stricter than the global setting only. |

### Global (`Wordpress:…`)

| Setting | Default | Description |
| --- | --- | --- |
| `ReadOnly` | `true` | When `true`, create/update/delete tools are disabled everywhere. |
| `AllowDelete` | `false` | Second gate for permanent deletion (posts/pages/comments with `force=true`, media, users, terms, menus, plugins). Trashing only needs write mode. |
| `AllowPluginInstall` | `false` | Second gate for `wp_install_plugin`. |
| `EnableContent` / `EnableComments` / `EnableUsers` / `EnableTaxonomies` / `EnablePlugins` / `EnableThemes` / `EnableSettings` / `EnableMenus` | `true` | Per-category feature toggles. |
| `EnableSetupDiagnostics` | `true` | Set `false` to hide the setup/diagnostic tools. |
| `DownloadDirectory` | _(temp)_ | Where `wp_download_media` writes files. |
| `MaxInlineContentBytes` | `65536` | Cap on inline content from `wp_get_post_content`. |
| `MaxInlineBinaryBytes` | `2000000` | Cap on inline base64 from `wp_download_media_inline`; above this the file is written to disk. |
| `DefaultPageSize` | `20` | Page size for list operations (WordPress caps at 100). |
| `MaxPages` | `5` | Max pages traversed when auto-paginating. |
| `RequestTimeoutSeconds` | `100` | HTTP timeout. |
| `UserAgent` | `WordpressMCPSharp` | UA header. |

### Server (`Server:…`)

| Setting | Default | Description |
| --- | --- | --- |
| `Host` | `localhost` | Host to bind. |
| `Port` | `5720` | HTTP port. |
| `Path` | `/mcp` | MCP endpoint path. |
| `WindowsServiceName` | `WordpressMCPSharp` | Service name when running under SCM. |
| `Password` | blank | Optional MCP endpoint password; blank disables password auth. |

When `Server:Password` is set, MCP requests must provide it as `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

## Tools

All tools below use the REST channel and accept an optional `site`.

### Setup & endpoints
- `wp_setup_probe` — diagnose a site from its URL (no configuration needed) and emit a configuration block.
- `wp_setup_instructions` — how to create an application password and configure an endpoint; needs no connectivity.
- `wp_list_endpoints` — the configured sites, their channels, effective safety, and configuration warnings.
- `wp_test_endpoint` — verify a configured endpoint end to end and explain what's broken.

### Site
- `wp_site_info` — site name, URL, timezone, and REST namespaces (detects plugin APIs).
- `wp_search` — site-wide search across posts, pages, and terms.
- `wp_list_post_types`, `wp_list_post_statuses`.

### Posts & pages
- `wp_list_posts`, `wp_get_post`, `wp_create_post`, `wp_update_post`, `wp_delete_post` (trash by default, `force=true` for permanent).
- `wp_list_pages`, `wp_get_page`, `wp_create_page`, `wp_update_page`, `wp_delete_page`.
- `wp_get_post_content` — rendered or raw body, capped by `MaxInlineContentBytes`.
- `wp_list_post_revisions`.

### Media
- `wp_list_media`, `wp_get_media`, `wp_update_media`.
- `wp_upload_media` — upload a local file into the media library.
- `wp_download_media`, `wp_download_media_inline`.
- `wp_delete_media` — permanent (requires `AllowDelete=true`).

### Comments
- `wp_list_comments`, `wp_get_comment`, `wp_create_comment`, `wp_update_comment`, `wp_delete_comment`.

### Users
- `wp_list_users`, `wp_get_user`, `wp_get_me`, `wp_create_user`, `wp_update_user`, `wp_delete_user`.
- `wp_list_application_passwords`, `wp_create_application_password`, `wp_delete_application_password`.

### Taxonomies
- `wp_list_taxonomies`, `wp_list_terms`, `wp_create_term`, `wp_update_term`, `wp_delete_term` — work on `categories`, `tags`, or any custom taxonomy `rest_base`.

### Plugins & themes
- `wp_list_plugins`, `wp_get_plugin`, `wp_install_plugin` (requires `AllowPluginInstall=true`), `wp_activate_plugin`, `wp_deactivate_plugin`, `wp_delete_plugin`.
- `wp_list_themes`, `wp_get_active_theme` (the core REST API cannot switch themes).

### Menus
- `wp_list_menus`, `wp_create_menu`, `wp_delete_menu`, `wp_list_menu_locations`.
- `wp_list_menu_items`, `wp_create_menu_item`, `wp_update_menu_item`, `wp_delete_menu_item`.

### Settings
- `wp_get_settings`, `wp_update_settings`.

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
  -e WORDPRESSMCP_Endpoints__clienta__RestApi__BaseUrl=https://clienta.com/ \
  -e WORDPRESSMCP_Endpoints__clienta__RestApi__Username=admin \
  -e "WORDPRESSMCP_Endpoints__clienta__RestApi__ApplicationPassword=xxxx xxxx xxxx xxxx" \
  -e WORDPRESSMCP_DefaultSite=clienta \
  -e WORDPRESSMCP_Server__Password=change-me \
  ghcr.io/wixely/wordpressmcpsharp:<version>
```

The image supports `linux/amd64` and `linux/arm64`. Read-only mode is on by default; set `WORDPRESSMCP_Wordpress__ReadOnly=false` (and `…__AllowDelete=true` / `…__AllowPluginInstall=true` for the destructive tools) only when you want write tools.

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

Put credentials in `C:\Services\WordpressMCPSharp\WordpressMCPSharp.Local.json` — never in `WordpressMCPSharp.json`, which is checked in.

To remove:

```powershell
sc.exe stop WordpressMCPSharp
sc.exe delete WordpressMCPSharp
```

Logs land in `<install-dir>\logs\wordpressmcp-*.log`.

## Safety model

- **Read-only by default.** All write tools call a gate that fails with a clear error naming the config key to flip and the endpoint involved.
- **Delete double-gate.** Permanent deletion (bypassing trash) also requires `Wordpress:AllowDelete=true`; moving to trash only needs write mode.
- **Plugin install double-gate.** Installing code from the plugin directory requires `Wordpress:AllowPluginInstall=true`.
- **Per-site lockdown.** An endpoint's own `ReadOnly` / `AllowDelete` / `AllowPluginInstall` can only restrict further, never widen.
- **Named sites only.** Tools address sites by configured name; URLs, hosts and paths cannot be passed in by a client.
- **Feature toggles.** Content, comments, users, taxonomies, plugins, themes, settings, menus and the setup tools can each be disabled.
- **Inline size caps.** Post content and binary media are capped; oversized payloads spill to disk instead of being returned through the MCP channel.
