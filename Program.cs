using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Anakim.Infrastructure;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory)
                      .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                      .AddEnvironmentVariables();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel((context, options) =>
                {
                    var configuration = context.Configuration;
                    var useHttps = configuration.GetValue<bool>("UseHttps");
                    var generalPorts = configuration.GetSection("GeneralPorts");

                    var portApi = generalPorts.GetValue<int>("PortApi");
                    var portPage = generalPorts.GetValue<int>("PortPage");
                    var portSocket = generalPorts.GetValue<int>("PortSocket");

                    if (useHttps)
                    {
                        var certSettings = configuration.GetSection("Certificate");
                        var certPath = certSettings.GetValue<string>("Path");
                        var certPassword = certSettings.GetValue<string>("Password");
                        var certificate = new X509Certificate2(certPath, certPassword);

                        options.ListenAnyIP(portApi, listenOptions => listenOptions.UseHttps(certificate));
                        options.ListenAnyIP(portPage, listenOptions => listenOptions.UseHttps(certificate));
                        options.ListenAnyIP(portSocket, listenOptions => listenOptions.UseHttps(certificate));
                    }
                    else
                    {
                        options.ListenAnyIP(portApi);
                        options.ListenAnyIP(portPage);
                        options.ListenAnyIP(portSocket);
                    }
                })
                .Configure(app =>
                {
                    var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
                    var proxySettings = config.GetSection("ProxySettings").Get<ProxySettings>();

                    app.UseRouting();

                    switch (proxySettings.Mode)
                    {
                        case 1: // Traffic Manager
                            app.UseMiddleware<RedirectToBestPIMiddleware>();
                            break;
                        case 2: // Proxy Instance
                            app.UseMiddleware<RedirectToBestAHMiddleware>();
                            break;
                        case 3: // Application Handler não redireciona
                            break;
                    }

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
                var configuration = hostingContext.Configuration;
                Logger.Initialize(configuration);

                var proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                    ?? throw new InvalidOperationException("ProxySettings not configured properly.");

                services.AddSingleton(proxySettings);
                services.AddSingleton<INodeStatisticsService, NodeStatisticsService>();
                services.AddSingleton<InstanceRankingManager>(); // ✅ necessário para os redirecionamentos

                switch (proxySettings.Mode)
                {
                    case 1:
                        Logger.LogInfo("Configuring as Traffic Manager");
                        services.AddHostedService<TrafficManagerService>();
                        break;
                    case 2:
                        Logger.LogInfo("Configuring as Proxy Instance");
                        services.AddHostedService<ProxyInstanceService>();
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

        Logger.LogSuccess("Application initialized successfully!");
        await builder.RunAsync();
    }
}

// (opcional) WorkerService com injeção dinâmica, mantido apenas como exemplo
public class WorkerService : BackgroundService
{
    private readonly IHostedService _service;

    public WorkerService(IEnumerable<IHostedService> services)
    {
        _service = services.FirstOrDefault(s =>
            s is TrafficManagerService || s is ProxyInstanceService || s is ApplicationHandlerService)
            ?? throw new InvalidOperationException("No valid service found.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInfo("WorkerService started.");

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
