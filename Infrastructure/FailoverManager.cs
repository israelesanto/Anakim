using System.Collections.Concurrent;
using System.Net.Http;

namespace Anakim.Infrastructure
{
    public class FailoverManager
    {
        private readonly ConcurrentDictionary<string, ApplicationHandlerInfo> _handlers = new();
        private readonly HttpClient _httpClient;
        private readonly Timer _healthCheckTimer;
        private readonly TimeSpan _retryInterval = TimeSpan.FromSeconds(30);
        private int _lastUsedIndex = -1;
        private readonly object _lock = new();

        public FailoverManager()
        {
            // ⚠️ Aceita certificados SSL inválidos (autoassinados) — apenas para testes
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            _httpClient = new HttpClient(handler);

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
            var availableHandlers = _handlers.Values
                .Where(h => !h.TemporarilyUnavailable)
                .OrderBy(h => h.InstanceId)
                .ToList();

            if (!availableHandlers.Any())
                throw new Exception("No Application Handler available for forwarding.");

            int startIndex;
            lock (_lock)
            {
                _lastUsedIndex = (_lastUsedIndex + 1) % availableHandlers.Count;
                startIndex = _lastUsedIndex;
            }

            for (int i = 0; i < availableHandlers.Count; i++)
            {
                var index = (startIndex + i) % availableHandlers.Count;
                var candidate = availableHandlers[index];

                try
                {
                    var forwardRequest = CloneRequest(originalRequest, candidate.Url);
                    var response = await _httpClient.SendAsync(forwardRequest);

                    if (response.IsSuccessStatusCode)
                        return response;

                    Logger.LogWarning($"[{candidate.InstanceName}] respondeu com falha ({response.StatusCode}).");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Erro ao encaminhar para [{candidate.InstanceName}]: {ex.Message}");
                    candidate.TemporarilyUnavailable = true;
                    candidate.LastFailureTime = DateTime.UtcNow;
                }
            }

            throw new Exception("All Application Handlers failed.");
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
                    {
                        handler.TemporarilyUnavailable = false;
                        Logger.LogInfo($"[{handler.InstanceName}] está disponível novamente.");
                    }
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

            var clone = new HttpRequestMessage(original.Method, uriBuilder.Uri)
            {
                Content = original.Content
            };

            foreach (var header in original.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (original.Content != null)
            {
                foreach (var header in original.Content.Headers)
                    clone.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }

        public ApplicationHandlerInfo? GetBestHandler()
        {
            lock (_lock)
            {
                var disponiveis = _handlers.Values
                    .Where(h => !h.TemporarilyUnavailable)
                    .OrderBy(h => h.Ranking)
                    .ToList();

                if (!disponiveis.Any())
                    return null;

                var escolhido = disponiveis[0];

                Logger.LogInfo($"[FailoverManager] Handler selecionado: {escolhido.InstanceId} - {escolhido.Url}");

                return escolhido;
            }
        }

    }
}
