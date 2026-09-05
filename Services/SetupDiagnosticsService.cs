using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Configuration;
using Microsoft.Extensions.Options;

namespace WordpressMCPSharp.Services;

/// <summary>
/// Onboarding diagnostics. These run without any endpoint configured — they take their inputs as
/// parameters — and turn the common WordPress connection traps into findings that name the fix:
/// plain permalinks serving HTML from /wp-json/, application passwords silently rejected over
/// plain HTTP, a BaseUrl that redirects elsewhere, and Authorization headers stripped by the host.
/// </summary>
public sealed class SetupDiagnosticsService
{
    private const int MaxRedirects = 10;

    private readonly WordpressOptions _options;

    public SetupDiagnosticsService(IOptions<WordpressOptions> options)
    {
        _options = options.Value;
    }

    public sealed record Finding(string Check, string Status, string Detail, string? Fix = null);

    public sealed class ProbeResult
    {
        public string RequestedUrl { get; init; } = string.Empty;
        public string? FinalUrl { get; set; }
        public bool IsWordPress { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<Finding> Findings { get; } = new();
        public Dictionary<string, object?> Site { get; } = new();
        public Dictionary<string, object?> Tls { get; } = new();
        public Dictionary<string, object?> Authentication { get; } = new();
        public List<string> Namespaces { get; } = new();
        public Dictionary<string, object?> SuggestedConfiguration { get; } = new();
        public List<string> NextSteps { get; } = new();
    }

    public async Task<ProbeResult> ProbeAsync(
        string url,
        string? username,
        string? applicationPassword,
        bool allowInvalidCertificate,
        CancellationToken ct)
    {
        var result = new ProbeResult { RequestedUrl = url };

        if (!TryNormalizeUrl(url, out var uri, out var urlError))
        {
            result.Findings.Add(new Finding("url", "fail", urlError!,
                "Pass the site's address, for example https://example.com/ or http://localhost:8080/."));
            result.Summary = "The supplied URL could not be parsed.";
            return result;
        }

        await CheckDnsAsync(uri!, result, ct);
        var reachable = await CheckReachabilityAsync(uri!, allowInvalidCertificate, result, ct);
        if (!reachable)
        {
            result.Summary = "The site could not be reached. Fix connectivity before configuring an endpoint.";
            return result;
        }

        var effective = new Uri(result.FinalUrl ?? uri!.ToString());
        if (effective.Scheme == Uri.UriSchemeHttps)
        {
            await InspectCertificateAsync(effective, result, ct);
        }
        else
        {
            await CheckHttpsAvailabilityAsync(effective, result, ct);
        }

        var restOk = await CheckRestApiAsync(effective, allowInvalidCertificate, result, ct);

        if (restOk && !string.IsNullOrWhiteSpace(username))
        {
            await CheckAuthenticationAsync(effective, username!, applicationPassword ?? string.Empty, allowInvalidCertificate, result, ct);
        }
        else if (restOk)
        {
            result.Findings.Add(new Finding("authentication", "warn",
                "No credentials supplied, so authentication was not tested.",
                "Re-run with username and applicationPassword to verify login and see which tool groups the account can drive. " +
                "Create one in wp-admin under Users → Profile → Application Passwords."));
        }

        ReconcileHttpFinding(result, effective);
        BuildSuggestedConfiguration(result, effective, username, applicationPassword, allowInvalidCertificate);
        Summarize(result);
        return result;
    }

    /// <summary>
    /// The HTTPS check runs before authentication. If application passwords turned out to work over plain
    /// HTTP, the site already sets WP_ENVIRONMENT_TYPE — so replace the "add it" advice with the real risk.
    /// </summary>
    private static void ReconcileHttpFinding(ProbeResult result, Uri effective)
    {
        if (effective.Scheme != Uri.UriSchemeHttp) return;
        if (result.Authentication.GetValueOrDefault("authenticated") as bool? != true) return;

        var index = result.Findings.FindIndex(f => f.Check == "https");
        if (index < 0) return;

        result.Findings[index] = new Finding("https", "warn",
            "The site is served over plain HTTP. Application passwords are being accepted, so the site is marked as a " +
            "development environment, but credentials and content travel unencrypted.",
            "Fine for a local development site. Enable HTTPS before managing anything internet-facing.");
    }

