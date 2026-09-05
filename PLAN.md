# WordpressMCPSharp — Roadmap

Goal: install, configure, update, maintain and diagnose WordPress sites end to end —
build-out, plugin management, snapshots/backups and restore, content and page management
for clients (including covers/featured images), user management, and data management in
popular plugins such as WooCommerce. One server instance manages **multiple sites**.

This document records what the current REST-only server can and cannot do, and the plan
to close the gaps.

## Where we are

v0.1 wraps the core WordPress REST API (60 `wp_*` tools) for a **single site**: posts,
pages, media, comments, users + application passwords, taxonomies, plugins
(list/install/activate/deactivate/delete), themes (read-only), menus, settings, search,
site info. Read-only by default with `AllowDelete` and `AllowPluginInstall` double gates
and per-category feature toggles.

## Gap analysis

### Gaps the REST API can cover (missing tools only)

| Capability | Gap | Route |
| --- | --- | --- |
| Multiple sites | Config knows exactly one site | Endpoint registry (below) |
| Popular plugin data (WooCommerce, ACF, Yoast, …) | No access to plugin REST namespaces | `wc/v3` and friends work with the same application-password Basic auth; add first-class WooCommerce tools plus a gated generic REST passthrough for everything else |
| Diagnostics | No health tooling | `wp-site-health/v1` REST namespace (verified working): loopback, background-updates, https-status, dotorg-communication, authorization-header tests, and `directory-sizes` |
| Client page workflows | Multi-step flows (upload cover + set featured image + publish) need many calls | Convenience tools: `wp_set_featured_image` (upload/attach/set in one), `wp_clone_post`, `wp_replace_in_post` |
| Block-theme content | No access to block templates/parts/patterns/navigation | `wp/v2/templates`, `template-parts`, `global-styles`, `navigation` endpoints |
| Update *visibility* | Can't see what's outdated | Core/plugin/theme version data via REST + wordpress.org API version checks (read-only, no channel needed) |

### Gaps the REST API cannot cover (needs a management channel)

The core REST API has **no endpoints** for these — verified against a live 7.1 site:

| Capability | Why REST can't | Needs |
| --- | --- | --- |
| Install WordPress (new site build-out) | REST doesn't exist until WP is installed | WP-CLI: `core download/config/install` |
| Core updates | No update endpoint | WP-CLI: `core update`, `core verify-checksums` |
| Plugin/theme **updates** | Plugins endpoint installs/activates but cannot update; themes endpoint is read-only | WP-CLI: `plugin update`, `theme install/update/activate` |
| Theme switching | Core REST cannot activate a theme | WP-CLI: `theme activate` |
| Permalink structure | `wp/v2/settings` has no `permalink_structure` key (verified) | WP-CLI: `rewrite structure --hard` |
| Snapshots / backups / restore | Nothing in REST | WP-CLI `db export/import` + file archive of wp-content |
| wp-config edits (WP_DEBUG, environment type, salts) | Nothing in REST | WP-CLI: `config set/get` |
| Search-replace (domain moves, staging→live) | Nothing in REST | WP-CLI: `search-replace` |
| Cron inspection/run, cache flush, maintenance mode | Nothing in REST | WP-CLI: `cron`, `cache flush`, `maintenance-mode` |
| debug.log reading, DB check/optimize | Nothing in REST | Shell/WP-CLI: `db check/optimize` |

**Architectural decision:** management operations run WP-CLI against the site, following
the family's RouterOSMCPSharp precedent (REST plus SSH fallback) and RemoteAdminMCPSharp's
double-gated command execution. Management is configured **per endpoint** (see below) as
one of `Ssh` (run `wp` over SSH), `Local` (`wp` on this host), or `Docker`
(`docker exec <container> wp …`); an endpoint with no management block is REST-only.

## Endpoint registry

The customer configures any number of named sites. Each endpoint independently declares
its REST access and/or its management access — the two channels are never required
together:

