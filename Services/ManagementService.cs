using System.Diagnostics;
using System.Text;
using WordpressMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Renci.SshNet;

namespace WordpressMCPSharp.Services;

/// <summary>
/// Runs WP-CLI against a configured endpoint over one of three channels: SSH, a local shell, or
/// `docker exec`. Commands are built from argument lists and quoted here — no caller-supplied
/// string is ever handed to a shell unquoted — and the install path always comes from configuration,
/// never from an MCP client.
/// </summary>
public sealed class ManagementService
{
    private readonly EndpointRegistry _registry;
    private readonly ManagementOptions _options;

    public ManagementService(EndpointRegistry registry, IOptions<ManagementOptions> options)
    {
        _registry = registry;
        _options = options.Value;
    }

    public ManagementOptions Options => _options;

    public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;

        /// <summary>Trimmed stdout, which is what most `wp` commands are actually asked for.</summary>
        public string Output => StdOut.Trim();
    }

    /// <summary>A resolved management target: the endpoint plus its effective safety and channel details.</summary>
    public sealed record Target(string Name, EndpointOptions Endpoint, EffectiveSafety Safety)
    {
        public string Path => EndpointRegistry.ManagementPath(Endpoint) ?? string.Empty;
        public string Mode => Endpoint.ManagementMode;
    }

    /// <summary>The master gate for anything that runs WP-CLI, configured endpoint or not.</summary>
    public void EnsureCliEnabled(string operation)
    {
        if (!_options.AllowCliManagement)
        {
            throw new McpException(
                $"MCP tool '{operation}' uses the WP-CLI management channel, which is disabled. " +
                "Set Management:AllowCliManagement=true to enable it.");
        }
    }

    /// <summary>Resolve a site for a read-only management operation.</summary>
    public Target Resolve(string? site, string operation)
    {
        EnsureCliEnabled(operation);
        var (name, endpoint) = _registry.RequireManagement(site, operation);
        ValidateChannel(name, endpoint, operation);
        return new Target(name, endpoint, _registry.SafetyFor(endpoint));
    }

    /// <summary>
    /// Run a read-only command against connection details supplied for onboarding, before any endpoint
    /// exists for the host. Still goes through the master CLI gate and the endpoint's effective safety —
    /// a probe is the same channel, just not yet a configured one.
    /// </summary>
    public Task<CliResult> RunProbeAsync(
        EndpointOptions endpoint, IReadOnlyList<string> arguments, string operation, CancellationToken ct)
    {
        EnsureCliEnabled(operation);
        return RunAsync(ProbeTarget(endpoint), arguments, ct);
    }

    /// <summary>Shell form of <see cref="RunProbeAsync"/>. Callers must quote every interpolated value.</summary>
    public Task<CliResult> RunProbeShellAsync(
        EndpointOptions endpoint, string command, string operation, CancellationToken ct)
    {
        EnsureCliEnabled(operation);
        return RunShellAsync(ProbeTarget(endpoint), command, ct, longRunning: false);
    }

    private Target ProbeTarget(EndpointOptions endpoint) =>
        new("(probe)", endpoint, _registry.SafetyFor(endpoint));

    /// <summary>Resolve a site for a mutating operation: applies the write gate and the identity cross-check.</summary>
    public async Task<Target> ResolveForWriteAsync(string? site, string operation, CancellationToken ct)
    {
        var target = Resolve(site, operation);

        if (target.Safety.ReadOnly)
        {
            throw new McpException(
                $"MCP tool '{operation}' is blocked by server configuration for endpoint '{target.Name}'. " +
                "Set Wordpress:ReadOnly=false to allow writes" +
                (target.Safety.ReadOnlyFromEndpoint
                    ? $" (and clear the per-endpoint override Endpoints:{target.Name}:ReadOnly)."
                    : "."));
        }

        await EnsureCorrectSiteAsync(target, operation, ct);
        return target;
    }

    /// <summary>
    /// Apply a category feature toggle to a CLI-backed tool, so disabling a category (users, plugins,
    /// themes, …) switches it off on both channels rather than only over REST.
    /// </summary>
    public void RequireFeature(Target target, Func<WordpressOptions, bool> selector, string feature)
    {
        if (!selector(_registry.Options))
        {
            throw new McpException($"{feature} tools are disabled by server configuration.");
        }
    }

    public void EnsureDeleteAllowed(Target target, string operation)
    {
        if (!target.Safety.AllowDelete)
        {
            throw new McpException(
                $"MCP tool '{operation}' requires Wordpress:AllowDelete=true (in addition to Wordpress:ReadOnly=false)" +
                (target.Safety.AllowDeleteFromEndpoint
                    ? $", and the per-endpoint override Endpoints:{target.Name}:AllowDelete must not be false."
                    : "."));
        }
    }

    /// <summary>
    /// A host commonly serves several WordPress installs. Before writing, confirm the configured path
    /// actually holds the site the endpoint claims — a stale path is how one client's change lands on
    /// another client's site.
    /// </summary>
    public async Task EnsureCorrectSiteAsync(Target target, string operation, CancellationToken ct)
    {
        if (_options.SkipIdentityCheck) return;

        var expected = target.Endpoint.EffectiveUrl;
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new McpException(
                $"MCP tool '{operation}' cannot verify which site it would change: endpoint '{target.Name}' has no Url " +
                $"and no RestApi:BaseUrl. Set Endpoints:{target.Name}:Url to the site's address so the identity check can run " +
                "(or, accepting the risk on a single-site host, set Management:SkipIdentityCheck=true).");
        }

        var result = await RunAsync(target, new[] { "option", "get", "siteurl" }, ct);
        if (!result.Success)
        {
            throw new McpException(
                $"MCP tool '{operation}' could not confirm the site at '{target.Path}' on endpoint '{target.Name}': " +
                $"`wp option get siteurl` exited {result.ExitCode}. {Describe(result)}");
        }

        var actual = result.Output;
        if (!UrlsEquivalent(actual, expected!))
        {
            throw new McpException(
                $"MCP tool '{operation}' refused to run: endpoint '{target.Name}' expects '{expected}', but the WordPress install " +
                $"at '{target.Path}' reports '{actual}'. The path may be stale or point at a different site on this host. " +
                $"Fix Endpoints:{target.Name}:{(target.Mode)}:Path or Endpoints:{target.Name}:Url before retrying.");
        }
    }

    /// <summary>Run a WP-CLI command. Arguments are passed as a list and quoted by the channel.</summary>
    public Task<CliResult> RunAsync(Target target, IReadOnlyList<string> arguments, CancellationToken ct, bool longRunning = false)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(5, longRunning ? _options.LongCommandTimeoutSeconds : _options.CommandTimeoutSeconds));
        return target.Endpoint switch
        {
            { Ssh: not null } => RunSshAsync(target, arguments, timeout, ct),
            { Docker: not null } => RunDockerAsync(target, arguments, timeout, ct),
            { Local: not null } => RunLocalAsync(target, arguments, timeout, ct),
            _ => throw new McpException($"Endpoint '{target.Name}' has no management channel configured."),
        };
    }

    /// <summary>Run a command and throw a descriptive error when it fails.</summary>
    public async Task<CliResult> RunOrThrowAsync(Target target, IReadOnlyList<string> arguments, string operation, CancellationToken ct, bool longRunning = false)
    {
        var result = await RunAsync(target, arguments, ct, longRunning);
        if (!result.Success)
        {
            throw new McpException(
                $"MCP tool '{operation}' failed on endpoint '{target.Name}': `wp {string.Join(' ', arguments)}` exited {result.ExitCode}. {Describe(result)}");
        }
        return result;
    }

    /// <summary>Run an arbitrary shell command on the endpoint's host (used for archives and file moves).</summary>
    public Task<CliResult> RunShellAsync(Target target, string command, CancellationToken ct, bool longRunning = true)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(5, longRunning ? _options.LongCommandTimeoutSeconds : _options.CommandTimeoutSeconds));
        return target.Endpoint switch
        {
            { Ssh: not null } => ExecuteSshAsync(target.Endpoint.Ssh!, command, timeout, ct),
            { Docker: not null } => ExecuteProcessAsync(
                target.Endpoint.Docker!.DockerPath,
                new[] { "exec", target.Endpoint.Docker.Container, "sh", "-c", command },
                timeout, ct),
            // Every caller builds POSIX commands (quoting, test -s, tar, heredocs), so handing them to
            // cmd.exe would not fail loudly — it would return empty output that reads as "no debug log"
            // or "snapshot missing". Refuse instead of reporting a confident wrong answer.
            { Local: not null } when OperatingSystem.IsWindows() && !HasPosixShell() =>
                throw new McpException(
                    "This operation needs a POSIX shell, but the endpoint's Local channel is on Windows without one. " +
                    "Install Git Bash or WSL and make `sh` available on the PATH, or manage this site through an " +
                    "Ssh or Docker channel instead."),
            { Local: not null } => ExecuteProcessAsync(PosixShell(), new[] { "-c", command }, timeout, ct),
            _ => throw new McpException($"Endpoint '{target.Name}' has no management channel configured."),
        };
    }

    // ---- channels ---------------------------------------------------------------------------

    private Task<CliResult> RunSshAsync(Target target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var ssh = target.Endpoint.Ssh!;
        var command = BuildWpCommand(ssh.WpCliPath, ssh.Path, arguments);
        return ExecuteSshAsync(ssh, command, timeout, ct);
    }

    private Task<CliResult> RunDockerAsync(Target target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var docker = target.Endpoint.Docker!;
        // Passed as separate argv entries, so no shell quoting is involved.
        var argv = new List<string> { "exec", docker.Container, docker.WpCliPath, $"--path={docker.Path}", "--allow-root" };
        argv.AddRange(arguments);
        return ExecuteProcessAsync(docker.DockerPath, argv, timeout, ct);
    }

    private Task<CliResult> RunLocalAsync(Target target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var local = target.Endpoint.Local!;
        var argv = new List<string> { $"--path={local.Path}" };
        argv.AddRange(arguments);
        return ExecuteProcessAsync(local.WpCliPath, argv, timeout, ct);
    }

    private static async Task<CliResult> ExecuteSshAsync(SshManagementOptions ssh, string command, TimeSpan timeout, CancellationToken ct)
    {
        using var client = CreateSshClient(ssh);
        // Connecting should not inherit a long command timeout — a dead host would otherwise block for
        // the full long-operation budget (30 minutes by default) before reporting anything.
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(Math.Min(30, Math.Max(5, timeout.TotalSeconds)));

        try
        {
            await Task.Run(() => client.Connect(), ct);
        }
        catch (Exception ex)
        {
            throw new McpException($"SSH connection to {ssh.Username}@{ssh.Host}:{ssh.Port} failed: {ex.Message}");
        }

        try
        {
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = timeout;
            await Task.Run(() => cmd.Execute(), ct);
            return new CliResult(cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty);
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            // SSH.NET's operation/connection exceptions would otherwise surface as an opaque
            // "An error occurred invoking wp_…" with no indication that SSH was the problem.
            throw new McpException(
                $"SSH command failed on {ssh.Username}@{ssh.Host}:{ssh.Port}: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    private static SshClient CreateSshClient(SshManagementOptions ssh)
    {
        if (string.IsNullOrWhiteSpace(ssh.Host)) throw new McpException("The Ssh block needs a Host.");
        if (string.IsNullOrWhiteSpace(ssh.Username)) throw new McpException("The Ssh block needs a Username.");

        if (!string.IsNullOrWhiteSpace(ssh.PrivateKeyPath))
        {
            if (!File.Exists(ssh.PrivateKeyPath))
                throw new McpException($"Private key file not found: {ssh.PrivateKeyPath}");

            var key = string.IsNullOrEmpty(ssh.Password)
                ? new PrivateKeyFile(ssh.PrivateKeyPath)
                : new PrivateKeyFile(ssh.PrivateKeyPath, ssh.Password);
            return new SshClient(ssh.Host, ssh.Port, ssh.Username, key);
        }

        if (!string.IsNullOrEmpty(ssh.Password))
        {
            return new SshClient(ssh.Host, ssh.Port, ssh.Username, ssh.Password);
        }

        throw new McpException("The Ssh block needs either a PrivateKeyPath or a Password.");
    }

    private static async Task<CliResult> ExecuteProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new McpException($"Could not start '{fileName}': {ex.Message}. Check the executable is installed and on the PATH.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Kill on client cancellation too, not only on timeout — otherwise an abandoned `wp core
            // update` or `db import` keeps running unsupervised against the site.
            TryKill(process);
            if (ct.IsCancellationRequested) throw;
            throw new McpException($"'{fileName}' did not finish within {timeout.TotalSeconds:0} seconds and was terminated.");
        }

        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Locate a POSIX shell. On Windows, Git Bash and WSL both provide one.</summary>
    private static string PosixShell()
    {
        if (!OperatingSystem.IsWindows()) return "/bin/sh";

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\Git\usr\bin\sh.exe",
                     @"C:\Program Files (x86)\Git\usr\bin\sh.exe",
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return "sh";
    }

    private static bool HasPosixShell()
    {
        var shell = PosixShell();
        if (File.Exists(shell)) return true;

        // `sh` may be on the PATH without an absolute location we know about.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir =>
            {
                try { return File.Exists(Path.Combine(dir, "sh.exe")) || File.Exists(Path.Combine(dir, "sh")); }
                catch { return false; }
            });
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* the process is already gone */ }
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Build a quoted `wp --path=… <args>` command line for shell-based channels.</summary>
    public static string BuildWpCommand(string wpCliPath, string sitePath, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { ShellQuote(wpCliPath), ShellQuote($"--path={sitePath}") };
        parts.AddRange(arguments.Select(ShellQuote));
        return string.Join(' ', parts);
    }

    /// <summary>Single-quote a value for POSIX shells.</summary>
    public static string ShellQuote(string value)
        => "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";

    private void ValidateChannel(string name, EndpointOptions endpoint, string operation)
    {
        var blocks = new[]
        {
            endpoint.Ssh is null ? null : "Ssh",
            endpoint.Local is null ? null : "Local",
            endpoint.Docker is null ? null : "Docker",
        }.Where(b => b is not null).ToList();

        if (blocks.Count > 1)
        {
            throw new McpException(
                $"MCP tool '{operation}': endpoint '{name}' declares {blocks.Count} management blocks ({string.Join(", ", blocks!)}). " +
                "Configure exactly one of Ssh, Local or Docker.");
        }

        if (string.IsNullOrWhiteSpace(EndpointRegistry.ManagementPath(endpoint)))
        {
            throw new McpException(
                $"MCP tool '{operation}': endpoint '{name}' has no install path. " +
                $"Set Endpoints:{name}:{endpoint.ManagementMode}:Path to the directory containing wp-config.php.");
        }
    }

    /// <summary>
    /// WP-CLI shells out to the MySQL client tools for db commands. Managed and containerised hosts
    /// often lack them, and the raw failure ("env: 'mysqldump': No such file or directory", exit 127)
    /// says nothing useful, so name the missing tool and how to get it.
    /// </summary>
    public static string? MissingDatabaseTool(CliResult result)
    {
        foreach (var tool in new[] { "mysqldump", "mysqlcheck", "mysql", "mysqladmin", "mysqlbinlog" })
        {
            if (result.StdErr.Contains($"'{tool}'", StringComparison.OrdinalIgnoreCase)
                && result.StdErr.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                return tool;
            }
        }
        return null;
    }

    public static string DescribeDatabaseFailure(CliResult result, string operation, string endpoint)
    {
        var missing = MissingDatabaseTool(result);
        if (missing is not null)
        {
            return $"MCP tool '{operation}' needs the MySQL client tool '{missing}', which is not installed on the host for " +
                   $"endpoint '{endpoint}'. WP-CLI shells out to it for database work. Install the client package " +
                   "(Debian/Ubuntu: `apt-get install mariadb-client` or `mysql-client`; RHEL: `dnf install mysql`), " +
                   "or run the database operation from a host that has it.";
        }
        return $"MCP tool '{operation}' failed on endpoint '{endpoint}'. {Describe(result)}";
    }

    /// <summary>
    /// PHP running out of memory mid-command is one of the most common WP-CLI failures on shared
    /// hosting, and its raw fatal error buries the cause. Surface it with the fix.
    /// </summary>
    public static string? MemoryExhaustionHint(CliResult result)
    {
        if (!result.StdErr.Contains("Allowed memory size", StringComparison.OrdinalIgnoreCase)
            && !result.StdOut.Contains("Allowed memory size", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            result.StdErr + result.StdOut, @"Allowed memory size of (\d+) bytes");
        var limit = match.Success && long.TryParse(match.Groups[1].Value, out var bytes)
            ? $"{bytes / 1024 / 1024} MB"
            : "the configured limit";

        return $"PHP ran out of memory ({limit}) while running this command. Raise PHP's CLI memory limit on the host — " +
               "for example set memory_limit = 512M in the CLI php.ini, or run WP-CLI as " +
               "`php -d memory_limit=512M $(which wp)` by pointing the endpoint's WpCliPath at a wrapper script.";
    }

    public static string Describe(CliResult result)
    {
        var memory = MemoryExhaustionHint(result);
        if (memory is not null) return memory;

        var stderr = result.StdErr.Trim();
        var stdout = result.StdOut.Trim();
        if (stderr.Length > 1500) stderr = stderr[..1500] + "…(truncated)";
        if (stdout.Length > 500) stdout = stdout[..500] + "…(truncated)";
        return string.IsNullOrWhiteSpace(stderr)
            ? $"Output: {stdout}"
            : $"Error: {stderr}" + (stdout.Length > 0 ? $" | Output: {stdout}" : string.Empty);
    }

    private static bool UrlsEquivalent(string a, string b)
    {
        static string Normalize(string value) => value.Trim().TrimEnd('/')
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
