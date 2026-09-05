# WordpressMCPSharp — Roadmap

Goal: install, configure, update, maintain and diagnose a WordPress site end to end —
build-out, plugin management, snapshots/backups and restore, content and page management
for clients (including covers/featured images), user management, and data management in
popular plugins such as WooCommerce.

This document records what the current REST-only server can and cannot do, and the plan
to close the gaps.

## Where we are

v0.1 wraps the core WordPress REST API (60 `wp_*` tools): posts, pages, media, comments,
users + application passwords, taxonomies, plugins (list/install/activate/deactivate/delete),
themes (read-only), menus, settings, search, site info. Read-only by default with
`AllowDelete` and `AllowPluginInstall` double gates and per-category feature toggles.

## Gap analysis

### Gaps the REST API can cover (missing tools only)

| Capability | Gap | Route |
| --- | --- | --- |
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

**Architectural decision:** add an optional **management channel** that executes WP-CLI
against the site, following the family's RouterOSMCPSharp precedent (REST plus SSH
fallback) and RemoteAdminMCPSharp's double-gated command execution. Modes:

- `Local` — `wp` on the same host (server runs next to the site).
- `Ssh` — run `wp` over SSH (host, port, user, key/password, remote path).
- `Docker` — `docker exec <container> wp …` for containerised sites.
- `None` (default) — channel disabled; REST-only behaviour, exactly as v0.1.

All channel tools are additionally gated (see Safety) and every invocation shells to
`wp` with `--path` pinned from configuration — never a free-form working directory.

## Channel model

The two channels are **independent**. Any of these deployments is valid:

| Deployment | REST configured | CLI configured | What works |
| --- | --- | --- | --- |
| Content/API management (v0.1 behaviour) | yes | no | REST tools only |
| Ops-only (SSH box, no app password issued) | no | yes | CLI tools only |
| Full lifecycle | yes | yes | everything, incl. composite diagnostics |

Rules:

1. **Neither channel is required.** The server must start (and pass `/healthz`) with only
   one — or neither — configured. This requires a v0.1 fix: `WordpressService` currently
   throws at construction when `Wordpress:BaseUrl` is missing; REST configuration must be
   validated lazily at call time instead.
2. **Every tool belongs to exactly one channel**, marked in the README tool list
   (`[REST]` / `[CLI]`). The only exceptions are composite diagnostics (`wp_diagnose`),
   which run the checks available on whichever channels are configured and report the
   skipped ones as `"skipped": "channel not configured"` rather than failing.
3. **Proper errors, family style.** A tool whose channel is not configured throws a
   `McpException` naming the exact keys to set, e.g.:
   - `MCP tool 'wp_core_update' requires the CLI management channel. Set Management:Mode
     (Local/Ssh/Docker) and configure Management:Sites. Current mode: None.`
   - `MCP tool 'wp_create_post' requires the REST channel. Set Wordpress:BaseUrl,
     Wordpress:Username and Wordpress:ApplicationPassword.`
   Consistent with the rest of the family, tools stay visible in `tools/list` and fail
   with these errors when called (same behaviour as the existing feature toggles).
4. Startup banner and `/healthz` report each channel's status independently
   (`rest: configured/unconfigured`, `management: None/Local/Ssh/Docker`).

## Site targeting (multi-site SSH hosts)

An SSH host often serves several WordPress installs. Guard rails:

1. **Named site registry, never raw paths.** CLI-backed tools take an optional `site`
   parameter that resolves against configuration only:

   ```json
   "Management": {
     "Mode": "Ssh",
     "Ssh": { "Host": "web1.example.com", "Username": "deploy", "PrivateKeyPath": "..." },
     "DefaultSite": "clienta",
     "Sites": {
       "clienta": { "Path": "/var/www/clienta.com", "Url": "https://clienta.com" },
       "clientb": { "Path": "/var/www/clientb.net", "Url": "https://clientb.net" }
     }
   }
   ```

   An MCP client can never supply a filesystem path. Unknown `site` values fail with an
   error that lists the configured site names. With no `site` argument, `DefaultSite`
   is used; if neither is set and more than one site is configured, the tool errors
   rather than guessing.
2. **Identity cross-check before mutating.** Before any mutating CLI operation, the
   service runs `wp option get siteurl --path=<site path>` and compares it to the
   profile's `Url` (when set, case-insensitive, ignoring scheme). Mismatch → refuse with
   both values in the error. This catches stale paths, moved installs, and copy-paste
   config mistakes — a wrong-site write is the worst failure mode this server has.
3. **REST↔CLI correlation is explicit.** Composite tools that combine channels
   (`wp_diagnose`, snapshot manifests) only treat the REST site and a CLI site as the
   same site when the site profile's `Url` matches `Wordpress:BaseUrl`; otherwise the
   CLI half runs per-site and the REST half is reported separately.
4. `wp_list_sites` (CLI channel) returns the configured registry — names, paths,
   URLs, and each site's cross-check status — so clients can discover valid targets.

## Plan

### Phase 1 — REST coverage (no new channel)

New options: `EnableWooCommerce` (default true), `EnableSiteHealth` (default true),
`AllowRestPassthrough` (default false, second gate).

