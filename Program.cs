using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AnakimOrchestrator.Infrastructure;
using AnakimOrchestrator.ProxyInstance;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AnakimOrchestrator.TrafficManager;
using AnakimOrchestrator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Cors.Infrastructure;
using AnakimSuite.AnakimAccessProvider;

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

                    Logger.LogInfo($"[STARTUP] Executando modo: {proxySettings.Mode}");

                    app.UseDefaultFiles();
                    app.UseStaticFiles();

                    app.UseCors();
                    app.UseRouting();

                    switch (proxySettings.Mode)
                    {
                        case 1:
                            Logger.LogInfo("[PIPELINE] Ativando middleware de redirecionamento para PI");
                            app.UseMiddleware<RedirectToBestPIMiddleware>();
                            break;
                        case 2:
                            Logger.LogInfo("[PIPELINE] Ativando middleware de redirecionamento para AH");
                            app.UseMiddleware<RedirectToBestAHMiddleware>();
                            break;
                        case 3:
                            Logger.LogInfo("[PIPELINE] Application Handler ativado - registrando endpoints personalizados");
                            break;
                    }

                    app.UseEndpoints(endpoints =>
                    {
                        Logger.LogInfo("[ENDPOINTS] Mapeando endpoints gerais");

                        if (proxySettings.Mode == 3)
                        {
                            Logger.LogInfo("[ENDPOINTS] Modo 3 detectado: mapeando /auth/login e /scripts/{scriptName}");

                            endpoints.MapPost("/auth/login", async context =>
                            {
                                var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
                                var authBaseUrl = configuration.GetSection("AnakimAuthService")["BaseUrl"];

                                Logger.LogInfo("[AUTH LOGIN] Endpoint /auth/login recebido");

                                if (string.IsNullOrWhiteSpace(authBaseUrl))
                                {
                                    Logger.LogError("[AUTH LOGIN] Configuração 'AnakimAuthService:BaseUrl' não encontrada.");
                                    context.Response.StatusCode = 500;
                                    await context.Response.WriteAsync("Configuração 'AnakimAuthService:BaseUrl' não encontrada.");
                                    return;
                                }

                                var targetUrl = $"{authBaseUrl}/login";
                                Logger.LogInfo($"[AUTH LOGIN] Redirecionando para {targetUrl}");

                                using var client = new HttpClient();
                                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                                var content = new StringContent(body, Encoding.UTF8, "application/json");

                                try
                                {
                                    var response = await client.PostAsync(targetUrl, content);
                                    context.Response.StatusCode = (int)response.StatusCode;
                                    var responseBody = await response.Content.ReadAsStringAsync();
                                    context.Response.ContentType = "application/json";
                                    await context.Response.WriteAsync(responseBody);
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError($"[AUTH PROXY ERROR] {ex.Message}");
                                    context.Response.StatusCode = 500;
                                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
                                }
                            });

                            endpoints.MapPost("/scripts/{scriptName}", async context =>
                            {
                                var scriptName = (string?)context.Request.RouteValues["scriptName"];
                                Logger.LogInfo($"[SCRIPT] Endpoint chamado para script: {scriptName}");

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

                                    Logger.LogInfo($"[SCRIPT] Executando script: {scriptName}");
                                    var result = await executor.RunScriptAsync(scriptName, args);

                                    context.Response.ContentType = "application/json";
                                    await context.Response.WriteAsync(JsonSerializer.Serialize(result));
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError($"[SCRIPT ERROR] {ex.Message}");
                                    context.Response.StatusCode = 500;
                                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
                                }
                            });
                        }
                        else
                        {
                            endpoints.MapGet("/", async context =>
                            {
                                Logger.LogInfo("[DEBUG] Endpoint GET / chamado");

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
                        }
                    });
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

                services.AddSingleton<IAnakimAccessProvider>(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    return AnakimAccessProviderFactory.Create(config);
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
