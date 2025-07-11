using Microsoft.AspNetCore.Http;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using System.Net;
using AnakimOrchestrator.Infrastructure;
using System.Configuration;
using Microsoft.Extensions.Configuration;

namespace AnakimOrchestrator.ProxyInstance
{
    public class ProxyCorsRedirectMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly InstanceRankingManager _rankingManager;
        private readonly IConfiguration _configuration;

        public ProxyCorsRedirectMiddleware(RequestDelegate next, IHttpClientFactory httpClientFactory, InstanceRankingManager rankingManager, IConfiguration configuration)
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
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Headers["Access-Control-Allow-Origin"] = origin ?? "*";
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
                context.Response.Headers["Access-Control-Max-Age"] = "86400";
                return;
            }

            // Seleciona o melhor AH disponível
            var instance = _rankingManager.GetBestInstance();
            if (instance == null)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("Nenhum Application Handler disponível.");
                return;
            }

            if (string.IsNullOrWhiteSpace(instance.SenderIp) || instance.SenderPort == null)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("Instância sem IP ou porta definida.");
                return;
            }

            // Reads the protocol from configuration
            bool useHttps = _configuration.GetValue<bool>("UseHttps");
            string protocol = useHttps ? "https" : "http";

            var targetUrl = $"{protocol}://{instance.SenderIp}:{instance.SenderPort?.GeneralPort}{context.Request.Path}{context.Request.QueryString}";

            using var client = _httpClientFactory.CreateClient();
            using var requestMessage = new HttpRequestMessage(new HttpMethod(method), targetUrl);

            // Copia cabeçalhos da requisição original
            foreach (var header in context.Request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            // Copia o corpo da requisição, se houver
            if (context.Request.ContentLength > 0)
            {
                using var ms = new MemoryStream();
                await context.Request.Body.CopyToAsync(ms);
                ms.Seek(0, SeekOrigin.Begin);
                requestMessage.Content = new StreamContent(ms);
                requestMessage.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(context.Request.ContentType);
            }

            var response = await client.SendAsync(requestMessage);

            context.Response.StatusCode = (int)response.StatusCode;

            // Copia todos os cabeçalhos de resposta, incluindo CORS
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            // Garante o header CORS mesmo que não tenha vindo do AH
            if (!context.Response.Headers.ContainsKey("Access-Control-Allow-Origin") && origin != null)
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;

            await response.Content.CopyToAsync(context.Response.Body);
        }
    }
}
