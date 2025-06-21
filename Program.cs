using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Anakim.Infrastructure;
using Anakim.ProxyInstance;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Anakim.TrafficManager;
using Anakim.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Cors.Infrastructure;
using ERPUSASolutions.DataAccessProvider;

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
                    var port = configuration.GetValue<int>("GeneralPort");

                    if (useHttps)
                    {
                        var certSettings = configuration.GetSection("Certificate");
                        var certPath = certSettings.GetValue<string>("Path")
                            ?? throw new InvalidOperationException("Missing 'Certificate:Path' in appsettings.json.");
                        var certPassword = certSettings.GetValue<string>("Password");
                        var certificate = new X509Certificate2(certPath, certPassword);

                        options.ListenAnyIP(port, listenOptions => listenOptions.UseHttps(certificate));
                    }
                    else
                    {
                        options.ListenAnyIP(port);
                    }
                })
                .Configure(app =>
                {
                    var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
                    var proxySettings = config.GetSection("ProxySettings").Get<ProxySettings>()
                        ?? throw new InvalidOperationException("Configuração 'ProxySettings' não encontrada ou inválida.");

                    // ✅ Habilita CORS
                    app.UseCors();

                    app.UseRouting();

                    switch (proxySettings.Mode)
                    {
                        case 1:
                            app.UseMiddleware<RedirectToBestPIMiddleware>();
                            break;
                        case 2:
                            app.UseMiddleware<RedirectToBestAHMiddleware>();
                            break;
                        case 3:
                            break;
                    }

                    if (proxySettings.Mode == 3)
                    {
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/scripts/{scriptName}", async context =>
                            {
                                var scriptName = (string?)context.Request.RouteValues["scriptName"];
                                Logger.LogInfo($"[DEBUG] Endpoint chamado para script: {scriptName}");
                                Logger.LogInfo($"BaseDirectory: {AppContext.BaseDirectory}");

                                if (string.IsNullOrWhiteSpace(scriptName))
                                {
                                    context.Response.StatusCode = 400;
                                    await context.Response.WriteAsync("Nome do script não especificado.");
                                    return;
                                }

                                var executor = context.RequestServices.GetRequiredService<ScriptExecutorService>();

                                try
                                {
                                    var args = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(context.Request.Body)
                                               ?? new Dictionary<string, object>();

                                    Logger.LogInfo($"[DEBUG] Executando script: {scriptName}");
                                    var result = await executor.RunScriptAsync(scriptName, args);

                                    context.Response.ContentType = "application/json";
                                    await context.Response.WriteAsync(JsonSerializer.Serialize(result));
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogInfo($"[ERRO] {ex.Message}");
                                    context.Response.StatusCode = 500;
                                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
                                }
                            });
                        });
                    }
                    else
                    {
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
                    }
                });
            })
            .ConfigureServices((hostingContext, services) =>
            {
                var configuration = hostingContext.Configuration;
                Logger.Initialize(configuration);

                var proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                    ?? throw new InvalidOperationException("ProxySettings not configured properly.");

                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.AllowAnyOrigin()
                              .AllowAnyMethod()
                              .AllowAnyHeader();
                    });
                });

                services.AddSingleton(proxySettings);
                services.AddSingleton<ScriptExecutorService>();
                services.AddSingleton<INodeStatisticsService, NodeStatisticsService>();
                services.AddSingleton<InstanceRankingManager>();

                // ✅ Injeta o provider de banco de dados baseado no appsettings.json
                services.AddSingleton<IDataAccessProvider>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    return DataAccessProviderFactory.Create(config);
                });

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
                        services.AddSingleton<ScriptExecutorService>();
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