using System.Net.Http;
using Anakim.Infrastructure;
using Anakim.ProxyInstance.Failover;
using Microsoft.AspNetCore.Http;
using System.Linq;

namespace Anakim.TrafficManager
{
    // Middleware used by the Traffic Manager to forward requests to the best available Proxy Instance
    public class RedirectToBestPIMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;

        // Constructor receives dependencies via Dependency Injection
        public RedirectToBestPIMiddleware(RequestDelegate next, InstanceRankingManager rankingManager, FailoverManager failoverManager)
        {
            _next = next;
            _rankingManager = rankingManager;
            _failoverManager = failoverManager;
        }

        // Middleware logic for handling and redirecting the request
        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            // Gets the list of ranked Proxy Instances from memory
            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503; // Service Unavailable
                await context.Response.WriteAsync("No Proxy Instance available.");
                return;
            }

            // Converts statistics into failover handler list
            var handlerList = rankedInstances.Select(pi => new ApplicationHandlerInfo
            {
                InstanceId = pi.ProcessStat.InstanceId,
                Url = $"https://{pi.SenderIp}:{pi.Ports.Api}", // Target URL of Proxy Instance
                Ranking = pi.ProcessStat.PrivateMemoryMB // Ranking logic (can be customized)
            }).ToList();

            // Updates internal handler list used by FailoverManager
            _failoverManager.UpdateHandlers(handlerList);

            // Converts current HTTP request into HttpRequestMessage
            var requestMessage = CreateHttpRequestFromContext(context);

            try
            {
                // Tries to forward using failover logic
                var responseMessage = await _failoverManager.ForwardWithFailover(requestMessage);

                // Copies status code and headers from response
                context.Response.StatusCode = (int)responseMessage.StatusCode;

                foreach (var header in responseMessage.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();

                foreach (var header in responseMessage.Content.Headers)
                    context.Response.Headers[header.Key] = header.Value.ToArray();

                // Forwards the body content to the original requester
                await responseMessage.Content.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error forwarding to Proxy Instance with failover: {ex.Message}");
                context.Response.StatusCode = 502; // Bad Gateway
                await context.Response.WriteAsync("Error redirecting to Proxy Instance.");
            }
        }

        // Converts HttpContext to HttpRequestMessage for outbound forwarding
        private HttpRequestMessage CreateHttpRequestFromContext(HttpContext context)
        {
            var placeholderUrl = "http://placeholder"; // Will be replaced by the FailoverManager
            var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), placeholderUrl);

            // Copies request body for methods that support it
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                request.Content = new StreamContent(context.Request.Body);
            }

            // Copies all headers
            foreach (var header in context.Request.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            return request;
        }
    }
}
