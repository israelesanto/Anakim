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
using System.Net.WebSockets;   // <- p/ WebSocket
using System.Net.Sockets;     // <- p/ TcpClient
using System.Text;            // <- p/ StringBuilder, Encoding
using System.IO;              // <- p/ File/Path
using Microsoft.AspNetCore.Server.Kestrel.Core;
using AnakimOrchestrator.Infrastructure.RequestProtection.Contracts;
using AnakimOrchestrator.Infrastructure.RequestProtection.Journal;
using AnakimOrchestrator.Infrastructure.RequestProtection.Options;
using AnakimOrchestrator.Infrastructure.RequestProtection.Services;
using AnakimOrchestrator.Infrastructure.RequestProtection.Stores;

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

                    // Carrega o certificado uma vez
                    X509Certificate2? certificate = null;
                    if (useHttps)
                    {
                        try
                        {
                            var pfxSection = configuration.GetSection("Certificate:Pfx");
                            var pemSection = configuration.GetSection("Certificate:Pem");

                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                var pfxPath = pfxSection.GetValue<string>("Path");
                                var password = pfxSection.GetValue<string>("Password");
                                if (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath))
                                    throw new FileNotFoundException($"Arquivo PFX não encontrado: {pfxPath}");
                                Logger.LogInfo("[CERT] Windows - carregando .pfx");
                                certificate = new X509Certificate2(pfxPath!, password);
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
                                var pemCert = X509Certificate2.CreateFromPemFile(certPath!, keyPath!);
                                if (!pemCert.HasPrivateKey)
                                {
                                    using var rsa = RSA.Create();
                                    rsa.ImportFromPem(File.ReadAllText(keyPath!));
                                    certificate = pemCert.CopyWithPrivateKey(rsa);
                                }
                                else
                                {
                                    certificate = pemCert;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("[CERT] Falha ao carregar certificado: " + ex.Message);
                            Logger.LogInfo("[CERT] Fallback para HTTP no listener principal.");
                        }
                    }

                    // Listener principal (pode ser h1 apenas se também for usar WS nele)
                    if (useHttps && certificate != null)
                        options.ListenAnyIP(port, lo =>
                        {
                            lo.Protocols = HttpProtocols.Http1;   // <- garante Upgrade p/ WS
                            lo.UseHttps(certificate);
                        });
                    else
                        options.ListenAnyIP(port, lo => lo.Protocols = HttpProtocols.Http1);

                    // WebSocket Bridge em 8083 (ws ou wss)
                    var wsBridgePort = configuration.GetValue<int?>("WebSocketBridge:Port") ?? 8083;
                    var wsBridgeUseHttps = configuration.GetValue<bool?>("WebSocketBridge:UseHttps") ?? useHttps;

                    if (wsBridgePort != port)
                    {
                        if (wsBridgeUseHttps && certificate != null)
                            options.ListenAnyIP(wsBridgePort, lo =>
                            {
                                lo.Protocols = HttpProtocols.Http1; // <- ESSENCIAL p/ WebSocket
                                lo.UseHttps(certificate);
                            });
                        else
                            options.ListenAnyIP(wsBridgePort, lo =>
                            {
                                lo.Protocols = HttpProtocols.Http1; // <- ESSENCIAL p/ WebSocket
                            });
                    }
                })

                .Configure((app) =>
                {
                    var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
                    var requestProtectionService = app.ApplicationServices.GetRequiredService<IRequestProtectionService>();

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

                    // Habilita WebSockets no pipeline (sempre antes do Map /stats)
                    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

                    app.Map("/stats", branch =>
                    {
                        branch.Run(async ctx =>
                        {
                            if (!ctx.WebSockets.IsWebSocketRequest)
                            {
                                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await ctx.Response.WriteAsync("WebSocket endpoint");
                                return;
                            }

                            var wsHost = config.GetValue<string>("WebSocketBridge:Host") ?? "127.0.0.1";
                            var portStream = config.GetValue<int?>("ProxySettings:PortStream") ?? 5101;

                            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                            using var tcp = new TcpClient { NoDelay = true };

                            try
                            {
                                await tcp.ConnectAsync(wsHost, portStream);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError($"[WSBRIDGE] Falha ao conectar TCP {wsHost}:{portStream}: {ex.Message}");
                                try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "TCP connect failed", ctx.RequestAborted); } catch { }
                                return;
                            }

                            using var ns = tcp.GetStream();
                            var buf = new byte[8192];
                            var acc = new StringBuilder();

                            // TCP (NDJSON) -> WS (texto)
                            while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
                            {
                                int read;
                                try
                                {
                                    read = await ns.ReadAsync(buf.AsMemory(0, buf.Length), ctx.RequestAborted);
                                    if (read <= 0) break;
                                }
                                catch (OperationCanceledException) { break; }
                                catch (Exception ex)
                                {
                                    Logger.LogError("[WSBRIDGE] Erro lendo do TCP NDJSON: " + ex.Message);
                                    break;
                                }

                                acc.Append(Encoding.UTF8.GetString(buf, 0, read));

                                string s = acc.ToString();
                                int idx;
                                while ((idx = s.IndexOf('\n')) >= 0)
                                {
                                    var line = s[..idx].TrimEnd('\r');
                                    if (line.Length > 0)
                                    {
                                        var payload = Encoding.UTF8.GetBytes(line);
                                        await ws.SendAsync(payload, WebSocketMessageType.Text, true, ctx.RequestAborted);
                                    }
                                    s = s[(idx + 1)..];
                                }
                                acc.Clear();
                                acc.Append(s);
                            }

                            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "TCP ended", ctx.RequestAborted); } catch { }
                        });
                    });

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
                                                Logger.LogInfo("[PIPELINE] PI → melhor AH");

                                                var requestBody = await ReadRequestBodyAsync(ctx);

                                                var protectedRequest = await requestProtectionService.RegisterAsync(
                                                    ctx,
                                                    requestBody,
                                                    ctx.RequestAborted);

                                                await requestProtectionService.MarkAsPersistedAsync(
                                                    protectedRequest.RequestId,
                                                    ctx.RequestAborted);

                                                var targetUrl = await PIResolver.ResolveBestApplicationHandlerUrlAsync(ctx, config);
                                                if (string.IsNullOrEmpty(targetUrl))
                                                {
                                                    await requestProtectionService.MarkAsFailedAsync(
                                                        protectedRequest.RequestId,
                                                        "Nenhum Application Handler disponível para processar a requisição.",
                                                        ctx.RequestAborted);

                                                    return;
                                                }

                                                await requestProtectionService.MarkAsDispatchingAsync(
                                                    protectedRequest.RequestId,
                                                    targetUrl,
                                                    ctx.RequestAborted);

                                                await requestProtectionService.MarkAsInFlightAsync(
                                                    protectedRequest.RequestId,
                                                    ctx.RequestAborted);

                                                var forwardResult = await ProxyUtils.RedirectWithBodyAsync(ctx, targetUrl);

                                                if (forwardResult.Success)
                                                {
                                                    await requestProtectionService.MarkAsCompletedAsync(
                                                        protectedRequest.RequestId,
                                                        forwardResult.UpstreamStatusCode ?? ctx.Response.StatusCode,
                                                        null,
                                                        ctx.RequestAborted);

                                                    return;
                                                }

                                                if (forwardResult.ClientCancelled)
                                                {
                                                    Logger.LogInfo($"[PI] Requisição cancelada pelo cliente. RequestId: {protectedRequest.RequestId}");

                                                    await requestProtectionService.MarkAsFailedAsync(
                                                        protectedRequest.RequestId,
                                                        forwardResult.ErrorMessage ?? "A requisição foi cancelada pelo cliente.",
                                                        ctx.RequestAborted);

                                                    return;
                                                }

                                                // A partir do momento em que a requisição já foi marcada como InFlight,
                                                // qualquer falha sem sucesso confirmado deve ser tratada como estado incerto.
                                                Logger.LogWarning($"[PI] Estado incerto detectado após InFlight. RequestId: {protectedRequest.RequestId}. Erro: {forwardResult.ErrorMessage}");

                                                await requestProtectionService.MarkAsUnknownAsync(
                                                    protectedRequest.RequestId,
                                                    forwardResult.ErrorMessage ?? "Falha em estado incerto durante o encaminhamento ao Application Handler.",
                                                    ctx.RequestAborted);

                                                return;
                                            }
                                            return;
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.LogError("Falha ao redirecionar: " + ex);
                                            if (!ctx.Response.HasStarted)
                                            {
                                                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                                                await ctx.Response.WriteAsync("Proxy error");
                                            }
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

                services.Configure<RequestProtectionOptions>(
                    configuration.GetSection("RequestProtection"));

                services.AddSingleton<IProtectedRequestStore, ProtectedRequestStore>();
                services.AddSingleton<IRequestJournal, FileRequestJournal>();
                services.AddSingleton<IRequestProtectionService, RequestProtectionService>();

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
                services.AddSingleton<IProxyStatisticsAggregator, ProxyStatisticsAggregator>();
                services.AddSingleton<ITrafficManagerStatisticsAggregator, TrafficManagerStatisticsAggregator>();
                services.AddSingleton<IAhMetricsProvider, MyAhMetricsProvider>(); // usado no PI
                //services.AddSingleton<IPiMetricsProvider, MyPiMetricsProvider>(); // usado no TM
                services.AddSingleton<IPiMetricsProvider, RankingPiMetricsProvider>(); // usado no TM
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

    private static async Task<string> ReadRequestBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();

        context.Request.Body.Position = 0;

        using var reader = new StreamReader(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var body = await reader.ReadToEndAsync();

        context.Request.Body.Position = 0;

        return body;
    }
}
