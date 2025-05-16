using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Anakim.Infrastructure;
using Anakim.ProxyInstance;
using Anakim.ProxyInstance.Failover;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Anakim.TrafficManager;

class Program
{
    static async Task Main(string[] args)
    {
        // Creates and configures the host builder
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService() // Enables execution as a Windows Service
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // Loads appsettings.json and environment variables
                config.SetBasePath(AppContext.BaseDirectory)
                      .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                      .AddEnvironmentVariables();
            })
            .ConfigureLogging(logging =>
            {
                // Configures console logging only
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel((context, options) =>
                {
                    // Load configuration values
                    var configuration = context.Configuration;
                    var useHttps = configuration.GetValue<bool>("UseHttps");
                    var generalPorts = configuration.GetSection("GeneralPorts");

                    // Reads configured ports
                    var portApi = generalPorts.GetValue<int>("PortApi");
                    var portPage = generalPorts.GetValue<int>("PortPage");
                    var portSocket = generalPorts.GetValue<int>("PortSocket");

                    if (useHttps)
                    {
                        // Loads certificate settings for HTTPS
                        var certSettings = configuration.GetSection("Certificate");
                        var certPath = certSettings.GetValue<string>("Path")
                            ?? throw new InvalidOperationException("Missing 'Certificate:Path' in appsettings.json.");

                        var certPassword = certSettings.GetValue<string>("Password");
                        var certificate = new X509Certificate2(certPath, certPassword);

                        // Configures HTTPS listeners for all three ports
                        options.ListenAnyIP(portApi, listenOptions => listenOptions.UseHttps(certificate));
                        options.ListenAnyIP(portPage, listenOptions => listenOptions.UseHttps(certificate));
                        options.ListenAnyIP(portSocket, listenOptions => listenOptions.UseHttps(certificate));
                    }
                    else
                    {
                        // Configures HTTP listeners as fallback
                        options.ListenAnyIP(portApi);
                        options.ListenAnyIP(portPage);
                        options.ListenAnyIP(portSocket);
                    }
                })
                .Configure(app =>
                {
                    // Gets ProxySettings from DI
                    var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
                    var proxySettings = config.GetSection("ProxySettings").Get<ProxySettings>();

                    if (proxySettings is null)
                    {
                        throw new InvalidOperationException("Configuração 'ProxySettings' não encontrada ou inválida.");
                    }

                    app.UseRouting(); // Enables routing middleware

                    // Middleware registration depending on running mode
                    switch (proxySettings.Mode)
                    {
                        case 1: // Traffic Manager
                            app.UseMiddleware<RedirectToBestPIMiddleware>();
                            break;
                        case 2: // Proxy Instance
                            app.UseMiddleware<RedirectToBestAHMiddleware>();
                            break;
                        case 3: // Application Handler (no redirect middleware)
                            break;
                    }

                    // Default endpoint for diagnostics
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/", async context =>
                        {
                            var instanceName = proxySettings.InstanceName ?? "Unknown";
                            var uid = Guid.NewGuid();
                            var timestamp = DateTime.UtcNow;
                            var threadCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
                            var threadPoolcount = ThreadPool.ThreadCount;

                            var response = new
                            {
                                uid,
                                timestamp,
                                instance = instanceName,
                                activeThreads = threadCount,
                                activeThrPoolcount = threadPoolcount
                            };

                            // Sends JSON response
                            var json = JsonSerializer.Serialize(response);
                            var jsonBytes = Encoding.UTF8.GetBytes(json);

                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength = jsonBytes.Length;

                            await context.Response.Body.WriteAsync(jsonBytes);
                            await context.Response.Body.FlushAsync();
                        });
                    });
                });
            })
            .ConfigureServices((hostingContext, services) =>
            {
                // Reads configuration
                var configuration = hostingContext.Configuration;
                Logger.Initialize(configuration); // Initializes logging system

                // Gets ProxySettings object
                var proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                    ?? throw new InvalidOperationException("ProxySettings not configured properly.");

                // Registers dependencies
                services.AddSingleton(proxySettings);
                services.AddSingleton<INodeStatisticsService, NodeStatisticsService>();
                services.AddSingleton<InstanceRankingManager>();

                // Registers hosted services based on mode
                switch (proxySettings.Mode)
                {
                    case 1:
                        Logger.LogInfo("Configuring as Traffic Manager");
                        services.AddHostedService<TrafficManagerService>();
                        services.AddSingleton<FailoverManager>();
                        break;
                    case 2:
                        Logger.LogInfo("Configuring as Proxy Instance");
                        services.AddHostedService<ProxyInstanceService>();
                        services.AddSingleton<FailoverManager>();
                        break;
                    case 3:
                        Logger.LogInfo("Configuring as Application Handler");
                        services.AddHostedService<ApplicationHandlerService>();
                        break;
                    default:
                        throw new InvalidOperationException($"Invalid mode: {proxySettings.Mode}");
                }
            })
            .Build();

        // Starts the application
        Logger.LogSuccess("Application initialized successfully!");
        await builder.RunAsync();
    }
}

// Optional background worker with dynamic service resolution
public class WorkerService : BackgroundService
{
    private readonly IHostedService _service;

    public WorkerService(IEnumerable<IHostedService> services)
    {
        // Selects the appropriate service implementation
        _service = services.FirstOrDefault(s =>
            s is TrafficManagerService || s is ProxyInstanceService || s is ApplicationHandlerService)
            ?? throw new InvalidOperationException("No valid service found.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInfo("WorkerService started.");

        // Executes the corresponding logic for the resolved service
        switch (_service)
        {
            case TrafficManagerService tmService:
                await tmService.StartAsync(stoppingToken);
                break;
            case ProxyInstanceService piService:
                await piService.ConnectToTrafficManager(stoppingToken);
                break;
            case ApplicationHandlerService ahService:
                await ahService.ConnectToProxyInstance(stoppingToken);
                break;
            default:
                Logger.LogError("Unsupported service type provided.");
                break;
        }
    }
}
