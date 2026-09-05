# WordpressMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **[WordPress](https://wordpress.org/)** over Streamable HTTP. It installs, configures, updates, maintains and diagnoses WordPress sites — **any number of them** from one server — through the [WordPress REST API](https://developer.wordpress.org/rest-api/) and, optionally, WP-CLI.

## Features

- HTTP MCP server using the Streamable HTTP transport. **122 tools.**
- **Multi-site**: sites are named endpoints; every tool takes an optional `site` argument.
- **Two independent channels per site** — the REST API and a WP-CLI management channel (SSH, local or Docker). Either works alone; tools tell you exactly what to configure when their channel is missing.
- **Guided setup**: `wp_setup_probe` diagnoses a site from just its URL and hands back the configuration to paste; `wp_setup_probe_ssh` discovers the WordPress installs on a host.
- **Read-only by default**, with separate gates for deletion, plugin installation, CLI management, arbitrary commands, provisioning and restore. Individual sites can be locked down further.
- **Full lifecycle**: build a site from nothing, manage content and media, run a store, update core/plugins/themes, snapshot and restore, and diagnose what's broken.

## Getting started

The server starts with nothing configured, so its setup tools can tell you what to configure.

1. Start the server (`dotnet run`) and point your MCP client at `http://localhost:5720/mcp`.
2. Run **`wp_setup_probe`** with your site URL (credentials optional but recommended):

   ```
   wp_setup_probe(url: "https://example.com", username: "admin", applicationPassword: "xxxx xxxx xxxx xxxx")
   ```

   It reports each check as `ok`/`warn`/`fail` with the fix, and returns a ready-to-paste `Endpoints` block. It catches the traps that make WordPress connections fail confusingly: a URL that redirects (authentication is lost across redirects), certificate problems, a REST API disabled by a security plugin, application passwords rejected because the site is on plain HTTP, and Authorization headers stripped by the web server.
3. No application password yet? **`wp_setup_instructions`** gives the wp-admin steps and the WP-CLI one-liner.
4. Managing sites over SSH? **`wp_setup_probe_ssh`** checks the host, confirms WP-CLI is installed, and **finds the WordPress installs on it** with their paths and site URLs — so endpoints are configured from what's actually there.
5. Put the suggested block in **`WordpressMCPSharp.Local.json`** next to the executable (gitignored — never put credentials in the checked-in `WordpressMCPSharp.json`), then restart.
6. Run **`wp_test_endpoint`** to verify, and **`wp_list_endpoints`** to see everything this server manages.
7. When you're ready to make changes, set `Wordpress:ReadOnly=false`. Deletion, plugin installs, CLI management, provisioning and restore each have their own additional gate.

## Configuring sites

Sites live under `Endpoints`. Each declares REST access, a management channel, or both:

```json
{
  "Endpoints": {
    "clienta": {
      "RestApi": {
        "BaseUrl": "https://clienta.com/",
        "Username": "admin",
        "ApplicationPassword": "xxxx xxxx xxxx xxxx xxxx xxxx"
      },
      "Ssh": {
        "Host": "web1.example.com",
        "Username": "deploy",
        "PrivateKeyPath": "/home/user/.ssh/id_ed25519",
        "Path": "/var/www/clienta.com"
      }
    },
    "clientb": {
      "RestApi": { "BaseUrl": "https://clientb.net/", "Username": "admin", "ApplicationPassword": "…" },
      "Description": "Look, don't touch",
      "ReadOnly": true
    },
    "localdev": {
      "Docker": { "Container": "wp-dev", "Path": "/var/www/html" },
      "Url": "http://localhost:8080/"
    }
  },
  "DefaultSite": "clienta"
}
```

**Choosing a site.** Every tool accepts `site`. Omitted, it uses `DefaultSite`, or the only configured endpoint; with several endpoints and no default the call fails rather than guessing, and unknown names fail with the list of valid ones. A tool can only ever reach a site named in configuration — an MCP client cannot supply a URL, host or filesystem path.

