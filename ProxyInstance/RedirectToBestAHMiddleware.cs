using Microsoft.AspNetCore.Http;
using Anakim.Infrastructure;
using Anakim.ProxyInstance.Failover;

namespace Anakim.ProxyInstance
{
    // Middleware that intercepts HTTP requests and forwards them to the best available Application Handler (AH) using failover
    public class RedirectToBestAHMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;

        // Constructor receives dependencies via DI
        public RedirectToBestAHMiddleware(RequestDelegate next, InstanceRankingManager rankingManager, FailoverManager failoverManager)
        {
            _next = next;
            _rankingManager = rankingManager;
            _failoverManager = failoverManager;
        }

        // Middleware execution logic
        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            // Gets ranked AH instances from in-memory ranking manager
            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503; // Service unavailable
                await context.Response.WriteAsync("No Application Handler available.");
                return;
            }

            // Converts statistics into a list of ApplicationHandlerInfo for the FailoverManager
            var handlerList = rankedInstances.Select(ah => new ApplicationHandlerInfo
            {
                InstanceId = ah.ProcessStat.InstanceId,
                Url = $"https://{ah.SenderIp}:{ah.Ports.Api}", // Constructs forwarding URL
                Ranking = ah.ProcessStat.PrivateMemoryMB // Ranking metric (you can adjust logic here)
            }).ToList();

            _failoverManager.UpdateHandlers(handlerList); // Updates internal state of failover manager

            // Creates an HttpRequestMessage from the current context
            var requestMessage = CreateHttpRequestFromContext(context);

            try
            {
                // Attempts to forward the request with failover handling
                var responseMessage = await _failoverManager.ForwardWithFailover(requestMessage);

                // Copies response status and headers to the client response
                context.Response.StatusCode = (int)responseMessage.StatusCode;

                foreach (var header in responseMessage.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();

                foreach (var header in responseMessage.Content.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();

                // Copies the response body
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

            // Only copy body for methods that allow it
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                request.Content = new StreamContent(context.Request.Body);
            }

            // Copy all request headers
            foreach (var header in context.Request.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            return request;
        }
    }
}
