using AnakimOrchestrator.Infrastructure.RequestProtection.Models;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Contracts
{
    public interface IRequestJournal
    {
        Task AppendAsync(RequestJournalEntry entry, CancellationToken cancellationToken = default);
    }
}