using System.Net.Http;
using Anakim.Infrastructure;
using Anakim.ProxyInstance.Failover;
using Microsoft.AspNetCore.Http;
using System.Linq;

namespace Anakim.TrafficManager
{
    public class RedirectToBestPIMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;

        public RedirectToBestPIMiddleware(RequestDelegate next, InstanceRankingManager rankingManager, FailoverManager failoverManager)
        {
            _next = next;
            _rankingManager = rankingManager;
            _failoverManager = failoverManager;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("Nenhum Proxy Instance disponível.");
                return;
            }

            var handlerList = rankedInstances.Select(pi => new ApplicationHandlerInfo
            {
                InstanceId = pi.ProcessStat.InstanceId,
                Url = $"https://{pi.SenderIp}:{pi.Ports.Api}",
                Ranking = pi.ProcessStat.PrivateMemoryMB
            }).ToList();

            _failoverManager.UpdateHandlers(handlerList);

            var requestMessage = CreateHttpRequestFromContext(context);

            try
            {
                var responseMessage = await _failoverManager.ForwardWithFailover(requestMessage);

                context.Response.StatusCode = (int)responseMessage.StatusCode;

                foreach (var header in responseMessage.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();

                foreach (var header in responseMessage.Content.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();

                await responseMessage.Content.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Erro ao redirecionar com failover para PI: {ex.Message}");
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Erro ao redirecionar para o Proxy Instance com failover.");
            }
        }

        private HttpRequestMessage CreateHttpRequestFromContext(HttpContext context)
        {
            var placeholderUrl = "http://placeholder"; // será substituído internamente
            var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), placeholderUrl);

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                request.Content = new StreamContent(context.Request.Body);
            }

            foreach (var header in context.Request.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            return request;
        }
    }
}
