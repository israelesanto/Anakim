using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Anakim.ProxyInstance.Failover
{
    // Represents a single Application Handler (AH) and its metadata for failover handling
    public class ApplicationHandlerInfo
    {
        public string InstanceId { get; set; } = string.Empty;             // Unique ID of the AH
        public string Url { get; set; } = string.Empty;                    // Base URL for forwarding
        public double Ranking { get; set; }                                // Priority for routing
        public DateTime LastFailureTime { get; set; }                      // Last time this AH failed
        public bool TemporarilyUnavailable { get; set; }                   // Marked unavailable due to failure
    }

    // Handles routing and automatic failover among multiple Application Handlers
    public class FailoverManager
    {
        private readonly ConcurrentDictionary<string, ApplicationHandlerInfo> _handlers = new(); // AH registry
        private readonly HttpClient _httpClient = new(); // Used for forwarding and health checks
        private readonly Timer _healthCheckTimer; // Periodically checks for AH recovery
        private readonly TimeSpan _retryInterval = TimeSpan.FromSeconds(30); // Retry interval for failed AHs

        public FailoverManager()
        {
            // Starts the health check timer
            _healthCheckTimer = new Timer(HealthCheckCallback, null, _retryInterval, _retryInterval);
        }

        // Updates or inserts new handlers into the dictionary
        public void UpdateHandlers(List<ApplicationHandlerInfo> handlerList)
        {
            foreach (var handler in handlerList)
            {
                _handlers.AddOrUpdate(handler.InstanceId, handler, (_, old) => handler);
            }
        }

        // Forwards the request to the best available AH, retrying others on failure
        public async Task<HttpResponseMessage> ForwardWithFailover(HttpRequestMessage originalRequest)
        {
            var tried = new HashSet<string>(); // Tracks attempted handlers

            while (true)
            {
                var candidate = SelectBestAvailableHandler()
                    ?? throw new Exception("No Application Handler available for forwarding.");

                if (tried.Contains(candidate.InstanceId))
                    throw new Exception("All Application Handlers failed.");

                tried.Add(candidate.InstanceId);

                try
                {
                    // Clone and forward request to selected AH
                    var forwardRequest = CloneRequest(originalRequest, candidate.Url);
                    var response = await _httpClient.SendAsync(forwardRequest);

                    // Return on success
                    if (response.IsSuccessStatusCode)
                        return response;
                }
                catch
                {
                    // On failure, mark the handler as temporarily unavailable
                    candidate.TemporarilyUnavailable = true;
                    candidate.LastFailureTime = DateTime.UtcNow;
                }
            }
        }

        // Returns the best ranked available handler (ignores temporarily unavailable ones)
        private ApplicationHandlerInfo? SelectBestAvailableHandler()
        {
            return _handlers.Values
                .Where(h => !h.TemporarilyUnavailable)
                .OrderByDescending(h => h.Ranking) // Higher rank = more preferred
                .FirstOrDefault();
        }

        // Periodic callback to re-evaluate failed AHs and bring them back if healthy
        private void HealthCheckCallback(object? state)
        {
            foreach (var handler in _handlers.Values)
            {
                if (!handler.TemporarilyUnavailable)
                    continue;

                // Skip handlers still within retry cooldown
                if ((DateTime.UtcNow - handler.LastFailureTime) < _retryInterval)
                    continue;

                try
                {
                    // Try calling the /health endpoint of the AH
                    var result = _httpClient.GetAsync($"{handler.Url}/health").Result;
                    if (result.IsSuccessStatusCode)
                        handler.TemporarilyUnavailable = false; // Mark as available again
                }
                catch
                {
                    // Handler remains unavailable
                }
            }
        }

        // Clones the original request and replaces the base URL with the target AH's URL
        private HttpRequestMessage CloneRequest(HttpRequestMessage original, string newBaseUrl)
        {
            var uriBuilder = new UriBuilder(newBaseUrl)
            {
                Path = original.RequestUri?.AbsolutePath,
                Query = original.RequestUri?.Query.TrimStart('?') ?? ""
            };

            var clone = new HttpRequestMessage(original.Method, uriBuilder.Uri)
            {
                Content = original.Content
            };

            foreach (var header in original.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return clone;
        }
    }
}