    // ---- individual checks --------------------------------------------------------------------

    private static async Task CheckDnsAsync(Uri uri, ProbeResult result, CancellationToken ct)
    {
        if (IPAddress.TryParse(uri.Host, out _))
        {
            result.Findings.Add(new Finding("dns", "ok", $"Host is a literal IP address ({uri.Host}); no DNS lookup needed."));
            return;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            result.Findings.Add(addresses.Length > 0
                ? new Finding("dns", "ok", $"{uri.Host} resolves to {string.Join(", ", addresses.Select(a => a.ToString()).Take(4))}.")
                : new Finding("dns", "fail", $"{uri.Host} did not resolve to any address.", "Check the hostname spelling and the DNS records."));
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            result.Findings.Add(new Finding("dns", "fail", $"{uri.Host} could not be resolved: {ex.Message}",
                "Check the hostname, or use an IP address or hosts-file entry if this site is internal."));
        }
    }

    /// <summary>Follow the redirect chain by hand so the final URL can be recommended as the BaseUrl.</summary>
    private async Task<bool> CheckReachabilityAsync(Uri uri, bool allowInvalidCertificate, ProbeResult result, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (allowInvalidCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        else
        {
            // Report certificate problems as findings rather than failing the whole probe here.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        using var http = CreateClient(handler);

        var current = uri;
        var hops = new List<string>();
        for (var i = 0; i < MaxRedirects; i++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                result.Findings.Add(new Finding("reachability", "fail",
                    $"Could not connect to {current}: {Flatten(ex)}",
                    "Check the site is running and that this server can reach it (firewall, VPN, container networking)."));
                return false;
            }
            catch (TaskCanceledException)
            {
                result.Findings.Add(new Finding("reachability", "fail",
                    $"Connection to {current} timed out.",
                    "Check the host is up and reachable from this server."));
                return false;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var location = response.Headers.Location;
                if (status is >= 300 and < 400 && location is not null)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    hops.Add($"{status} {current} → {next}");
                    current = next;
                    continue;
                }

                result.FinalUrl = SiteRoot(current);

                if (hops.Count > 0)
                {
                    result.Findings.Add(new Finding("redirects", "warn",
                        $"The site redirects: {string.Join("; ", hops)}. Final response {status} from {current}.",
                        $"Use the final address as BaseUrl: {result.FinalUrl}. Configuring the pre-redirect URL causes " +
                        "authentication to fail, because WordPress drops the Authorization header across redirects."));
                }
                else
                {
                    result.Findings.Add(new Finding("reachability", "ok", $"{current} responded {status} {response.ReasonPhrase}."));
                }

                if (status >= 500)
                {
                    result.Findings.Add(new Finding("http-status", "fail",
                        $"The site returned a server error ({status}).",
                        "The site itself is failing. Check the host's PHP error log and WordPress debug.log."));
                    return false;
                }

                return true;
            }
        }

        result.Findings.Add(new Finding("redirects", "fail",
            $"More than {MaxRedirects} redirects starting at {uri}: {string.Join("; ", hops)}",
            "The site has a redirect loop. Check its Site Address / WordPress Address settings and any HTTPS or www redirect rules."));
        return false;
    }

