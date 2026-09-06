using System.Net;
using WordpressMCPSharp.Configuration;
using WordpressMCPSharp.Hosting;
using WordpressMCPSharp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;

namespace WordpressMCPSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        var contentRoot = GetContentRoot();
        var isService = WindowsServiceHelpers.IsWindowsService();
        if (!isService)
        {
            McpSharpIcon.ApplyConsoleWindowIcon();
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "wordpressmcp-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "WordpressMCPSharp.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, $"WordpressMCPSharp.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
                .AddJsonFile(ResolveConfigFile(contentRoot, "WordpressMCPSharp.Local.json"), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "WORDPRESSMCP_")
                .AddCommandLine(args);

            if (isService)
            {
                var svcOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
                builder.Host.UseWindowsService(o => o.ServiceName = svcOptions.WindowsServiceName);
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<WordpressOptions>(
                builder.Configuration.GetSection(WordpressOptions.SectionName));
            builder.Services.Configure<ServerOptions>(
                builder.Configuration.GetSection(ServerOptions.SectionName));
            // Endpoints and DefaultSite live at the configuration root.
            builder.Services.Configure<RegistryOptions>(builder.Configuration);
            builder.Services.Configure<ManagementOptions>(
                builder.Configuration.GetSection(ManagementOptions.SectionName));
            builder.Services.Configure<SnapshotOptions>(
                builder.Configuration.GetSection(SnapshotOptions.SectionName));

            builder.Services.AddSingleton<EndpointRegistry>();
            builder.Services.AddSingleton<SetupDiagnosticsService>();
            builder.Services.AddSingleton<UpdateCheckService>();
            builder.Services.AddSingleton<ManagementService>();
            builder.Services.AddSingleton<SnapshotService>();

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    k.ListenLocalhost(server.Port);
                }
                else if (IPAddress.TryParse(server.Host, out var ip))
                {
                    k.Listen(ip, server.Port);
                }
                else
                {
                    k.ListenAnyIP(server.Port);
                }
            });

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            var registry = app.Services.GetRequiredService<EndpointRegistry>();
            var wordpress = registry.Options;

            var managementOptions = app.Services.GetRequiredService<IOptions<ManagementOptions>>().Value;
            var snapshotOptions = app.Services.GetRequiredService<IOptions<SnapshotOptions>>().Value;

            var details = new List<string>
            {
                $"Read-only: {wordpress.ReadOnly}",
                $"Allow delete: {wordpress.AllowDelete}",
                $"Allow plugin install: {wordpress.AllowPluginInstall}",
                $"CLI management: {(managementOptions.AllowCliManagement ? "enabled" : "disabled")}" +
                    (managementOptions.AllowCliManagement && managementOptions.AllowArbitraryCli ? " (arbitrary commands allowed)" : string.Empty),
                $"Snapshot restore: {(snapshotOptions.AllowRestore ? "allowed" : "blocked")}",
                $"Disabled categories: {DescribeDisabledFeatures(wordpress)}",
                $"Endpoints: {registry.Count}{(registry.LegacyMapped ? " (from legacy Wordpress:BaseUrl)" : string.Empty)}",
            };
            details.AddRange(registry.All.Select(entry =>
                $"  {entry.Name}: rest={(entry.Endpoint.HasRest ? entry.Endpoint.EffectiveUrl : "none")}, management={entry.Endpoint.ManagementMode}"));

            LogStartup(
                "WordpressMCPSharp",
                $"http://{server.Host}:{server.Port}{server.Path}",
                "HTTP",
                isService ? "WindowsService" : "Console",
                contentRoot,
                details.ToArray());

            foreach (var warning in registry.ConfigurationWarnings)
            {
                Log.ForContext("SourceContext", "WordpressMCPSharp.Startup").Warning("Configuration: {Warning}", warning);
            }

            app.UseMiddleware<McpPasswordMiddleware>();

            app.MapFavicon();
            app.MapGet("/healthz", () => new
            {
                status = "ok",
                server = "WordpressMCPSharp",
                path = server.Path,
                readOnly = wordpress.ReadOnly,
                allowDelete = wordpress.AllowDelete,
                cliManagement = managementOptions.AllowCliManagement,
                allowRestore = snapshotOptions.AllowRestore,
                endpoints = registry.All.Select(entry => new
                {
                    name = entry.Name,
                    rest = entry.Endpoint.HasRest,
                    management = entry.Endpoint.ManagementMode,
                }),
                configurationWarnings = registry.ConfigurationWarnings.Count,
                timeUtc = DateTimeOffset.UtcNow,
            });
            app.MapMcp(server.Path);

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>List the tool categories an operator has switched off, so the posture is visible at startup.</summary>
    private static string DescribeDisabledFeatures(WordpressOptions options)
    {
        var disabled = new List<string>();
        if (!options.EnableContent) disabled.Add("content");
        if (!options.EnableComments) disabled.Add("comments");
        if (!options.EnableUsers) disabled.Add("users");
        if (!options.EnableTaxonomies) disabled.Add("taxonomies");
        if (!options.EnablePlugins) disabled.Add("plugins");
        if (!options.EnableThemes) disabled.Add("themes");
        if (!options.EnableSettings) disabled.Add("settings");
        if (!options.EnableMenus) disabled.Add("menus");
        if (!options.EnableWooCommerce) disabled.Add("woocommerce");
        if (!options.EnableSiteHealth) disabled.Add("site-health");
        if (!options.EnableSetupDiagnostics) disabled.Add("setup-diagnostics");
        return disabled.Count == 0 ? "(none)" : string.Join(", ", disabled);
    }

    private static void LogStartup(string serviceName, string endpoint, string transport, string mode, string contentRoot, params string[] details)
    {
        var startupLog = Log.ForContext("SourceContext", serviceName + ".Startup");
        startupLog.Information("{ServiceName} startup", serviceName);
        startupLog.Information("  Endpoint: {Endpoint}", endpoint);
        startupLog.Information("  Transport: {Transport}", transport);
        startupLog.Information("  Mode: {Mode}", mode);
        foreach (var detail in details)
        {
            startupLog.Information("  {Detail}", detail);
        }
        startupLog.Information("  Content root: {ContentRoot}", contentRoot);
    }

    private static string GetContentRoot() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string ResolveConfigFile(string contentRoot, string fileName)
    {
        if (File.Exists(Path.Combine(contentRoot, fileName)))
        {
            return fileName;
        }

        try
        {
            var match = Directory.EnumerateFiles(contentRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

            return match is null ? fileName : Path.GetFileName(match);
        }
        catch (DirectoryNotFoundException)
        {
            return fileName;
        }
    }
}