**Wrong-site protection.** A host often runs several WordPress installs, and a stale path is how one client's change lands on another's site. Before any mutating CLI operation the server runs `wp option get siteurl` at the configured path and refuses unless it matches the endpoint's `Url` (or `RestApi.BaseUrl`), naming both values.

**Per-site safety.** `ReadOnly`, `AllowDelete` and `AllowPluginInstall` can be set per endpoint, but only to make a site *stricter* than the global setting.

**Legacy configuration.** A v0.1-style flat `Wordpress:{BaseUrl,Username,ApplicationPassword}` section still works, mapped to an endpoint named `default`.

## Configuration reference

Environment variables win over JSON; use the `WORDPRESSMCP_` prefix and `__` for nesting, e.g. `WORDPRESSMCP_Endpoints__clienta__RestApi__BaseUrl=https://clienta.com/`.

### Per endpoint (`Endpoints:<name>:…`)

| Setting | Description |
| --- | --- |
| `RestApi:BaseUrl` | WordPress site root (no `/wp-json`). |
| `RestApi:Username` / `ApplicationPassword` | Account the server acts as, and its application password. |
| `RestApi:AllowInvalidCertificate` | Skip TLS verification for this site (self-signed homelab only). |
| `RestApi:WooCommerceConsumerKey` / `Secret` | Optional, for stores requiring key/secret auth. |
| `Ssh:{Host,Port,Username,PrivateKeyPath,Password,Path,WpCliPath}` | Run WP-CLI over SSH. |
| `Local:{Path,WpCliPath}` | Run WP-CLI on this machine. |
| `Docker:{Container,Path,DockerPath,WpCliPath}` | Run WP-CLI via `docker exec`. |
| `Url` | Expected site address for the identity check (required on management-only endpoints). |
| `Description` | Free text shown by `wp_list_endpoints`. |
| `ReadOnly` / `AllowDelete` / `AllowPluginInstall` | Per-site overrides; stricter than global only. |

### Global safety (`Wordpress:…`)

| Setting | Default | Description |
| --- | --- | --- |
| `ReadOnly` | `true` | When `true`, every create/update/delete tool is disabled. |
| `AllowDelete` | `false` | Second gate for permanent deletion (posts/pages/comments with `force=true`, media, users, terms, menus, plugins, snapshots). Trashing only needs write mode. |
| `AllowPluginInstall` | `false` | Second gate for `wp_install_plugin`. |
| `AllowRestPassthrough` | `false` | Second gate for `wp_rest_request`. |
| `Enable*` | `true` | Feature toggles for content, comments, users, taxonomies, plugins, themes, settings, menus, WooCommerce, site health and setup diagnostics. |

### Management channel (`Management:…`)

| Setting | Default | Description |
| --- | --- | --- |
| `AllowCliManagement` | `false` | Master gate for every WP-CLI-backed tool. |
| `AllowArbitraryCli` | `false` | Second gate for the raw `wp_cli` tool. |
| `AllowProvisioning` | `false` | Second gate for `wp_provision_site`. |
| `CommandTimeoutSeconds` | `300` | Timeout for a normal command. |
| `LongCommandTimeoutSeconds` | `1800` | Timeout for updates, backups and restores. |
| `SkipIdentityCheck` | `false` | Disables the wrong-site guard. Leave off. |
| `DeniedCliCommands` | `eval`, `eval-file`, `shell`, `server` | Commands `wp_cli` always refuses. |

### Snapshots (`Snapshots:…`)

| Setting | Default | Description |
| --- | --- | --- |
| `Directory` | _(none)_ | Directory **on the managed host** for snapshots, one subdirectory per endpoint. |
| `MaxSnapshots` | `10` | Snapshots kept per endpoint; older ones are pruned after a successful capture. `0` disables pruning. |
| `IncludeUploads` | `true` | Include `wp-content/uploads` in archives. |
| `AllowRestore` | `false` | Gate for `wp_restore_snapshot`, which overwrites the live site. |