```json
{
  "Endpoints": {
    "site1": {
      "RestApi": {
        "BaseUrl": "https://site1.example.com/",
        "Username": "admin",
        "ApplicationPassword": "xxxx xxxx xxxx xxxx"
      },
      "Ssh": {
        "Host": "web1.example.com",
        "Port": 22,
        "Username": "deploy",
        "PrivateKeyPath": "C:/keys/web1",
        "Path": "/var/www/site1",
        "WpCliPath": "wp"
      }
    },
    "site2": {
      "RestApi": { "BaseUrl": "https://site2.example.net/", "Username": "admin", "ApplicationPassword": "…" }
    },
    "localdev": {
      "Docker": { "Container": "wp-dev", "Path": "/var/www/html" }
    }
  },
  "DefaultSite": "site1"
}
```

Rules:

1. **Per-endpoint blocks:** `RestApi` is optional; at most one management block
   (`Ssh` | `Local` | `Docker`) per endpoint. More than one management block on the
   same endpoint is a startup configuration error. An endpoint with neither block is
   a configuration error.
2. **Site selection:** every tool takes an optional `site` parameter resolved against
   the registry only — an MCP client can never supply a URL, host, or filesystem path.
   Resolution: explicit `site` → `DefaultSite` → the single configured endpoint →
   otherwise an error listing the configured names. Unknown names error the same way.
   `wp_list_endpoints` returns the registry: names, which channels each endpoint has,
   URLs, and cross-check status.
3. **Channel independence + proper errors:** a tool whose required channel is missing
   on the resolved endpoint throws a `McpException` naming the endpoint and the keys
   to set, e.g.
   - `MCP tool 'wp_core_update' requires management access, but endpoint 'site2' has
     no Ssh/Local/Docker block configured.`
   - `MCP tool 'wp_create_post' requires REST access, but endpoint 'localdev' has no
     RestApi block. Set Endpoints:localdev:RestApi:BaseUrl/Username/ApplicationPassword.`
   Tools stay visible in `tools/list` and fail with these errors when called (same
   behaviour as the existing feature toggles). Every tool is marked `[REST]` or `[CLI]`
   in the README; the only exceptions are composite diagnostics (`wp_diagnose`), which
   run whatever the endpoint's channels allow and report the rest as
   `"skipped": "channel not configured"`.
4. **Identity cross-check (multi-site SSH hosts):** an SSH host often serves several
   WordPress installs, and a wrong-site write is the worst failure mode this server
   has. Before any mutating CLI operation, the service runs
   `wp option get siteurl --path=<endpoint path>` and compares it against the
   endpoint's `RestApi.BaseUrl` (or an explicit `Url` field for CLI-only endpoints),
   case-insensitively, ignoring scheme. Mismatch → refuse, showing both values. This
   catches stale paths, moved installs, and copy-paste config mistakes.
5. **Per-endpoint safety overrides:** endpoints may override `ReadOnly` and
   `AllowDelete`; the effective value is the **stricter** of global and per-endpoint
   (a per-endpoint override can lock a client site down further, never open it up
   beyond the global setting).
6. **Legacy single-site config:** the v0.1 flat `Wordpress:{BaseUrl,Username,…}`
   section keeps working by mapping to an implicit endpoint named `default` — same
   spirit as the family's legacy `appsettings*` compatibility layer.

Startup banner and `/healthz` report the endpoint count and each endpoint's channel
status (`rest: yes/no`, `management: Ssh/Local/Docker/none`).

## Plan

### Phase 1 — Endpoint registry + channel independence (restructure)

Do this first: it touches all 60 existing tools, so it must land before more tools pile
onto the flat config.

