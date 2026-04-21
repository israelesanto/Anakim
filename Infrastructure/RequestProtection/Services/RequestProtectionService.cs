using System.Security.Cryptography;
using System.Text;
using AnakimOrchestrator.Infrastructure.RequestProtection.Contracts;
using AnakimOrchestrator.Infrastructure.RequestProtection.Enums;
using AnakimOrchestrator.Infrastructure.RequestProtection.Models;
using AnakimOrchestrator.Infrastructure.RequestProtection.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Services
{
    public class RequestProtectionService : IRequestProtectionService
    {
        private readonly IProtectedRequestStore _store;
        private readonly IRequestJournal _journal;
        private readonly RequestProtectionOptions _options;

        public RequestProtectionService(
            IProtectedRequestStore store,
            IRequestJournal journal,
            IOptions<RequestProtectionOptions> options)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<ProtectedRequest> RegisterAsync(
            HttpContext httpContext,
            string requestBody,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var now = DateTime.UtcNow;
            var requestId = Guid.NewGuid().ToString("N");
            var correlationId = ResolveCorrelationId(httpContext, requestId);

            var protectedRequest = new ProtectedRequest
            {
                RequestId = requestId,
                CorrelationId = correlationId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                HttpMethod = httpContext.Request.Method,
                Route = httpContext.Request.Path.HasValue
                    ? httpContext.Request.Path.Value!
                    : string.Empty,
                QueryString = httpContext.Request.QueryString.HasValue
                    ? httpContext.Request.QueryString.Value!
                    : string.Empty,
                Headers = ExtractHeaders(httpContext),
                Body = requestBody ?? string.Empty,
                BodyHash = ComputeSha256(requestBody ?? string.Empty),
                Status = ProtectedRequestStatus.Received,
                ReplayPolicy = ResolveReplayPolicy(httpContext),
                ProcessingKind = ResolveProcessingKind(httpContext),
                AttemptCount = 0,
                ExpiresAtUtc = now.AddMinutes(_options.RequestTtlMinutes)
            };

            if (!_store.TryAdd(protectedRequest))
            {
                throw new InvalidOperationException(
                    $"Não foi possível registrar a requisição protegida '{requestId}' na store.");
            }

            await _journal.AppendAsync(
                new RequestJournalEntry
                {
                    EventType = "RequestReceived",
                    RequestId = protectedRequest.RequestId,
                    TimestampUtc = now,
                    Status = protectedRequest.Status.ToString(),
                    Message = "Requisição recebida e registrada na store.",
                    PayloadJson = BuildMinimalPayloadJson(protectedRequest)
                },
                cancellationToken);

            return protectedRequest;
        }

        public async Task MarkAsPersistedAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var request = GetRequiredRequest(requestId);

            request.Status = ProtectedRequestStatus.Persisted;
            request.UpdatedAtUtc = DateTime.UtcNow;

            EnsureUpdated(request);

            await _journal.AppendAsync(
                new RequestJournalEntry
                {
                    EventType = "RequestPersisted",
                    RequestId = request.RequestId,
                    TimestampUtc = request.UpdatedAtUtc,
                    Status = request.Status.ToString(),
                    Message = "Requisição marcada como persistida."
                },
                cancellationToken);
        }

        public async Task MarkAsDispatchingAsync(
            string requestId,
            string applicationHandlerInstanceName,
            CancellationToken cancellationToken = default)
        {
            var request = GetRequiredRequest(requestId);

            request.Status = ProtectedRequestStatus.Dispatching;
            request.SelectedApplicationHandler = applicationHandlerInstanceName;
            request.UpdatedAtUtc = DateTime.UtcNow;

            var attempt = new ProtectedRequestAttempt
            {
                AttemptNumber = request.AttemptCount + 1,
                ApplicationHandlerInstanceName = applicationHandlerInstanceName,
                StartedAtUtc = request.UpdatedAtUtc
            };

            request.Attempts.Add(attempt);
            request.AttemptCount = request.Attempts.Count;
            request.LastAttemptAtUtc = request.UpdatedAtUtc;

            EnsureUpdated(request);

            await _journal.AppendAsync(
                new RequestJournalEntry
                {
                    EventType = "RequestDispatching",
                    RequestId = request.RequestId,
                    TimestampUtc = request.UpdatedAtUtc,
                    Status = request.Status.ToString(),
                    ApplicationHandlerInstanceName = applicationHandlerInstanceName,
                    AttemptNumber = attempt.AttemptNumber,
                    Message = "Requisição em processo de despacho para o Application Handler."
                },
                cancellationToken);
        }

        public async Task MarkAsInFlightAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var request = GetRequiredRequest(requestId);

            request.Status = ProtectedRequestStatus.InFlight;
            request.UpdatedAtUtc = DateTime.UtcNow;

            var attempt = GetCurrentAttempt(request);
            attempt.WasConnectionEstablished = true;
            attempt.WasBodySent = true;

            EnsureUpdated(request);

            await _journal.AppendAsync(
                new RequestJournalEntry
                {
                    EventType = "RequestInFlight",
                    RequestId = request.RequestId,
                    TimestampUtc = request.UpdatedAtUtc,
                    Status = request.Status.ToString(),
                    ApplicationHandlerInstanceName = request.SelectedApplicationHandler,
                    AttemptNumber = attempt.AttemptNumber,
                    Message = "Requisição enviada ao Application Handler."
                },
                cancellationToken);
        }

        public async Task MarkAsCompletedAsync(
            string requestId,
            int responseStatusCode,
            string? responseBody,
            CancellationToken cancellationToken = default)
        {
            var request = GetRequiredRequest(requestId);

            request.Status = ProtectedRequestStatus.Completed;
            request.ResponseStatusCode = responseStatusCode;
            request.ResponseBody = responseBody;
            request.CompletedAtUtc = DateTime.UtcNow;
            request.UpdatedAtUtc = request.CompletedAtUtc.Value;
            request.IsStateUncertain = false;
            request.LastError = null;

            var attempt = GetCurrentAttempt(request);
            attempt.FinishedAtUtc = request.UpdatedAtUtc;
            attempt.WasAckReceived = true;
            attempt.Result = ProtectedRequestAttemptResult.Success;

            EnsureUpdated(request);

            await _journal.AppendAsync(
                new RequestJournalEntry
                {
                    EventType = "RequestCompleted",
                    RequestId = request.RequestId,
                    TimestampUtc = request.UpdatedAtUtc,
                    Status = request.Status.ToString(),
                    ApplicationHandlerInstanceName = request.SelectedApplicationHandler,
                    AttemptNumber = attempt.AttemptNumber,
                    Message = $"Requisição concluída com status HTTP {responseStatusCode}."
                },
                cancellationToken);
        }

        public async Task MarkAsFailedAsync(
            string requestId,
            string errorMessage,
            CancellationToken cancellationToken = default)
        {
            var request = GetRequiredRequest(requestId);

            request.Status = ProtectedRequestStatus.Failed;
            request.UpdatedAtUtc = DateTime.UtcNow;
            request.LastError = errorMessage;
            request.IsStateUncertain = false;

            var attempt = GetCurrentAttempt(request, false);
            if (attempt != null)
            {
                attempt.FinishedAtUtc = request.UpdatedAtUtc;
                attempt.Result = ClassifyFailureResult(attempt);
                attempt.FailureReason = errorMessage;
            }

            EnsureUpdated(request);

            await _journal.AppendAsync(
                new RequestJournalEntry
                {
                    EventType = "RequestFailed",
                    RequestId = request.RequestId,
                    TimestampUtc = request.UpdatedAtUtc,
                    Status = request.Status.ToString(),
                    ApplicationHandlerInstanceName = request.SelectedApplicationHandler,
                    AttemptNumber = attempt?.AttemptNumber ?? 0,
                    Message = errorMessage
                },
                cancellationToken);
        }

        public async Task MarkAsUnknownAsync(
            string requestId,
            string errorMessage,
            CancellationToken cancellationToken = default)
        {
            var request = GetRequiredRequest(requestId);

            request.Status = ProtectedRequestStatus.Unknown;
            request.UpdatedAtUtc = DateTime.UtcNow;
            request.LastError = errorMessage;
            request.IsStateUncertain = true;

            var attempt = GetCurrentAttempt(request, false);
            if (attempt != null)
            {
                attempt.FinishedAtUtc = request.UpdatedAtUtc;
                attempt.Result = ProtectedRequestAttemptResult.UnknownState;
                attempt.FailureReason = errorMessage;
            }

            EnsureUpdated(request);

            await _journal.AppendAsync(
                new RequestJournalEntry
                {
                    EventType = "RequestUnknown",
                    RequestId = request.RequestId,
                    TimestampUtc = request.UpdatedAtUtc,
                    Status = request.Status.ToString(),
                    ApplicationHandlerInstanceName = request.SelectedApplicationHandler,
                    AttemptNumber = attempt?.AttemptNumber ?? 0,
                    Message = errorMessage
                },
                cancellationToken);
        }

        private ProtectedRequest GetRequiredRequest(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new ArgumentException("O requestId não pode ser nulo ou vazio.", nameof(requestId));
            }

            if (!_store.TryGet(requestId, out var request) || request == null)
            {
                throw new KeyNotFoundException(
                    $"A requisição protegida '{requestId}' não foi encontrada na store.");
            }

            return request;
        }

        private void EnsureUpdated(ProtectedRequest request)
        {
            if (!_store.TryUpdate(request))
            {
                throw new InvalidOperationException(
                    $"Não foi possível atualizar a requisição protegida '{request.RequestId}'.");
            }
        }

        private static Dictionary<string, string> ExtractHeaders(HttpContext httpContext)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in httpContext.Request.Headers)
            {
                headers[header.Key] = header.Value.ToString();
            }

            return headers;
        }

        private static string ResolveCorrelationId(HttpContext httpContext, string fallbackRequestId)
        {
            if (httpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var correlationId) &&
                !string.IsNullOrWhiteSpace(correlationId.ToString()))
            {
                return correlationId.ToString();
            }

            if (httpContext.TraceIdentifier is not null && !string.IsNullOrWhiteSpace(httpContext.TraceIdentifier))
            {
                return httpContext.TraceIdentifier;
            }

            return fallbackRequestId;
        }

        private static string ComputeSha256(string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);

            return Convert.ToHexString(hash);
        }

        private static RequestReplayPolicy ResolveReplayPolicy(HttpContext httpContext)
        {
            if (HttpMethods.IsGet(httpContext.Request.Method) ||
                HttpMethods.IsHead(httpContext.Request.Method) ||
                HttpMethods.IsOptions(httpContext.Request.Method))
            {
                return RequestReplayPolicy.SafeReplay;
            }

            return RequestReplayPolicy.RequiresIdempotency;
        }

        private static RequestProcessingKind ResolveProcessingKind(HttpContext httpContext)
        {
            if (HttpMethods.IsGet(httpContext.Request.Method) ||
                HttpMethods.IsHead(httpContext.Request.Method))
            {
                return RequestProcessingKind.Query;
            }

            return RequestProcessingKind.Command;
        }

        private static ProtectedRequestAttempt GetCurrentAttempt(
            ProtectedRequest request,
            bool throwIfMissing = true)
        {
            var attempt = request.Attempts
                .OrderByDescending(x => x.AttemptNumber)
                .FirstOrDefault();

            if (attempt == null && throwIfMissing)
            {
                throw new InvalidOperationException(
                    $"A requisição '{request.RequestId}' não possui tentativa de despacho registrada.");
            }

            return attempt!;
        }

        private static ProtectedRequestAttemptResult ClassifyFailureResult(ProtectedRequestAttempt attempt)
        {
            if (!attempt.WasConnectionEstablished)
            {
                return ProtectedRequestAttemptResult.ConnectionFailure;
            }

            if (attempt.WasConnectionEstablished && attempt.WasBodySent && !attempt.WasAckReceived)
            {
                return ProtectedRequestAttemptResult.ConnectionLost;
            }

            return ProtectedRequestAttemptResult.Cancelled;
        }

        private static string BuildMinimalPayloadJson(ProtectedRequest request)
        {
            return
                $"{{\"RequestId\":\"{EscapeJson(request.RequestId)}\",\"Method\":\"{EscapeJson(request.HttpMethod)}\",\"Route\":\"{EscapeJson(request.Route)}\",\"QueryString\":\"{EscapeJson(request.QueryString)}\",\"Status\":\"{request.Status}\"}}";
        }

        private static string EscapeJson(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }
    }
}