using Microsoft.AspNetCore.Http;
using Anakim.Infrastructure;
using Anakim.ProxyInstance.Failover;

namespace Anakim.ProxyInstance
{
    public class RedirectToBestAHMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;

        public RedirectToBestAHMiddleware(RequestDelegate next, InstanceRankingManager rankingManager, FailoverManager failoverManager)
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
                await context.Response.WriteAsync("Nenhum AH disponível.");
                return;
            }

            var handlerList = rankedInstances.Select(ah => new ApplicationHandlerInfo
            {
                InstanceId = ah.ProcessStat.InstanceId,
                Url = $"https://{ah.SenderIp}:{ah.Ports.Api}",
                Ranking = ah.ProcessStat.PrivateMemoryMB // ajuste aqui a lógica desejada
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
                Logger.LogError($"Erro ao redirecionar com failover: {ex.Message}");
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Erro ao redirecionar para o AH com failover.");
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
