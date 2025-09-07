using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AnakimOrchestrator.Infrastructure;
using AnakimOrchestrator.ProxyInstance;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AnakimOrchestrator.TrafficManager;
using AnakimOrchestrator.Services;
using Microsoft.AspNetCore.Http;
using AnakimSuite.AnakimAccessProvider;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using AnakimSuite.AnakimMetadataManagment;

using TMResolver = AnakimOrchestrator.TrafficManager.TrafficManagerHelpers;
using PIResolver = AnakimOrchestrator.ProxyInstance.ProxyInstanceHelpers;

class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory)
                      .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                      .AddEnvironmentVariables();

                var configuration = config.Build();

                //var databuilder = new AnakimDataBuilder(configuration);
                //databuilder.Create();
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
                        try
                        {
                            // Carregar certificado conforme SO
                            var pfxSection = configuration.GetSection("Certificate:Pfx");
                            var pemSection = configuration.GetSection("Certificate:Pem");

                            X509Certificate2 certificate;

                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                var pfxPath = pfxSection.GetValue<string>("Path");
                                var password = pfxSection.GetValue<string>("Password");

                                if (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath))
                                    throw new FileNotFoundException($"Arquivo PFX não encontrado: {pfxPath}");

                                Logger.LogInfo("[CERT] Windows - carregando .pfx");
                                certificate = new X509Certificate2(pfxPath, password);
                            }
                            else
                            {
                                var certPath = pemSection.GetValue<string>("CertPath");
                                var keyPath = pemSection.GetValue<string>("KeyPath");

                                if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
                                    throw new FileNotFoundException($"Arquivo PEM (cert) não encontrado: {certPath}");
                                if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
                                    throw new FileNotFoundException($"Arquivo PEM (key) não encontrado: {keyPath}");

                                Logger.LogInfo("[CERT] Linux - carregando .pem + .key");
                                var pemCert = X509Certificate2.CreateFromPemFile(certPath, keyPath);

                                if (!pemCert.HasPrivateKey)
                                {
                                    using var rsa = RSA.Create();
                                    rsa.ImportFromPem(File.ReadAllText(keyPath));
                                    certificate = pemCert.CopyWithPrivateKey(rsa);
                                }
                                else
                                {
                                    certificate = pemCert;
                                }
                            }

                            options.ListenAnyIP(port, lo => lo.UseHttps(certificate));
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("[CERT] Falha ao carregar certificado: " + ex.Message);
                            Logger.LogInfo("[CERT] Fallback para HTTP (dev). Defina Certificate no appsettings para HTTPS.");
                            options.ListenAnyIP(port); // fallback
                        }
                    }
                    else
                    {
                        options.ListenAnyIP(port);
                    }
                })
                .Configure((app) =>
                {
                    var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
                    var proxySettings = config.GetSection("ProxySettings").Get<ProxySettings>()
                        ?? throw new InvalidOperationException("Configuração 'ProxySettings' não encontrada ou inválida.");

                    Logger.LogInfo($"[STARTUP] Executando modo: {proxySettings.Mode}");

                    // AH pode servir estáticos
                    if (proxySettings.Mode == 3)
                    {
                        // 👉 Rewrite: /foo -> /foo.html se existir
                        app.Use(async (ctx, next) =>
                        {
                            if (HttpMethods.IsGet(ctx.Request.Method))
                            {
                                var path = ctx.Request.Path.Value;
                                if (!string.IsNullOrEmpty(path) &&
                                    path != "/" &&
                                    !Path.HasExtension(path))
                                {
                                    // ✅ pegue o ambiente via DI (IApplicationBuilder não tem .Environment)
                                    var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
                                    var webRoot = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");

                                    var candidate = Path.Combine(webRoot, path.TrimStart('/') + ".html");
                                    if (File.Exists(candidate))
                                    {
                                        ctx.Request.Path = new PathString(path + ".html");
                                    }
                                }
                            }
                            await next();
                        });

                        app.UseDefaultFiles();
                        app.UseStaticFiles();

                    }

                    // CORS único (sem middleware manual duplicado)
                    app.UseCors();

                    switch (proxySettings.Mode)
                    {
                        case 1: // TM
                        case 2: // PI
                            {
                                // Terminal proxy (exceto /health)
                                app.Use(async (ctx, next) =>
                                {
                                    if (!ctx.Request.Path.StartsWithSegments("/health"))
                                    {
                                        try
                                        {
                                            if (proxySettings.Mode == 1)
                                            {
                                                Logger.LogInfo("[PIPELINE] TM → melhor PI");
                                                var targetUrl = await TMResolver.ResolveBestProxyInstanceUrlAsync(ctx, config);
                                                if (string.IsNullOrEmpty(targetUrl))
                                                {
                                                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                                                    await ctx.Response.WriteAsync("No Proxy Instance available");
                                                    return;
                                                }
                                                await ProxyUtils.RedirectWithBodyAsync(ctx, targetUrl);
                                            }
                                            else
                                            {
                                                Logger.LogInfo("[PIPELINE] PI → melhor AH]");
                                                var targetUrl = await PIResolver.ResolveBestApplicationHandlerUrlAsync(ctx, config);
                                                if (string.IsNullOrEmpty(targetUrl))
                                                {
                                                    // O resolver do PI já respondeu 503 ("No Application Handler available")
                                                    return;
                                                }
                                                await ProxyUtils.RedirectWithBodyAsync(ctx, targetUrl);
                                            }
                                            return;
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.LogError("Falha ao redirecionar: " + ex);
                                            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                                            await ctx.Response.WriteAsync("Proxy error");
                                            return;
                                        }
                                    }

                                    await next();
                                });

                                app.UseRouting();
                                app.UseEndpoints(endpoints =>
                                {
                                    endpoints.MapGet("/health", async ctx =>
                                    {
                                        var role = proxySettings.Mode == 1 ? "TM" : "PI";
                                        ctx.Response.ContentType = "application/json; charset=utf-8";
                                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { role, ok = true }));
                                    });
                                });

                                Logger.LogInfo("[ENDPOINTS] TM/PI sem controllers mapeados");
                                break;
                            }

                        case 3: // AH
                            {
                                app.UseRouting();
                                app.UseEndpoints(endpoints =>
                                {
                                    Logger.LogInfo("[ENDPOINTS] Controllers mapeados (AH)");
                                    endpoints.MapControllers();
                                    endpoints.MapGet("/health", async ctx =>
                                    {
                                        ctx.Response.ContentType = "application/json; charset=utf-8";
                                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { role = "AH", ok = true }));
                                    });

                                    endpoints.MapFallbackToFile("index.html");
                                });

                                Logger.LogInfo("[PIPELINE] Application Handler ativado");
                                break;
                            }

                        default:
                            throw new InvalidOperationException($"Invalid mode: {proxySettings.Mode}");
                    }
                });
            })
            .ConfigureServices((hostingContext, services) =>
            {
                var configuration = hostingContext.Configuration;
                Logger.Initialize(configuration);

                // ✅ REGISTRA O ACCESSOR AQUI
                services.AddHttpContextAccessor();

                var proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                    ?? throw new InvalidOperationException("ProxySettings not configured properly.");
                var dockerSettings = configuration.GetSection("DockerSettings").Get<DockerSettings>() ?? new DockerSettings();

                // --------- CORS (único) ----------
                var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
                if (allowedOrigins == null || allowedOrigins.Length == 0)
                {
                    var port = configuration.GetValue<int>("GeneralPort", 7001);
                    allowedOrigins = new[] { $"https://localhost:{port}" };
                }

                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                        policy.WithOrigins(allowedOrigins)
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials());
                });

                // --------- HttpClients ----------
                services.AddHttpClient();
                services.AddHttpClient("AuthClient")
                    .ConfigurePrimaryHttpMessageHandler(sp =>
                    {
                        var baseUrl = configuration["AuthService:BaseUrl"] ?? "";
                        var isLocal = baseUrl.Contains("://localhost", StringComparison.OrdinalIgnoreCase);
                        var h = new SocketsHttpHandler();
                        if (isLocal)
                        {
                            h.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                            {
                                RemoteCertificateValidationCallback = (_, __, ___, ____) => true
                            };
                        }
                        return h;
                    });

                // --------- Serviços internos ----------
                if (proxySettings.Mode == 3) // AH
                {
                    services.AddControllers();

                    // ✅ ScriptExecutorService depende de IHttpContextAccessor agora
                    services.AddSingleton<ScriptExecutorService>();
                }

                services.AddSingleton(proxySettings);
                services.AddSingleton(dockerSettings);
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
        await host.RunAsync();
    }
}
