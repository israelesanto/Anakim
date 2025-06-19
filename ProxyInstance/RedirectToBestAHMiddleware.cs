using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Anakim.Infrastructure;

namespace Anakim.ProxyInstance
{
    // Middleware that intercepts HTTP requests and forwards them to the best available Application Handler (AH) using failover
    public class RedirectToBestAHMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;
        private readonly IConfiguration _configuration;

        // Constructor receives dependencies via DI
        public RedirectToBestAHMiddleware(
            RequestDelegate next,
            InstanceRankingManager rankingManager,
            FailoverManager failoverManager,
            IConfiguration configuration)
        {
            _next = next;
            _rankingManager = rankingManager;
            _failoverManager = failoverManager;
            _configuration = configuration;
        }

        // Middleware execution logic
        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503; // Service unavailable
                await context.Response.WriteAsync("No Application Handler available.");
                return;
            }

            // Reads the protocol from configuration
            bool useHttps = _configuration.GetValue<bool>("UseHttps");
            string protocol = useHttps ? "https" : "http";

            /*
            // Builds the list of handler URLs
            var handlerList = rankedInstances
                .Select(ah => new ApplicationHandlerInfo
            {
                InstanceId = ah.ProcessStat.InstanceId,
                Url = $"{protocol}://{ah.SenderIp}:{ah.Ports.Api}",
                Ranking = ah.ProcessStat.PrivateMemoryMB
            }).ToList();
            */

            // Builds the list of handler URLs
            var handlerList = rankedInstances
                .Select(ah => new ApplicationHandlerInfo
                {
                    InstanceId = ah.ProcessStat?.InstanceId ?? "unknow",
                    Url = $"{protocol}://{ah.SenderIp}:{ah.Port?.GeneralPort ?? 0}",
                    Ranking = ah.ProcessStat?.PrivateMemoryMB ?? 0
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
                Logger.LogError($"Error forwarding request with failover: {ex.Message}");
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Error forwarding to Application Handler with failover.");
            }
        }

        // Converts the current HttpContext into an HttpRequestMessage for forwarding
        private HttpRequestMessage CreateHttpRequestFromContext(HttpContext context)
        {
            var placeholderUrl = "http://placeholder"; // Will be replaced by FailoverManager
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