### Server (`Server:…`)

| Setting | Default | Description |
| --- | --- | --- |
| `Host` / `Port` / `Path` | `localhost` / `5720` / `/mcp` | HTTP listener. |
| `WindowsServiceName` | `WordpressMCPSharp` | Service name under SCM. |
| `Password` | blank | Optional MCP endpoint password (`Authorization: Bearer`, Basic auth password, or `X-MCP-Password`). |

## Tools

Tools are marked **[REST]** (WordPress REST API) or **[CLI]** (WP-CLI management channel). Every tool accepts `site`.

### Setup & endpoints
- `wp_setup_probe` **[none]** — diagnose a site from its URL and emit a configuration block. Needs no configuration.
- `wp_setup_probe_ssh` **[none]** — check an SSH host and discover the WordPress installs on it.
- `wp_setup_instructions` **[none]** — how to create an application password and configure an endpoint.
- `wp_list_endpoints`, `wp_test_endpoint` — what this server manages, and whether each channel actually works.

### Site & content **[REST]**
- `wp_site_info`, `wp_search`, `wp_list_post_types`, `wp_list_post_statuses`.
- Posts: `wp_list_posts`, `wp_get_post`, `wp_create_post`, `wp_update_post`, `wp_delete_post`, `wp_list_post_revisions`.
- Pages: `wp_list_pages`, `wp_get_page`, `wp_create_page`, `wp_update_page`, `wp_delete_page`.
- `wp_get_post_content` — rendered or raw body, size-capped.
- Media: `wp_list_media`, `wp_get_media`, `wp_upload_media`, `wp_update_media`, `wp_download_media`, `wp_download_media_inline`, `wp_delete_media`.
- Comments: `wp_list_comments`, `wp_get_comment`, `wp_create_comment`, `wp_update_comment`, `wp_delete_comment`.
- Taxonomies: `wp_list_taxonomies`, `wp_list_terms`, `wp_create_term`, `wp_update_term`, `wp_delete_term`.
- Users: `wp_list_users`, `wp_get_user`, `wp_get_me`, `wp_create_user`, `wp_update_user`, `wp_delete_user`, and the application-password tools.
- Menus: `wp_list_menus`, `wp_create_menu`, `wp_delete_menu`, `wp_list_menu_locations`, `wp_list_menu_items`, `wp_create_menu_item`, `wp_update_menu_item`, `wp_delete_menu_item`.
- Settings: `wp_get_settings`, `wp_update_settings`.

### Client workflows **[REST]**
- `wp_set_featured_image` — upload a file (or reuse a media id), attach it and set it as the cover, in one call.
- `wp_clone_post` — duplicate a post or page as a draft, keeping terms, cover and template.
- `wp_replace_in_post` — find/replace inside one item's body; previews by default.

### Block themes **[REST]**
- `wp_list_templates`, `wp_get_template`, `wp_update_template`, `wp_list_template_parts`, `wp_list_block_patterns`, `wp_list_navigation`.

### WooCommerce **[REST]**
- Products: `wp_wc_list_products`, `wp_wc_get_product`, `wp_wc_create_product`, `wp_wc_update_product`, `wp_wc_delete_product`.
- Orders and customers: `wp_wc_list_orders`, `wp_wc_get_order`, `wp_wc_update_order`, `wp_wc_list_customers`.
- Catalogue and store: `wp_wc_list_product_categories`, `wp_wc_create_product_category`, `wp_wc_get_reports`, `wp_wc_get_settings`.

### Plugins & themes
- `wp_list_plugins`, `wp_get_plugin`, `wp_install_plugin`, `wp_activate_plugin`, `wp_deactivate_plugin`, `wp_delete_plugin` **[REST]**.
- `wp_list_themes`, `wp_get_active_theme` **[REST]**; `wp_install_theme`, `wp_update_theme`, `wp_activate_theme` **[CLI]** (the REST API cannot switch themes).
- `wp_update_plugin` **[CLI]** — the REST API can install but not *update*.

