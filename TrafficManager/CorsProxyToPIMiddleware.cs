using Microsoft.AspNetCore.Http;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using System.Net;
using AnakimOrchestrator.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace AnakimOrchestrator.TrafficManager
{
    public class CorsProxyToPIMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly InstanceRankingManager _rankingManager;
        private readonly IConfiguration _configuration;

        public CorsProxyToPIMiddleware(RequestDelegate next, IHttpClientFactory httpClientFactory, InstanceRankingManager rankingManager, IConfiguration configuration)
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

            if (method == HttpMethods.Options)
            {
                var instance = _rankingManager.GetBestInstance();
                if (instance == null || string.IsNullOrWhiteSpace(instance.SenderIp) || instance.SenderPort == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Headers["Access-Control-Allow-Origin"] = origin ?? "*";
                    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
                    context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
                    context.Response.Headers["Access-Control-Max-Age"] = "86400";
                    await context.Response.WriteAsync("Nenhum Proxy Instance disponível.");
                    return;
                }

                // Reads the protocol from configuration
                bool useHttps = _configuration.GetValue<bool>("UseHttps");
                string protocol = useHttps ? "https" : "http";

                var targetUrl = $"{protocol}://{instance.SenderIp}:{instance.SenderPort.GeneralPort}{context.Request.Path}{context.Request.QueryString}";

                using var client = _httpClientFactory.CreateClient();
                using var requestMessage = new HttpRequestMessage(HttpMethod.Options, targetUrl);

                foreach (var header in context.Request.Headers)
                    requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());

                try
                {
                    var response = await client.SendAsync(requestMessage);

                    context.Response.StatusCode = (int)response.StatusCode;

                    foreach (var header in response.Headers.Concat(response.Content.Headers))
                        context.Response.Headers[header.Key] = header.Value.ToArray();

                    if (!context.Response.Headers.ContainsKey("Access-Control-Allow-Origin") && origin != null)
                        context.Response.Headers["Access-Control-Allow-Origin"] = origin;

                    await response.Content.CopyToAsync(context.Response.Body);
                    await context.Response.CompleteAsync();
                }
                catch (HttpRequestException ex)
                {
                    context.Response.StatusCode = 502;
                    await context.Response.WriteAsync($"Erro ao encaminhar preflight: {ex.Message}");
                }

                return;
            }

            await _next(context);
        }
    }
}