    private async Task InspectCertificateAsync(Uri uri, ProbeResult result, CancellationToken ct)
    {
        X509Certificate2? certificate = null;
        SslPolicyErrors policyErrors = SslPolicyErrors.None;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port, ct);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, cert, _, errors) =>
                {
                    if (cert is not null) certificate = new X509Certificate2(cert);
                    policyErrors = errors;
                    return true;
                });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, ct);
        }
        catch (Exception ex)
        {
            result.Findings.Add(new Finding("tls", "warn", $"Could not inspect the TLS certificate: {Flatten(ex)}"));
            return;
        }

        if (certificate is null)
        {
            result.Findings.Add(new Finding("tls", "warn", "The server did not present a certificate."));
            return;
        }

        using (certificate)
        {
            var daysRemaining = (int)(certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            var subjectNames = SubjectNames(certificate);

            result.Tls["subject"] = certificate.Subject;
            result.Tls["issuer"] = certificate.Issuer;
            result.Tls["names"] = subjectNames;
            result.Tls["validFrom"] = certificate.NotBefore.ToUniversalTime();
            result.Tls["validTo"] = certificate.NotAfter.ToUniversalTime();
            result.Tls["daysRemaining"] = daysRemaining;
            result.Tls["policyErrors"] = policyErrors.ToString();

            if (policyErrors == SslPolicyErrors.None)
            {
                result.Findings.Add(new Finding("tls", "ok",
                    $"Valid certificate for {string.Join(", ", subjectNames)}, issued by {ShortIssuer(certificate)}, expires in {daysRemaining} days."));
            }
            else
            {
                var nameMismatch = policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
                var detail = nameMismatch
                    ? $"The certificate is for {string.Join(", ", subjectNames)}, which does not match {uri.Host}."
                    : $"Certificate validation failed ({policyErrors}). Issued by {ShortIssuer(certificate)}.";
                result.Findings.Add(new Finding("tls", "warn", detail,
                    nameMismatch
                        ? $"Use a URL matching the certificate ({subjectNames.FirstOrDefault() ?? "the certificate name"}), or fix the certificate. " +
                          "As a last resort for a homelab site, set the endpoint's RestApi:AllowInvalidCertificate=true."
                        : "Install a trusted certificate. For a self-signed homelab site only, set the endpoint's RestApi:AllowInvalidCertificate=true."));
            }

            if (daysRemaining < 0)
            {
                result.Findings.Add(new Finding("tls-expiry", "fail",
                    $"The certificate expired on {certificate.NotAfter.ToUniversalTime():u}.", "Renew the certificate."));
            }
            else if (daysRemaining <= 30)
            {
                result.Findings.Add(new Finding("tls-expiry", "warn",
                    $"The certificate expires in {daysRemaining} days ({certificate.NotAfter.ToUniversalTime():u}).", "Renew it soon."));
            }
        }
    }

    /// <summary>The site answered on HTTP. Application passwords need HTTPS, so check whether HTTPS is available.</summary>
    private async Task CheckHttpsAvailabilityAsync(Uri httpUri, ProbeResult result, CancellationToken ct)
    {
        var httpsUri = new UriBuilder(httpUri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        var isLocal = httpUri.IsLoopback || IsPrivateHost(httpUri.Host);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = CreateClient(handler, TimeSpan.FromSeconds(10));

        try
        {
            using var response = await http.GetAsync(httpsUri, HttpCompletionOption.ResponseHeadersRead, ct);
            result.Findings.Add(new Finding("https", "warn",
                $"The URL uses HTTP, but {httpsUri} also answers ({(int)response.StatusCode}).",
                $"Use {SiteRoot(httpsUri)} as BaseUrl. WordPress rejects application passwords over plain HTTP."));
            result.Tls["httpsAvailable"] = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            result.Tls["httpsAvailable"] = false;
            result.Findings.Add(isLocal
                ? new Finding("https", "warn",
                    "The site is served over plain HTTP and HTTPS is not available.",
                    "WordPress only accepts application passwords over HTTPS. For a local development site, add " +
                    "define( 'WP_ENVIRONMENT_TYPE', 'local' ); to wp-config.php to allow them over HTTP.")
                : new Finding("https", "fail",
                    "The site is served over plain HTTP and HTTPS is not available.",
                    "WordPress rejects application passwords over HTTP for non-local sites. Enable HTTPS on the site " +
                    "(or, for a development host only, set WP_ENVIRONMENT_TYPE=local in wp-config.php)."));
        }
    }

    private async Task<bool> CheckRestApiAsync(Uri siteUri, bool allowInvalidCertificate, ProbeResult result, CancellationToken ct)
    {
        using var handler = new HttpClientHandler();
        if (allowInvalidCertificate) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var http = CreateClient(handler);

        var root = SiteRoot(siteUri);
        var pretty = await TryFetchJsonAsync(http, root + "wp-json/", ct);
        var plain = pretty.Node is null ? await TryFetchJsonAsync(http, root + "index.php?rest_route=/", ct) : default;

        var node = pretty.Node ?? plain.Node;
        if (node is null)
        {
            var status = pretty.Status ?? plain.Status;
            var htmlReturned = pretty.WasHtml || plain.WasHtml;
            result.Findings.Add(new Finding("rest-api", "fail",
                htmlReturned
                    ? "The REST API returned HTML instead of JSON on both /wp-json/ and ?rest_route=/."
                    : $"The REST API did not respond with JSON (status {status?.ToString() ?? "no response"}).",
                "If a security plugin or WAF disables the REST API, re-enable it. Confirm this is a WordPress site and that " +
                "BaseUrl is the site root (not /wp-admin or /wp-json)."));
            return false;
        }

        result.IsWordPress = node["name"] is not null || node["namespaces"] is not null;
        if (!result.IsWordPress)
        {
            result.Findings.Add(new Finding("wordpress", "fail",
                "The URL responded with JSON, but it does not look like the WordPress REST API.",
                "Check the URL points at a WordPress site root."));
            return false;
        }

        result.Findings.Add(new Finding("wordpress", "ok", "Confirmed this is a WordPress site."));

        if (pretty.Node is not null)
        {
            result.Findings.Add(new Finding("permalinks", "ok", "Pretty permalinks are enabled; the REST API answers on /wp-json/."));
            result.Site["restAddressing"] = "pretty (/wp-json/)";
        }
        else
        {
            result.Findings.Add(new Finding("permalinks", "warn",
                "The site uses plain permalinks, so /wp-json/ serves the site's HTML; the REST API only answers on ?rest_route=.",
                "This server handles it automatically. To enable pretty permalinks, set Settings → Permalinks to " +
                "'Post name' in wp-admin (or run: wp rewrite structure '/%postname%/' --hard)."));
            result.Site["restAddressing"] = "plain (?rest_route=)";
        }

        result.Site["name"] = node["name"]?.GetValue<string?>();
        result.Site["description"] = node["description"]?.GetValue<string?>();
        result.Site["url"] = node["url"]?.GetValue<string?>();
        result.Site["home"] = node["home"]?.GetValue<string?>();
        result.Site["timezone"] = node["timezone_string"]?.GetValue<string?>();
        result.Site["gmtOffset"] = node["gmt_offset"]?.ToString();

        if (node["namespaces"] is JsonArray namespaces)
        {
            result.Namespaces.AddRange(namespaces.Select(n => n?.GetValue<string?>() ?? string.Empty).Where(n => n.Length > 0));
        }

        // A siteurl that differs from the probed address breaks REST calls through redirects.
        var declared = node["url"]?.GetValue<string?>();
        if (!string.IsNullOrWhiteSpace(declared) && !UrlsEquivalent(declared!, root))
        {
            result.Findings.Add(new Finding("site-url", "warn",
                $"WordPress reports its address as {declared}, but was probed at {root.TrimEnd('/')}.",
                $"Use {SiteRoot(new Uri(declared!))} as BaseUrl — requests to the other address get redirected and lose authentication."));
            result.FinalUrl = SiteRoot(new Uri(declared!));
        }

        if (result.Namespaces.Contains("wc/v3"))
        {
            result.Findings.Add(new Finding("woocommerce", "ok", "WooCommerce is active (wc/v3 namespace present); WooCommerce tools will work."));
        }

        return true;
    }

    private async Task CheckAuthenticationAsync(
        Uri siteUri, string username, string applicationPassword, bool allowInvalidCertificate, ProbeResult result, CancellationToken ct)
    {
        using var handler = new HttpClientHandler();
        if (allowInvalidCertificate) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var http = CreateClient(handler);

        // WordPress accepts the password with or without the spaces it displays.
        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{applicationPassword}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credential);

        var root = SiteRoot(siteUri);
        var pretty = string.Equals(result.Site.GetValueOrDefault("restAddressing") as string, "pretty (/wp-json/)", StringComparison.Ordinal);
        var meUrl = pretty
            ? root + "wp-json/wp/v2/users/me?context=edit"
            : root + "index.php?rest_route=/wp/v2/users/me&context=edit";

        var (node, status, _) = await TryFetchJsonAsync(http, meUrl, ct);

        if (node is null || status is not (>= 200 and < 300))
        {
            var code = node?["code"]?.GetValue<string?>();
            var message = node?["message"]?.GetValue<string?>();
            result.Authentication["authenticated"] = false;
            result.Authentication["status"] = status;
            result.Authentication["code"] = code;

            var overHttp = siteUri.Scheme == Uri.UriSchemeHttp;
            var fix = code switch
            {
                "rest_not_logged_in" or "rest_cannot_view" when overHttp =>
                    "The credentials were not accepted over plain HTTP. WordPress ignores application passwords on non-HTTPS sites " +
                    "unless wp-config.php sets define( 'WP_ENVIRONMENT_TYPE', 'local' ); — add that for a development site, or use HTTPS.",
                "rest_not_logged_in" =>
                    "Check the username, and that the application password was copied in full. Create a fresh one under " +
                    "Users → Profile → Application Passwords (or: wp user application-password create <user> wordpressmcpsharp). " +
                    "If it still fails, the host may be stripping the Authorization header — see the authorization-header check below.",
                _ => "Verify the username and application password, and that the account still exists and is not blocked.",
            };

            result.Findings.Add(new Finding("authentication", "fail",
                $"Authentication failed (HTTP {status}{(code is null ? "" : $", {code}")}){(message is null ? "." : $": {message}")}",
                fix));

            await CheckAuthorizationHeaderAsync(http, root, pretty, result, ct);
            return;
        }

        var roles = (node["roles"] as JsonArray)?.Select(r => r?.GetValue<string?>()).Where(r => r is not null).ToList() ?? new List<string?>();
        var capabilities = node["capabilities"] as JsonObject;

        result.Authentication["authenticated"] = true;
        result.Authentication["userId"] = node["id"]?.GetValue<int?>();
        result.Authentication["username"] = node["username"]?.GetValue<string?>() ?? node["slug"]?.GetValue<string?>();
        result.Authentication["name"] = node["name"]?.GetValue<string?>();
        result.Authentication["roles"] = roles;

        result.Findings.Add(new Finding("authentication", "ok",
            $"Authenticated as {node["name"]?.GetValue<string?>() ?? username} (id {node["id"]?.GetValue<int?>()}, roles: {string.Join(", ", roles)})."));

        // Report which tool groups this account can actually drive.
        var groups = new (string Capability, string Group)[]
        {
            ("edit_posts", "posts, pages and content"),
            ("upload_files", "media"),
            ("moderate_comments", "comments"),
            ("manage_categories", "categories and tags"),
            ("list_users", "user listing"),
            ("edit_users", "user management"),
            ("manage_options", "site settings and menus"),
            ("activate_plugins", "plugins"),
            ("switch_themes", "themes"),
            ("install_plugins", "plugin installation"),
        };

        var allowed = new List<string>();
        var denied = new List<string>();
        foreach (var (capability, group) in groups)
        {
            var has = capabilities?[capability]?.GetValue<bool?>() ?? false;
            (has ? allowed : denied).Add(group);
        }

        result.Authentication["enabledToolGroups"] = allowed;
        result.Authentication["unavailableToolGroups"] = denied;

        result.Findings.Add(denied.Count == 0
            ? new Finding("capabilities", "ok", "The account has full administrative capabilities; every tool group is available.")
            : new Finding("capabilities", "warn",
                $"Available to this account: {string.Join(", ", allowed)}. Not available: {string.Join(", ", denied)}.",
                "Use an administrator account if you need the unavailable groups."));
    }

    /// <summary>Some hosts (Apache/CGI, certain nginx setups) drop the Authorization header before PHP sees it.</summary>
    private async Task CheckAuthorizationHeaderAsync(HttpClient http, string root, bool pretty, ProbeResult result, CancellationToken ct)
    {
        var url = pretty
            ? root + "wp-json/wp-site-health/v1/tests/authorization-header"
            : root + "index.php?rest_route=/wp-site-health/v1/tests/authorization-header";

        var (node, status, _) = await TryFetchJsonAsync(http, url, ct);
        if (node is null || status is not (>= 200 and < 300)) return;

        var testStatus = node["status"]?.GetValue<string?>();
        var label = node["label"]?.GetValue<string?>();
        if (string.Equals(testStatus, "good", StringComparison.OrdinalIgnoreCase))
        {
            result.Findings.Add(new Finding("authorization-header", "ok",
                "The host passes the Authorization header through to WordPress, so application passwords can work."));
        }
        else
        {
            result.Findings.Add(new Finding("authorization-header", "fail",
                $"WordPress reports a problem with the Authorization header{(label is null ? "" : $": {label}")}.",
                "The web server is stripping the Authorization header before PHP sees it. On Apache/CGI add: " +
                "SetEnvIf Authorization \"(.*)\" HTTP_AUTHORIZATION=$1 (or RewriteRule with [E=HTTP_AUTHORIZATION:%{HTTP:Authorization}])."));
        }
    }

    // ---- output shaping -----------------------------------------------------------------------

    private void BuildSuggestedConfiguration(
        ProbeResult result, Uri effective, string? username, string? applicationPassword, bool allowInvalidCertificate)
    {
        if (!result.IsWordPress) return;

        var authenticated = result.Authentication.GetValueOrDefault("authenticated") as bool? ?? false;
        var needsInsecureTls = allowInvalidCertificate
            || (result.Tls.GetValueOrDefault("policyErrors") is string errors && errors != nameof(SslPolicyErrors.None));

        var rest = new Dictionary<string, object?>
        {
            ["BaseUrl"] = result.FinalUrl ?? SiteRoot(effective),
            ["Username"] = string.IsNullOrWhiteSpace(username) ? "<wordpress-username>" : username,
            ["ApplicationPassword"] = authenticated && !string.IsNullOrWhiteSpace(applicationPassword)
                ? "<the application password you probed with>"
                : "<application password from Users → Profile → Application Passwords>",
        };
        if (needsInsecureTls) rest["AllowInvalidCertificate"] = true;

        var suggestedName = SuggestName(result.FinalUrl ?? effective.ToString());
        result.SuggestedConfiguration["Endpoints"] = new Dictionary<string, object?>
        {
            [suggestedName] = new Dictionary<string, object?> { ["RestApi"] = rest },
        };
        result.SuggestedConfiguration["DefaultSite"] = suggestedName;
        result.SuggestedConfiguration["_note"] =
            $"Put this in WordpressMCPSharp.Local.json (never in the checked-in WordpressMCPSharp.json). " +
            $"Writes stay disabled until Wordpress:ReadOnly=false.";

        if (!authenticated)
        {
            result.NextSteps.Add(string.IsNullOrWhiteSpace(username)
                ? "Create an application password (wp-admin → Users → Profile → Application Passwords) and re-run wp_setup_probe with username and applicationPassword to verify it."
                : "Fix the authentication finding above, then re-run wp_setup_probe to confirm.");
        }
        else
        {
            result.NextSteps.Add($"Add the suggested Endpoints block to WordpressMCPSharp.Local.json and restart the server.");
            result.NextSteps.Add($"Run wp_test_endpoint with site='{suggestedName}' to confirm the configured endpoint works.");
            result.NextSteps.Add("Set Wordpress:ReadOnly=false when you are ready to allow writes (AllowDelete and AllowPluginInstall are separate gates).");
        }
    }

    private static void Summarize(ProbeResult result)
    {
        var fails = result.Findings.Count(f => f.Status == "fail");
        var warns = result.Findings.Count(f => f.Status == "warn");
        var authenticated = result.Authentication.GetValueOrDefault("authenticated") as bool? ?? false;

        result.Summary = (result.IsWordPress, fails, authenticated) switch
        {
            (false, _, _) => "Not usable yet: the site did not present a working WordPress REST API. See the failing checks.",
            (true, 0, true) => $"Ready to configure. WordPress reachable and authenticated{(warns > 0 ? $", with {warns} warning(s) to review" : "")}.",
            (true, 0, false) => "WordPress reachable, but authentication was not confirmed. Supply credentials and re-run.",
            _ => $"WordPress detected, but {fails} check(s) failed. Fix those before configuring the endpoint.",
        };
    }

    // ---- helpers ------------------------------------------------------------------------------

    private HttpClient CreateClient(HttpClientHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 5, 60)),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static async Task<(JsonNode? Node, int? Status, bool WasHtml)> TryFetchJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return (null, (int)response.StatusCode, true);
            }
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return (null, (int)response.StatusCode, false);
            try
            {
                return (JsonNode.Parse(text), (int)response.StatusCode, false);
            }
            catch (System.Text.Json.JsonException)
            {
                return (null, (int)response.StatusCode, text.TrimStart().StartsWith('<'));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, null, false);
        }
    }

    private static bool TryNormalizeUrl(string url, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        var candidate = url.Trim();
        if (candidate.Length == 0)
        {
            error = "No URL supplied.";
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = $"'{url}' is not a valid http(s) URL.";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Normalise to a site root with a trailing slash, dropping known WordPress sub-paths.</summary>
    private static string SiteRoot(Uri uri)
    {
        var path = uri.AbsolutePath;
        foreach (var suffix in new[] { "/wp-json/", "/wp-json", "/wp-admin/", "/wp-admin", "/wp-login.php", "/index.php" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }
        if (!path.EndsWith('/')) path += "/";
        return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri.ToString();
    }

    private static bool UrlsEquivalent(string a, string b)
    {
        static string Normalize(string value) => value.TrimEnd('/')
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SubjectNames(X509Certificate2 certificate)
    {
        var names = new List<string>();
        var common = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!string.IsNullOrWhiteSpace(common)) names.Add(common);

        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17") continue; // subjectAltName
            foreach (var line in extension.Format(multiLine: true).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim();
                var separator = value.IndexOf('=');
                if (separator >= 0) value = value[(separator + 1)..].Trim();
                if (value.Length > 0 && !names.Contains(value, StringComparer.OrdinalIgnoreCase)) names.Add(value);
            }
        }
        return names;
    }

    private static string ShortIssuer(X509Certificate2 certificate)
    {
        var issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        return string.IsNullOrWhiteSpace(issuer) ? certificate.Issuer : issuer;
    }

    private static bool IsPrivateHost(string host) =>
        host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var ip) && IsPrivateAddress(ip));

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31));
    }

    private static string SuggestName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "site1";
        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        var label = host.Split('.').FirstOrDefault(part => part.Length > 0) ?? "site1";
        var cleaned = new string(label.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return cleaned.Length == 0 ? "site1" : cleaned.ToLowerInvariant();
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message)) messages.Add(current.Message);
        }
        return string.Join(" → ", messages);
    }
}
