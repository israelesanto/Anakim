using System.Text;
using System.Text.Json;
using AnakimOrchestrator.Infrastructure.RequestProtection.Contracts;
using AnakimOrchestrator.Infrastructure.RequestProtection.Models;
using AnakimOrchestrator.Infrastructure.RequestProtection.Options;
using Microsoft.Extensions.Options;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Journal
{
    public class FileRequestJournal : IRequestJournal
    {
        private static readonly SemaphoreSlim _writeLock = new(1, 1);

        private readonly RequestProtectionOptions _options;
        private readonly JsonSerializerOptions _jsonOptions;

        public FileRequestJournal(IOptions<RequestProtectionOptions> options)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false
            };
        }

        public async Task AppendAsync(RequestJournalEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var directoryPath = Path.GetFullPath(_options.JournalDirectory);
            Directory.CreateDirectory(directoryPath);

            var filePath = BuildJournalFilePath(directoryPath);

            var line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await using var stream = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private string BuildJournalFilePath(string directoryPath)
        {
            var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
            var fileName = $"{_options.JournalFileNamePrefix}-{datePart}.jsonl";
            return Path.Combine(directoryPath, fileName);
        }
    }
}