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
using AnakimSuite.AnakimAccessProvider;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using AnakimOrchestrator.Controllers;

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
                        var pfxSection = configuration.GetSection("Certificate:Pfx");
                        var pfxPath = pfxSection.GetValue<string>("Path");
                        var password = pfxSection.GetValue<string>("Password");
                        var pemSection = configuration.GetSection("Certificate:Pem");
                        var certPath = pemSection.GetValue<string>("CertPath");
                        var keyPath = pemSection.GetValue<string>("KeyPath");

                        X509Certificate2 certificate;

                        Logger.LogInfo($"[CERT DEBUG] pfxPath: {pfxPath}");
                        Logger.LogInfo($"[CERT DEBUG] password: {(string.IsNullOrEmpty(password) ? "NULL ou vazio" : "****")}");

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            Logger.LogInfo("[CERT] Ambiente Windows - carregando .pfx");
                            certificate = new X509Certificate2(pfxPath, password);
                        }
                        else
                        {
                            Logger.LogInfo("[CERT] Ambiente Linux - carregando .pem + .key");
                            var pemCert = X509Certificate2.CreateFromPemFile(certPath, keyPath);

                            if (!pemCert.HasPrivateKey)
                            {
                                var rsa = RSA.Create();
                                rsa.ImportFromPem(File.ReadAllText(keyPath));
                                certificate = pemCert.CopyWithPrivateKey(rsa);
                            }
                            else
                            {
                                certificate = pemCert;
                            }
                        }

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

                    if (proxySettings.Mode == 3) // AH
                    {
                        app.UseDefaultFiles();
                        app.UseStaticFiles();
                    }

                    app.UseCors();

                    app.Use(async (context, next) =>
                    {
                        var origin = context.Request.Headers["Origin"].FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(origin))
                        {
                            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
                            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
                            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
                        }

                        if (context.Request.Method == HttpMethods.Options)
                        {
                            context.Response.StatusCode = 204;
                            await context.Response.CompleteAsync();
                            return;
                        }

                        await next();
                    });

                    app.UseRouting();

                    switch (proxySettings.Mode)
                    {
                        case 1:
                            Logger.LogInfo("[PIPELINE] Ativando middleware de redirecionamento para PI");
                            app.UseMiddleware<RedirectToBestPIMiddleware>();
                            app.UseMiddleware<CorsProxyToPIMiddleware>();
                            break;
                        case 2:
                            Logger.LogInfo("[PIPELINE] Ativando middleware de redirecionamento para AH");
                            app.UseMiddleware<RedirectToBestAHMiddleware>();
                            app.UseMiddleware<ProxyCorsRedirectMiddleware>();
                            break;
                        case 3:
                            Logger.LogInfo("[PIPELINE] Application Handler ativado - registrando endpoints personalizados");
                            app.UseEndpoints(endpoints =>
                            {
                                Logger.LogInfo("[ENDPOINTS] Mapeando endpoints gerais");
                                endpoints.MapControllers();

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

                                    var targetUrl = $"{authBaseUrl}/auth/login";
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
                            });
                            break;
                    }
                });
            })
            .ConfigureServices((hostingContext, services) =>
            {
                var configuration = hostingContext.Configuration;
                Logger.Initialize(configuration);

                var proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                    ?? throw new InvalidOperationException("ProxySettings not configured properly.");

                var dockerSettings = configuration.GetSection("DockerSettings").Get<DockerSettings>() ?? new DockerSettings();

                var allowedOrigins = configuration
                    .GetSection("Cors:AllowedOrigins")
                    .Get<string[]>();

                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.WithOrigins(allowedOrigins!)
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials();
                    });
                });

                services.AddHttpClient();
                services.AddControllers();
                services.AddSingleton(proxySettings);
                services.AddSingleton(dockerSettings);
                services.AddSingleton<ScriptExecutorService>();
                services.AddSingleton<INodeStatisticsService, NodeStatisticsService>();
                services.AddSingleton<InstanceRankingManager>();
                services.AddSingleton<FailoverManager>();

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