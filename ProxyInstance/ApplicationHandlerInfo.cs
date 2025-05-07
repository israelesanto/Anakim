using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Anakim.ProxyInstance.Failover
{
    public class ApplicationHandlerInfo
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public double Ranking { get; set; }
        public DateTime LastFailureTime { get; set; }
        public bool TemporarilyUnavailable { get; set; }
    }

    public class FailoverManager
    {
        private readonly ConcurrentDictionary<string, ApplicationHandlerInfo> _handlers = new();
        private readonly HttpClient _httpClient = new();
        private readonly Timer _healthCheckTimer;
        private readonly TimeSpan _retryInterval = TimeSpan.FromSeconds(30);

        public FailoverManager()
        {
            _healthCheckTimer = new Timer(HealthCheckCallback, null, _retryInterval, _retryInterval);
        }

        public void UpdateHandlers(List<ApplicationHandlerInfo> handlerList)
        {
            foreach (var handler in handlerList)
            {
                _handlers.AddOrUpdate(handler.InstanceId, handler, (_, old) => handler);
            }
        }

        public async Task<HttpResponseMessage> ForwardWithFailover(HttpRequestMessage originalRequest)
        {
            var tried = new HashSet<string>();

            while (true)
            {
                var candidate = SelectBestAvailableHandler();

                if (candidate == null)
                    throw new Exception("Nenhum Application Handler disponível para redirecionamento.");

                if (tried.Contains(candidate.InstanceId))
                    throw new Exception("Todos os Application Handlers falharam.");

                tried.Add(candidate.InstanceId);

                try
                {
                    var forwardRequest = CloneRequest(originalRequest, candidate.Url);
                    var response = await _httpClient.SendAsync(forwardRequest);

                    if (response.IsSuccessStatusCode)
                        return response;
                }
                catch
                {
                    candidate.TemporarilyUnavailable = true;
                    candidate.LastFailureTime = DateTime.UtcNow;
                }
            }
        }

        private ApplicationHandlerInfo? SelectBestAvailableHandler()
        {
            return _handlers.Values
                .Where(h => !h.TemporarilyUnavailable)
                .OrderByDescending(h => h.Ranking)
                .FirstOrDefault();
        }

        private void HealthCheckCallback(object? state)
        {
            foreach (var handler in _handlers.Values)
            {
                if (!handler.TemporarilyUnavailable)
                    continue;

                if ((DateTime.UtcNow - handler.LastFailureTime) < _retryInterval)
                    continue;

                try
                {
                    var result = _httpClient.GetAsync($"{handler.Url}/health").Result;
                    if (result.IsSuccessStatusCode)
                        handler.TemporarilyUnavailable = false;
                }
                catch
                {
                    // Continua indisponível
                }
            }
        }

        private HttpRequestMessage CloneRequest(HttpRequestMessage original, string newBaseUrl)
        {
            var uriBuilder = new UriBuilder(newBaseUrl)
            {
                Path = original.RequestUri?.AbsolutePath,
                Query = original.RequestUri?.Query.TrimStart('?') ?? ""
            };

            var clone = new HttpRequestMessage(original.Method, uriBuilder.Uri);
            clone.Content = original.Content;
            foreach (var header in original.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return clone;
        }
    }
}