0. **Channel-independence groundwork** — make REST configuration lazy: move the
   `BaseUrl` check out of the `WordpressService` constructor into call-time validation
   with the channel-error message above, so a CLI-only deployment starts cleanly.
1. **Generic REST passthrough** — `wp_rest_request(method, route, bodyJson?, query?)`
   for any namespace discovered via `wp_site_info`. GET allowed in read-only mode;
   mutating verbs need write mode; the tool itself needs `AllowRestPassthrough=true`.
   This single tool unlocks every well-behaved plugin API (ACF, Yoast, Rank Math, …).
2. **WooCommerce tools** (`wc/v3`, app-password auth; optional consumer key/secret
   options for stores that require them): `wp_wc_list_products`, `wp_wc_get_product`,
   `wp_wc_create_product`, `wp_wc_update_product`, `wp_wc_delete_product`,
   `wp_wc_list_orders`, `wp_wc_get_order`, `wp_wc_update_order` (status/notes),
   `wp_wc_list_customers`, `wp_wc_list_product_categories`, `wp_wc_create_product_category`,
   `wp_wc_get_reports` (sales/top sellers), `wp_wc_get_settings`. Detect absence of the
   `wc/v3` namespace and return a clear "WooCommerce not installed/active" error.
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

### Phase 2 — Management channel + lifecycle

New options section `Management`: `Mode` (None/Local/Ssh/Docker), `WpCliPath`,
`Sites` + `DefaultSite` (the registry above), `Ssh:{Host,Port,Username,PrivateKeyPath,Password}`,
`Docker:{Container}`, plus gates `AllowCliManagement` (master, default false) and
`AllowArbitraryCli` (default false, for the raw escape hatch only).

1. **Channel plumbing** — `ManagementService` with one execution primitive
   (argument-list based, no shell string interpolation), timeout, output capture,
   and structured `{exitCode, stdout, stderr}` results. Site resolution + identity
   cross-check live here so every CLI tool inherits them. Startup banner reports the
   active mode and site count; `/healthz` gains a `management` field. `wp_list_sites`
   ships in this step.
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

### Phase 3 — Snapshots, backup, restore

New options: `Snapshots:{Directory, MaxSnapshots, IncludeUploads}` and gate
`AllowRestore` (default false — restoring overwrites the site).

1. `wp_create_snapshot` — `wp db export` + tar of `wp-content` (optionally uploads),
   manifest JSON (WP version, plugin list + versions, active theme, site URL, sizes,
   timestamp). Write mode required.
2. `wp_list_snapshots`, `wp_get_snapshot` — manifest browsing, integrity check.
3. `wp_restore_snapshot` — DB import + file restore, **requires `AllowRestore=true`**
   and a `confirm` parameter naming the snapshot id; takes an automatic pre-restore
   safety snapshot first.
4. `wp_delete_snapshot` — requires `AllowDelete=true`.
5. `wp_export_content` / `wp_import_content` — WXR export/import for content-only
   moves between sites (CLI `export`/`import`).

### Phase 4 — Diagnostics & client operations polish

1. **Log tooling** — `wp_read_debug_log` (tail with size cap), `wp_clear_debug_log`,
   `wp_set_debug` (WP_DEBUG/WP_DEBUG_LOG via `config set`).
2. **Doctor flow** — `wp_diagnose` composite: front-page HTTP status, REST reachability,
   site-health test aggregate, update backlog, cron overdue count, debug.log error
   tail, DB check — one structured report for "the site is acting up" triage.
3. **Plugin conflict helper** — `wp_bisect_plugins` (guided: deactivate half /
   reactivate, driven by the client between calls; CLI mode only, write-gated).
4. **README + MCPHub** — document the channel model, new gates, and snapshot
   workflow; add the server to MCPHub's catalogue table.

## Safety model additions

| Gate | Default | Unlocks |
| --- | --- | --- |
| `Wordpress:AllowRestPassthrough` | `false` | `wp_rest_request` |
| `Management:AllowCliManagement` | `false` | all Phase 2/3 CLI-backed tools |
| `Management:AllowArbitraryCli` | `false` | raw `wp_cli` only |
| `Snapshots:AllowRestore` | `false` | `wp_restore_snapshot` |

Existing rules keep applying: `ReadOnly=true` blocks every mutating tool regardless of
the gates above; error messages always name the exact config keys required.

## Test plan

The WSL2 rig (wordpress:latest + MariaDB in Docker, WP-CLI container) already exercises
Phase 1 and the Docker channel mode. Add WooCommerce to the test site for the `wc/v3`
tools, and cover: snapshot → mutate site → restore → verify content reverted; core
update path on a pinned older wordpress image; provisioning a second fresh site from
nothing via the Docker channel.

Channel-model cases: start with REST unconfigured and confirm clean startup, healthy
`/healthz`, working CLI tools, and the channel error from REST tools; the mirror case
with `Management:Mode=None`; two registered sites where the identity cross-check
refuses a mutation when a profile's `Url` deliberately mismatches the install; an
unknown `site` argument returning the configured-names error; and no `DefaultSite`
with multiple sites erroring instead of guessing.
