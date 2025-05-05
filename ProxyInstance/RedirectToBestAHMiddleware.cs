//using System.Net.Http;
//using Anakim.Infrastructure;
//using Microsoft.AspNetCore.Http;

//namespace Anakim.Infrastructure
//{
//    public class RedirectToBestAHMiddleware
//    {
//        private readonly RequestDelegate _next;
//        private readonly InstanceRankingManager _rankingManager;

//        public RedirectToBestAHMiddleware(RequestDelegate next, InstanceRankingManager rankingManager)
//        {
//            _next = next;
//            _rankingManager = rankingManager;
//        }

//        public async Task InvokeAsync(HttpContext context)
//        {
//            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

//            var bestAH = _rankingManager.GetBestInstance();

//            if (bestAH == null)
//            {
//                context.Response.StatusCode = 503;
//                await context.Response.WriteAsync("Nenhum AH disponível.");
//                return;
//            }

//            var destinationUrl = $"https://{bestAH.SenderIp}:{bestAH.Ports.Api}{context.Request.Path}{context.Request.QueryString}";

//            using var client = new HttpClient();
//            var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), destinationUrl);

//            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
//                requestMessage.Content = new StreamContent(context.Request.Body);

//            foreach (var header in context.Request.Headers)
//                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());

//            try
//            {
//                var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

//                context.Response.StatusCode = (int)responseMessage.StatusCode;

//                foreach (var header in responseMessage.Headers)
//                    context.Response.Headers[header.Key] = header.Value.ToArray();

//                foreach (var header in responseMessage.Content.Headers)
//                    context.Response.Headers[header.Key] = header.Value.ToArray();

//                await responseMessage.Content.CopyToAsync(context.Response.Body);
//            }
//            catch (Exception ex)
//            {
//                Logger.LogError($"Erro ao redirecionar para AH: {ex.Message}");
//                context.Response.StatusCode = 502;
//                await context.Response.WriteAsync("Erro ao redirecionar para o AH.");
//            }
//        }
//    }
//}

using Microsoft.AspNetCore.Http;
using Anakim.Infrastructure;

namespace Anakim.ProxyInstance
{
    public class RedirectToBestAHMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;

        // ✅ HttpClient singleton
        private static readonly HttpClient _httpClient = new HttpClient(
            new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
        {
            Timeout = TimeSpan.FromSeconds(30) // ajuste conforme necessário
        };

        public RedirectToBestAHMiddleware(RequestDelegate next, InstanceRankingManager rankingManager)
        {
            _next = next;
            _rankingManager = rankingManager;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            var bestAH = _rankingManager.GetBestInstance();

            if (bestAH == null)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("Nenhum AH disponível.");
                return;
            }

            var destinationUrl = $"https://{bestAH.SenderIp}:{bestAH.Ports.Api}{context.Request.Path}{context.Request.QueryString}";

            var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), destinationUrl);

            // Copia o corpo da requisição se não for GET ou HEAD
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                requestMessage.Content = new StreamContent(context.Request.Body);
            }

            // Copia os headers
            foreach (var header in context.Request.Headers)
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            try
            {
                var responseMessage = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                context.Response.StatusCode = (int)responseMessage.StatusCode;

                foreach (var header in responseMessage.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                foreach (var header in responseMessage.Content.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                await responseMessage.Content.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Erro ao redirecionar para AH: {ex.Message}");
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Erro ao redirecionar para o AH.");
            }
        }
    }
}