1. `EndpointRegistry` service: parses `Endpoints`, validates blocks, maps legacy flat
   config to `default`, caches one `WordpressRestClient` (today's `WordpressService`,
   renamed) per endpoint with lazy call-time validation — a CLI-only endpoint or an
   empty registry must start cleanly (the v0.1 constructor throws on missing BaseUrl;
   fix here).
2. Add the optional `site` parameter to every existing tool; tools resolve through the
   registry and inherit the selection/error rules above.
3. `wp_list_endpoints`, updated startup banner and `/healthz`.
4. Per-endpoint safety overrides (stricter-wins).
5. Update README, `WordpressMCPSharp.json` sample, and Docker env examples to the
   `Endpoints` shape.

### Phase 2 — REST coverage

New options: `EnableWooCommerce` (default true), `EnableSiteHealth` (default true),
`AllowRestPassthrough` (default false, second gate).

1. **Generic REST passthrough** — `wp_rest_request(site, method, route, bodyJson?, query?)`
   for any namespace discovered via `wp_site_info`. GET allowed in read-only mode;
   mutating verbs need write mode; the tool itself needs `AllowRestPassthrough=true`.
   This single tool unlocks every well-behaved plugin API (ACF, Yoast, Rank Math, …).
2. **WooCommerce tools** (`wc/v3`, app-password auth; optional consumer key/secret in
   the endpoint's `RestApi` block for stores that require them): `wp_wc_list_products`,
   `wp_wc_get_product`, `wp_wc_create_product`, `wp_wc_update_product`,
   `wp_wc_delete_product`, `wp_wc_list_orders`, `wp_wc_get_order`, `wp_wc_update_order`
   (status/notes), `wp_wc_list_customers`, `wp_wc_list_product_categories`,
   `wp_wc_create_product_category`, `wp_wc_get_reports` (sales/top sellers),
   `wp_wc_get_settings`. Detect absence of the `wc/v3` namespace and return a clear
   "WooCommerce not installed/active on endpoint 'x'" error.
3. **Site health (REST)** — `wp_site_health_tests` (runs the five direct tests and
   aggregates), `wp_directory_sizes`, `wp_check_updates` (core/plugin/theme versions
   vs wordpress.org version-check API — read-only HTTP to api.wordpress.org).
4. **Client content conveniences** — `wp_set_featured_image` (upload file or reuse
   media id, attach, set as cover, return URLs), `wp_clone_post` (duplicate a
   post/page as draft for client revisions), `wp_replace_in_post` (safe string
   replace in one post's raw content).
5. **Block-theme building blocks** — `wp_list_templates`, `wp_get_template`,
   `wp_update_template`, `wp_list_template_parts`, `wp_list_block_patterns`,
   `wp_list_navigation` (wp_navigation posts) so page building works on modern
   block themes, not just classic menus.

### Phase 3 — Management channel + lifecycle

Global gates: `Management:AllowCliManagement` (master, default false) and
`Management:AllowArbitraryCli` (default false, raw escape hatch only). Global
`Management:CommandTimeoutSeconds`.

1. **Channel plumbing** — `ManagementService` with one execution primitive per mode
   (argument-list based, no shell string interpolation), timeout, output capture,
   and structured `{exitCode, stdout, stderr}` results. Endpoint resolution + the
   identity cross-check live here so every CLI tool inherits them.
2. **Maintenance tools** — `wp_core_version` (+ available updates), `wp_core_update`,
   `wp_core_verify_checksums`, `wp_update_plugin`, `wp_update_all_plugins`,
   `wp_install_theme`, `wp_update_theme`, `wp_activate_theme`,
   `wp_set_permalink_structure`, `wp_flush_cache`, `wp_maintenance_mode`,
   `wp_list_cron_events`, `wp_run_cron_event`, `wp_db_check`, `wp_db_optimize`.
3. **Site build-out** — `wp_provision_site` (core download → config create →
   core install, idempotent checks at each step, requires `AllowCliManagement`
   and explicit non-default confirmation parameter), `wp_config_get`, `wp_config_set`
   (deny-list for auth salts/DB credentials unless a named override flag is set).
4. **Escape hatch** — `wp_cli` (raw WP-CLI command, argument array), double-gated by
   `AllowArbitraryCli` + write mode, with a deny-list (`eval`, `eval-file`, `shell`)
   in the RouterOS style.

### Phase 4 — Snapshots, backup, restore

New options: `Snapshots:{Directory, MaxSnapshots, IncludeUploads}` and gate
`AllowRestore` (default false — restoring overwrites the site). Snapshots are stored
per endpoint (`<Directory>/<site>/<timestamp>/`).

1. `wp_create_snapshot` — `wp db export` + tar of `wp-content` (optionally uploads),
   manifest JSON (WP version, plugin list + versions, active theme, site URL, sizes,
   timestamp). Write mode required.
2. `wp_list_snapshots`, `wp_get_snapshot` — manifest browsing, integrity check.
3. `wp_restore_snapshot` — DB import + file restore, **requires `AllowRestore=true`**
   and a `confirm` parameter naming the snapshot id; refuses to restore a snapshot
   onto a different endpoint than it was taken from (manifest site URL must match);
   takes an automatic pre-restore safety snapshot first.
4. `wp_delete_snapshot` — requires `AllowDelete=true`.
5. `wp_export_content` / `wp_import_content` — WXR export/import for content-only
   moves between sites (CLI `export`/`import`).

### Phase 5 — Diagnostics & client operations polish

1. **Log tooling** — `wp_read_debug_log` (tail with size cap), `wp_clear_debug_log`,
   `wp_set_debug` (WP_DEBUG/WP_DEBUG_LOG via `config set`).
2. **Doctor flow** — `wp_diagnose` composite: front-page HTTP status, REST reachability,
   site-health test aggregate, update backlog, cron overdue count, debug.log error
   tail, DB check — one structured report for "the site is acting up" triage, using
   whichever channels the endpoint has and marking the rest skipped.
3. **Plugin conflict helper** — `wp_bisect_plugins` (guided: deactivate half /
   reactivate, driven by the client between calls; CLI channel, write-gated).
4. **README + MCPHub** — document the endpoint model, new gates, and snapshot
   workflow; add the server to MCPHub's catalogue table.

## Safety model additions

| Gate | Default | Unlocks |
| --- | --- | --- |
| `Wordpress:AllowRestPassthrough` | `false` | `wp_rest_request` |
| `Management:AllowCliManagement` | `false` | all Phase 3 CLI-backed tools |
| `Management:AllowArbitraryCli` | `false` | raw `wp_cli` only |
| `Snapshots:AllowRestore` | `false` | `wp_restore_snapshot` |

Existing rules keep applying: `ReadOnly=true` blocks every mutating tool regardless of
the gates above (per-endpoint overrides can only be stricter); error messages always
name the exact config keys required.

## Test plan

The WSL2 rig (wordpress:latest + MariaDB in Docker, WP-CLI container) exercises the
REST phases and the Docker management mode. Add WooCommerce to the test site for the
`wc/v3` tools, and cover: snapshot → mutate site → restore → verify content reverted;
core update path on a pinned older wordpress image; provisioning a second fresh site
from nothing via the Docker mode.

Endpoint/channel cases: two endpoints where one is REST-only and one is CLI-only,
confirming each side's tools work and the other side's tools return the
channel-not-configured error naming the endpoint; empty registry starting cleanly;
`site` resolution (explicit → DefaultSite → single endpoint → error listing names);
unknown `site` error; the identity cross-check refusing a mutation when an endpoint's
path deliberately points at a different install than its `BaseUrl`; per-endpoint
`ReadOnly=true` override blocking writes on one site while another stays writable;
legacy flat `Wordpress:*` config still working as endpoint `default`; and
`wp_restore_snapshot` refusing a snapshot taken from a different endpoint.