### Maintenance **[CLI]**
- `wp_core_version`, `wp_core_update`, `wp_core_verify_checksums`.
- `wp_set_permalink_structure`, `wp_flush_cache`, `wp_maintenance_mode`.
- `wp_list_cron_events`, `wp_run_cron_event`.
- `wp_db_check`, `wp_db_optimize`, `wp_search_replace` (dry run by default — the safe way to move domains).

### Provisioning & configuration **[CLI]**
- `wp_provision_site` — download core, write `wp-config.php`, install. Idempotent, and gated behind `AllowProvisioning` + `confirm`.
- `wp_config_get`, `wp_config_set` — wp-config constants; credentials and salts are refused.
- `wp_cli` — raw WP-CLI escape hatch, gated by `AllowArbitraryCli` with a deny-list.

### Snapshots & migration **[CLI]**
- `wp_create_snapshot`, `wp_list_snapshots`, `wp_get_snapshot`, `wp_restore_snapshot`, `wp_delete_snapshot`.
- `wp_export_content`, `wp_import_content` — WXR export/import.

### Diagnostics
- `wp_diagnose` — one-shot triage across whatever channels the endpoint has, reporting what it couldn't cover rather than failing.
- `wp_site_health`, `wp_directory_sizes`, `wp_check_updates` **[REST]**.
- `wp_read_debug_log`, `wp_clear_debug_log`, `wp_set_debug` **[CLI]**.
- `wp_bisect_plugins` **[CLI]** — guided bisection to find which plugin broke a site.
- `wp_rest_request` **[REST]** — gated passthrough to any REST namespace, for plugin APIs without a dedicated tool.

## Running

```sh
dotnet run
```

Then point your MCP client at `http://localhost:5720/mcp`.

## Docker

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

The image supports `linux/amd64` and `linux/arm64`. Read-only mode is on by default.

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

Put credentials in `C:\Services\WordpressMCPSharp\WordpressMCPSharp.Local.json` — never in the checked-in `WordpressMCPSharp.json`. To remove: `sc.exe stop` then `sc.exe delete`. Logs land in `<install-dir>\logs\wordpressmcp-*.log`.

## Requirements on managed hosts

REST tools need only an application password. The CLI tools additionally need, on the managed host:

- **WP-CLI** on the PATH (or set the endpoint's `WpCliPath`).
- **MySQL client tools** (`mysqldump`, `mysqlcheck`) for the database and snapshot tools. Their absence is reported by name with the package to install.
- Enough **PHP CLI memory** — `wp core download` needs more than the common 128 MB default. Out-of-memory failures are reported with the fix rather than a raw PHP fatal error.

## Safety model

- **Read-only by default.** Write tools fail with an error naming the config key to flip and the endpoint involved.
- **Layered gates.** Deletion, plugin installation, REST passthrough, CLI management, arbitrary commands, provisioning and restore each need their own opt-in on top of write mode.
- **Per-site lockdown.** An endpoint's own overrides can only restrict further, never widen.
- **Named sites only.** Tools address sites by configured name; clients cannot pass URLs, hosts or paths.
- **Wrong-site guard.** Mutating CLI operations verify the install's `siteurl` matches the endpoint before running.
- **Restore protections.** `wp_restore_snapshot` needs its own gate, a `confirm` matching the snapshot id, and refuses a snapshot taken from a different site; it captures a safety snapshot first.
- **Credential protection.** `wp_config_get`/`wp_config_set` refuse database credentials and authentication salts, and redact them in listings. Probes never echo passwords back.
- **Destructive commands denied.** `wp_cli` refuses `eval`, `eval-file`, `shell` and `server` regardless of gates.
- **Size caps.** Post content and binary media are capped; oversized payloads spill to disk instead of the MCP channel.
