using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using AnakimOrchestrator.Infrastructure.RequestProtection.Models;

namespace AnakimOrchestrator.Infrastructure
{
    public static class ProxyUtils
    {
        private static readonly HashSet<string> RestrictedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host","Connection","Proxy-Connection","Keep-Alive","Upgrade","TE","Trailer",
            "Transfer-Encoding","Expect",
            "Accept-Encoding", // HttpClient descompacta sozinho
            "Content-Length"   // deixa o HttpClient calcular
        };

        private static readonly HashSet<string> RestrictedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Transfer-Encoding","Connection","Keep-Alive","Proxy-Authenticate","Proxy-Authorization",
            "TE","Trailer","Upgrade","Content-Length"
        };

        // ---------- HttpClient (pool) ----------
        private static HttpClient CreateClient(bool acceptAnyCert)
        {
            var h = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 1024
            };

            if (acceptAnyCert)
            {
                h.SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, __, ___, ____) => true
                };
            }

            return new HttpClient(h, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan // controlado via CTS
            };
        }

        private static readonly Lazy<HttpClient> ClientRelaxed = new(() => CreateClient(acceptAnyCert: true));
        private static readonly Lazy<HttpClient> ClientStrict = new(() => CreateClient(acceptAnyCert: false));

        private static bool IsPrivateOrLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

            if (IPAddress.TryParse(host, out var ip))
            {
                if (IPAddress.IsLoopback(ip)) return true;

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var b = ip.GetAddressBytes();
                    if (b[0] == 10) return true;                             // 10/8
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16/12
                    if (b[0] == 192 && b[1] == 168) return true;              // 192.168/16
                    if (b[0] == 169 && b[1] == 254) return true;              // 169.254/16
                }
            }
            return false;
        }

        // ---------- Proxy principal ----------
        public static async Task<ProxyForwardResult> RedirectWithBodyAsync(HttpContext context, string targetUrl)
        {
            var result = new ProxyForwardResult();

            var cfg = context.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;
            var target = new Uri(targetUrl);

            // Anti-loop: impedir redirecionar para o próprio host/porta
            if (context.Request.Host.HasValue)
            {
                var reqHost = context.Request.Host.Host;
                var reqPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
                var sameHost = string.Equals(target.Host, reqHost, StringComparison.OrdinalIgnoreCase);
                var samePort = target.IsDefaultPort ? (reqPort == 80 || reqPort == 443) : (target.Port == reqPort);

                if (sameHost && samePort)
                {
                    result.ErrorMessage = "Self-proxy loop prevented";

                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await context.Response.WriteAsync("Self-proxy loop prevented", context.RequestAborted);
                    return result;
                }
            }

            // Usa client "relaxado" apenas para destinos internos (localhost/RFC1918)
            var client = IsPrivateOrLoopbackHost(target.Host) ? ClientRelaxed.Value : ClientStrict.Value;

            // Timeout (segundos) — se não existir, usa 30
            var timeoutSec = cfg?.GetValue<int?>("ProxySettings:RequestTimeout") ?? 30;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            if (timeoutSec > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            }

            // Conteúdo: não copiar p/ memória; stream direto do request
            StreamContent? streamContent = null;
            if (string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.Request.Method, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.Request.Method, "TRACE", StringComparison.OrdinalIgnoreCase))
            {
                streamContent = null;
            }
            else
            {
                var body = context.Request.Body;
                if (body.CanSeek)
                {
                    body.Position = 0;
                }

                streamContent = new StreamContent(body);
            }

            using var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = target,
                Content = streamContent
            };

            // Copia headers (exceto hop-by-hop / problemáticos)
            foreach (var header in context.Request.Headers)
            {
                if (RestrictedRequestHeaders.Contains(header.Key))
                {
                    continue;
                }

                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token
                );

                result.ConnectionEstablished = true;
                result.RequestBodySent = requestMessage.Content != null;
                result.ResponseHeadersReceived = true;
                result.UpstreamStatusCode = (int)response.StatusCode;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                result.ClientCancelled = true;
                result.ErrorMessage = "Client cancelled the request.";
                return result;
            }
            catch (OperationCanceledException)
            {
                result.IsTimeout = true;
                result.IsUnknownState = result.ConnectionEstablished || result.RequestBodySent;
                result.ErrorMessage = "Upstream timeout";

                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response.WriteAsync("Upstream timeout", context.RequestAborted);
                return result;
            }
            catch (Exception ex)
            {
                result.IsUnknownState = result.ConnectionEstablished || result.RequestBodySent;
                result.ErrorMessage = ex.Message;

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Proxy error", context.RequestAborted);
                Logger.LogError("Proxy forward error → " + ex);
                return result;
            }

            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
            {
                if (!RestrictedResponseHeaders.Contains(header.Key))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            foreach (var header in response.Content.Headers)
            {
                if (!RestrictedResponseHeaders.Contains(header.Key))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            context.Response.Headers.Remove("Content-Length");
            context.Response.Headers.Remove("Transfer-Encoding");

            result.ResponseBodyStarted = true;

            await using var respStream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            await respStream.CopyToAsync(context.Response.Body, 81_920, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            result.Success = true;
            return result;
        }

        /// <summary>
        /// 1 = localhost, 2 = SenderIp, 3 = ContainerName (fallback para SenderIp)
        /// </summary>
        public static string GetRedirectHost(IConfiguration config, NodeStatistics instance)
        {
            var mode = config.GetValue<int>("ProxySettings:RedirectionMode");
            return mode switch
            {
                1 => "localhost",
                2 => instance.SenderIp ?? "localhost",
                3 => !string.IsNullOrWhiteSpace(instance.ContainerName) ? instance.ContainerName : instance.SenderIp ?? "localhost",
                _ => instance.SenderIp ?? "localhost"
            };
        }
    }
}
