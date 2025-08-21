using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.ProxyInstance
{
    public class ProxyCorsRedirectMiddleware
    {
        private static readonly string[] RestrictedResponseHeaders = new[]
        {
            // hop-by-hop / calculados
            "Transfer-Encoding", "Content-Length", "Keep-Alive", "Connection",
            "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Upgrade"
        };

        private static readonly string[] RestrictedRequestHeaders = new[]
        {
            // não devem ser reenviados como headers "normais"
            "Host", "Content-Length", "Connection", "Transfer-Encoding", "Proxy-Connection",
            "TE", "Trailer", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization", "Keep-Alive"
        };

        private readonly RequestDelegate _next;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly InstanceRankingManager _rankingManager;
        private readonly IConfiguration _configuration;

        public ProxyCorsRedirectMiddleware(
            RequestDelegate next,
            IHttpClientFactory httpClientFactory,
            InstanceRankingManager rankingManager,
            IConfiguration configuration)
        {
            _next = next;
            _httpClientFactory = httpClientFactory;
            _rankingManager = rankingManager;
            _configuration = configuration;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var origin = context.Request.Headers["Origin"].FirstOrDefault();
            var method = context.Request.Method;

            // Pré-flight
            if (HttpMethods.Options.Equals(method, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                if (!string.IsNullOrEmpty(origin))
                {
                    context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                    context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
                }
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
                context.Response.Headers["Access-Control-Max-Age"] = "86400";
                return;
            }

            // Seleciona o melhor AH disponível
            var instance = _rankingManager.GetBestInstance();
            if (instance == null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Nenhum Application Handler disponível.");
                return;
            }

            if (string.IsNullOrWhiteSpace(instance.SenderIp) || instance.SenderPort == null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Instância sem IP ou porta definida.");
                return;
            }

            // Protocolo público a usar
            bool useHttps = _configuration.GetValue<bool>("UseHttps");
            string protocol = useHttps ? "https" : "http";

            var targetUrl = $"{protocol}://{instance.SenderIp}:{instance.SenderPort?.GeneralPort}{context.Request.Path}{context.Request.QueryString}";

            var client = _httpClientFactory.CreateClient();
            using var outbound = new HttpRequestMessage(new HttpMethod(method), targetUrl);

            // Copia cabeçalhos de requisição (exceto proibidos)
            foreach (var header in context.Request.Headers)
            {
                var key = header.Key;

                if (RestrictedRequestHeaders.Contains(key, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Authorization do cliente é ignorado — será substituído pelo do cookie
                if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Tenta adicionar como header "geral", senão como header de conteúdo
                if (!outbound.Headers.TryAddWithoutValidation(key, header.Value.ToArray()))
                {
                    if (outbound.Content == null)
                        outbound.Content = new ByteArrayContent(Array.Empty<byte>());

                    outbound.Content.Headers.TryAddWithoutValidation(key, header.Value.ToArray());
                }
            }

            // Corpo da requisição (se houver)
            if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > 0)
            {
                context.Request.EnableBuffering();
                context.Request.Body.Position = 0;

                var ms = new MemoryStream((int)context.Request.ContentLength.Value);
                await context.Request.Body.CopyToAsync(ms, context.RequestAborted);
                ms.Position = 0;

                outbound.Content = new StreamContent(ms);

                if (!string.IsNullOrEmpty(context.Request.ContentType))
                {
                    // define Content-Type corretamente
                    outbound.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
                }
            }

            // 🔐 Injeta Authorization a partir do cookie HttpOnly (BFF)
            var cookieName = _configuration["Cookies:Name"] ?? "anakim_auth";
            if (context.Request.Cookies.TryGetValue(cookieName, out var tok) && !string.IsNullOrEmpty(tok))
            {
                outbound.Headers.Remove("Authorization");
                outbound.Headers.TryAddWithoutValidation("Authorization", $"Bearer {tok}");
            }

            // Envia (streaming)
            HttpResponseMessage inbound;
            try
            {
                inbound = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[PROXY ERROR] {ex.Message}");
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Bad Gateway");
                return;
            }

            // Status
            context.Response.StatusCode = (int)inbound.StatusCode;

            // Copia headers de resposta (exceto proibidos)
            foreach (var h in inbound.Headers)
            {
                if (RestrictedResponseHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)) continue;
                context.Response.Headers[h.Key] = h.Value.ToArray();
            }
            foreach (var h in inbound.Content.Headers)
            {
                if (RestrictedResponseHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)) continue;
                context.Response.Headers[h.Key] = h.Value.ToArray();
            }

            // CORS garantido
            if (!string.IsNullOrEmpty(origin))
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            }

            // Evita conflitos de transferência
            context.Response.Headers.Remove("transfer-encoding");
            context.Response.Headers.Remove("content-length");

            // Corpo
            await inbound.Content.CopyToAsync(context.Response.Body);
        }
    }
}
