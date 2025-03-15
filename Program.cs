using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AccessPoint.Infrastructure;
using System.Security.Cryptography.X509Certificates;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService() // Permite executar como serviço do Windows
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
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/", async context =>
                        {
                            await context.Response.WriteAsync("AccessPoint is running!");
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
                services.AddSingleton<INodeStatisticsService, NodeStatisticsService>(); // Serviço de estatísticas

                // Seleção dinâmica do serviço correto baseado no modo configurado
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

                // Registra WorkerService como serviço de fundo
                //services.AddHostedService<WorkerService>();
            })
            .Build();

        Logger.LogSuccess("Application initialized successfully!");
        await builder.RunAsync();
    }
}

// 🚀 WorkerService Agora Usa Injeção de Dependência Corretamente
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
