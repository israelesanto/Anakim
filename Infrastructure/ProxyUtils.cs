using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;

namespace AnakimOrchestrator.Infrastructure
{
    public static class ProxyUtils
    {
        private static readonly HashSet<string> RestrictedResponseHeaders = new()
        {
            "Transfer-Encoding",
            "Content-Length",
            "Keep-Alive",
            "Connection",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Upgrade"
        };

        public static async Task RedirectWithBodyAsync(HttpContext context, string targetUrl)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            using var httpClient = new HttpClient(handler);

            // Habilita leitura múltipla do body
            context.Request.EnableBuffering();

            byte[] buffer;
            if (context.Request.ContentLength != null && context.Request.ContentLength > 0)
            {
                buffer = new byte[context.Request.ContentLength.Value];
                await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length));
                context.Request.Body.Position = 0;
            }
            else
            {
                buffer = Array.Empty<byte>();
            }

            using var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = new Uri(targetUrl),
                Content = new ByteArrayContent(buffer)
            };

            // Copia os headers da requisição original
            foreach (var header in context.Request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            // Faz a requisição
            using var response = await httpClient.SendAsync(requestMessage);

            context.Response.StatusCode = (int)response.StatusCode;

            // Copia headers válidos da resposta
            foreach (var header in response.Headers)
            {
                if (!RestrictedResponseHeaders.Contains(header.Key))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in response.Content.Headers)
            {
                if (!RestrictedResponseHeaders.Contains(header.Key))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            // Copia o body da resposta
            var responseBody = await response.Content.ReadAsByteArrayAsync();
            await context.Response.Body.WriteAsync(responseBody);
            await context.Response.Body.FlushAsync(); 
        }
    }
}
